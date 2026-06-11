namespace Csmx.Compiler;

internal sealed class CsmxFactoryLoweringBackend(CsmxLoweringContext context) : ICsmxLoweringBackend
{
    private readonly ICsmxComponentLoweringBackend componentLowering =
        context.Options.ComponentLowering == CsmxComponentLowering.FactoryCall
            ? new CsmxFactoryComponentLoweringBackend(context)
            : new CsmxDirectComponentLoweringBackend(context);

    public CsmxChildrenStrategy ChildrenStrategy => CsmxChildrenStrategy.Materialized;

    public void EmitElement(CsmxElementIr element)
    {
        var keyedSequenceMatch = CsmxKeyedSequenceLowering.Match(
            element,
            context,
            out var sequence
        );
        if (keyedSequenceMatch == CsmxKeyedSequenceMatch.Invalid)
        {
            context.Append("default!");
            return;
        }

        if (keyedSequenceMatch == CsmxKeyedSequenceMatch.Valid)
        {
            CsmxKeyedSequenceLowering.ApplyTemplate(
                context,
                element,
                sequence,
                EmitAttributeExpression,
                EmitMappedExpression
            );
            return;
        }

        var generatedStart = context.Position;
        if (
            element.Kind == CsmxElementKind.Component
            && !string.IsNullOrWhiteSpace(context.Options.ComponentTemplate)
        )
        {
            ApplyComponentTemplate(element);
            context.AddMapping(element.Span, generatedStart, ProjectionKind.JsxElement);
            return;
        }

        EmitElementCallStart(element);

        EmitProps(element);
        context.Append(", ");
        EmitChildren(element);
        context.Append(')');
        context.AddMapping(element.Span, generatedStart, ProjectionKind.JsxElement);
    }

    private void EmitChildren(CsmxElementIr element)
    {
        context.Append(context.Options.ChildrenFactory);
        context.Append('(');

        var needsSeparator = false;

        foreach (var child in element.Children)
        {
            if (needsSeparator)
            {
                context.Append(", ");
            }
            else
            {
                needsSeparator = true;
            }

            EmitChild(child);
        }

        context.Append(')');
    }

    private void EmitElementCallStart(CsmxElementIr element)
    {
        if (element.Kind == CsmxElementKind.Component)
        {
            componentLowering.EmitCallStart(element);
            return;
        }

        context.Append(context.Options.ElementFactory);
        context.Append('(');
        context.AppendStringLiteral(element.Name);
        context.Append(", ");
    }

    private void EmitProps(CsmxElementIr element)
    {
        if (element.PropsType is not null)
        {
            context.Append("new ");
            context.Append(element.PropsType);
            context.Append(" {");
            var needsSeparator = false;
            foreach (var attribute in element.Attributes)
            {
                AppendItemSeparator(ref needsSeparator);
                var propertyGeneratedStart = context.Position;
                context.Append(ToPropertyName(attribute.Name));
                context.AddMapping(
                    attribute.NameSpan,
                    propertyGeneratedStart,
                    ProjectionKind.AttributeReference
                );
                context.Append(" = ");
                EmitAttributeValue(attribute);
            }

            context.Append(needsSeparator ? " }" : "}");
            return;
        }

        context.Append(context.Options.PropsFactory);
        context.Append('(');
        var needsAttrSeparator = false;
        foreach (var attribute in element.Attributes)
        {
            if (needsAttrSeparator)
            {
                context.Append(", ");
            }
            else
            {
                needsAttrSeparator = true;
            }

            EmitAttribute(attribute);
        }

        context.Append(')');
    }

    private void AppendItemSeparator(ref bool needsSeparator)
    {
        if (needsSeparator)
        {
            context.Append(", ");
        }
        else
        {
            context.Append(' ');
            needsSeparator = true;
        }
    }

    private void EmitAttribute(CsmxAttributeIr attribute)
    {
        context.Append(context.Options.AttributeFactory);
        context.Append('(');
        context.AppendStringLiteral(attribute.Name);
        context.Append(", ");
        EmitAttributeValue(attribute);
        context.Append(')');
    }

    private void EmitAttributeValue(CsmxAttributeIr attribute)
    {
        if (attribute.IsBoolean)
        {
            context.Append("true");
            return;
        }

        if (attribute.StringValue is not null)
        {
            context.AppendStringLiteral(attribute.StringValue);
        }
        else if (
            attribute.ExpressionValue is not null
            && attribute.ExpressionSpan is { } expressionSpan
        )
        {
            context.EmitExpression(
                attribute.ExpressionValue,
                expressionSpan,
                ProjectionKind.AttributeExpression
            );
        }
        else
        {
            context.Append("null");
        }
    }

    private void EmitChild(CsmxChildIr child)
    {
        switch (child)
        {
            case CsmxElementIr element:
                EmitElement(element);
                break;

            case CsmxTextIr text:
                context.Append(context.Options.TextFactory);
                context.Append('(');
                context.AppendStringLiteral(text.Text);
                context.Append(')');
                break;

            case CsmxExpressionIr expression:
                if (expression.Format is { } format)
                {
                    ApplyFormattedTextTemplate(expression, format);
                    break;
                }

                if (expression.ContainsNestedJsx)
                {
                    context.Append(context.Options.ChildSequenceFactory);
                    context.Append('(');
                    EmitMappedExpression(expression);
                    context.Append(')');
                    break;
                }

                context.Append(context.Options.TextFactory);
                context.Append('(');
                EmitMappedExpression(expression);
                context.Append(')');
                break;
        }
    }

    private void ApplyFormattedTextTemplate(
        CsmxExpressionIr expression,
        CsmxExpressionFormatIr format
    )
    {
        var template = format.Alignment is null
            ? context.Options.FormattedTextChild
            : context.Options.AlignedFormattedTextChild;
        var templateName = format.Alignment is null
            ? nameof(CsmxTransformOptions.FormattedTextChild)
            : nameof(CsmxTransformOptions.AlignedFormattedTextChild);

        CsmxTemplate.Apply(
            context,
            new CsmxTemplateDefinition(templateName, template),
            expression.Span,
            token =>
            {
                switch (token)
                {
                    case "TextFactory":
                        context.Append(context.Options.TextFactory);
                        return true;

                    case "Value":
                        EmitMappedExpression(expression);
                        return true;

                    case "Format":
                        AppendNullableStringLiteral(format.Format);
                        return true;

                    case "Alignment":
                        context.Append(format.Alignment ?? "null");
                        return true;
                }

                return false;
            }
        );
    }

    private void EmitMappedExpression(CsmxExpressionIr expression)
    {
        context.EmitExpression(
            expression.Expression,
            expression.ExpressionSpan,
            ProjectionKind.ChildExpression
        );
    }

    private void EmitAttributeExpression(CsmxAttributeIr attribute)
    {
        context.EmitExpression(
            attribute.ExpressionValue ?? string.Empty,
            attribute.ExpressionSpan ?? attribute.Span,
            ProjectionKind.AttributeExpression
        );
    }

    private void AppendNullableStringLiteral(string? value)
    {
        if (value is null)
        {
            context.Append("null");
            return;
        }

        context.AppendStringLiteral(value);
    }

    private void ApplyComponentTemplate(CsmxElementIr element)
    {
        CsmxTemplate.Apply(
            context,
            new CsmxTemplateDefinition(
                nameof(CsmxTransformOptions.ComponentTemplate),
                context.Options.ComponentTemplate
            ),
            element.Span,
            token =>
            {
                switch (token)
                {
                    case "CallSite":
                        context.AppendCallSiteLiteral(element);
                        return true;

                    case "Component":
                        var nameGeneratedStart = context.Position;
                        context.Append(element.Name);
                        context.AddMapping(
                            element.NameSpan,
                            nameGeneratedStart,
                            ProjectionKind.ComponentReference
                        );
                        return true;

                    case "Props":
                        EmitProps(element);
                        return true;

                    case "Children":
                        EmitChildren(element);
                        return true;
                }

                return false;
            }
        );
    }

    private static string ToPropertyName(string attributeName)
    {
        var builder = new System.Text.StringBuilder();
        var capitalizeNext = true;
        foreach (var c in attributeName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        return builder.Length == 0 ? "Value" : builder.ToString();
    }
}
