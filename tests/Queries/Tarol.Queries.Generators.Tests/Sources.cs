namespace Tarol.Queries.Generators.Tests;

internal static class Sources
{
    /// <summary>A row with every column type, a creating When with the RecordedEvent, an entity event, and a nested one.</summary>
    public const string Projections = """
        using System;
        using Tarol.Primitives;
        using Tarol.Queries;

        namespace Shop;

        [Projection("order-list", Version = 2)]
        public sealed partial record OrderRow(Guid Id, string Customer, [Sort] DateTimeOffset At, OrderState State, int Lines, decimal Total,
            string? Note, DateTimeOffset? PaidAt, OrderState? Previous, bool Urgent, long Big, short Small, double Score, Guid Owner, int[] Tags,
            Guid[]? Watchers, System.Collections.Generic.IReadOnlyList<string> Labels, OrderState[] History)
        {
            [Sort]
            public string UrlPath => "/orders/" + Id;

            public static OrderRow When(Order.Opened e, RecordedEvent r)
                => new(r.StreamId, e.Customer, e.At, OrderState.Open, 0, 0m, null, null, null, false, 0, 0, 0, Guid.Empty, [], null, [], []);

            public OrderRow When(Line.Added e) => this with { Lines = Lines + e.Quantity };

            public OrderRow When(Order.WasClosed e, RecordedEvent r) => this with { State = OrderState.Paid, PaidAt = r.RecordedAt };
        }

        public static partial class Reports
        {
            [Projection("customers")]
            public sealed partial record CustomerRow(Guid Id, [Sort] Guid Since)
            {
                public static CustomerRow When(Customer.Registered e, RecordedEvent r) => new(r.StreamId, r.EventId);
            }
        }
        """;
}
