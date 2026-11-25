namespace Tarol.Changes;

/// <summary>
/// A topic of the application's push contract (BC-25) as the OpenAPI document describes it (<c>x-tarol-topics</c>).
/// <c>Tarol.Messaging.SignalR</c>'s <c>UiPushTopics</c> provides them; the generated <c>AddTarolEndpoints</c> of every
/// assembly writes them, whether or not that assembly references SignalR (TN-24, Tatalu FB-F17.3-2).
/// </summary>
/// <param name="Name">The topic.</param>
/// <param name="Version">The contract version this application publishes, if it has a local registration.</param>
/// <param name="Stream">The simple name of the aggregate whose id is a message's subject, if known.</param>
/// <param name="ContractType">The contract record of <paramref name="Version"/>, if known.</param>
/// <param name="Subscribe">Whether the rules let someone subscribe; <see langword="null"/> when an authorizer decides.</param>
/// <param name="Publish">Whether the rules let someone publish; <see langword="null"/> when an authorizer decides.</param>
public sealed record ContractTopic(string Name, int? Version, string? Stream, Type? ContractType, bool? Subscribe, bool? Publish);

/// <summary>The push topics of the application, for the OpenAPI document (TN-24).</summary>
public interface IContractTopics
{
    /// <summary>The topics, sorted by name.</summary>
    IReadOnlyList<ContractTopic> ContractTopics { get; }
}
