using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// usage: ArtFlow.ZitadelSetup <issuer> <dir>   (<dir>/admin.pat is written by Zitadel's first instance)
// writes <dir>/project.txt (the API's audience) and <dir>/clients.txt ("client <name> <client id> <secret> <user id>")
var (issuer, dir) = (args[0], args[1]);
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
    """{"roles":[{"key":"seller","displayName":"seller"},{"key":"bidder","displayName":"bidder"},{"key":"service","displayName":"service"}]}""");
var clients = new StringBuilder();
foreach (var (name, roles) in new[] { ("alice", "\"seller\""), ("bob", "\"bidder\"") })
{
    var user = (await Call(HttpMethod.Post, "/management/v1/users/machine", $$"""{"userName":"{{name}}","name":"{{name}}","accessTokenType":"ACCESS_TOKEN_TYPE_JWT"}""")).GetProperty("userId").GetString()!;
    var secret = await Call(HttpMethod.Put, $"/management/v1/users/{user}/secret");
    _ = await Call(HttpMethod.Post, $"/management/v1/users/{user}/grants", $$"""{"projectId":"{{project}}","roleKeys":[{{roles}}]}""");
    clients.Append(System.Globalization.CultureInfo.InvariantCulture, $"client {name} {secret.GetProperty("clientId").GetString()} {secret.GetProperty("clientSecret").GetString()} {user}\n");
}
await File.WriteAllTextAsync(Path.Combine(dir, "clients.txt"), clients.ToString());
await File.WriteAllTextAsync(Path.Combine(dir, "project.txt"), project);              // last: the API waits for the setup to complete
Console.WriteLine($"Zitadel provisioned: project {project}, machine users alice (seller), bob (bidder)");
