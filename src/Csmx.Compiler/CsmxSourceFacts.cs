namespace Csmx.Compiler;

public sealed record CsmxSourceCompletionFacts(
    IReadOnlyList<CsmxComponentDescriptor> Components,
    IReadOnlyList<string> IntrinsicElements,
    IReadOnlyList<CsmxElementAttributeFacts> ElementAttributes,
    IReadOnlyList<string> Attributes
);

public sealed record CsmxElementAttributeFacts(
    string ElementName,
    IReadOnlyList<string> Attributes
);

public static class CsmxSourceFacts
{
    public static CsmxSourceCompletionFacts GetCompletionFacts(
        string text,
        CsmxTransformOptions? options = null
    )
    {
        text ??= string.Empty;
        var transformOptions = CsmxSourceOptions.Apply(
            text,
            options ?? CsmxTransformOptions.Default
        );
        var components = CsmxComponentRegistry
            .FromSource(text, transformOptions.ComponentNames)
            .Descriptors;
        var intrinsicElements = new SortedSet<string>(StringComparer.Ordinal);
        var attributes = new SortedSet<string>(StringComparer.Ordinal);
        var attributesByElement = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var element in ParseTopLevelElements(text, transformOptions))
        {
            CollectElementFacts(
                text,
                transformOptions,
                element,
                intrinsicElements,
                attributes,
                attributesByElement
            );
        }

        return new CsmxSourceCompletionFacts(
            components,
            intrinsicElements.ToArray(),
            attributesByElement
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new CsmxElementAttributeFacts(item.Key, item.Value.ToArray()))
                .ToArray(),
            attributes.ToArray()
        );
    }

    private static IEnumerable<CsmxElementFact> ParseTopLevelElements(
        string text,
        CsmxTransformOptions options
    )
    {
        var position = 0;
        while (position < text.Length)
        {
            if (TrySkipTriviaOrLiteral(text, position, out var skipped))
            {
                position = skipped;
                continue;
            }

            if (text[position] == '<' && IsLikelyJsxStart(text, position))
            {
                var facts = CsmxFacts.ParseElement(text, position, options);
                yield return facts.Element;
                position = Math.Max(facts.Position, position + 1);
                continue;
            }

            position++;
        }
    }

    public static IEnumerable<CsmxParsedElementFact> ParseElementsInExpression(
        string text,
        TextSpan expressionSpan,
        CsmxTransformOptions? options = null
    )
    {
        text ??= string.Empty;
        var transformOptions = CsmxSourceOptions.Apply(
            text,
            options ?? CsmxTransformOptions.Default
        );
        var end = CsmxCompat.Clamp(expressionSpan.End, 0, text.Length);
        var position = CsmxCompat.Clamp(expressionSpan.Start, 0, end);

        while (position < end)
        {
            if (TrySkipTriviaOrLiteral(text, position, out var skipped))
            {
                position = Math.Min(skipped, end);
                continue;
            }

            if (text[position] == '<' && IsLikelyJsxStart(text, position, expressionSpan.Start))
            {
                var facts = CsmxFacts.ParseElement(text, position, transformOptions);
                yield return new CsmxParsedElementFact(
                    facts.Element,
                    Math.Min(facts.Position, end)
                );
                position = Math.Max(Math.Min(facts.Position, end), position + 1);
                continue;
            }

            position++;
        }
    }

    private static void CollectElementFacts(
        string text,
        CsmxTransformOptions options,
        CsmxElementFact element,
        SortedSet<string> intrinsicElements,
        SortedSet<string> attributes,
        Dictionary<string, SortedSet<string>> attributesByElement
    )
    {
        if (element.Kind == CsmxElementFactKind.Intrinsic)
        {
            intrinsicElements.Add(element.Name);
        }

        if (!attributesByElement.TryGetValue(element.Name, out var elementAttributes))
        {
            elementAttributes = new SortedSet<string>(StringComparer.Ordinal);
            attributesByElement.Add(element.Name, elementAttributes);
        }

        foreach (var attribute in element.Attributes)
        {
            attributes.Add(attribute.Name);
            elementAttributes.Add(attribute.Name);
        }

        foreach (var child in element.Children.OfType<CsmxElementFact>())
        {
            CollectElementFacts(
                text,
                options,
                child,
                intrinsicElements,
                attributes,
                attributesByElement
            );
        }

        foreach (var expression in element.Children.OfType<CsmxExpressionFact>())
        {
            if (!expression.ContainsNestedJsx)
            {
                continue;
            }

            foreach (
                var nested in ParseElementsInExpression(text, expression.ExpressionSpan, options)
            )
            {
                CollectElementFacts(
                    text,
                    options,
                    nested.Element,
                    intrinsicElements,
                    attributes,
                    attributesByElement
                );
            }
        }
    }

    private static bool IsLikelyJsxStart(string text, int index) =>
        IsLikelyJsxStart(text, index, 0);

    private static bool IsLikelyJsxStart(string text, int index, int minPreviousIndex)
    {
        if (
            index < 0
            || index >= text.Length
            || text[index] != '<'
            || index + 1 >= text.Length
            || text[index + 1] == '/'
            || !IsNameStart(text[index + 1])
        )
        {
            return false;
        }

        var previous = PreviousNonWhitespaceIndex(text, index - 1, minPreviousIndex);
        if (previous < minPreviousIndex)
        {
            return true;
        }

        var previousChar = text[previous];
        if (previousChar is '=' or '(' or '[' or '{' or ',' or ':' or '?' or ';')
        {
            return true;
        }

        if (previousChar == '>' && previous > 0 && text[previous - 1] == '=')
        {
            return true;
        }

        var previousWord = ReadPreviousWord(text, previous);
        return previousWord is "return" or "throw" or "case" or "yield";
    }

    private static bool TrySkipTriviaOrLiteral(string text, int start, out int end)
    {
        if (start >= text.Length)
        {
            end = start;
            return false;
        }

        if (char.IsWhiteSpace(text[start]))
        {
            end = start + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            return true;
        }

        if (start + 1 < text.Length && text[start] == '/' && text[start + 1] == '/')
        {
            var newline = text.IndexOf('\n', start + 2);
            end = newline < 0 ? text.Length : newline + 1;
            return true;
        }

        if (start + 1 < text.Length && text[start] == '/' && text[start + 1] == '*')
        {
            var close = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            end = close < 0 ? text.Length : close + 2;
            return true;
        }

        if (text[start] == '"')
        {
            end = SkipStringLiteral(text, start);
            return true;
        }

        if (text[start] == '\'')
        {
            end = SkipCharLiteral(text, start);
            return true;
        }

        if (text[start] == '@' && start + 1 < text.Length && text[start + 1] == '"')
        {
            end = SkipVerbatimStringLiteral(text, start + 1);
            return true;
        }

        if (text[start] == '$' && start + 1 < text.Length && text[start + 1] == '"')
        {
            end = SkipStringLiteral(text, start + 1);
            return true;
        }

        if (
            text[start] == '$'
            && start + 2 < text.Length
            && text[start + 1] == '@'
            && text[start + 2] == '"'
        )
        {
            end = SkipVerbatimStringLiteral(text, start + 2);
            return true;
        }

        if (
            text[start] == '@'
            && start + 2 < text.Length
            && text[start + 1] == '$'
            && text[start + 2] == '"'
        )
        {
            end = SkipVerbatimStringLiteral(text, start + 2);
            return true;
        }

        end = start;
        return false;
    }

    private static int SkipStringLiteral(string text, int quote)
    {
        var index = quote + 1;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == '"')
            {
                return index + 1;
            }

            index++;
        }

        return text.Length;
    }

    private static int SkipVerbatimStringLiteral(string text, int quote)
    {
        var index = quote + 1;
        while (index < text.Length)
        {
            if (text[index] == '"')
            {
                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        return text.Length;
    }

    private static int SkipCharLiteral(string text, int quote)
    {
        var index = quote + 1;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == '\'')
            {
                return index + 1;
            }

            index++;
        }

        return text.Length;
    }

    private static int PreviousNonWhitespaceIndex(string text, int index) =>
        PreviousNonWhitespaceIndex(text, index, 0);

    private static int PreviousNonWhitespaceIndex(string text, int index, int minIndex)
    {
        for (var i = index; i >= minIndex; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return minIndex - 1;
    }

    private static string ReadPreviousWord(string text, int endIndex)
    {
        var end = endIndex + 1;
        var start = endIndex;
        while (start >= 0 && IsCSharpIdentifierPart(text[start]))
        {
            start--;
        }

        start++;
        return start < end ? text.Substring(start, end - start) : string.Empty;
    }

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
