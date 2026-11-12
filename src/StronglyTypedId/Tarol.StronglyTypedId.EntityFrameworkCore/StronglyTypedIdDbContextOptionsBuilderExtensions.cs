using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tarol.StronglyTypedId.EntityFrameworkCore;

/// <summary>Registers strongly-typed id support on a <see cref="DbContextOptionsBuilder" />.</summary>
public static class StronglyTypedIdDbContextOptionsBuilderExtensions
{
    private const string Reason =
        "Closes IdValueConverter<> over each Id<T> property type at model-building time. " +
        "Use ModelConfigurationBuilder.ConfigureStronglyTypedId<T>() for a reflection-free model.";

    /// <summary>
    ///     Maps every <see cref="Id{T}" /> and <c>Id&lt;T&gt;?</c> property in the model to a <see cref="Guid" /> column:
    ///     <code>
    ///     services.AddDbContext&lt;ShopContext&gt;(o => o.UseNpgsql(connectionString).UseStronglyTypedIds());
    ///     </code>
    ///     Replaces EF Core's <see cref="IValueConverterSelector" /> service with
    ///     <see cref="StronglyTypedIdValueConverterSelector" />.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same builder.</returns>
    [RequiresDynamicCode(Reason)]
    [RequiresUnreferencedCode(Reason)]
    public static DbContextOptionsBuilder UseStronglyTypedIds(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.ReplaceService<IValueConverterSelector, StronglyTypedIdValueConverterSelector>();
    }

    /// <inheritdoc cref="UseStronglyTypedIds(DbContextOptionsBuilder)" />
    /// <typeparam name="TContext">The context type.</typeparam>
    [RequiresDynamicCode(Reason)]
    [RequiresUnreferencedCode(Reason)]
    public static DbContextOptionsBuilder<TContext> UseStronglyTypedIds<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.ReplaceService<IValueConverterSelector, StronglyTypedIdValueConverterSelector>();
    }
}
