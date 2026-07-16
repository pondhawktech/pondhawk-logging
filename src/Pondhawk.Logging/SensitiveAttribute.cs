// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging;

/// <summary>
/// Marks a property or field as containing sensitive data that should not be logged.
/// </summary>
/// <remarks>
/// <para>
/// When an object is serialized via <c>LogObject()</c>, properties marked with
/// this attribute will have their values replaced with "Sensitive - HasValue: true/false"
/// instead of the actual value.
/// </para>
/// <para>
/// Use this attribute on properties containing passwords, API keys, tokens,
/// personal identifiable information (PII), or any other sensitive data.
/// </para>
/// <example>
/// <code>
/// public class UserCredentials
/// {
///     public string Username { get; set; }
///
///     [Sensitive]
///     public string Password { get; set; }
///
///     [Sensitive]
///     public string ApiKey { get; set; }
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class SensitiveAttribute : Attribute;
