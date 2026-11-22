namespace Tarol.Messaging.SignalR;

/// <summary>
/// One topic of the UI push as a client sees it (BC-25): what the hub forwards or lets clients publish, the contract's version
/// and record, the aggregate whose id is a message's subject, and whether the rules let anyone subscribe or publish.
/// </summary>
/// <param name="Name">The topic, e.g. <c>artflow.bid-placed</c>.</param>
public sealed record UiPushTopic(string Name)
{
    /// <summary>The contract version this application publishes (the highest, if it publishes several); <see langword="null"/>
    /// for a topic without a local <c>[Publishes]</c> registration.</summary>
    public int? Version { get; init; }

    /// <summary>The simple name of the aggregate whose id is a message's subject (e.g. <c>Auction</c>); <see langword="null"/>
    /// when it is not known statically.</summary>
    public string? Stream { get; init; }

    /// <summary>The contract record of <see cref="Version"/> (a message's <c>data</c>); <see langword="null"/> without a local registration.</summary>
    public Type? ContractType { get; init; }

    /// <summary>Whether the forwarder sends the topic's messages to UI clients (<see cref="UiPushOptions.Forward"/>).</summary>
    public bool Forwarded { get; init; }

    /// <summary>Whether <see cref="UiPushOptions.Rules"/> let someone (any user, or a role) subscribe; <see langword="null"/>
    /// when the application registered its own <see cref="ITopicAuthorizer"/>, which decides instead.</summary>
    public bool? Subscribe { get; init; }

    /// <summary>Whether <see cref="UiPushOptions.Rules"/> let someone publish; <see langword="null"/> with your own <see cref="ITopicAuthorizer"/>.</summary>
    public bool? Publish { get; init; }
}

/// <summary>
/// The topics of the UI push (BC-25), registered by <see cref="UiPushExtensions.PushToUi"/>: every forwarded topic, and every
/// exact topic of <see cref="UiPushOptions.Rules"/> (a client-only topic: nothing forwarded, clients publish to each other),
/// sorted by name. A topic with a <c>[Publishes]</c> registration of the application (<c>GeneratedPublications.Set</c>,
/// given to <c>WithOutboxRows</c> or <c>Publish</c>) has its version, record and stream. The generated
/// <c>AddTarolEndpoints()</c> of <c>Tarol.Changes.AspNetCore</c> writes them to the OpenAPI document as <c>x-tarol-topics</c>.
/// </summary>
public sealed class UiPushTopics
{
    /// <summary>Describes the topics of <paramref name="options"/>.</summary>
    /// <param name="options">The UI push's topics and rules.</param>
    /// <param name="publications">The application's publications (contract, version, record, stream).</param>
    /// <param name="rulesDecide"><see langword="true"/> when <see cref="UiPushOptions.Rules"/> is the hub's authorizer;
    /// <see langword="false"/> leaves <see cref="UiPushTopic.Subscribe"/> and <see cref="UiPushTopic.Publish"/> unknown.</param>
    public UiPushTopics(UiPushOptions options, IEnumerable<PublicationInfo> publications, bool rulesDecide = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(publications);
        var latest = new Dictionary<string, PublicationInfo>(StringComparer.Ordinal);
        foreach (var publication in publications)
        {
            ArgumentNullException.ThrowIfNull(publication, nameof(publications));
            if (!latest.TryGetValue(publication.Contract, out var known) || known.Version < publication.Version)
                latest[publication.Contract] = publication;
        }
        var names = new SortedSet<string>(options.Topics, StringComparer.Ordinal);
        foreach (var topic in options.Rules.ExactTopics)
            if (Message.SystemOf(topic).Length > 0)
                names.Add(topic);
        var forwarded = options.Topics.ToHashSet(StringComparer.Ordinal);
        Topics = [.. names.Select(name =>
        {
            var published = latest.GetValueOrDefault(name);
            return new UiPushTopic(name)
            {
                Version = published?.Version,
                Stream = published?.Stream,
                ContractType = published?.ContractType,
                Forwarded = forwarded.Contains(name),
                Subscribe = rulesDecide ? options.Rules.Permits(TopicOperation.Subscribe, name) : null,
                Publish = rulesDecide ? options.Rules.Permits(TopicOperation.Publish, name) : null,
            };
        })];
    }

    /// <summary>The topics, sorted by name.</summary>
    public IReadOnlyList<UiPushTopic> Topics { get; }
}
