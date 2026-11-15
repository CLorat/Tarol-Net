// Hand-written in the shape the generator would emit (one class per assembly; here one per application because the probe
// keeps both systems in one assembly). Static arrays for the span-returning tables (CS9203).
using System.Text.Json;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Primitives;

namespace ArtFlow
{
    public static class GeneratedConsumers
    {
        private static readonly string[] Topics_RecordPayment = ["billing.invoice-paid"];

        /// <summary>The consumers of ArtFlow.</summary>
        public static ConsumerSet Set { get; } = new([new ConsumerInfo("RecordPayment", Topics_RecordPayment)], Run);

        private static async ValueTask<ConsumerRun> Run(string consumer, ChangePipeline pipeline, Message message, MessageScope scope, CancellationToken cancellationToken)
        {
            switch (consumer, message.Type, message.Version)
            {
                case ("RecordPayment", "billing.invoice-paid", 1):
                {
                    var payload = scope.Payload<Contracts.InvoicePaidV1>(message.Data);
                    if (payload is null)
                        return ConsumerRun.Not(Mapped.Malformed);
                    return new(Mapped.Ran, await pipeline.Run(RecordPayment.From(payload), scope.Operation, scope.Options, cancellationToken));
                }
                case ("RecordPayment", "billing.invoice-paid", _):
                    return ConsumerRun.Not(Mapped.UnknownVersion);
                default:
                    return ConsumerRun.Not(Mapped.UnknownContract);
            }
        }
    }

    public static class GeneratedPublications
    {
        private const string SettledV1 = "publish:artflow.auction-settled@1";
        private const string SettledV2 = "publish:artflow.auction-settled@2";
        private const string NotedV1 = "publish:artflow.auction-noted@1";
        private static readonly string[] OnSettled = [SettledV1];
        private static readonly string[] OnSettledDual = [SettledV1, SettledV2];
        private static readonly string[] OnNoted = [NotedV1];

        /// <summary>The publications of ArtFlow (dual: v1 and v2 of auction-settled, while consumers migrate).</summary>
        public static PublicationSet Create(bool dual = false) => new("artflow",
            [new(SettledV1, "artflow.auction-settled", 1), new(SettledV2, "artflow.auction-settled", 2), new(NotedV1, "artflow.auction-noted", 1)],
            e => e switch
            {
                Auction.Settled => dual ? OnSettledDual : OnSettled,
                Auction.Noted => OnNoted,
                _ => ReadOnlySpan<string>.Empty,
            },
            Map);

        private static byte[]? Map(string publication, RecordedEvent r) => publication switch
        {
            SettledV1 => JsonSerializer.SerializeToUtf8Bytes(Publications.SettledV1((Auction.Settled)r.Event.Event, r), ArtFlowJson.Default.AuctionSettledV1),
            SettledV2 => JsonSerializer.SerializeToUtf8Bytes(Publications.SettledV2((Auction.Settled)r.Event.Event, r), ArtFlowJson.Default.AuctionSettledV2),
            NotedV1 => JsonSerializer.SerializeToUtf8Bytes(Publications.Noted((Auction.Noted)r.Event.Event, r), ArtFlowJson.Default.AuctionNotedV1),
            _ => throw new InvalidOperationException($"No publication named '{publication}'."),
        };
    }
}

namespace Billing
{
    public static class GeneratedConsumers
    {
        private static readonly string[] Topics_Settled = ["artflow.auction-settled"];

        /// <summary>The consumers of billing: two of the same contract, each with its own subscription and key.</summary>
        public static ConsumerSet Set { get; } = new([new ConsumerInfo("IssueInvoice", Topics_Settled), new ConsumerInfo("RecordSale", Topics_Settled)], Run);

        private static async ValueTask<ConsumerRun> Run(string consumer, ChangePipeline pipeline, Message message, MessageScope scope, CancellationToken cancellationToken)
        {
            switch (consumer, message.Type, message.Version)
            {
                case ("IssueInvoice", "artflow.auction-settled", 1):
                {
                    var payload = scope.Payload<Contracts.AuctionSettledV1>(message.Data);
                    if (payload is null)
                        return ConsumerRun.Not(Mapped.Malformed);
                    return new(Mapped.Ran, await pipeline.Run(IssueInvoice.From(payload), scope.Operation, scope.Options, cancellationToken));
                }
                case ("IssueInvoice", "artflow.auction-settled", 2):
                {
                    var payload = scope.Payload<Contracts.AuctionSettledV2>(message.Data);
                    if (payload is null)
                        return ConsumerRun.Not(Mapped.Malformed);
                    return new(Mapped.Ran, await pipeline.Run(IssueInvoice.From(payload), scope.Operation, scope.Options, cancellationToken));
                }
                case ("IssueInvoice", "artflow.auction-settled", _):
                    return ConsumerRun.Not(Mapped.UnknownVersion);
                case ("RecordSale", "artflow.auction-settled", 1):
                {
                    var payload = scope.Payload<Contracts.AuctionSettledV1>(message.Data);
                    if (payload is null)
                        return ConsumerRun.Not(Mapped.Malformed);
                    return new(Mapped.Ran, await pipeline.Run(RecordSale.From(payload, message), scope.Operation, scope.Options, cancellationToken));
                }
                case ("RecordSale", "artflow.auction-settled", _):
                    return ConsumerRun.Not(Mapped.UnknownVersion);
                default:
                    return ConsumerRun.Not(Mapped.UnknownContract);
            }
        }
    }

    public static class GeneratedPublications
    {
        private const string PaidV1 = "publish:billing.invoice-paid@1";
        private static readonly string[] OnPaid = [PaidV1];

        /// <summary>The publications of billing.</summary>
        public static PublicationSet Create() => new("billing", [new(PaidV1, "billing.invoice-paid", 1)],
            e => e is Invoice.Paid ? OnPaid : ReadOnlySpan<string>.Empty, Map);

        private static byte[]? Map(string publication, RecordedEvent r) => publication switch
        {
            PaidV1 => JsonSerializer.SerializeToUtf8Bytes(Publications.Paid((Invoice.Paid)r.Event.Event, r), BillingJson.Default.InvoicePaidV1),
            _ => throw new InvalidOperationException($"No publication named '{publication}'."),
        };
    }
}
