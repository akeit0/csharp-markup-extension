using Csmx.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace Csmx.LanguageServer;

internal sealed record OpenDocumentSnapshot(
    string Uri,
    string Text,
    string SourceFilePath,
    CsmxTransformResult Transform,
    CsmxProjectContext? ProjectContext
);

internal sealed class CsmxProjectedDocument
{
    public CsmxProjectedDocument(
        OpenDocumentSnapshot source,
        CSharpCompilation compilation,
        SyntaxTree syntaxTree
    )
    {
        Source = source;
        Compilation = compilation;
        SyntaxTree = syntaxTree;
        SemanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
        Root = syntaxTree.GetRoot();
    }

    public OpenDocumentSnapshot Source { get; }

    public CSharpCompilation Compilation { get; }

    public SyntaxTree SyntaxTree { get; }

    public SemanticModel SemanticModel { get; }

    public SyntaxNode Root { get; }

    public string SourceText => Source.Text;

    public string GeneratedText => Source.Transform.Code;

    public bool TryMapSourcePositionToGenerated(int sourceIndex, out int generatedIndex)
    {
        var mapping = FindBestSourceMapping(sourceIndex);
        if (mapping is null)
        {
            generatedIndex = 0;
            return false;
        }

        var offset = Math.Min(
            Math.Max(0, sourceIndex - mapping.OriginalSpan.Start),
            Math.Max(0, mapping.GeneratedSpan.Length - 1)
        );
        generatedIndex = mapping.GeneratedSpan.Start + offset;
        return true;
    }

    public bool TryMapSourceCompletionPositionToGenerated(int sourceIndex, out int generatedIndex)
    {
        if (TryMapSourcePositionToGenerated(sourceIndex, out generatedIndex))
        {
            return true;
        }

        if (sourceIndex > 0 && TryMapSourcePositionToGenerated(sourceIndex - 1, out generatedIndex))
        {
            generatedIndex = Math.Min(generatedIndex + 1, GeneratedText.Length);
            return true;
        }

        return false;
    }

    public bool TryMapGeneratedSymbolSpanToSource(
        RoslynTextSpan generatedSpan,
        out int sourceStart,
        out int sourceLength
    )
    {
        sourceStart = 0;
        sourceLength = 0;
        if (generatedSpan.Length <= 0)
        {
            return false;
        }

        var mapping = FindBestGeneratedMapping(generatedSpan.Start, generatedSpan.Length);
        if (mapping is null)
        {
            return false;
        }

        if (IsReferenceMapping(mapping.Kind))
        {
            sourceStart = mapping.OriginalSpan.Start;
            sourceLength = mapping.OriginalSpan.Length;
            return IsValidSourceSpan(sourceStart, sourceLength);
        }

        var offset = generatedSpan.Start - mapping.GeneratedSpan.Start;
        if (offset < 0 || offset + generatedSpan.Length > mapping.OriginalSpan.Length)
        {
            return false;
        }

        sourceStart = mapping.OriginalSpan.Start + offset;
        sourceLength = generatedSpan.Length;
        return IsValidSourceSpan(sourceStart, sourceLength)
            && TextMatches(
                GeneratedText,
                generatedSpan.Start,
                generatedSpan.Length,
                SourceText,
                sourceStart,
                sourceLength
            );
    }

    public bool TryMapGeneratedDiagnosticSpanToSource(
        RoslynTextSpan generatedSpan,
        out int sourceStart,
        out int sourceLength
    )
    {
        sourceStart = 0;
        sourceLength = 0;
        if (generatedSpan.Length < 0)
        {
            return false;
        }

        var mapping = FindBestGeneratedMapping(generatedSpan.Start, generatedSpan.Length);
        if (mapping is null)
        {
            return false;
        }

        if (IsReferenceMapping(mapping.Kind))
        {
            sourceStart = mapping.OriginalSpan.Start;
            sourceLength = generatedSpan.Length == 0 ? 0 : mapping.OriginalSpan.Length;
            return IsValidSourceSpan(sourceStart, sourceLength);
        }

        var offset = generatedSpan.Start - mapping.GeneratedSpan.Start;
        if (offset < 0 || offset + generatedSpan.Length > mapping.OriginalSpan.Length)
        {
            return false;
        }

        sourceStart = mapping.OriginalSpan.Start + offset;
        sourceLength = generatedSpan.Length;
        if (!IsValidSourceSpan(sourceStart, sourceLength))
        {
            return false;
        }

        return generatedSpan.Length == 0
            || TextMatches(
                GeneratedText,
                generatedSpan.Start,
                generatedSpan.Length,
                SourceText,
                sourceStart,
                sourceLength
            );
    }

    private SourceMapEntry? FindBestSourceMapping(int sourceIndex) =>
        Source
            .Transform.Mappings.Where(mapping =>
                IsRoslynProjectionKind(mapping.Kind)
                && mapping.OriginalSpan.Length > 0
                && mapping.GeneratedSpan.Length > 0
                && sourceIndex >= mapping.OriginalSpan.Start
                && sourceIndex < mapping.OriginalSpan.End
            )
            .OrderBy(mapping => mapping.OriginalSpan.Length)
            .ThenBy(mapping => mapping.GeneratedSpan.Length)
            .FirstOrDefault();

    private SourceMapEntry? FindBestGeneratedMapping(int generatedStart, int generatedLength)
    {
        var generatedEnd = generatedStart + generatedLength;
        return Source
            .Transform.Mappings.Where(mapping =>
                IsRoslynProjectionKind(mapping.Kind)
                && mapping.OriginalSpan.Length > 0
                && mapping.GeneratedSpan.Length > 0
                && generatedStart >= mapping.GeneratedSpan.Start
                && generatedEnd <= mapping.GeneratedSpan.End
            )
            .OrderBy(mapping => mapping.GeneratedSpan.Length)
            .ThenBy(mapping => mapping.OriginalSpan.Length)
            .FirstOrDefault();
    }

    private static bool IsRoslynProjectionKind(ProjectionKind kind) =>
        kind
            is ProjectionKind.CSharp
                or ProjectionKind.ChildExpression
                or ProjectionKind.AttributeExpression
                or ProjectionKind.ComponentReference
                or ProjectionKind.ElementReference
                or ProjectionKind.AttributeReference;

    private static bool IsReferenceMapping(ProjectionKind kind) =>
        kind
            is ProjectionKind.ComponentReference
                or ProjectionKind.ElementReference
                or ProjectionKind.AttributeReference;

    private bool IsValidSourceSpan(int start, int length) =>
        start >= 0 && length >= 0 && start + length <= SourceText.Length;

    private static bool TextMatches(
        string left,
        int leftStart,
        int leftLength,
        string right,
        int rightStart,
        int rightLength
    )
    {
        if (
            leftLength != rightLength
            || leftStart < 0
            || rightStart < 0
            || leftStart + leftLength > left.Length
            || rightStart + rightLength > right.Length
        )
        {
            return false;
        }

        return left.AsSpan(leftStart, leftLength)
            .SequenceEqual(right.AsSpan(rightStart, rightLength));
    }
}
