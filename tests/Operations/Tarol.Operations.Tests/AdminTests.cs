using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Operations.Tests;

/// <summary>The admin entry and the health endpoints over HTTP (TestServer; O-2, O-3; operations.md §11, 15C).</summary>
public sealed class AdminTests
{
    private static readonly Id<ITenant> Tenant = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000a");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record App(WebApplication Web, HttpClient Client, FakeSource Reactions, FakeReadModels ReadModels) : IAsyncDisposable
    {

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Web.StopAsync();
            await Web.DisposeAsync();
        }
    }

    private static HttpRequestMessage As(HttpMethod method, string path, string? user = "ops-alice", string? role = "tarol-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null)
            request.Headers.Add("X-User", user);
        if (role is not null)
            request.Headers.Add("X-Role", role);
        return request;
    }

    private static async Task<App> Start(int deadReactions = 5, IDataSubjects? subjects = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var reactions = new FakeSource(DeadLetterKinds.Reactions, deadReactions, Tenant);
        var readModels = new FakeReadModels();
        foreach (var service in Services.With(new Knobs(), new FakeTimeProvider(Services.Start), null, reactions, new FakeSource(DeadLetterKinds.Jobs, 1)))
            if (service.ServiceType != typeof(IHostedService))                                        // no poll: the tests call PollOnce
                builder.Services.Add(service);
        builder.Services.AddSingleton<IReadModelAdmin>(readModels);
        if (subjects is not null)
            builder.Services.AddSingleton(subjects);
        builder.Services.AddAuthentication(HeaderAuthentication.Name).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.Name, null);
        builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));
        var web = builder.Build();
        web.UseAuthentication();
        web.UseAuthorization();
        web.MapTarolHealth();
        web.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");
        await web.StartAsync(Ct);
        return new App(web, web.GetTestClient(), reactions, readModels);
    }

    [Fact]
    public void Mapping_the_admin_entry_without_a_policy_throws()
    {
        var web = WebApplication.CreateSlimBuilder().Build();

        Should.Throw<InvalidOperationException>(() => web.MapTarolAdmin("/tarol/admin", policy: "")).Message.ShouldContain("policy");
        Should.Throw<InvalidOperationException>(() => web.MapTarolAdmin("/tarol/admin", policy: "  "));
    }

    [Fact]
    public async Task Without_a_principal_it_is_401_and_without_the_role_403()
    {
        await using var app = await Start();

        (await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/reactions", user: null, role: null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/reactions", role: "member"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/1/redrive", role: null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        app.Reactions.Acts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pages_follow_next_and_filter_by_tenant_and_name()
    {
        await using var app = await Start(deadReactions: 7);

        var seen = new List<long>();
        string? after = null;
        do
        {
            var response = await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/reactions?limit=3" + (after is null ? "" : "&after=" + after)), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = (await response.Content.ReadFromJsonAsync(OperationsJson.Default.DeadLetterPageView, Ct))!;
            seen.AddRange(page.Items.Select(i => i.Id));
            after = page.Next?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        while (after is not null);
        seen.ShouldBe([1, 2, 3, 4, 5, 6, 7]);

        var filtered = (await (await app.Client.SendAsync(As(HttpMethod.Get, $"/tarol/admin/dead/reactions?tenant={Tenant.Value}&name=Even"), Ct))
            .Content.ReadFromJsonAsync(OperationsJson.Default.DeadLetterPageView, Ct))!;
        filtered.Items.ShouldHaveSingleItem().Id.ShouldBe(6);
        filtered.Items[0].Tenant.ShouldBe(Tenant.Value);
        (await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/reactions?limit=201"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/sagas"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Redrive_and_discard_by_id_name_the_actor_and_answer_204_or_404()
    {
        await using var app = await Start();

        (await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/2/redrive"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/2/redrive"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Client.SendAsync(As(HttpMethod.Delete, "/tarol/admin/dead/reactions/3", user: "ops-bob"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client.SendAsync(As(HttpMethod.Delete, "/tarol/admin/dead/reactions/3"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        app.Reactions.Acts.ShouldBe([("redrive", 2L, "ops-alice"), ("discard", 3L, "ops-bob")]);
    }

    [Fact]
    public async Task A_filtered_redrive_is_capped_and_counts_what_it_moved()
    {
        await using var app = await Start(deadReactions: 1500);

        var capped = await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/redrive?name=Odd&max=5"), Ct);
        (await capped.Content.ReadFromJsonAsync(OperationsJson.Default.RedriveView, Ct))!.Redriven.ShouldBe(5);
        var all = await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/redrive"), Ct);
        (await all.Content.ReadFromJsonAsync(OperationsJson.Default.RedriveView, Ct))!.Redriven.ShouldBe(1000);       // at most 1,000 per call
        (await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/redrive?max=1001"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        app.Reactions.Acts.Count.ShouldBe(1005);
        app.Reactions.Acts.Take(5).ShouldAllBe(a => a.Id % 2 == 1 && a.Actor == "ops-alice");
    }

    [Fact]
    public async Task Projection_versions_are_listed_and_a_superseded_one_dropped_the_newest_is_409()
    {
        await using var app = await Start();

        var versions = (await (await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/projections"), Ct))
            .Content.ReadFromJsonAsync(OperationsJson.Default.IReadOnlyListProjectionVersion, Ct))!;
        versions.Select(v => (v.Version, v.Superseded)).ShouldBe([(1, true), (2, false)]);
        (await app.Client.SendAsync(As(HttpMethod.Delete, "/tarol/admin/projections/auction-list/1"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var newest = await app.Client.SendAsync(As(HttpMethod.Delete, "/tarol/admin/projections/auction-list/2"), Ct);
        newest.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(newest, HttpStatusCode.Conflict, AdminProblems.ProjectionDrop)).GetProperty("detail").GetString().ShouldBe("the newest version");
        (await app.Client.SendAsync(As(HttpMethod.Delete, "/tarol/admin/projections/auction-list/3"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        app.ReadModels.Dropped.ShouldBe([("auction-list", 1, "ops-alice")]);
    }

    /// <summary>Asserts an RFC 9457 problem with the code (BC-10) and returns its body.</summary>
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().ShouldBe((int)status);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("title").GetString().ShouldBe(code);
        body.GetProperty("type").GetString().ShouldStartWith("https://tools.ietf.org/html/rfc9110#section-15.");
        return body;
    }

    [Fact]
    public async Task Every_admin_error_is_a_problem_with_a_code()                // BC-10
    {
        await using var app = await Start(subjects: new FailingSubjects(failures: 0));

        await Problem(await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/reactions?limit=201"), Ct), HttpStatusCode.BadRequest, AdminProblems.Query);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/reactions?after=x"), Ct), HttpStatusCode.BadRequest, AdminProblems.Query);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/redrive?max=0"), Ct), HttpStatusCode.BadRequest, AdminProblems.Query);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead/sagas"), Ct), HttpStatusCode.NotFound, AdminProblems.Kind);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Post, "/tarol/admin/dead/reactions/99/redrive"), Ct), HttpStatusCode.NotFound, AdminProblems.NotFound);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Delete, "/tarol/admin/dead/reactions/99"), Ct), HttpStatusCode.NotFound, AdminProblems.NotFound);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Post, $"/tarol/admin/subjects/{Guid.NewGuid()}/erase"), Ct), HttpStatusCode.BadRequest, AdminProblems.Tenant);
        await Problem(await app.Client.SendAsync(As(HttpMethod.Get, $"/tarol/admin/subjects/{Guid.NewGuid()}/export?tenant=nope"), Ct), HttpStatusCode.BadRequest, AdminProblems.Tenant);
    }

    [Fact]
    public async Task The_dead_letter_kinds_are_listed()
    {
        await using var app = await Start();

        var kinds = await (await app.Client.SendAsync(As(HttpMethod.Get, "/tarol/admin/dead"), Ct)).Content.ReadFromJsonAsync<string[]>(Ct);

        kinds.ShouldBe([DeadLetterKinds.Jobs, DeadLetterKinds.Reactions]);
    }

    [Fact]
    public async Task An_incomplete_erasure_is_409_with_the_failed_participants_and_a_rerun_completes_it()   // BC-10: was a 500
    {
        var subjects = new FailingSubjects(failures: 1);
        await using var app = await Start(subjects: subjects);
        var subject = Guid.NewGuid();

        var first = await app.Client.SendAsync(As(HttpMethod.Post, $"/tarol/admin/subjects/{subject}/erase?tenant={Tenant.Value}"), Ct);
        var body = await Problem(first, HttpStatusCode.Conflict, AdminProblems.ErasureIncomplete);
        body.GetProperty("failed").EnumerateArray().Select(f => f.GetString()).ShouldBe(["search", "page-cache"]);
        body.GetProperty("subject").GetGuid().ShouldBe(subject);
        body.GetProperty("tenant").GetGuid().ShouldBe(Tenant.Value);

        var again = await app.Client.SendAsync(As(HttpMethod.Post, $"/tarol/admin/subjects/{subject}/erase?tenant={Tenant.Value}"), Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync(OperationsJson.Default.ErasureView, Ct))!.AlreadyErased.ShouldBeTrue();
        subjects.Calls.ShouldBe(2);
    }

    /// <summary>An <see cref="IDataSubjects"/> whose first <paramref name="failures"/> erasures are incomplete.</summary>
    private sealed class FailingSubjects(int failures) : IDataSubjects
    {
        public int Calls { get; private set; }

        public ValueTask<SubjectErasure> EraseSubject(Guid tenant, Guid subject, string actor, CancellationToken cancellationToken)
            => ++Calls <= failures
                ? throw new Incomplete(tenant, subject, ["search", "page-cache"])
                : ValueTask.FromResult(new SubjectErasure(tenant, subject, AlreadyErased: Calls > 1, []));

        public ValueTask<SubjectErasure> EraseTenant(Guid tenant, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SubjectExport> Export(Guid tenant, Guid subject, string actor, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SubjectExport(tenant, subject, "{}"u8.ToArray()));
    }

    private sealed class Incomplete(Guid tenant, Guid? subject, IReadOnlyList<string> failed) : Exception("incomplete"), IIncompleteErasure
    {
        public Guid Tenant { get; } = tenant;

        public Guid? Subject { get; } = subject;

        public IReadOnlyList<string> Failed { get; } = failed;
    }

    [Fact]
    public async Task Live_ready_and_tarol_answer_on_their_paths()
    {
        await using var app = await Start();

        (await app.Client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.Client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);   // no snapshot yet
        _ = await app.Web.Services.GetRequiredService<OperationsHost>().PollOnce(Ct);
        (await app.Client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tarol = await app.Client.GetAsync("/health/tarol", Ct);
        tarol.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await tarol.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
    }
}
