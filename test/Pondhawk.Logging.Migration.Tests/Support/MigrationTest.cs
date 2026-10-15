// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Pondhawk.Logging.Migration.Tests.Support;

/// <summary>
/// A test of the analyzer and its fixes against a source file and the Fabrica.Watch stub. The fixed code is not
/// required to compile: a single rule's fix leaves the rest of the migration for the others. Rules other than
/// the ones named are switched off, so each test sees only what it is about.
/// </summary>
internal sealed class MigrationTest : CSharpCodeFixTest<FabricaWatchAnalyzer, FabricaWatchCodeFixProvider, DefaultVerifier>
{
    private static readonly string[] AllRules = ["PHLM001", "PHLM005", "PHLM012", "PHLM013", "PHLM014", "PHLM015", "PHLM016"];

    private static readonly string Stub = ReadStub();

    public MigrationTest(string source, string fixedSource, params string[] rules)
    {
        ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
        TestState.AdditionalReferences.Add(typeof(Microsoft.Extensions.Logging.ILogger).Assembly);
        TestState.AdditionalReferences.Add(typeof(LoggingExtensions).Assembly);

        TestState.Sources.Add(("/0/Test0.cs", source));
        TestState.Sources.Add(("/0/FabricaWatch.cs", Stub));
        FixedState.Sources.Add(("/0/Test0.cs", fixedSource));
        FixedState.Sources.Add(("/0/FabricaWatch.cs", Stub));

        CompilerDiagnostics = CompilerDiagnostics.None;

        foreach (var rule in AllRules.Except(rules))
            DisabledDiagnostics.Add(rule);
    }

    public static Task RunAsync(string source, string fixedSource, params string[] rules) =>
        new MigrationTest(source, fixedSource, rules).RunAsync();

    /// <summary>A test that expects no diagnostic of the given rules: the code is left alone.</summary>
    public static Task NoneAsync(string source, params string[] rules) =>
        new MigrationTest(source, source, rules).RunAsync();

    private static string ReadStub()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FabricaWatch.cs")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
