using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// usage: ArtFlow.ZitadelSetup <issuer> <dir> [<access token lifetime in seconds, e.g. 60s>]   (<dir>/admin.pat is written by Zitadel's first instance)
// writes <dir>/project.txt (the API's audience), <dir>/clients.txt ("client <name> <client id> <secret> <user id>"),
// <dir>/web.txt ("web <client id>": the browser app, PKCE) and <dir>/users.txt ("user <login> <password> <user id> <roles>")
var (issuer, dir) = (args[0], args[1]);
var tokenLifetime = args.Length > 2 && args[2].Length > 0 ? args[2] : null;
var patFile = Path.Combine(dir, "admin.pat");
for (var i = 0; i < 240 && !(File.Exists(patFile) && new FileInfo(patFile).Length > 0); i++)
    await Task.Delay(500);
using var api = new HttpClient { BaseAddress = new Uri(issuer) };
api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(patFile)).Trim());

async Task<JsonElement> Call(HttpMethod method, string path, string body = "{}")
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            using var response = await api.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 240)            // the first instance is still being set up
            {
                await Task.Delay(500);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return JsonElement.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException) when (attempt < 240)
        {
            await Task.Delay(500);
        }
    }
}

var project = (await Call(HttpMethod.Post, "/management/v1/projects", """{"name":"artflow","projectRoleAssertion":true}""")).GetProperty("id").GetString()!;
_ = await Call(HttpMethod.Post, $"/management/v1/projects/{project}/roles/_bulk",
    """{"roles":[{"key":"seller","displayName":"seller"},{"key":"bidder","displayName":"bidder"},{"key":"service","displayName":"service"},{"key":"tarol-admin","displayName":"tarol-admin"}]}""");
var clients = new StringBuilder();
foreach (var (name, roles) in new[] { ("alice", "\"seller\",\"tarol-admin\""), ("bob", "\"bidder\"") })
{
    var user = (await Call(HttpMethod.Post, "/management/v1/users/machine", $$"""{"userName":"{{name}}","name":"{{name}}","accessTokenType":"ACCESS_TOKEN_TYPE_JWT"}""")).GetProperty("userId").GetString()!;
    var secret = await Call(HttpMethod.Put, $"/management/v1/users/{user}/secret");
    _ = await Call(HttpMethod.Post, $"/management/v1/users/{user}/grants", $$"""{"projectId":"{{project}}","roleKeys":[{{roles}}]}""");
    clients.Append(System.Globalization.CultureInfo.InvariantCulture, $"client {name} {secret.GetProperty("clientId").GetString()} {secret.GetProperty("clientSecret").GetString()} {user}\n");
}
await File.WriteAllTextAsync(Path.Combine(dir, "clients.txt"), clients.ToString());

// BC-9: the browser client: a user-agent app (no secret, PKCE), JWT access tokens with the roles, any path of the Vite dev
// server as redirect (dev mode allows http and globs); human users with grants and a password generated per run
var web = await Call(HttpMethod.Post, $"/management/v1/projects/{project}/apps/oidc", """
    {"name":"artflow-web","redirectUris":["http://localhost:5173/*"],"postLogoutRedirectUris":["http://localhost:5173/*"],
     "responseTypes":["OIDC_RESPONSE_TYPE_CODE"],"grantTypes":["OIDC_GRANT_TYPE_AUTHORIZATION_CODE","OIDC_GRANT_TYPE_REFRESH_TOKEN"],
     "appType":"OIDC_APP_TYPE_USER_AGENT","authMethodType":"OIDC_AUTH_METHOD_TYPE_NONE","devMode":true,
     "accessTokenType":"OIDC_TOKEN_TYPE_JWT","accessTokenRoleAssertion":true,"idTokenRoleAssertion":true,"idTokenUserinfoAssertion":true}
    """);
await File.WriteAllTextAsync(Path.Combine(dir, "web.txt"), $"web {web.GetProperty("clientId").GetString()}\n");
var users = new StringBuilder();
foreach (var (login, first, roles) in new[] { ("ada", "Ada", "\"seller\",\"tarol-admin\""), ("ben", "Ben", "\"bidder\""), ("cleo", "Cleo", "\"bidder\"") })
{
    var password = "Tarol-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)) + "-A1!";
    var user = (await Call(HttpMethod.Post, "/management/v1/users/human/_import", $$"""
        {"userName":"{{login}}","profile":{"firstName":"{{first}}","lastName":"ArtFlow"},"email":{"email":"{{login}}@artflow.example","isEmailVerified":true},
         "password":"{{password}}","passwordChangeRequired":false}
        """)).GetProperty("userId").GetString()!;
    _ = await Call(HttpMethod.Post, $"/management/v1/users/{user}/grants", $$"""{"projectId":"{{project}}","roleKeys":[{{roles}}]}""");
    users.Append(System.Globalization.CultureInfo.InvariantCulture, $"user {login} {password} {user} {roles.Replace("\"", "", StringComparison.Ordinal)}\n");
}
await File.WriteAllTextAsync(Path.Combine(dir, "users.txt"), users.ToString());
// BC-18: a login page for the browser app. Zitadel v4 sends every authorize request to login v2 (instance feature
// loginV2.required), a separate application this AppHost does not run (/ui/v2/login/login answered 404); the login built
// into Zitadel (v1, /ui/login) serves the PKCE app instead. No "set up 2-factor?" prompt on a user's first sign-in.
_ = await Call(HttpMethod.Put, "/v2/features/instance", """{"loginV2":{"required":false}}""");
_ = await Call(HttpMethod.Put, "/admin/v1/policies/login", """
    {"allowUsernamePassword":true,"allowRegister":false,"allowExternalIdp":false,"passwordlessType":"PASSWORDLESS_TYPE_NOT_ALLOWED",
     "passwordCheckLifetime":"864000s","externalLoginCheckLifetime":"864000s","mfaInitSkipLifetime":"0s",
     "secondFactorCheckLifetime":"64800s","multiFactorCheckLifetime":"43200s"}
    """);
if (tokenLifetime is not null)                                                          // BC-9: an "expired token" test
    _ = await Call(HttpMethod.Put, "/admin/v1/settings/oidc", $$"""
        {"accessTokenLifetime":"{{tokenLifetime}}","idTokenLifetime":"{{tokenLifetime}}","refreshTokenIdleExpiration":"2592000s","refreshTokenExpiration":"7776000s"}
        """);
await File.WriteAllTextAsync(Path.Combine(dir, "project.txt"), project);              // last: the API waits for the setup to complete
Console.WriteLine($"Zitadel provisioned: project {project}, machine users alice (seller, tarol-admin), bob (bidder), the web app, "
    + $"human users ada (seller, tarol-admin), ben and cleo (bidder){(tokenLifetime is null ? "" : $", access tokens valid {tokenLifetime}")}");
