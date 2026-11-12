using System.Collections.Concurrent;
using System.ComponentModel;

namespace Tarol.StronglyTypedId;

/// <summary>
///     Registers the closed <see cref="IdTypeConverter{T}" /> for an <see cref="Id{T}" /> with
///     <see cref="TypeDescriptor" />.
///     <para>
///         <see cref="TypeDescriptor" /> is what <c>Microsoft.Extensions.Configuration</c> binding (reflection
///         and source-generated binder), classic MVC binders and designers use. Without a registered converter
///         the configuration binder silently leaves an <see cref="Id{T}" /> property empty.
///     </para>
///     <para>
///         The bundled source generator emits one <c>[ModuleInitializer]</c> per consuming assembly that calls
///         <see cref="Register{T}" /> for every closed <c>Id&lt;X&gt;</c> the assembly uses. Call it yourself only
///         for markers the generator cannot see (assemblies compiled without it, or ids created only through
///         reflection).
///     </para>
///     <para>
///         System.Text.Json needs no registration: <see cref="Id{T}" /> carries the open generic
///         <see cref="IdJsonConverter{T}" /> attribute.
///     </para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)] // infrastructure: called by generated code
public static class IdConverterRegistry
{
    private static readonly ConcurrentDictionary<Type, byte> Registrations = new();

    /// <summary>
    ///     Registers the <see cref="TypeConverter" /> for <see cref="Id{T}" />. Idempotent and thread-safe.
    /// </summary>
    /// <typeparam name="T">The phantom entity type.</typeparam>
    public static void Register<T>()
    {
        // AddAttributes stacks a TypeDescriptionProvider per call: make repeated registration a no-op.
        if (!Registrations.TryAdd(typeof(Id<T>), 0))
            return;

        // Required when the app enables the 'System.ComponentModel.TypeDescriptor.RequireRegisteredTypes'
        // feature switch (the AOT-friendly TypeDescriptor mode); harmless otherwise.
        TypeDescriptor.RegisterType<Id<T>>();

        TypeDescriptor.AddAttributes(typeof(Id<T>), new TypeConverterAttribute(typeof(IdTypeConverter<T>)));

        // Root the converter's constructor for the trimmer: TypeDescriptor instantiates it later through the attribute.
        _ = new IdTypeConverter<T>();
    }

    /// <summary>Whether <see cref="Register{T}" /> ran for <paramref name="idType" /> (tests and diagnostics).</summary>
    internal static bool IsRegistered(Type idType) => Registrations.ContainsKey(idType);
}
