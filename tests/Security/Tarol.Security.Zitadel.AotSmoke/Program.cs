using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Kernel;
using Tarol.Security;
using Tarol.Security.Zitadel;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

const string Project = "399724483650256899";
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddTarol(SmokeJson.Default, _ => { }).UseInMemoryStore().AddSecurity(_ => { }).AddEndpoints()
    .AddZitadel(o => (o.Authority, o.ProjectId, o.RequireHttpsMetadata) = ("http://localhost:8080", Project, false));
await using var app = builder.Build();

var jwt = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
Check("JwtBearer against the issuer and the project", jwt.Authority == "http://localhost:8080" && jwt.Audience == Project && !jwt.MapInboundClaims);
var identity = new ClaimsIdentity(
[
    new Claim("sub", "399724483734142979"), new Claim("client_id", "billing"),
    new Claim($"urn:zitadel:iam:org:project:{Project}:roles", """{"service":{"1":"x"},"seller":{"1":"x"}}""", "JSON"),
    new Claim("urn:zitadel:iam:user:resourceowner:id", "399724476671000579"),
], "AuthenticationTypes.Federation", "sub", "role");
var user = await app.Services.GetRequiredService<IClaimsTransformation>().TransformAsync(new ClaimsPrincipal(identity));
Check("project roles become roles natively", user.IsInRole("seller") && user.IsInRole("service"));
Check("the organisation becomes the tenant", user.FindFirst("tenant")?.Value == ZitadelTenants.Of("399724476671000579").ToString());
Check("the service role makes a service actor", app.Services.GetRequiredService<ChangeEndpointOptions>().Actor!(user) == Actor.Service("billing"));
Check("the change authorization is registered", app.Services.GetService<IChangeAuthorization>() is ChangeAuthorization);

Console.WriteLine(failures == 0 ? "Tarol.Security.Zitadel AOT smoke: all checks passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
internal sealed partial class SmokeJson : System.Text.Json.Serialization.JsonSerializerContext;
