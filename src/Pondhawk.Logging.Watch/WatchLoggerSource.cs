// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using CommunityToolkit.Diagnostics;
using Pondhawk.Logging.Utilities;
using Serilog;
using Serilog.Core;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// A switch-aware <see cref="ILoggerSource"/>: the loggers it creates consult the live Watch switch
/// table via <see cref="WatchLogger"/>, so the logging API skips serialization for switch-dropped
/// categories. Construct it with the <em>same</em> <see cref="SwitchSource"/> the Watch sink uses so
/// the call-site guard and the sink filter read one source of truth.
/// </summary>
public sealed class WatchLoggerSource : ILoggerSource
{
    private readonly ILogger _root;
    private readonly SwitchSource _switches;

    /// <summary>Initializes a new instance sharing a switch source with the Watch sink.</summary>
    /// <param name="root">The root Serilog logger.</param>
    /// <param name="switches">The switch source — the same instance the sink was wired with.</param>
    public WatchLoggerSource(ILogger root, SwitchSource switches)
    {
        Guard.IsNotNull(root);
        Guard.IsNotNull(switches);

        _root = root;
        _switches = switches;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string category)
        => new WatchLogger(_root.ForContext(Constants.SourceContextPropertyName, category), category, _switches);

    /// <inheritdoc />
    public ILogger CreateLogger(Type source) => CreateLogger(source.GetConciseFullName());

    /// <inheritdoc />
    public ILogger CreateLogger<T>() => CreateLogger(typeof(T).GetConciseFullName());
}
