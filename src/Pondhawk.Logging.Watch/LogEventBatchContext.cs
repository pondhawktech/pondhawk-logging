// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Source-generated JSON serialization context for LogEvent and LogEventBatch.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(LogEvent))]
[JsonSerializable(typeof(LogEventBatch))]
public partial class LogEventBatchContext : JsonSerializerContext;
