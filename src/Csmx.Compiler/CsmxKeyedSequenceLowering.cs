namespace Csmx.Compiler;

internal readonly record struct CsmxKeyedSequenceIr(
    CsmxAttributeIr Items,
    CsmxAttributeIr Key,
    CsmxExpressionIr Render
);

internal enum CsmxKeyedSequenceMatch
{
    None,
    Valid,
    Invalid,
}

internal static class CsmxKeyedSequenceLowering
{
    public static CsmxKeyedSequenceMatch Match(
        CsmxElementIr element,
        CsmxLoweringContext context,
        out CsmxKeyedSequenceIr sequence
    )
    {
        sequence = default;
        var options = context.Options;
        if (
            string.IsNullOrWhiteSpace(options.KeyedSequenceElement)
            || !string.Equals(element.Name, options.KeyedSequenceElement, StringComparison.Ordinal)
        )
        {
            return CsmxKeyedSequenceMatch.None;
        }

        if (element.Kind == CsmxElementKind.Component)
        {
            context.AddWarning(
                $"Element '<{element.Name}>' is both a configured/discovered component and the configured keyed sequence element; keyed sequence lowering wins.",
                element.NameSpan
            );
        }

        var hasError = false;
        if (string.IsNullOrWhiteSpace(options.KeyedSequenceTemplate))
        {
            context.AddDiagnostic(
                $"Keyed sequence element '<{element.Name}>' requires CsmxKeyedSequenceTemplate.",
                element.NameSpan
            );
            hasError = true;
        }

        var items = FindExpressionAttribute(element, options.KeyedSequenceItemsAttribute);
        var key = FindExpressionAttribute(element, options.KeyedSequenceKeyAttribute);
        var render = element.Children.OfType<CsmxExpressionIr>().FirstOrDefault();

        if (items is null)
        {
            context.AddDiagnostic(
                $"Keyed sequence element '<{element.Name}>' requires expression attribute '{options.KeyedSequenceItemsAttribute}'.",
                element.NameSpan
            );
            hasError = true;
        }

        if (key is null)
        {
            context.AddDiagnostic(
                $"Keyed sequence element '<{element.Name}>' requires expression attribute '{options.KeyedSequenceKeyAttribute}'.",
                element.NameSpan
            );
            hasError = true;
        }

        if (render is null)
        {
            context.AddDiagnostic(
                $"Keyed sequence element '<{element.Name}>' requires one child expression render lambda.",
                element.Span
            );
            hasError = true;
        }

        if (hasError)
        {
            return CsmxKeyedSequenceMatch.Invalid;
        }

        sequence = new CsmxKeyedSequenceIr(items!, key!, render!);
        return CsmxKeyedSequenceMatch.Valid;
    }

    public static void ApplyTemplate(
        CsmxLoweringContext context,
        CsmxElementIr element,
        CsmxKeyedSequenceIr sequence,
        Action<CsmxAttributeIr> emitAttributeExpression,
        Action<CsmxExpressionIr> emitChildExpression
    )
    {
        CsmxTemplate.Apply(
            context,
            new CsmxTemplateDefinition(
                nameof(CsmxTransformOptions.KeyedSequenceTemplate),
                context.Options.KeyedSequenceTemplate
            ),
            element.Span,
            token =>
            {
                switch (token)
                {
                    case "CallSite":
                        context.AppendCallSiteLiteral(element);
                        return true;

                    case "Items":
                        emitAttributeExpression(sequence.Items);
                        return true;

                    case "Key":
                        emitAttributeExpression(sequence.Key);
                        return true;

                    case "Render":
                        emitChildExpression(sequence.Render);
                        return true;
                }

                return false;
            }
        );
    }

    private static CsmxAttributeIr? FindExpressionAttribute(
        CsmxElementIr element,
        string attributeName
    ) =>
        element.Attributes.FirstOrDefault(attribute =>
            attribute.ExpressionValue is not null
            && string.Equals(attribute.Name, attributeName, StringComparison.Ordinal)
        );
}
