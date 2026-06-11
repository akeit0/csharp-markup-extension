namespace Csmx.Compiler;

public static class CsmxFacts
{
    public static CsmxFactsResult ParseElement(
        string text,
        int start,
        CsmxTransformOptions? options = null
    )
    {
        var transformOptions = options ?? CsmxTransformOptions.Default;
        var parsed = CsmxParser.ParseElement(text, start);
        var ir = new CsmxIrBuilder(
            transformOptions,
            CsmxComponentRegistry.FromSource(text, transformOptions.ComponentNames)
        ).Build(parsed.Element);
        return new CsmxFactsResult(ToFact(ir), parsed.Diagnostics, parsed.Position);
    }

    private static CsmxElementFact ToFact(CsmxElementIr element) =>
        new(
            element.Name,
            element.NameSpan,
            ToFact(element.Kind),
            ToFact(element.StaticKind),
            element.PropsType,
            element.Attributes.Select(ToFact).ToArray(),
            element.Children.Select(ToFact).ToArray(),
            element.Span
        );

    private static CsmxChildFact ToFact(CsmxChildIr child) =>
        child switch
        {
            CsmxElementIr element => ToFact(element),
            CsmxTextIr text => new CsmxTextFact(text.Text, ToFact(text.StaticKind), text.Span),
            CsmxExpressionIr expression => new CsmxExpressionFact(
                expression.Expression,
                ToFact(expression.StaticKind),
                expression.ExpressionSpan,
                expression.Format is null
                    ? null
                    : new CsmxExpressionFormatFact(
                        expression.Format.Alignment,
                        expression.Format.AlignmentSpan,
                        expression.Format.Format,
                        expression.Format.FormatSpan
                    ),
                expression.ContainsNestedJsx,
                expression.Span
            ),
            _ => throw new InvalidOperationException($"Unknown CSMX IR: {child.GetType().Name}"),
        };

    private static CsmxAttributeFact ToFact(CsmxAttributeIr attribute) =>
        new(
            attribute.Name,
            attribute.NameSpan,
            ToFact(attribute.ValueKind),
            ToFact(attribute.StaticKind),
            attribute.StringValue,
            attribute.ExpressionValue,
            attribute.ExpressionSpan,
            attribute.IsBoolean,
            attribute.Span
        );

    private static CsmxElementFactKind ToFact(CsmxElementKind kind) =>
        kind switch
        {
            CsmxElementKind.Intrinsic => CsmxElementFactKind.Intrinsic,
            CsmxElementKind.Component => CsmxElementFactKind.Component,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static CsmxStaticFactKind ToFact(CsmxStaticKind kind) =>
        kind switch
        {
            CsmxStaticKind.Static => CsmxStaticFactKind.Static,
            CsmxStaticKind.Dynamic => CsmxStaticFactKind.Dynamic,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static CsmxAttributeValueFactKind ToFact(CsmxAttributeValueKind kind) =>
        kind switch
        {
            CsmxAttributeValueKind.Boolean => CsmxAttributeValueFactKind.Boolean,
            CsmxAttributeValueKind.String => CsmxAttributeValueFactKind.String,
            CsmxAttributeValueKind.Expression => CsmxAttributeValueFactKind.Expression,
            CsmxAttributeValueKind.Missing => CsmxAttributeValueFactKind.Missing,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}
