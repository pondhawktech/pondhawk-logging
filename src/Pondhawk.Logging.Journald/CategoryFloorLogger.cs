// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// The provider's logger for one category, held to the host's category floor (<see cref="CategoryFloors"/>) as
/// well as to the rule MEL picked: below the floor it is not enabled, so nothing is formatted for it.
/// </summary>
internal sealed class CategoryFloorLogger(ILogger inner, string category, CategoryFloors floors) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => AboveFloor(logLevel) && inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (AboveFloor(logLevel))
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    private bool AboveFloor(LogLevel logLevel) => floors.Floor(category) is not { } floor || logLevel >= floor;
}
