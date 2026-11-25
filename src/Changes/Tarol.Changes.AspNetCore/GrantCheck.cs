using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tarol.Changes.AspNetCore;

public sealed partial class ChangeEndpoints
{
    /// <summary>
    /// Reports an endpoint nobody may call (TN-43, VA-3): the generated <c>MapChanges</c> asks once per authorized entry whether
    /// <see cref="IChangeAuthorization.MayBeGranted"/> could grant its intent. In Development a <see langword="false"/> stops the
    /// host (<see cref="InvalidOperationException"/>); otherwise it is warning 1003. An authorization that cannot tell answers nothing.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="intent">The change input type, or <c>Read&lt;T&gt;</c>.</param>
    /// <param name="scope">The view or query record of a read; <see langword="null"/> for a change.</param>
    /// <param name="entry">The entry as the message names it, e.g. <c>list MyNotifications</c>.</param>
    /// <returns>Whether it may be granted (<see langword="true"/> when unknown).</returns>
    /// <exception cref="InvalidOperationException">In Development: nothing grants the entry.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool Granted(IServiceProvider services, Type intent, ReadScope? scope, string entry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(intent);
        if (services.GetService<IChangeAuthorization>()?.MayBeGranted(intent, scope) is not false)
            return true;
        var needs = scope is { } read
            ? read.Restricted ? $"MayRead<{Aggregate(intent)}, {read.View.Name}>()" : $"{Name(intent)} (or MayRead<{Aggregate(intent)}, {read.View.Name}>())"
            : Name(intent);
        var message = $"The {entry} needs {needs}; no role and no row rule grants it, so every call is refused with 403. Grant it in the role map (AddSecurity) or add a row rule.";
        if (services.GetService<IHostEnvironment>()?.IsDevelopment() == true)
            throw new InvalidOperationException(message + " (This check stops the host in Development only; elsewhere it is warning 1003.)");
        var log = services.GetService<ILoggerFactory>()?.CreateLogger("Tarol.Security.Grants");
        if (log is not null)
            LogUngranted(log, message);
        return false;
    }

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogUngranted(ILogger logger, string message);

    private static string Name(Type type)
        => type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>" : type.Name;

    private static string Aggregate(Type read) => read.IsGenericType ? Name(read.GetGenericArguments()[0]) : read.Name;
}
