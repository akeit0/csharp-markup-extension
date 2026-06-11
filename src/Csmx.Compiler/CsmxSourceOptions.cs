namespace Csmx.Compiler;

public static class CsmxSourceOptions
{
    public static CsmxTransformOptions Apply(string text, CsmxTransformOptions options)
    {
        var current = options;
        foreach (var line in (text ?? string.Empty).Split('\n').Take(32))
        {
            var trimmed = line.Trim(' ', '\t', '/', '*', '\r');
            if (!trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = trimmed.IndexOfAny(new[] { ' ', '\t' });
            if (separator <= 0 || separator + 1 >= trimmed.Length)
            {
                continue;
            }

            var directive = trimmed.Substring(0, separator);
            var value = trimmed.Substring(separator + 1).Trim();

            current = directive switch
            {
                "@jsxCompileMode" or "@csmxCompileMode" => TryParseCompileMode(
                    value,
                    out var compileMode
                )
                    ? current with
                    {
                        CompileMode = compileMode,
                    }
                    : current,
                "@jsx" or "@csmxElementFactory" => IsValidFactoryExpression(value)
                    ? current with
                    {
                        ElementFactory = value,
                    }
                    : current,
                "@jsxAttr" or "@csmxAttributeFactory" => IsValidFactoryExpression(value)
                    ? current with
                    {
                        AttributeFactory = value,
                    }
                    : current,
                "@jsxText" or "@csmxTextFactory" => IsValidFactoryExpression(value)
                    ? current with
                    {
                        TextFactory = value,
                    }
                    : current,
                "@jsxFormattedText" or "@csmxFormattedTextChild" => current with
                {
                    FormattedTextChild = value,
                },
                "@jsxAlignedFormattedText" or "@csmxAlignedFormattedTextChild" => current with
                {
                    AlignedFormattedTextChild = value,
                },
                "@jsxProps" or "@csmxPropsFactory" => IsValidFactoryExpression(value)
                    ? current with
                    {
                        PropsFactory = value,
                    }
                    : current,
                "@jsxChildren" or "@csmxChildrenFactory" => current with
                {
                    ChildrenFactory = value,
                },
                "@jsxChildSequence" or "@csmxChildSequenceFactory" => IsValidFactoryExpression(
                    value
                )
                    ? current with
                    {
                        ChildSequenceFactory = value,
                    }
                    : current,
                "@jsxKeyedSequenceElement" or "@csmxKeyedSequenceElement" => current with
                {
                    KeyedSequenceElement = value,
                },
                "@jsxKeyedSequenceItems" or "@csmxKeyedSequenceItemsAttribute" => current with
                {
                    KeyedSequenceItemsAttribute = value,
                },
                "@jsxKeyedSequenceKey" or "@csmxKeyedSequenceKeyAttribute" => current with
                {
                    KeyedSequenceKeyAttribute = value,
                },
                "@jsxKeyedSequenceTemplate" or "@csmxKeyedSequenceTemplate" => current with
                {
                    KeyedSequenceTemplate = value,
                },
                "@jsxComponentTemplate" or "@csmxComponentTemplate" => current with
                {
                    ComponentTemplate = value,
                },
                "@jsxFluentCreate" or "@csmxFluentCreate" => current with { FluentCreate = value },
                "@jsxFluentAttr" or "@csmxFluentAttribute" => current with
                {
                    FluentAttribute = value,
                },
                "@jsxFluentText" or "@csmxFluentTextChild" => current with
                {
                    FluentTextChild = value,
                },
                "@jsxFluentExpression" or "@csmxFluentExpressionChild" => current with
                {
                    FluentExpressionChild = value,
                },
                "@jsxFluentFormattedExpression" or "@csmxFluentFormattedExpressionChild" =>
                    current with
                    {
                        FluentFormattedExpressionChild = value,
                    },
                "@jsxFluentElement" or "@csmxFluentElementChild" => current with
                {
                    FluentElementChild = value,
                },
                "@jsxFluentComponent" or "@csmxFluentComponentTemplate" => current with
                {
                    FluentComponentTemplate = value,
                },
                _ => current,
            };
        }

        return current;
    }

    public static bool IsValidFactoryExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (
                c != '_'
                && c != '.'
                && c != ':'
                && c != '{'
                && c != '}'
                && !char.IsLetterOrDigit(c)
            )
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParseCompileMode(string value, out CsmxCompileMode compileMode)
    {
        if (string.Equals(value, "factory", StringComparison.OrdinalIgnoreCase))
        {
            compileMode = CsmxCompileMode.Factory;
            return true;
        }

        if (string.Equals(value, "fluent", StringComparison.OrdinalIgnoreCase))
        {
            compileMode = CsmxCompileMode.Fluent;
            return true;
        }

        compileMode = default;
        return false;
    }
}
