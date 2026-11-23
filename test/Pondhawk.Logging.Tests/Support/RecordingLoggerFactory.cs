// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Tests.Support;

/// <summary>
/// An <see cref="ILoggerFactory"/> that records the categories requested and hands out one shared
/// <see cref="CollectingLogger"/>, for asserting logger acquisition.
/// </summary>
internal sealed class RecordingLoggerFactory(LogLevel minimumLevel = LogLevel.Trace) : ILoggerFactory
{
    public List<string> Categories { get; } = [];

    public CollectingLogger Logger { get; } = new(minimumLevel);

    public ILogger CreateLogger(string categoryName)
    {
        Categories.Add(categoryName);
        return Logger;
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
