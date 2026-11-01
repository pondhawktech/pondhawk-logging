// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Journald;

/// <summary>Options for the journald sink.</summary>
public sealed class JournaldOptions
{
    /// <summary>
    /// The <c>SYSLOG_IDENTIFIER</c> on every event. By default the name of the process's executable, as journald
    /// names a service's stdout lines when its unit sets no <c>SyslogIdentifier=</c>. Set it to match a unit that does.
    /// </summary>
    public string? SyslogIdentifier { get; set; }
}
