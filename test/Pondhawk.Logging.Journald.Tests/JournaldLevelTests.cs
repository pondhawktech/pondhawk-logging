// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Drawing;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Journald.Tests;

/// <summary>
/// The journal's levels: its floor, configuration under the <c>Journald</c> alias, the host's provider-less
/// category floors, and Watch beside it. With no journal every event goes to stdout, which is what these read.
/// </summary>
public sealed class JournaldLevelTests : IDisposable
{
    private readonly ConcurrentQueue<string> _stdout = new();
    private ServiceProvider _services;

    public void Dispose() => _services?.Dispose();

    private ILoggerFactory Build(LogLevel floor = LogLevel.Information, IConfiguration configuration = null, Action<ILoggingBuilder> more = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (configuration is not null)
                builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddJournald(floor, configure: null, journal: () => null, _stdout.Enqueue);
            more?.Invoke(builder);
        });

        _services = services.BuildServiceProvider();
        return _services.GetRequiredService<ILoggerFactory>();
    }

    private static IConfigurationRoot Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)))
            .Build();

    private bool Written(string line) => _stdout.Contains(line);

    [Fact]
    public void TheFloor_IsInformation_ByDefault()
    {
        var logger = Build().CreateLogger("App");

        logger.LogDebug("debug");
        logger.LogInformation("info");

        _stdout.ShouldBe(["<6>App: info"]);
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
    }

    [Fact]
    public void ALowerFloor_LetsDebugThrough_ButNotTrace()
    {
        var logger = Build(LogLevel.Debug).CreateLogger("App");

        logger.LogTrace("trace");
        logger.LogDebug("debug");

        _stdout.ShouldBe(["<7>App: debug"]);
    }

    [Fact]
    public void ACategoryUnderTheAlias_RaisesThatCategory()
    {
        var factory = Build(configuration: Configuration(("Logging:Journald:LogLevel:Noisy", "Warning")));

        factory.CreateLogger("Noisy.Component").LogInformation("noise");
        factory.CreateLogger("Quiet").LogInformation("kept");

        _stdout.ShouldBe(["<6>Quiet: kept"]);
    }

    [Fact]
    public void ACategoryUnderTheAlias_CanLowerThatCategory_BelowTheFloor()
    {
        var factory = Build(configuration: Configuration(("Logging:Journald:LogLevel:Chatty", "Debug")));

        factory.CreateLogger("Chatty").LogDebug("detail");
        factory.CreateLogger("Other").LogDebug("dropped");

        _stdout.ShouldBe(["<7>Chatty: detail"]);
    }

    [Fact]
    public void DefaultUnderTheAlias_OverridesTheFloor()
    {
        var logger = Build(configuration: Configuration(("Logging:Journald:LogLevel:Default", "Warning"))).CreateLogger("App");

        logger.LogInformation("info");
        logger.LogWarning("warn");

        _stdout.ShouldBe(["<4>App: warn"]);
    }

    [Fact]
    public void AnEnvironmentVariable_SetsACategoryUnderTheAlias()
    {
        // As a systemd EnvironmentFile would set it: no dots anywhere in the name.
        var category = "JdEnv" + Guid.NewGuid().ToString("N")[..8];
        var variable = $"Logging__Journald__LogLevel__{category}";
        Environment.SetEnvironmentVariable(variable, "Warning");
        try
        {
            var factory = Build(configuration: new ConfigurationBuilder().AddEnvironmentVariables().Build());

            factory.CreateLogger(category).LogInformation("dropped");
            factory.CreateLogger(category).LogWarning("kept");

            _stdout.ShouldBe([$"<4>{category}: kept"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void AProviderlessCategoryFilter_HoldsTheJournal()
    {
        var factory = Build(more: builder => builder.AddFilter("Gateway.Credentials", LogLevel.Warning));

        factory.CreateLogger("Gateway.Credentials.Token").LogInformation("secret");
        factory.CreateLogger("Gateway.Credentials.Token").LogWarning("refused");
        factory.CreateLogger("Gateway.Routing").LogInformation("routed");

        _stdout.ShouldBe(["<4>Gateway.Credentials.Token: refused", "<6>Gateway.Routing: routed"]);
        factory.CreateLogger("Gateway.Credentials.Token").IsEnabled(LogLevel.Information).ShouldBeFalse();
    }

    [Fact]
    public void AProviderlessRuleAddedToLoggerFilterOptions_HoldsTheJournal()
    {
        var factory = Build(more: builder => builder.Services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(providerName: null, "Gateway", LogLevel.Error, filter: null))));

        factory.CreateLogger("Gateway.Auth").LogWarning("dropped");
        factory.CreateLogger("Gateway.Auth").LogError("kept");

        _stdout.ShouldBe(["<3>Gateway.Auth: kept"]);
    }

    [Fact]
    public void AProviderlessCategoryFromConfiguration_HoldsTheJournal_EvenWhereTheAliasLowersIt()
    {
        var factory = Build(configuration: Configuration(
            ("Logging:LogLevel:Gateway.Credentials", "Warning"),
            ("Logging:Journald:LogLevel:Gateway", "Debug")));

        factory.CreateLogger("Gateway.Credentials").LogInformation("secret");
        factory.CreateLogger("Gateway.Routing").LogDebug("routed");

        _stdout.ShouldBe(["<7>Gateway.Routing: routed"]);
    }

    [Fact]
    public void TheMostSpecificProviderlessCategory_Wins()
    {
        var factory = Build(more: builder =>
        {
            builder.AddFilter("Gateway", LogLevel.Error);
            builder.AddFilter("Gateway.Health", LogLevel.Information);
        });

        factory.CreateLogger("Gateway.Health").LogInformation("kept");
        factory.CreateLogger("Gateway.Auth").LogWarning("dropped");

        _stdout.ShouldBe(["<6>Gateway.Health: kept"]);
    }

    [Fact]
    public void AProviderlessCategory_MatchesAsMelDoes_WithAWildcard_AndIgnoringCase()
    {
        var factory = Build(more: builder => builder.AddFilter("gateway.*.credentials", LogLevel.Warning));

        factory.CreateLogger("Gateway.Oidc.Credentials").LogInformation("dropped");
        factory.CreateLogger("Gateway.Oidc.Tokens").LogInformation("kept");

        _stdout.ShouldBe(["<6>Gateway.Oidc.Tokens: kept"]);
    }

    [Fact]
    public void AProviderlessDefault_DoesNotHoldTheJournal()
    {
        var factory = Build(configuration: Configuration(("Logging:LogLevel:Default", "Error")));

        factory.CreateLogger("App").LogInformation("kept");

        _stdout.ShouldBe(["<6>App: kept"]);
    }

    [Fact]
    public void AProviderlessFloor_ChangedByAReload_Applies()
    {
        var configuration = Configuration(("Logging:LogLevel:Gateway", "Warning"));
        var logger = Build(configuration: configuration).CreateLogger("Gateway.Auth");

        logger.LogInformation("before");
        configuration["Logging:LogLevel:Gateway"] = "Information";
        configuration.Reload();
        logger.LogInformation("after");

        _stdout.ShouldBe(["<6>Gateway.Auth: after"]);
    }

    [Fact]
    public void AWatchSwitch_NeitherRaisesNorLowersTheJournal_AndTheJournalDoesNotHoldItDown()
    {
        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);
        switches.WhenMatched("Orders", LogLevel.Error, Color.White);

        using var http = new HttpClient(new Accepting()) { BaseAddress = new Uri("http://localhost/") };
        var factory = Build(more: builder =>
            builder.AddWatch(http, switches, new WatchDestination("http://localhost/D"), new WatchOptions(), ownsDependencies: false));
        var logger = factory.CreateLogger("Orders.Api");

        // Switch raised to Error: the journal still takes Information.
        logger.LogInformation("raised");
        Written("<6>Orders.Api: raised").ShouldBeTrue();
        logger.IsEnabled(LogLevel.Information).ShouldBeTrue();

        // Switch lowered to Trace: Watch is open for Debug, the journal still is not.
        switches.WhenMatched("Orders", LogLevel.Trace, Color.White);
        logger.IsEnabled(LogLevel.Debug).ShouldBeTrue();
        logger.LogDebug("lowered");
        Written("<7>Orders.Api: lowered").ShouldBeFalse();

        // Back to Error: nothing wants Debug, so the call site skips it.
        switches.WhenMatched("Orders", LogLevel.Error, Color.White);
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
    }

    [Fact]
    public void TheProviderIsFoundByItsAlias()
    {
        var factory = Build(configuration: Configuration(("Logging:Journald:LogLevel:Default", "None")));

        factory.CreateLogger("App").LogCritical("dropped");

        _stdout.ShouldBeEmpty();
    }

    private sealed class Accepting : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
