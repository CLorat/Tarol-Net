using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tarol.Results;

// Usage: web [loglevel]  (default Information). Prints one line per probe request, then exits.
var level = args.Length > 0 ? args[0] : "Information";
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(level));
builder.WebHost.UseUrls("http://127.0.0.1:5199");
builder.Services.AddAuthentication("Header").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Header", null);
builder.Services.AddAuthorization();
if (Environment.GetEnvironmentVariable("PROBE_PROBLEMDETAILS") == "1") builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/auctions/{seller}/cancel", (string seller, ClaimsPrincipal user, HttpContext http) =>
{
    var actor = user.Identity?.IsAuthenticated == true ? $"user:{user.FindFirstValue("sub")}" : "anonymous";
    Result result = actor == "anonymous" ? Error.Unauthorized("Auth.Required")
        : actor == $"user:{seller}" ? Result.Success() : Error.Forbidden("Auction.Cancel.NotSeller");
    var trace = Activity.Current is { } a ? $"{a.TraceId} (parent={a.ParentId ?? "-"})" : "NO ACTIVITY";
    http.Response.Headers["x-probe"] = $"actor={actor}; activity={trace}; traceIdentifier={http.TraceIdentifier}";
    return result switch
    {
        Success => Results.NoContent(),
        Error e => Results.Problem(statusCode: e.ToHttpStatusCode(), title: e.Code),
        Invalid i => Results.ValidationProblem(new Dictionary<string, string[]>()),
    };
});

await app.StartAsync();
using var client = new HttpClient { BaseAddress = new("http://127.0.0.1:5199") };
async Task Probe(string name, string? sub, string? traceparent)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, "/auctions/alice/cancel");
    if (sub is not null) req.Headers.Add("x-sub", sub);
    if (traceparent is not null) req.Headers.Add("traceparent", traceparent);
    using var resp = await client.SendAsync(req);
    Console.WriteLine($"[{level}] {name,-28} {(int)resp.StatusCode} {resp.Headers.GetValues("x-probe").Single()}");
}
await Probe("seller", "alice", null);
await Probe("other user", "bob", null);
await Probe("anonymous", null, null);
await Probe("incoming traceparent", "alice", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
await app.StopAsync();

sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(Request.Headers["x-sub"] is [{ } sub]
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "Header")), "Header"))
            : AuthenticateResult.NoResult());
}
