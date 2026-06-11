namespace Csmx.Compiler;

internal sealed class CsmxIrBuilder(
    CsmxTransformOptions options,
    CsmxComponentRegistry? components = null
)
{
    private readonly CsmxComponentRegistry components =
        components ?? CsmxComponentRegistry.Parse(options.ComponentNames);

    public CsmxElementIr Build(JsxElementNode element)
    {
        var kind = components.IsComponent(element.Name)
            ? CsmxElementKind.Component
            : CsmxElementKind.Intrinsic;
        var attributes = element.Attributes.Select(Build).ToArray();
        var children = element.Children.Select(Build).ToArray();
        var staticKind =
            kind == CsmxElementKind.Intrinsic
            && attributes.All(attribute => attribute.StaticKind == CsmxStaticKind.Static)
            && children.All(child => child.StaticKind == CsmxStaticKind.Static)
                ? CsmxStaticKind.Static
                : CsmxStaticKind.Dynamic;

        return new(
            element.Name,
            element.NameSpan,
            kind,
            staticKind,
            components.GetPropsType(element.Name),
            attributes,
            children,
            element.Span
        );
    }

    private static CsmxAttributeIr Build(JsxAttributeNode attribute)
    {
        var valueKind = attribute switch
        {
            { IsBoolean: true } => CsmxAttributeValueKind.Boolean,
            { StringValue: not null } => CsmxAttributeValueKind.String,
            { ExpressionValue: not null } => CsmxAttributeValueKind.Expression,
            _ => CsmxAttributeValueKind.Missing,
        };
        var staticKind =
            valueKind == CsmxAttributeValueKind.Expression
                ? CsmxStaticKind.Dynamic
                : CsmxStaticKind.Static;

        return new(
            attribute.Name,
            attribute.NameSpan,
            valueKind,
            staticKind,
            attribute.StringValue,
            attribute.ExpressionValue,
            attribute.ExpressionSpan,
            attribute.IsBoolean,
            attribute.Span
        );
    }

    private CsmxChildIr Build(JsxNode node) =>
        node switch
        {
            JsxElementNode element => Build(element),
            JsxTextNode text => new CsmxTextIr(
                NormalizeText(text.Text),
                CsmxStaticKind.Static,
                text.Span
            ),
            JsxExpressionNode expression => new CsmxExpressionIr(
                expression.Expression,
                CsmxStaticKind.Dynamic,
                expression.ExpressionSpan,
                expression.Format is null
                    ? null
                    : new CsmxExpressionFormatIr(
                        expression.Format.Alignment,
                        expression.Format.AlignmentSpan,
                        expression.Format.Format,
                        expression.Format.FormatSpan
                    ),
                expression.ContainsNestedJsx,
                expression.Span
            ),
            _ => throw new InvalidOperationException($"Unknown CSMX node: {node.GetType().Name}"),
        };

    private static string NormalizeText(string text)
    {
        var normalized = CsmxCompat.ReplaceOrdinal(text, "\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        if (lines.Length == 1)
        {
            return normalized;
        }

        return string.Join(" ", lines.Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
