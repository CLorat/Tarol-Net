using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tarol.StronglyTypedId;
using Tarol.StronglyTypedId.EntityFrameworkCore;
using Tarol.StronglyTypedId.PackageTests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

// 1. The generator from the package ran on this assembly: Id<Order> is only inherited, never spelled.
var orderIdType = typeof(Id<>).MakeGenericType(typeof(Order));
Check("bundled generator registered the inherited Id<Order>",
    TypeDescriptor.GetConverter(orderIdType).GetType() == typeof(IdTypeConverter<Order>));

// 2. JSON through a source-generated context.
var order = new Order { Id = Id<Order>.New(), Customer = Id<Customer>.New() };
var json = JsonSerializer.Serialize(order, ConsumerJson.Default.Order);
var back = JsonSerializer.Deserialize(json, ConsumerJson.Default.Order)!;
Check("JSON round-trip", back.Id == order.Id && back.Customer == order.Customer);

// 3. EF Core satellite package.
using (var connection = new SqliteConnection("Data Source=:memory:"))
{
    connection.Open();
    var options = new DbContextOptionsBuilder<ConsumerContext>().UseSqlite(connection).UseStronglyTypedIds().Options;
    using (var db = new ConsumerContext(options))
    {
        db.Database.EnsureCreated();
        db.Orders.Add(order);
        db.SaveChanges();
    }

    using (var db = new ConsumerContext(options))
        Check("EF Core round-trip and query", db.Orders.Single(o => o.Customer == order.Customer).Id == order.Id);
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.StronglyTypedId.PackageTests
{
    public abstract class AggregateRoot<TSelf>
    {
        public Id<TSelf> Id { get; set; }
    }

    public sealed class Order : AggregateRoot<Order>
    {
        public Id<Customer> Customer { get; set; }
    }

    public sealed class Customer;

    [JsonSerializable(typeof(Order))]
    internal sealed partial class ConsumerJson : JsonSerializerContext;

    internal sealed class ConsumerContext(DbContextOptions<ConsumerContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Order>().Property(o => o.Id).ValueGeneratedNever();
    }
}
