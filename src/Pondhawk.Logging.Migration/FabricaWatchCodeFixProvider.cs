// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Rewrites what <see cref="FabricaWatchAnalyzer"/> finds. Fix All applies every diagnostic in a document as one
/// rewrite, so nested and neighbouring fixes compose instead of conflicting.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FabricaWatchCodeFixProvider))]
[Shared]
public sealed class FabricaWatchCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
        Rules.LevelCall.Id,
        Rules.Template.Id,
        Rules.Parts.Id,
        Rules.Lazy.Id,
        Rules.LevelEnabled.Id,
        Rules.MethodTrace.Id,
        Rules.Scope.Id,
        Rules.ContextCall.Id,
        Rules.InspectName.Id,
        Rules.Pretty.Id,
        Rules.Events.Id,
        Rules.FactoryLocator.Id,
        Rules.ObjectExtension.Id,
        Rules.SharedType.Id,
        Rules.Usings.Id,
        Rules.LoggerType.Id);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create((context, document, diagnostics) => ApplyAsync(document, diagnostics, context.CancellationToken)!);

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

        foreach (var diagnostic in context.Diagnostics)
        {
            if (string.Equals(diagnostic.Id, Rules.Events.Id, StringComparison.Ordinal)
                && (root is null || model is null || Rewrites.For(diagnostic, root, model, "\n", context.CancellationToken) is null))
                continue;   // an event this cannot rewrite stays reported only

            var title = string.Equals(diagnostic.Id, Rules.Usings.Id, StringComparison.Ordinal) ? "Replace the Fabrica.Watch usings" : "Rewrite for Microsoft.Extensions.Logging and Pondhawk.Logging";
            context.RegisterCodeFix(
                CodeAction.Create(title, ct => ApplyAsync(context.Document, ImmutableArray.Create(diagnostic), ct), equivalenceKey: diagnostic.Id),
                diagnostic);
        }
    }

    private static async Task<Document> ApplyAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return document;

        var newLine = root.DescendantTrivia().FirstOrDefault(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EndOfLineTrivia)).ToFullString();
        if (newLine.Length == 0)
            newLine = "\n";

        // One rewrite per node and kind: a block can be both a traced function's body and hold an event pair, so
        // different kinds on one node are composed; the same kind twice (two diagnostics in one body) runs once.
        var rewrites = new Dictionary<SyntaxNode, (HashSet<string> Kinds, Func<SyntaxNode, SyntaxNode> Rewrite)>();
        foreach (var diagnostic in diagnostics)
        {
            if (Rewrites.For(diagnostic, root, model, newLine, cancellationToken) is not { } found)
                continue;

            var kind = diagnostic.Id is "PHLM006" or "PHLM007" ? "tracing" : diagnostic.Id;
            if (!rewrites.TryGetValue(found.Node, out var existing))
                rewrites[found.Node] = (new HashSet<string>(StringComparer.Ordinal) { kind }, found.Rewrite);
            else if (existing.Kinds.Add(kind))
                rewrites[found.Node] = (existing.Kinds, current => found.Rewrite(existing.Rewrite(current)));
        }

        var rewritten = root.ReplaceNodes(rewrites.Keys, (original, current) => rewrites[original].Rewrite(current));

        var swap = diagnostics.Any(d => string.Equals(d.Id, Rules.Usings.Id, StringComparison.Ordinal));
        rewritten = UsingSwap.Apply(rewritten, swap, HasGlobalFabricaUsing(model.Compilation));
        rewritten = UsingSwap.MarkQualifiedNames(rewritten);

        var changed = document.WithSyntaxRoot(rewritten);
        changed = await Simplifier.ReduceAsync(changed, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // A global using of Fabrica.Watch is in scope everywhere; adding Microsoft.Extensions.Logging beside it would
    // make ILogger ambiguous.
    private static bool HasGlobalFabricaUsing(Compilation compilation) =>
        compilation.SyntaxTrees
            .SelectMany(t => t.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or UsingDirectiveSyntax).OfType<UsingDirectiveSyntax>())
            .Any(u => !u.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) && FabricaWatch.IsFabricaNamespace(u.Name?.ToString()));
}
