// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Migration;

/// <summary>The shapes of a Fabrica.Watch level call, each with its own rule.</summary>
internal enum LevelShape
{
    /// <summary>A plain message, or a template called with no arguments (PHLM001).</summary>
    Simple,

    /// <summary>A positional template with arguments (PHLM002).</summary>
    Template,

    /// <summary>An interpolated or concatenated message (PHLM003).</summary>
    Parts,

    /// <summary>A <c>Func&lt;string&gt;</c> evaluated only when the level is on (PHLM004).</summary>
    Lazy,
}
