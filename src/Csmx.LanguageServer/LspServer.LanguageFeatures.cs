using System.Text;
using Csmx.Compiler;

namespace Csmx.LanguageServer;

internal sealed partial class LspServer
{
    private static readonly string[] CompletionTriggerCharacters =
        new[] { "<", " ", "=", "\"", "." }
            .Concat("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_".Select(c => c.ToString()))
            .ToArray();

    private static bool IsTagCompletion(string text, int index)
    {
        if (index <= 0 || index > text.Length)
        {
            return false;
        }

        return text[index - 1] == '<' && (index < 2 || text[index - 2] != '/');
    }

    private static bool IsAttributeCompletion(string text, int index) =>
        IsInsideOpeningTag(text, index) && !IsTagCompletion(text, index);

    private static string? GetCurrentOpeningTagName(string text, int index)
    {
        var searchStart = Math.Clamp(index - 1, 0, Math.Max(0, text.Length - 1));
        var open = text.LastIndexOf('<', searchStart);
        var close = text.LastIndexOf('>', searchStart);
        if (open < 0 || open <= close || open + 1 >= text.Length || text[open + 1] == '/')
        {
            return null;
        }

        var cursor = open + 1;
        if (cursor >= text.Length || !IsNameStart(text[cursor]))
        {
            return null;
        }

        var start = cursor;
        while (cursor < text.Length && IsNamePart(text[cursor]))
        {
            cursor++;
        }

        return start < cursor ? text[start..cursor] : null;
    }

    private static bool IsInsideOpeningTag(string text, int index)
    {
        var searchStart = Math.Clamp(index - 1, 0, Math.Max(0, text.Length - 1));
        var lastOpen = text.LastIndexOf('<', searchStart);
        var lastClose = text.LastIndexOf('>', searchStart);

        if (
            lastOpen < 0
            || lastOpen <= lastClose
            || lastOpen + 1 >= text.Length
            || text[lastOpen + 1] == '/'
        )
        {
            return false;
        }

        var braceDepth = 0;
        var quote = '\0';
        for (var cursor = lastOpen + 1; cursor < index && cursor < text.Length; cursor++)
        {
            var c = text[cursor];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    cursor++;
                    continue;
                }

                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                continue;
            }

            if (c == '}' && braceDepth > 0)
            {
                braceDepth--;
            }
        }

        return braceDepth == 0 && quote == '\0';
    }

    private static string? GetSyntaxHover(string text, int index)
    {
        var options = CsmxSourceOptions.Apply(text, CsmxTransformOptions.Default);
        if (TryGetOpeningTagHover(text, index, options, out var openingTagHover))
        {
            return openingTagHover;
        }

        if (TryGetClosingTagHover(text, index, options, out var closingTagHover))
        {
            return closingTagHover;
        }

        return null;
    }

    private static bool TryGetOpeningTagHover(
        string text,
        int index,
        CsmxTransformOptions options,
        out string? hover
    )
    {
        hover = null;
        var searchStart = Math.Clamp(index, 0, Math.Max(0, text.Length - 1));
        var open = text.LastIndexOf('<', searchStart);
        var closeBefore = text.LastIndexOf('>', searchStart);
        if (
            open < 0
            || open <= closeBefore
            || open + 1 >= text.Length
            || text[open + 1] == '/'
            || !IsLikelyJsxStart(text, open)
        )
        {
            return false;
        }

        var tagEnd = FindTagEnd(text, open);
        if (tagEnd >= 0 && index > tagEnd)
        {
            return false;
        }

        var facts = CsmxFacts.ParseElement(text, open, options);
        var element = facts.Element;
        if (!Contains(element.Span, index))
        {
            return false;
        }

        if (index == open || Contains(element.NameSpan, index))
        {
            if (Contains(element.NameSpan, index) && element.Kind == CsmxElementFactKind.Component)
            {
                return false;
            }

            hover = string.Join(
                "\n\n",
                $"**CSMX element `<{element.Name}>`**",
                $"Kind: `{element.Kind}`. Static: `{element.StaticKind}`.",
                element.PropsType is null
                    ? "Props: `CsmxProps`."
                    : $"Props: `{element.PropsType}`.",
                "Attributes become props. Child text, expressions, and nested elements are passed as children."
            );
            return true;
        }

        foreach (var attribute in element.Attributes)
        {
            if (Contains(attribute.Span, index))
            {
                var valueDescription = GetAttributeValuePreview(attribute);
                hover = string.Join(
                    "\n\n",
                    $"**CSMX attribute `{attribute.Name}`**",
                    $"{GetAttributeValueKindDescription(attribute)}. Static: `{attribute.StaticKind}`.",
                    element.PropsType is null
                        ? $"Runtime prop key: `{attribute.Name}`. Value: `{EscapeMarkdownCode(valueDescription)}`."
                        : $"Props member: `{ToPropertyName(attribute.Name)}`. Value: `{EscapeMarkdownCode(valueDescription)}`."
                );
                return true;
            }
        }

        return false;
    }

    private static bool TryGetClosingTagHover(
        string text,
        int index,
        CsmxTransformOptions options,
        out string? hover
    )
    {
        hover = null;
        var searchStart = Math.Clamp(index, 0, Math.Max(0, text.Length - 1));
        var open = text.LastIndexOf("</", searchStart, StringComparison.Ordinal);
        var closeBefore = text.LastIndexOf('>', searchStart);
        if (open < 0 || open <= closeBefore)
        {
            return false;
        }

        var cursor = open + 2;
        if (cursor >= text.Length || !IsNameStart(text[cursor]))
        {
            return false;
        }

        var tagNameStart = cursor;
        while (cursor < text.Length && IsNamePart(text[cursor]))
        {
            cursor++;
        }

        var tagEnd = FindTagEnd(text, open);
        if (tagEnd < 0 || index > tagEnd)
        {
            return false;
        }

        var tagName = text[tagNameStart..cursor];
        hover = string.Join(
            "\n\n",
            $"**CSMX closing tag `</{tagName}>`**",
            $"Closes the `<{tagName}>` element."
        );
        return true;
    }

    private static string GetAttributeValuePreview(CsmxAttributeFact attribute) =>
        attribute.ValueKind switch
        {
            CsmxAttributeValueFactKind.Boolean => "true",
            CsmxAttributeValueFactKind.String => $"\"{attribute.StringValue ?? string.Empty}\"",
            CsmxAttributeValueFactKind.Expression => attribute.ExpressionValue ?? string.Empty,
            _ => "null",
        };

    private static string GetAttributeValueKindDescription(CsmxAttributeFact attribute) =>
        attribute.ValueKind switch
        {
            CsmxAttributeValueFactKind.Boolean => "Boolean attribute",
            CsmxAttributeValueFactKind.String => "String attribute",
            CsmxAttributeValueFactKind.Expression => "C# expression attribute",
            _ => "Missing attribute value",
        };

    private static bool Contains(TextSpan span, int index) =>
        index >= span.Start && index <= span.End;

    private static bool IsLikelyJsxStart(string text, int index)
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

        var previous = PreviousNonWhitespaceIndex(text, index - 1);
        if (previous < 0)
        {
            return true;
        }

        var previousChar = text[previous];
        if (previousChar is '=' or '(' or '[' or '{' or ',' or ':' or '?' or ';')
        {
            return true;
        }

        if (previousChar == '>')
        {
            return true;
        }

        return ReadPreviousWord(text, previous) is "return" or "throw" or "case" or "yield";
    }

    private static int PreviousNonWhitespaceIndex(string text, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static ReadOnlySpan<char> ReadPreviousWord(string text, int endIndex)
    {
        var end = endIndex + 1;
        var start = endIndex;
        while (start >= 0 && IsCSharpIdentifierPart(text[start]))
        {
            start--;
        }

        start++;
        return start < end ? text.AsSpan(start, end - start) : ReadOnlySpan<char>.Empty;
    }

    private static string ToPropertyName(string attributeName)
    {
        var builder = new StringBuilder();
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

    private static int FindTagEnd(string text, int open)
    {
        var cursor = open + 1;
        while (cursor < text.Length)
        {
            if (text[cursor] == '"' || text[cursor] == '\'')
            {
                var quote = text[cursor++];
                while (cursor < text.Length && text[cursor] != quote)
                {
                    cursor++;
                }
            }

            if (cursor < text.Length && text[cursor] == '>')
            {
                return cursor;
            }

            cursor++;
        }

        return -1;
    }

    private static string EscapeMarkdownCode(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsNamePart(char c) =>
        c == '_' || c == '-' || c == ':' || c == '.' || char.IsLetterOrDigit(c);

    private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
