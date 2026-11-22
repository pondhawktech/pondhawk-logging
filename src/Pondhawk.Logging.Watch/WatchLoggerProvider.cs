// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using ZLogger;
using ZLogger.Providers;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The ZLogger provider that delivers to Watch. It exists as its own type so the switch filter can tell Watch from
/// the other providers: retro logging lets a marked unit of work's Debug events through to Watch only, since every
/// ZLogger processor otherwise shares one provider type.
/// </summary>
public sealed class WatchLoggerProvider : ZLoggerLogProcessorLoggerProvider
{
    /// <summary>Creates the provider over a Watch processor.</summary>
    /// <param name="processor">The processor that delivers to Watch.</param>
    /// <param name="options">The ZLogger options the provider's loggers use.</param>
    public WatchLoggerProvider(WatchLoggerProcessor processor, ZLoggerOptions options)
        : base(processor, options)
    {
    }
}
