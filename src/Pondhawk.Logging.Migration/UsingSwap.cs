// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Replaces a file's Fabrica.Watch usings with <c>using Microsoft.Extensions.Logging;</c> and
/// <c>using Pondhawk.Logging;</c> — each only when the file uses something from it — and marks the
/// fully qualified names the rewrites wrote for reduction.
/// </summary>
internal static class UsingSwap
{
    private const string Logging = "Microsoft.Extensions.Logging";
    private const string Pondhawk = "Pondhawk.Logging";

    // Names that, once Fabrica.Watch is gone, can only come from one of the two.
    private static readonly HashSet<string> LoggingNames = new(StringComparer.Ordinal)
    {
        "ILogger", "ILoggerFactory", "LogLevel", "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError",
        "LogCritical", "IsEnabled", "CreateLogger", "BeginScope",
    };

    private static readonly HashSet<string> PondhawkNames = new(StringComparer.Ordinal)
    {
        "EnterMethod", "GetLogger", "Inspect", "LogObject", "LogJson", "LogSql", "LogXml", "LogYaml", "LogText",
        "ErrorWithContext", "WarningWithContext", "LogWithContext", "LoggingFactoryLocator", "Sensitive", "SensitiveAttribute", "PayloadType", "MethodLogger",
    };

    /// <summary>
    /// Removes the file's Fabrica.Watch usings when <paramref name="remove"/> is set; then, when no Fabrica.Watch
    /// using is left in scope, adds the two the file needs.
    /// </summary>
    public static SyntaxNode Apply(SyntaxNode root, bool remove, bool globalFabricaUsing)
    {
        var fabrica = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(FabricaWatch.IsFabricaUsing).ToList();
        if (globalFabricaUsing || (fabrica.Count > 0 && !remove))
            return root;

        var needed = Needed(root);
        var anchor = fabrica.FirstOrDefault();
        var newLine = root.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).ToFullString();
        if (newLine.Length == 0)
            newLine = "\n";

        var added = needed.Select(ns => Using(ns, anchor, newLine)).ToList();

        if (anchor is null)
            return added.Count == 0 ? root : Insert((CompilationUnitSyntax)root, added, newLine);

        var containers = fabrica.Select(u => u.Parent!).Distinct().ToList();
        return root.ReplaceNodes(containers, (original, current) => Replace(current, original == anchor.Parent ? added : []));
    }

    /// <summary>
    /// Marks every name written fully qualified into Microsoft.Extensions.Logging or Pondhawk.Logging — by a
    /// rewrite now, or by one run earlier — so the simplifier can shorten it once the usings allow.
    /// </summary>
    public static SyntaxNode MarkQualifiedNames(SyntaxNode root)
    {
        var marked = root.DescendantNodes()
            .Where(IsStartOfDottedName)
            .Where(n => n.FirstAncestorOrSelf<UsingDirectiveSyntax>() is null)
            .Select(Outermost)
            .Where(n => StartsWithTarget(n.ToString()))
            .ToList();

        return root.ReplaceNodes(marked, (_, current) => current.WithAdditionalAnnotations(Simplifier.Annotation));
    }

    private static bool IsStartOfDottedName(SyntaxNode node) =>
        (node is AliasQualifiedNameSyntax alias && alias.Alias.Identifier.IsKind(SyntaxKind.GlobalKeyword))
        || (node is IdentifierNameSyntax { Identifier.ValueText: "Microsoft" or "Pondhawk" } name
            && (name.Parent is QualifiedNameSyntax q && q.Left == name || name.Parent is MemberAccessExpressionSyntax m && m.Expression == name));

    private static bool StartsWithTarget(string text)
    {
        var name = text.StartsWith("global::", StringComparison.Ordinal) ? text.Substring("global::".Length) : text;
        return name.StartsWith(Logging + ".", StringComparison.Ordinal) || name.StartsWith(Pondhawk + ".", StringComparison.Ordinal);
    }

    // The longest dotted name a global:: name starts, short of a method being called.
    private static SyntaxNode Outermost(SyntaxNode node)
    {
        while (node.Parent is QualifiedNameSyntax q && q.Left == node
               || node.Parent is MemberAccessExpressionSyntax m && m.Expression == node && m.Parent is not InvocationExpressionSyntax)
            node = node.Parent;

        return node;
    }

    private static List<string> Needed(SyntaxNode root)
    {
        var present = new HashSet<string>(root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.Alias is null && u.Name is not null).Select(u => u.Name!.ToString()), StringComparer.Ordinal);

        var names = new HashSet<string>(root.DescendantNodes()
            .Where(n => n.FirstAncestorOrSelf<UsingDirectiveSyntax>() is null)
            .OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText), StringComparer.Ordinal);

        var needed = new List<string>();
        if (!present.Contains(Logging) && names.Overlaps(LoggingNames))
            needed.Add(Logging);
        if (!present.Contains(Pondhawk) && names.Overlaps(PondhawkNames))
            needed.Add(Pondhawk);

        return needed;
    }

    private static UsingDirectiveSyntax Using(string ns, UsingDirectiveSyntax? like, string newLine)
    {
        var directive = SyntaxFactory.ParseCompilationUnit("using " + ns + ";" + newLine).Usings[0];
        return like is null ? directive : directive.WithLeadingTrivia(like.GetLeadingTrivia()).WithTrailingTrivia(like.GetTrailingTrivia());
    }

    // Into a file without Fabrica.Watch usings: after its last using, or at the top with a blank line after.
    private static CompilationUnitSyntax Insert(CompilationUnitSyntax unit, List<UsingDirectiveSyntax> added, string newLine)
    {
        if (unit.Usings.Count == 0)
            added[added.Count - 1] = added[added.Count - 1].WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine), SyntaxFactory.EndOfLine(newLine));

        return unit.WithUsings(unit.Usings.AddRange(added));
    }

    // A container's usings without Fabrica.Watch's, with the new ones where the first of those was.
    private static SyntaxNode Replace(SyntaxNode container, List<UsingDirectiveSyntax> added)
    {
        var usings = container switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax ns => ns.Usings,
            _ => default,
        };

        var at = usings.IndexOf(FabricaWatch.IsFabricaUsing);
        var kept = usings.Where(u => !FabricaWatch.IsFabricaUsing(u)).ToList();
        kept.InsertRange(Math.Max(0, Math.Min(at, kept.Count)), added);

        return container switch
        {
            CompilationUnitSyntax unit => unit.WithUsings(SyntaxFactory.List(kept)),
            BaseNamespaceDeclarationSyntax ns => ns.WithUsings(SyntaxFactory.List(kept)),
            _ => container,
        };
    }
}
