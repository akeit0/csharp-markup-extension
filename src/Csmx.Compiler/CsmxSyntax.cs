namespace Csmx.Compiler;

internal sealed record CsmxParseResult(
    JsxElementNode Element,
    IReadOnlyList<CsmxDiagnostic> Diagnostics,
    int Position
);

internal abstract record JsxNode(TextSpan Span);

internal sealed record JsxElementNode(
    string Name,
    TextSpan NameSpan,
    IReadOnlyList<JsxAttributeNode> Attributes,
    IReadOnlyList<JsxNode> Children,
    TextSpan Span
) : JsxNode(Span);

internal sealed record JsxTextNode(string Text, TextSpan Span) : JsxNode(Span);

internal sealed record JsxExpressionNode(
    string Expression,
    TextSpan ExpressionSpan,
    JsxExpressionFormat? Format,
    bool ContainsNestedJsx,
    TextSpan Span
) : JsxNode(Span);

internal sealed record JsxExpressionFormat(
    string? Alignment,
    TextSpan? AlignmentSpan,
    string? Format,
    TextSpan? FormatSpan
);

internal sealed record JsxAttributeNode(
    string Name,
    TextSpan NameSpan,
    string? StringValue,
    string? ExpressionValue,
    TextSpan? ExpressionSpan,
    bool IsBoolean,
    TextSpan Span
);
