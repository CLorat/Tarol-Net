using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Security;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-43 (VA-3): an endpoint nobody may call stops the host in Development and is warning 1003 elsewhere.</summary>
public sealed class GrantCheckTests
{
    private sealed class Nobody(bool? answer) : IChangeAuthorization
    {
        public ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T>
            => ValueTask.FromResult<Result>(Error.Forbidden("Auth.Forbidden"));

        public bool? MayBeGranted(Type intent, ReadScope? scope) => answer;
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = "/";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Captured : ILoggerProvider, ILogger
    {
        public List<(EventId Id, string Text)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((eventId, formatter(state, exception)));

        public void Dispose()
        {
        }
    }

    private static ServiceProvider Services(string environment, bool? answer, Captured? log = null)
    {
        var services = new ServiceCollection().AddSingleton<IChangeAuthorization>(new Nobody(answer)).AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddLogging(b => b.AddProvider(log ?? new Captured()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void In_Development_an_endpoint_nobody_may_call_stops_the_host_and_names_the_grant()
    {
        using var services = Services(Environments.Development, false);

        var change = Should.Throw<InvalidOperationException>(() => ChangeEndpoints.Granted(services, typeof(CloseAuction), null, "change CloseAuction"));
        var view = Should.Throw<InvalidOperationException>(() => ChangeEndpoints.Granted(services, typeof(Read<Auction>), new ReadScope(typeof(AuctionSummary), true), "view AuctionSummary"));
        var list = Should.Throw<InvalidOperationException>(() => ChangeEndpoints.Granted(services, typeof(Read<Auction>), new ReadScope(typeof(AuctionSearch), false), "list AuctionSearch"));

        change.Message.ShouldStartWith("The change CloseAuction needs CloseAuction; no role and no row rule grants it");
        view.Message.ShouldContain("needs MayRead<Auction, AuctionSummary>()");
        list.Message.ShouldContain("needs Read<Auction> (or MayRead<Auction, AuctionSearch>())");
    }

    [Fact]
    public void Elsewhere_it_is_warning_1003_and_an_authorization_that_cannot_tell_is_trusted()
    {
        var log = new Captured();
        using var production = Services(Environments.Production, false, log);
        using var unknown = Services(Environments.Development, null);
        using var granted = Services(Environments.Development, true);

        ChangeEndpoints.Granted(production, typeof(CloseAuction), null, "change CloseAuction").ShouldBeFalse();
        ChangeEndpoints.Granted(unknown, typeof(CloseAuction), null, "change CloseAuction").ShouldBeTrue();
        ChangeEndpoints.Granted(granted, typeof(CloseAuction), null, "change CloseAuction").ShouldBeTrue();

        log.Entries.Single(e => e.Id.Id == 1003).Text.ShouldContain("The change CloseAuction needs CloseAuction");
    }
}
