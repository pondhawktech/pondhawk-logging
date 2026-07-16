/*
The MIT License (MIT)

Copyright (c) 2024 Pond Hawk Technologies Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

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
