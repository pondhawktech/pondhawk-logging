// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Utilities;
using Serilog;
using Serilog.Core;

namespace Pondhawk.Logging;

/// <summary>
/// Default <see cref="ILoggerSource"/> backed by canonical Serilog: each logger is the root logger
/// with its <c>SourceContext</c> set to the requested category. Use this when no switch-aware
/// provider (e.g. Watch) is configured — the logging API works identically, and
/// <c>IsEnabled</c>-based guards fall back to the configured Serilog minimum level.
/// </summary>
public sealed class SerilogLoggerSource : ILoggerSource
{
    private readonly ILogger _root;

    /// <summary>Initializes a new instance deriving category loggers from the given root logger.</summary>
    /// <param name="root">The root Serilog logger.</param>
    public SerilogLoggerSource(ILogger root)
    {
        _root = root;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string category) => _root.ForContext(Constants.SourceContextPropertyName, category);

    /// <inheritdoc />
    public ILogger CreateLogger(Type source) => CreateLogger(source.GetConciseFullName());

    /// <inheritdoc />
    public ILogger CreateLogger<T>() => CreateLogger(typeof(T).GetConciseFullName());
}
