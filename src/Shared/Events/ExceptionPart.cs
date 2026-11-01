// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Events;

/// <summary>One exception of the chain: its type, message, and stack trace one frame per entry.</summary>
internal sealed record ExceptionPart(string? Type, string Message, IReadOnlyList<string> StackTrace);
