namespace Tarol.Security.Zitadel;

/// <summary>The Zitadel instance and project an application trusts.</summary>
public sealed class ZitadelOptions
{
    /// <summary>The issuer, e.g. <c>https://auth.example.com</c> (discovery and signing keys are read from it).</summary>
    public string Authority { get; set; } = "";

    /// <summary>The Zitadel project id: the tokens' audience, and the project whose roles are read.</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>Metadata over HTTPS only (default); a local container without TLS needs <see langword="false"/>.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>The project role that marks a machine user as a service: its runs record <c>Actor.Service(client_id)</c>
    /// (Zitadel's machine users have a <c>sub</c> of their own). Default <c>service</c>; <see langword="null"/>: none.</summary>
    public string? ServiceRole { get; set; } = "service";

    /// <summary>
    /// The path of the message hub (<c>Tarol.Messaging.SignalR</c>'s <c>UiPushOptions.HubPath</c>, default
    /// <c>/tarol/messages</c>), where the bearer token is also read from the query string <c>access_token</c> (BC-2): a browser
    /// cannot set headers on a WebSocket or an EventSource. Only requests under this path read it; everywhere else a token in
    /// the URL is ignored (URLs end up in logs). <see langword="null"/>: never from the query.
    /// </summary>
    public string? HubPath { get; set; } = "/tarol/messages";

    internal void Validate()
    {
        if (HubPath is not null && !HubPath.StartsWith('/'))
            throw new InvalidOperationException("ZitadelOptions.HubPath starts with '/' (or is null: no token from the query string).");
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out _))
            throw new InvalidOperationException("ZitadelOptions.Authority must be the issuer's absolute URL.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
    }
}
