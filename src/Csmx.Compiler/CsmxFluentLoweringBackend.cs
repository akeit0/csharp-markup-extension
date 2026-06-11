namespace Csmx.Compiler;

internal sealed class CsmxFluentLoweringBackend(CsmxLoweringContext context) : ICsmxLoweringBackend
{
    public CsmxChildrenStrategy ChildrenStrategy => CsmxChildrenStrategy.BackendOwned;

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
            && !string.IsNullOrWhiteSpace(context.Options.FluentComponentTemplate)
        )
        {
            ApplyComponentTemplate(element);
            context.AddMapping(element.Span, generatedStart, ProjectionKind.JsxElement);
            return;
        }

        ApplyTemplate(
            nameof(CsmxTransformOptions.FluentCreate),
            context.Options.FluentCreate,
            element,
            value: null,
            mappingSpan: null,
            mappingKind: null
        );

        foreach (var attribute in element.Attributes)
        {
            EmitAttribute(element, attribute);
        }

        foreach (var child in element.Children)
        {
            EmitChild(element, child);
        }

        context.AddMapping(element.Span, generatedStart, ProjectionKind.JsxElement);
    }

    private void EmitAttribute(CsmxElementIr element, CsmxAttributeIr attribute)
    {
        var value = new DeferredValue(writer => EmitAttributeValue(attribute, writer));
        ApplyTemplate(
            nameof(CsmxTransformOptions.FluentAttribute),
            context.Options.FluentAttribute,
            element,
            value,
            attribute.ExpressionSpan,
            ProjectionKind.AttributeExpression,
            attribute.Name,
            attribute.NameSpan
        );
    }

    private void EmitChild(CsmxElementIr parent, CsmxChildIr child)
    {
        switch (child)
        {
            case CsmxTextIr text:
                ApplyTemplate(
                    nameof(CsmxTransformOptions.FluentTextChild),
                    context.Options.FluentTextChild,
                    parent,
                    new DeferredValue(writer => writer.AppendStringLiteral(text.Text)),
                    mappingSpan: null,
                    mappingKind: null
                );
                break;

            case CsmxExpressionIr expression:
                ApplyTemplate(
                    expression.Format is null
                        ? nameof(CsmxTransformOptions.FluentExpressionChild)
                        : nameof(CsmxTransformOptions.FluentFormattedExpressionChild),
                    expression.Format is null
                        ? context.Options.FluentExpressionChild
                        : context.Options.FluentFormattedExpressionChild,
                    parent,
                    new DeferredValue(
                        writer =>
                            writer.AppendExpression(
                                expression.Expression,
                                expression.ExpressionSpan,
                                ProjectionKind.ChildExpression
                            ),
                        mapsValue: true
                    ),
                    expression.ExpressionSpan,
                    ProjectionKind.ChildExpression,
                    format: expression.Format
                );
                break;

            case CsmxElementIr element:
                ApplyTemplate(
                    nameof(CsmxTransformOptions.FluentElementChild),
                    context.Options.FluentElementChild,
                    parent,
                    new DeferredValue(_ => EmitElement(element), mapsValue: true),
                    element.Span,
                    ProjectionKind.JsxElement
                );
                break;
        }
    }

    private void EmitAttributeValue(CsmxAttributeIr attribute, TemplateWriter writer)
    {
        if (attribute.IsBoolean)
        {
            writer.AppendRaw("true");
            return;
        }

        if (attribute.StringValue is not null)
        {
            writer.AppendStringLiteral(attribute.StringValue);
            return;
        }

        if (attribute.ExpressionValue is not null)
        {
            writer.AppendExpression(
                attribute.ExpressionValue,
                attribute.ExpressionSpan!.Value,
                ProjectionKind.AttributeExpression
            );
            return;
        }

        writer.AppendRaw("null");
    }

    private void EmitAttributeExpression(CsmxAttributeIr attribute)
    {
        context.EmitExpression(
            attribute.ExpressionValue ?? string.Empty,
            attribute.ExpressionSpan ?? attribute.Span,
            ProjectionKind.AttributeExpression
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

    private void ApplyTemplate(
        string templateName,
        string template,
        CsmxElementIr element,
        DeferredValue? value,
        TextSpan? mappingSpan,
        ProjectionKind? mappingKind,
        string? attributeName = null,
        TextSpan? attributeNameSpan = null,
        CsmxExpressionFormatIr? format = null
    )
    {
        var writer = new TemplateWriter(context);
        CsmxTemplate.Apply(
            context,
            new CsmxTemplateDefinition(templateName, template),
            mappingSpan ?? element.Span,
            token =>
            {
                switch (token)
                {
                    case "Element":
                        var elementGeneratedStart = context.Position;
                        context.Append(element.Name);
                        context.AddMapping(
                            element.NameSpan,
                            elementGeneratedStart,
                            ProjectionKind.ElementReference
                        );
                        return true;

                    case "Name":
                        var memberName = ToMemberName(attributeName ?? element.Name);
                        var memberGeneratedStart = context.Position;
                        context.Append(memberName);
                        if (attributeNameSpan is { } nameSpan)
                        {
                            context.AddMapping(
                                nameSpan,
                                memberGeneratedStart,
                                ProjectionKind.AttributeReference
                            );
                        }

                        return true;

                    case "RawName":
                        context.Append(attributeName ?? element.Name);
                        return true;

                    case "CallSite":
                        context.AppendCallSiteLiteral(element);
                        return true;

                    case "Value":
                        if (value is not null)
                        {
                            var generatedStart = context.Position;
                            value.Emit(writer);
                            if (
                                !value.MapsValue
                                && mappingSpan is { } span
                                && mappingKind is { } kind
                            )
                            {
                                context.AddMapping(span, generatedStart, kind);
                            }
                        }

                        return true;

                    case "Format":
                        writer.AppendNullableStringLiteral(format?.Format);
                        return true;

                    case "Alignment":
                        writer.AppendRaw(format?.Alignment ?? "null");
                        return true;
                }

                return false;
            }
        );
    }

    private void ApplyComponentTemplate(CsmxElementIr element)
    {
        CsmxTemplate.Apply(
            context,
            new CsmxTemplateDefinition(
                nameof(CsmxTransformOptions.FluentComponentTemplate),
                context.Options.FluentComponentTemplate
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
                        EmitComponentProps(element);
                        return true;

                    case "Children":
                        EmitComponentChildren(element);
                        return true;
                }

                return false;
            }
        );
    }

    private void EmitComponentProps(CsmxElementIr element)
    {
        if (element.PropsType is not null)
        {
            context.Append("new ");
            context.Append(element.PropsType);
            context.Append(" {");
            var needsSeparator = false;
            foreach (var attribute in element.Attributes)
            {
                AppendComponentItemSeparator(ref needsSeparator);
                var memberGeneratedStart = context.Position;
                context.Append(ToMemberName(attribute.Name));
                context.AddMapping(
                    attribute.NameSpan,
                    memberGeneratedStart,
                    ProjectionKind.AttributeReference
                );
                context.Append(" = ");
                EmitComponentAttributeValue(attribute);
            }

            context.Append(needsSeparator ? " }" : "}");
            return;
        }

        context.Append(context.Options.PropsFactory);
        context.Append('(');
        context.Append(')');
    }

    private void EmitComponentAttributeValue(CsmxAttributeIr attribute)
    {
        if (attribute.IsBoolean)
        {
            context.Append("true");
            return;
        }

        if (attribute.StringValue is not null)
        {
            context.AppendStringLiteral(attribute.StringValue);
            return;
        }

        if (attribute.ExpressionValue is not null)
        {
            context.EmitExpression(
                attribute.ExpressionValue,
                attribute.ExpressionSpan!.Value,
                ProjectionKind.AttributeExpression
            );
            return;
        }

        context.Append("null");
    }

    private void EmitComponentChildren(CsmxElementIr element)
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

            EmitComponentChild(child);
        }

        context.Append(')');
    }

    private void EmitComponentChild(CsmxChildIr child)
    {
        switch (child)
        {
            case CsmxTextIr text:
                context.Append(context.Options.TextFactory);
                context.Append('(');
                context.AppendStringLiteral(text.Text);
                context.Append(')');
                break;

            case CsmxExpressionIr expression:
                EmitMappedExpression(expression);
                break;

            case CsmxElementIr element:
                EmitElement(element);
                break;
        }
    }

    private void AppendComponentItemSeparator(ref bool needsSeparator)
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

    private static string ToMemberName(string value)
    {
        var builder = new System.Text.StringBuilder();
        var capitalizeNext = true;
        foreach (var c in value)
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

    private sealed class DeferredValue(Action<TemplateWriter> emit, bool mapsValue = false)
    {
        public bool MapsValue { get; } = mapsValue;

        public void Emit(TemplateWriter writer) => emit(writer);
    }

    private sealed class TemplateWriter(CsmxLoweringContext context)
    {
        public void AppendRaw(string value) => context.Append(value);

        public void AppendStringLiteral(string value) => context.AppendStringLiteral(value);

        public void AppendExpression(string value, TextSpan span, ProjectionKind kind) =>
            context.EmitExpression(value, span, kind);

        public void AppendNullableStringLiteral(string? value)
        {
            if (value is null)
            {
                context.Append("null");
                return;
            }

            context.AppendStringLiteral(value);
        }
    }
}
