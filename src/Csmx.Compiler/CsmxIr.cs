namespace Csmx.Compiler;

internal enum CsmxElementKind
{
    Intrinsic,
    Component,
}

internal enum CsmxStaticKind
{
    Static,
    Dynamic,
}

internal enum CsmxAttributeValueKind
{
    Boolean,
    String,
    Expression,
    Missing,
}

internal abstract record CsmxChildIr(TextSpan Span, CsmxStaticKind StaticKind);

internal sealed record CsmxElementIr(
    string Name,
    TextSpan NameSpan,
    CsmxElementKind Kind,
    CsmxStaticKind StaticKind,
    string? PropsType,
    IReadOnlyList<CsmxAttributeIr> Attributes,
    IReadOnlyList<CsmxChildIr> Children,
    TextSpan Span
) : CsmxChildIr(Span, StaticKind);

internal sealed record CsmxTextIr(string Text, CsmxStaticKind StaticKind, TextSpan Span)
    : CsmxChildIr(Span, StaticKind);

internal sealed record CsmxExpressionIr(
    string Expression,
    CsmxStaticKind StaticKind,
    TextSpan ExpressionSpan,
    CsmxExpressionFormatIr? Format,
    bool ContainsNestedJsx,
    TextSpan Span
) : CsmxChildIr(Span, StaticKind);

internal sealed record CsmxExpressionFormatIr(
    string? Alignment,
    TextSpan? AlignmentSpan,
    string? Format,
    TextSpan? FormatSpan
);

internal sealed record CsmxAttributeIr(
    string Name,
    TextSpan NameSpan,
    CsmxAttributeValueKind ValueKind,
    CsmxStaticKind StaticKind,
    string? StringValue,
    string? ExpressionValue,
    TextSpan? ExpressionSpan,
    bool IsBoolean,
    TextSpan Span
);
