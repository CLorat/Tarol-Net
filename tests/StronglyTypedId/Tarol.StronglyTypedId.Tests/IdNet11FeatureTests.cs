using System.ComponentModel;
using System.Numerics;
using Microsoft.Extensions.Configuration;

namespace Tarol.StronglyTypedId.Tests;

/// <summary>Behaviour added in Tarol-Net (see docs/migration/stronglytypedid.md).</summary>
public class IdNet11FeatureTests
{
    private static readonly Guid KnownGuid = Guid.Parse("018f2b3c-1234-7abc-9def-0123456789ab");

    [Fact]
    public void New_With_TimeProvider_Embeds_The_Providers_Time()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var early = Id<UserMarker>.New(clock);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var late = Id<UserMarker>.New(clock);

        early.Value.Version.ShouldBe(7);
        early.ShouldBeLessThan(late);
        early.ShouldBeLessThan(Id<UserMarker>.New(clock.Now.AddDays(1)));
    }

    [Fact]
    public void Generic_Math_Comparison_Constraints_Accept_Ids()
    {
        var a = Id<UserMarker>.New(DateTimeOffset.UnixEpoch);
        var b = Id<UserMarker>.New(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Max(a, b).ShouldBe(b);
        Max(b, a).ShouldBe(b);

        static TId Max<TId>(TId x, TId y) where TId : IComparisonOperators<TId, TId, bool> => x > y ? x : y;
    }

    [Fact]
    public void Id_Closed_Only_Through_A_Generic_Base_Is_Registered_By_The_Generator()
    {
        // InheritedMarker never spells Id<InheritedMarker>: it inherits `Id<TSelf> Id` from EntityBase<TSelf>.
        // Before Tarol-Net the generator missed exactly this shape (aggregate ids, review finding S-2).
        var idType = typeof(Id<>).MakeGenericType(typeof(InheritedMarker));

        IdConverterRegistry.IsRegistered(idType).ShouldBeTrue();
        TypeDescriptor.GetConverter(idType).GetType().ShouldBe(typeof(IdTypeConverter<InheritedMarker>));
    }

    [Fact]
    public void Reflection_Configuration_Binder_Binds_Ids_Through_The_Registered_TypeConverter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Owner"] = KnownGuid.ToString() })
            .Build();

        var settings = configuration.Get<OwnerSettings>();

        settings.ShouldNotBeNull();
        settings.Owner.Value.ShouldBe(KnownGuid);
    }

    public sealed class OwnerSettings
    {
        public Id<UserMarker> Owner { get; set; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;

        public void Advance(TimeSpan by) => Now += by;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public abstract class EntityBase<TSelf>
{
    public Id<TSelf> Id { get; init; }
}

public sealed class InheritedMarker : EntityBase<InheritedMarker>;
