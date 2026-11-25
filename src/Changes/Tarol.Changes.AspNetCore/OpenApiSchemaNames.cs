using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tarol.Kernel;
using Tarol.Options;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// The schema names of an OpenAPI document (TN-22, Tatalu FB-INT-1): the generated <c>AddTarolEndpoints</c> wraps the
/// options' <c>CreateSchemaReferenceId</c> once per document with <see cref="Name"/>, so that two different types with the same
/// name fail instead of overwriting each other's schema, <see cref="OpenApiSchemaNameAttribute"/> pins a name, and
/// <c>Option&lt;T&gt;</c> is no component of its own (it is written in place as <c>T</c> or <c>null</c>).
/// </summary>
/// <remarks>The package does not reference the OpenAPI packages; the options are passed as <see cref="object"/>.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class OpenApiSchemaNames
{
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, Type>> Documents = new();

    /// <summary>Whether this is the first call for <paramref name="openApiOptions"/> (the caller then wraps its naming).</summary>
    /// <param name="openApiOptions">A document's <c>OpenApiOptions</c>.</param>
    /// <returns><see langword="true"/> once per options instance.</returns>
    public static bool Claim(object openApiOptions)
    {
        ArgumentNullException.ThrowIfNull(openApiOptions);
        var claimed = false;
        Documents.GetValue(openApiOptions, _ =>
        {
            claimed = true;
            return new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);
        });
        return claimed;
    }

    /// <summary>The schema name of <paramref name="type"/>, from the name the options' naming proposed.</summary>
    /// <param name="openApiOptions">The document's <c>OpenApiOptions</c>.</param>
    /// <param name="type">The type.</param>
    /// <param name="proposed">The name of the default naming; <see langword="null"/> for a schema in place.</param>
    /// <returns>The schema name, or <see langword="null"/> for a schema in place (also for every <c>Option&lt;T&gt;</c>).</returns>
    /// <exception cref="InvalidOperationException">Another type of the document has the same schema name.</exception>
    public static string? Name(object openApiOptions, Type type, string? proposed)
    {
        ArgumentNullException.ThrowIfNull(openApiOptions);
        ArgumentNullException.ThrowIfNull(type);
        if (proposed is null || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>)))
            return null;
        var name = Pinned(type) ?? (type.IsGenericType && type.GetGenericArguments().Any(HasPinnedName) ? Generic(type) : proposed);
        var names = Documents.GetValue(openApiOptions, static _ => new ConcurrentDictionary<string, Type>(StringComparer.Ordinal));
        var owner = names.GetOrAdd(name, type);
        if (owner != type)
        {
            throw new InvalidOperationException(
                $"The OpenAPI schema name '{name}' belongs to two types: '{owner.FullName}' and '{type.FullName}'. One schema would overwrite the other "
                + $"and clients would check the wrong rules. Rename one, or give it [OpenApiSchemaName(\"…\")] (Tarol.Kernel).");
        }

        return name;
    }

    private static string? Pinned(Type type) => type.GetCustomAttribute<OpenApiSchemaNameAttribute>(inherit: false)?.Name;

    private static bool HasPinnedName(Type type)
        => Pinned(type) is not null || (type.IsGenericType && type.GetGenericArguments().Any(HasPinnedName));

    /// <summary>The name of a generic type as ASP.NET writes it (<c>PageOfChildLabel</c>), with the pinned names of its arguments.</summary>
    private static string Generic(Type type)
    {
        if (!type.IsGenericType)
            return Pinned(type) ?? type.Name;
        var definition = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return definition + "Of" + string.Join("And", type.GetGenericArguments().Select(Generic));
    }
}
