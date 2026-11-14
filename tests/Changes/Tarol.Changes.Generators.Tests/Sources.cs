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
}
