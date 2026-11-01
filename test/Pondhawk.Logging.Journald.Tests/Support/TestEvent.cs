// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

extern alias CloudWatch;

using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Events;

namespace Pondhawk.Logging.Journald.Tests.Support;

/// <summary>
/// One event's values, built as either provider's captured event: each package compiles its own copy of the
/// shared event types.
/// </summary>
public sealed record TestEvent
{
    public LogLevel Level { get; init; } = LogLevel.Error;
    public string Category { get; init; } = "My.Category";
    public string Title { get; init; } = "it broke";
    public string CorrelationId { get; init; } = "corr-1";
    public string Subject { get; init; }
    public string Tenant { get; init; }
    public int Nesting { get; init; }
    public int PayloadType { get; init; }
    public string Payload { get; init; }
    public string Context { get; init; }
    public Exception Exception { get; init; }

    internal CapturedEvent Journald() => new()
    {
        Level = Level, Category = Category, Title = Title, CorrelationId = CorrelationId, Subject = Subject, Tenant = Tenant,
        Occurred = DateTime.UnixEpoch, Exception = Exception, Nesting = Nesting, PayloadType = PayloadType, Payload = Payload, ErrorContext = Context,
    };

    internal CloudWatch::Pondhawk.Logging.Events.CapturedEvent CloudWatch() => new()
    {
        Level = Level, Category = Category, Title = Title, CorrelationId = CorrelationId, Subject = Subject, Tenant = Tenant,
        Occurred = DateTime.UnixEpoch, Exception = Exception, Nesting = Nesting, PayloadType = PayloadType, Payload = Payload, ErrorContext = Context,
    };

    public static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
