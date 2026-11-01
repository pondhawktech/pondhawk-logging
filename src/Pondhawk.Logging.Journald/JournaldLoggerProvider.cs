// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// The ZLogger provider that writes to the journal. It is its own type so its floor can be a rule scoped to it,
/// configurable under the alias <c>Journald</c> — <c>Logging:Journald:LogLevel:Some.Category</c>, or
/// <c>Logging__Journald__LogLevel__Some.Category</c> from a systemd <c>EnvironmentFile</c>, which drops variable
/// names with a dot in them. A rule scoped to it also outranks Watch's switch filter, so a switch neither raises nor
/// lowers the journal. The host's provider-less category floors still apply (<see cref="CategoryFloors"/>).
/// </summary>
[ProviderAlias("Journald")]
public sealed class JournaldLoggerProvider : ZLoggerLogProcessorLoggerProvider, ILoggerProvider
{
    private readonly CategoryFloors _floors;

    internal JournaldLoggerProvider(JournaldLoggerProcessor processor, ZLoggerOptions options, CategoryFloors floors)
        : base(processor, options)
    {
        Processor = processor;
        _floors = floors;
    }

    internal JournaldLoggerProcessor Processor { get; }

    /// <summary>Creates the logger for <paramref name="categoryName"/>, held to the host's category floor.</summary>
    /// <param name="categoryName">The category of the logger.</param>
    /// <returns>The logger.</returns>
    // ZLogger's CreateLogger is not virtual; re-implementing ILoggerProvider routes the factory's calls here.
    public new ILogger CreateLogger(string categoryName) =>
        new CategoryFloorLogger(base.CreateLogger(categoryName), categoryName, _floors);

    /// <summary>Stops following reloaded filter rules, and disposes the processor.</summary>
    public new void Dispose()
    {
        _floors.Dispose();
        base.Dispose();
    }
}
