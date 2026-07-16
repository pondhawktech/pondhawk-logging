// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging;

/// <summary>
/// Specifies the type of payload content in a log event.
/// Used by UI viewers to provide appropriate syntax highlighting and formatting.
/// </summary>
public enum PayloadType
{
    /// <summary>No payload content.</summary>
    None = 0,

    /// <summary>JSON-formatted content. Displayed with JavaScript/JSON syntax highlighting.</summary>
    Json = 1,

    /// <summary>SQL query content. Displayed with SQL syntax highlighting.</summary>
    Sql = 2,

    /// <summary>XML content. Displayed with XML syntax highlighting.</summary>
    Xml = 3,

    /// <summary>Plain text content. Displayed without syntax highlighting.</summary>
    Text = 4,

    /// <summary>YAML content. Displayed with YAML syntax highlighting.</summary>
    Yaml = 5
}
