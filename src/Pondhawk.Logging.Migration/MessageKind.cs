// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Migration;

/// <summary>What is written for a level call's message.</summary>
internal enum MessageKind
{
    /// <summary>No message, or a null one: the exception's message stands in.</summary>
    None,

    /// <summary>A constant string: it becomes the template as it is.</summary>
    Constant,

    /// <summary>A string computed at run time: it becomes the value of a <c>{Message}</c> hole.</summary>
    Text,

    /// <summary>Anything else: it becomes the value of a <c>{Value}</c> hole.</summary>
    Value,
}
