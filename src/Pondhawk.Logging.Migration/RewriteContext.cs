// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// What one rewrite knows of where it is: whether the call stands as a statement of its own (so it may become
/// a guard or two statements), and the TODOs it leaves for a person — written above the statement as
/// <c>// TODO(Fabrica.Watch migration): …</c>, one search away.
/// </summary>
internal sealed class RewriteContext(bool isStatement, string newLine)
{
    public const string Marker = "TODO(Fabrica.Watch migration): ";

    private readonly List<string> _todos = [];

    /// <summary>Whether the rewritten call is an expression statement.</summary>
    public bool IsStatement { get; } = isStatement;

    /// <summary>The line ending the file uses.</summary>
    public string NewLine { get; } = newLine;

    /// <summary>Leaves a note for a person to check what the rewrite could not settle.</summary>
    public void Todo(string note)
    {
        if (!_todos.Contains(note))
            _todos.Add(note);
    }

    /// <summary>The statement with this rewrite's TODOs above it, at its indentation.</summary>
    public T Mark<T>(T statement)
        where T : SyntaxNode
    {
        if (_todos.Count == 0)
            return statement;

        var leading = statement.GetLeadingTrivia();
        var indent = leading.LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var added = new List<SyntaxTrivia>();
        var present = leading.ToFullString();
        foreach (var todo in _todos.Where(t => !present.Contains(Marker + t)))
        {
            added.Add(SyntaxFactory.Comment("// " + Marker + todo));
            added.Add(SyntaxFactory.EndOfLine(NewLine));
            if (indent.IsKind(SyntaxKind.WhitespaceTrivia))
                added.Add(indent);
        }

        return statement.WithLeadingTrivia(leading.AddRange(added));
    }

    /// <summary>An expression with this rewrite's TODOs after it, where there is no statement to put them above.</summary>
    public SyntaxNode MarkInline(SyntaxNode node) =>
        _todos.Count == 0
            ? node
            : node.WithTrailingTrivia(node.GetTrailingTrivia().AddRange(_todos.Select(t => SyntaxFactory.Comment(" /* " + Marker + t + " */"))));
}
