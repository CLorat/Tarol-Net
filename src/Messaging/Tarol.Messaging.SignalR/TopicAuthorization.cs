using System.Security.Claims;

namespace Tarol.Messaging.SignalR;

/// <summary>What a UI client asks to do with a topic.</summary>
public enum TopicOperation
{
    /// <summary>Receive the topic's messages (in the client's own tenant).</summary>
    Subscribe,

    /// <summary>Send a message to the topic's subscribers (in the client's own tenant).</summary>
    Publish,
}

/// <summary>
/// Decides every subscribe and publish of the message hub (the old <c>ITopicAuthorizer(topic)</c>, now with the principal and
/// the operation; F3). The tenant is never the client's choice: it comes from the principal, so a client only reaches its
/// own tenant's group.
/// </summary>
public interface ITopicAuthorizer
{
    /// <summary>Whether <paramref name="principal"/> may <paramref name="operation"/> <paramref name="topic"/>.</summary>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="operation">Subscribe or publish.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    /// <returns><see langword="true"/> to allow.</returns>
    ValueTask<bool> Authorize(ClaimsPrincipal principal, TopicOperation operation, string topic, CancellationToken cancellationToken);
}

/// <summary>
/// The rule list most applications need: per topic (exact, or a prefix ending in <c>*</c>) and operation, who may (any
/// authenticated user, or users in a role). Anything not allowed is refused.
/// </summary>
public sealed class TopicRules : ITopicAuthorizer
{
    private readonly List<(TopicOperation Operation, string Pattern, string? Role)> _rules = [];

    /// <summary>Allows <paramref name="operation"/> on <paramref name="topic"/> for every authenticated user, or for <paramref name="role"/>.</summary>
    /// <param name="operation">Subscribe or publish.</param>
    /// <param name="topic">A topic, or a prefix ending in <c>*</c> (e.g. <c>artflow.*</c>).</param>
    /// <param name="role">The role required; <see langword="null"/> = any authenticated user.</param>
    /// <returns>This.</returns>
    public TopicRules Allow(TopicOperation operation, string topic, string? role = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _rules.Add((operation, topic, role));
        return this;
    }

    /// <summary>Allows subscribing to <paramref name="topic"/>.</summary>
    /// <param name="topic">A topic, or a prefix ending in <c>*</c>.</param>
    /// <param name="role">The role required; <see langword="null"/> = any authenticated user.</param>
    /// <returns>This.</returns>
    public TopicRules Subscribe(string topic, string? role = null) => Allow(TopicOperation.Subscribe, topic, role);

    /// <summary>Allows publishing to <paramref name="topic"/>.</summary>
    /// <param name="topic">A topic, or a prefix ending in <c>*</c>.</param>
    /// <param name="role">The role required; <see langword="null"/> = any authenticated user.</param>
    /// <returns>This.</returns>
    public TopicRules Publish(string topic, string? role = null) => Allow(TopicOperation.Publish, topic, role);

    /// <inheritdoc />
    public ValueTask<bool> Authorize(ClaimsPrincipal principal, TopicOperation operation, string topic, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(topic);
        if (principal.Identity?.IsAuthenticated != true)
            return ValueTask.FromResult(false);
        foreach (var (op, pattern, role) in _rules)
            if (op == operation && Matches(pattern, topic) && (role is null || principal.IsInRole(role)))
                return ValueTask.FromResult(true);
        return ValueTask.FromResult(false);
    }

    /// <summary>The topics of the rules that are not prefixes (BC-25: client-only topics are described too).</summary>
    internal IEnumerable<string> ExactTopics => _rules.Where(static r => !r.Pattern.EndsWith('*')).Select(static r => r.Pattern);

    /// <summary>Whether a rule lets someone (any user, or a role) do <paramref name="operation"/> on <paramref name="topic"/>.</summary>
    internal bool Permits(TopicOperation operation, string topic) => _rules.Any(r => r.Operation == operation && Matches(r.Pattern, topic));

    private static bool Matches(string pattern, string topic)
        => pattern.EndsWith('*') ? topic.StartsWith(pattern[..^1], StringComparison.Ordinal) : string.Equals(pattern, topic, StringComparison.Ordinal);
}
