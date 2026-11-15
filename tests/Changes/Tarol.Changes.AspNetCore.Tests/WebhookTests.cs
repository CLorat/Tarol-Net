using System.Net;
using Tarol.Kernel;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>Webhooks (changes.md §8, R-10): signature, window, rotation, delivery id, versions, the answer table, tenants.</summary>
public sealed class WebhookTests : IAsyncLifetime
{
    private static readonly Caller Sam = new("sam", null, "seller");                    // no tenant: the shared webhook URL's
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static string Envelope(string delivery, string version, string data, string type = "payment.succeeded")
        => $$"""{"id":"{{delivery}}","type":"{{type}}","version":"{{version}}","data":{{data}}}""";

    private static string V2(string auction, decimal amount = 150, string payment = "pay_1")
        => $$"""{"paymentId":"{{payment}}","reference":"{{auction}}","amount":{{amount}},"currency":"EUR"}""";

    private Task<Answer> Deliver(string body, string secret = "whsec_new", long? at = null, string path = "/webhooks/payments")
        => _app.Post(path, body, Nobody, Sign(body, secret, at));

    private async Task<string> Closed(Caller seller)
    {
        var id = await _app.Created(seller);
        (await _app.Post($"/auctions/{id}/close", null, seller)).Status.ShouldBe(HttpStatusCode.NoContent);
        return id;
    }

    [Fact]
    public async Task A_signed_delivery_runs_its_change_as_the_sender_and_a_redelivery_writes_nothing()
    {
        var id = await Closed(Sam);
        var body = Envelope("evt_1", "2", V2(id));

        var first = await Deliver(body);
        var events = _app.Store.Recorded.Count;
        var again = await Deliver(body);

        first.Status.ShouldBe(HttpStatusCode.OK);
        again.Status.ShouldBe(HttpStatusCode.OK);
        _app.Store.Recorded.Count.ShouldBe(events);
        _app.Store.Recorded[^1].Context.Actor.ShouldBe(Actor.Service("webhook:payments"));
        _app.Store.Recorded[^1].Event.Event.ShouldBe(new Auction.PaymentRecorded(150, "pay_1"));
    }

    [Fact]
    public async Task Forged_tampered_stale_and_unsigned_deliveries_are_401()
    {
        var body = Envelope("evt_1", "2", V2(Guid.CreateVersion7().ToString()));

        (await Deliver(body, "wrong")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.Post("/webhooks/payments", body.Replace("150", "1", StringComparison.Ordinal), Nobody, Sign(body))).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await Deliver(body, at: TimeProvider.System.GetUtcNow().AddMinutes(-10).ToUnixTimeSeconds())).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.Post("/webhooks/payments", body, Nobody)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_previous_secret_is_accepted_during_a_rotation()
    {
        var id = await Closed(Sam);
        (await Deliver(Envelope("evt_old", "2", V2(id)), "whsec_old")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_type_is_202_and_an_unknown_version_of_a_known_type_422()
    {
        var id = await Closed(Sam);

        (await Deliver(Envelope("evt_2", "2", V2(id), "payment.refunded"))).Status.ShouldBe(HttpStatusCode.Accepted);
        (await Deliver(Envelope("evt_3", "3", V2(id)))).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Each_contract_version_has_its_own_factory()
    {
        var id = await Closed(Sam);

        var v1 = await Deliver(Envelope("evt_4", "1", $$"""{"id":"pay_9","order":"{{id}}","cents":12500}"""));

        v1.Status.ShouldBe(HttpStatusCode.OK);
        _app.Store.Recorded[^1].Event.Event.ShouldBe(new Auction.PaymentRecorded(125, "pay_9"));
    }

    [Fact]
    public async Task Conflicts_and_unknown_targets_ask_for_a_retry_with_503()
    {
        var open = await _app.Created(Sam);
        var closed = await Closed(Sam);
        _ = await Deliver(Envelope("evt_5", "2", V2(closed)));

        var notClosed = await Deliver(Envelope("evt_6", "2", V2(open)));
        var paid = await Deliver(Envelope("evt_7", "2", V2(closed, payment: "pay_other")));
        var ghost = await Deliver(Envelope("evt_8", "2", V2(Guid.CreateVersion7().ToString())));

        notClosed.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        notClosed.Response.Headers.GetValues("Webhook-Error").ShouldBe(["Auction.NotClosed"]);
        paid.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);                          // C-9: a Conflict retries
        ghost.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);                         // S-7: may arrive before its target
    }

    [Fact]
    public async Task An_invalid_mapping_and_data_off_contract_are_422_and_a_broken_envelope_400()
    {
        var id = await Closed(Sam);

        (await Deliver(Envelope("evt_9", "2", V2(id, 0, "pay_0")))).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Deliver(Envelope("evt_10", "2", """{"paymentId":7}"""))).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Deliver("""{"type":"payment.succeeded"}""")).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Deliver($$$"""{"id":"evt_11","type":"payment.succeeded","data":{"pad":"{{{new string('x', 70_000)}}}"}}""")).Status
            .ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_per_tenant_url_runs_in_its_tenant_with_its_own_secret()
    {
        var id = await Closed(Carol);
        var body = Envelope("evt_b", "2", V2(id, payment: "pay_b"));

        var inTenant = await Deliver(body, "whsec_b", path: $"/webhooks/payments/{TenantB}");
        var sharedSecret = await Deliver(body, path: $"/webhooks/payments/{TenantB}");
        var unknownTenant = await Deliver(body, "whsec_b", path: $"/webhooks/payments/{Guid.CreateVersion7()}");
        var sharedUrl = await Deliver(Envelope("evt_b2", "2", V2(id, payment: "pay_b")));

        inTenant.Status.ShouldBe(HttpStatusCode.OK);
        sharedSecret.Status.ShouldBe(HttpStatusCode.Unauthorized);
        unknownTenant.Status.ShouldBe(HttpStatusCode.NotFound);
        sharedUrl.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);                      // tenant B's auction is not visible there
    }
}
