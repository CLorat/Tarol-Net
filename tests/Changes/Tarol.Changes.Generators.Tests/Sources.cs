namespace Tarol.Changes.Generators.Tests;

internal static class Sources
{
    public const string Changes = GeneratorTestHost.Usings + """
        namespace Shop;

        public sealed partial record CloseOrder(Id<Order> Order) : IChange<Order>
        {
            public Result Execute(Order order, ChangeContext context) => order.Close();
        }

        public sealed partial record OpenOrder : ICreation<Order>
        {
            public Result<Order> Execute(ChangeContext context) => Order.Open();
        }

        public sealed partial record CloseForCustomer(Id<Order> Order, [Read] Id<Customer> Customer) : IChange<Order>
        {
            public Result Execute(Order order, ChangeContext context)
                => context.Read<Customer>().Blocked ? Error.Conflict("Customer.Blocked") : order.Close();
        }

        public sealed partial record CloseByNumber(string Number) : ILoads<Order>
        {
            public ValueTask<Result<Order>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
                => ValueTask.FromResult<Result<Order>>(Error.NotFound("Order.NotFound"));
            public Result Execute(Order order, ChangeContext context) => order.Close();
        }

        public static partial class Admin
        {
            public sealed partial record ForceClose(Id<Order> Order) : IChange<Order>
            {
                public Result Execute(Order order, ChangeContext context) => order.Close();
            }
        }
        """;

    /// <summary>TN-8: the module attribute and a JSON context (the System.Text.Json generator does not run here).</summary>
    public const string Module = """
        [assembly: Tarol.Changes.GenerateTarolModule("Shop")]

        namespace Shop;

        [System.Text.Json.Serialization.JsonSerializable(typeof(int))]
        public sealed partial class ShopJson : System.Text.Json.Serialization.JsonSerializerContext;
        """;

    public const string Reactions = GeneratorTestHost.Usings + """
        namespace Shop;

        // a change reacting to two events (one name), with and without the RecordedEvent
        public sealed partial record CloseOrderOnEvent(Id<Order> Order) : IChange<Order>
        {
            [OnEvent<Customer.Registered>]
            public static CloseOrderOnEvent From(Customer.Registered e, RecordedEvent recorded) => new((Id<Order>)recorded.StreamId);

            [OnEvent<Order.WasClosed>]
            internal static CloseOrderOnEvent Again(Order.WasClosed e) => new(Id<Order>.Empty);

            public Result Execute(Order order, ChangeContext context) => order.Close();
        }

        // a creation with a pinned name
        public sealed partial record OpenOrderForCustomer : ICreation<Order>
        {
            [OnEvent<Customer.Registered>(Name = "Orders.OpenForNewCustomer")]
            public static OpenOrderForCustomer From(Customer.Registered e) => new();

            public Result<Order> Execute(ChangeContext context) => Order.Open();
        }

        // a scheduled reaction
        public static partial class Reminders
        {
            public sealed partial record RemindCustomer(Id<Order> Order) : IChange<Order>
            {
                [OnEvent<Customer.ReminderSet>(DueAt = nameof(Customer.ReminderSet.DueAt))]
                public static RemindCustomer From(Customer.ReminderSet e, RecordedEvent recorded) => new((Id<Order>)recorded.StreamId);

                public Result Execute(Order order, ChangeContext context) => order.Close();
            }
        }
        """;

    public const string Jobs = GeneratorTestHost.Usings + """
        namespace Shop;

        // a creation every night in a zone
        public sealed partial record OpenNightlyOrder : ICreation<Order>
        {
            [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
            public static OpenNightlyOrder From(JobOccurrence o) => new();

            public Result<Order> Execute(ChangeContext context) => Order.Open();
        }

        // a change per tenant with a pinned name, every missed occurrence, names in the fields
        public static partial class Closing
        {
            public sealed partial record CloseHouseOrder(Id<Order> Order) : IChange<Order>
            {
                [Scheduled("*/15 9-17 * * MON-FRI", Name = "Orders.CloseHouse", Missed = Missed.All, PerTenant = true)]
                internal static CloseHouseOrder From(JobOccurrence o) => new(Id<Order>.Empty);

                public Result Execute(Order order, ChangeContext context) => order.Close();
            }
        }
        """;
}
