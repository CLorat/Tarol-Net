using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Tarol.StronglyTypedId.EntityFrameworkCore.Tests;

public sealed class StronglyTypedIdEntityFrameworkCoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public StronglyTypedIdEntityFrameworkCoreTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(Mapping.Convention)]
    [InlineData(Mapping.PerType)]
    public async Task Ids_Round_Trip_And_Translate_In_Queries(Mapping mapping)
    {
        var customer = new Customer { Id = Id<Customer>.New(), Name = "Ada" };
        var order = new Order { Id = Id<Order>.New(), CustomerId = customer.Id, ParentId = null, SalesRep = Id<ISalesRep>.New(), Region = Id<Region>.New() };
        var child = new Order { Id = Id<Order>.New(), CustomerId = customer.Id, ParentId = order.Id, SalesRep = order.SalesRep, Region = order.Region };
        var ct = TestContext.Current.CancellationToken;

        await using (var db = Create(mapping))
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.AddRange(customer, order, child);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = Create(mapping))
        {
            var loaded = await db.Orders.SingleAsync(o => o.ParentId == order.Id, ct);
            loaded.Id.ShouldBe(child.Id);
            loaded.CustomerId.ShouldBe(customer.Id);
            loaded.SalesRep.ShouldBe(order.SalesRep);
            loaded.Region.ShouldBe(order.Region);

            var roots = await db.Orders.Where(o => o.ParentId == null).Select(o => o.Id).ToListAsync(ct);
            roots.ShouldBe([order.Id]);

            (await db.Customers.FindAsync([customer.Id], ct))!.Name.ShouldBe("Ada");
        }
    }

    [Fact]
    public void Convention_Maps_Every_Id_Property_To_A_Guid_Column()
    {
        using var db = Create(Mapping.Convention);
        var entity = db.Model.FindEntityType(typeof(Order))!;

        foreach (var name in new[] { nameof(Order.Id), nameof(Order.CustomerId), nameof(Order.ParentId), nameof(Order.SalesRep), nameof(Order.Region) })
        {
            var property = entity.FindProperty(name).ShouldNotBeNull();
            property.GetTypeMapping().Converter.ShouldNotBeNull().ProviderClrType.ShouldBe(typeof(Guid));
        }
    }

    // Each mapping mode gets its own model: UseStronglyTypedIds() replaces a service, so EF builds a separate
    // internal service provider (and model cache) for the Convention mode.
    private ShopContext Create(Mapping mapping)
    {
        var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection);
        if (mapping == Mapping.Convention)
            options.UseStronglyTypedIds();
        return new ShopContext(options.Options, mapping);
    }

    public enum Mapping
    {
        Convention,
        PerType,
    }

    public sealed class Customer
    {
        public Id<Customer> Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class Order
    {
        public Id<Order> Id { get; set; }
        public Id<Customer> CustomerId { get; set; }
        public Id<Order>? ParentId { get; set; }
        public Id<ISalesRep> SalesRep { get; set; } // interface marker (skipped by Tarol's old assembly scan)
        public Id<Region> Region { get; set; }      // struct marker (also skipped by the old scan)
    }

    public interface ISalesRep;

    public readonly record struct Region;

    private sealed class ShopContext(DbContextOptions<ShopContext> options, Mapping mapping) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Order> Orders => Set<Order>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            if (mapping == Mapping.PerType)
            {
                configurationBuilder
                    .ConfigureStronglyTypedId<Customer>()
                    .ConfigureStronglyTypedId<Order>()
                    .ConfigureStronglyTypedId<ISalesRep>()
                    .ConfigureStronglyTypedId<Region>();
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Keys are created by the domain (Id<T>.New()), never by the database.
            modelBuilder.Entity<Customer>().Property(c => c.Id).ValueGeneratedNever();
            modelBuilder.Entity<Order>().Property(o => o.Id).ValueGeneratedNever();
        }
    }
}
