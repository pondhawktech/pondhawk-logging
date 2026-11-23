// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Watch;

/// <summary>What the switch filter needs of a category's switch: its level, and whether it trails Debug for retro logging.</summary>
internal readonly record struct SwitchGate(LogLevel Level, bool Retro);
