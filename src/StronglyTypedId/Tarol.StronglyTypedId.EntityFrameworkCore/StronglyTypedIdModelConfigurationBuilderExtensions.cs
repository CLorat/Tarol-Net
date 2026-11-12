using Microsoft.EntityFrameworkCore;

namespace Tarol.StronglyTypedId.EntityFrameworkCore;

/// <summary>Per-type, reflection-free registration of strongly-typed id conversions.</summary>
public static class StronglyTypedIdModelConfigurationBuilderExtensions
{
    /// <summary>
    ///     Maps every <see cref="Id{T}" /> property of the model (nullable ones included) to a <see cref="Guid" /> column:
    ///     <code>
    ///     protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    ///         => builder.ConfigureStronglyTypedId&lt;User&gt;().ConfigureStronglyTypedId&lt;Order&gt;();
    ///     </code>
    ///     Use this instead of <see cref="StronglyTypedIdDbContextOptionsBuilderExtensions.UseStronglyTypedIds" /> when
    ///     the model must be built without run-time generic instantiation (for example with a compiled model).
    /// </summary>
    /// <typeparam name="T">The phantom entity type.</typeparam>
    /// <param name="builder">The model configuration builder.</param>
    /// <returns>The same builder.</returns>
    public static ModelConfigurationBuilder ConfigureStronglyTypedId<T>(this ModelConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Properties<Id<T>>().HaveConversion<IdValueConverter<T>>();
        return builder;
    }
}
