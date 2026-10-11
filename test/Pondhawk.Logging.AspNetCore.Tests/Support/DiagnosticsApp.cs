// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.AspNetCore.Tests.Support;

/// <summary>A test server running a pipeline the test builds, logging to a <see cref="CapturingLoggerProvider"/>.</summary>
internal sealed class DiagnosticsApp : IAsyncDisposable
{
    private readonly IHost _host;

    private DiagnosticsApp(IHost host, CapturingLoggerProvider log)
    {
        _host = host;
        Log = log;
        Client = host.GetTestClient();
    }

    public CapturingLoggerProvider Log { get; }

    public HttpClient Client { get; }

    public static async Task<DiagnosticsApp> StartAsync(
        Action<IApplicationBuilder> pipeline,
        Action<DiagnosticsOptions> configure = null,
        CapturingLoggerProvider log = null)
    {
        log ??= new CapturingLoggerProvider();

        var host = await new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log))
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting().AddPondhawkDiagnostics(configure))
                .Configure(pipeline))
            .StartAsync();

        return new DiagnosticsApp(host, log);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
