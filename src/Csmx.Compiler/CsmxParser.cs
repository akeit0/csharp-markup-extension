using System.Text;

namespace Csmx.Compiler;

internal sealed class CsmxParser
{
    private readonly string _text;
    private readonly List<CsmxDiagnostic> _diagnostics = new();
    private int _pos;

    private CsmxParser(string text, int start)
    {
        _text = text ?? string.Empty;
        _pos = CsmxCompat.Clamp(start, 0, _text.Length);
    }

    public static CsmxParseResult ParseElement(string text, int start)
    {
        var parser = new CsmxParser(text, start);
        var element = parser.ParseElement();
        return new CsmxParseResult(element, parser._diagnostics, parser._pos);
    }

    private JsxElementNode ParseElement()
    {
        var start = _pos;
        Expect('<');

        if (Current == '/')
        {
            AddError("Unexpected closing JSX tag.", CurrentSpan());
            _pos++;
        }

        var (name, nameSpan) = ReadTagName();
        var attributes = new List<JsxAttributeNode>();
        var children = new List<JsxNode>();

        while (!IsEnd)
        {
            var whitespaceStart = _pos;
            var skippedNewline = SkipWhitespaceInJsx();
            if (skippedNewline && IsLikelyCSharpStatementStart(_pos))
            {
                _pos = whitespaceStart;
                AddError(
                    $"Element '<{name}>' is missing an opening tag terminator '>'.",
                    TextSpan.FromBounds(start, _pos)
                );
                return new JsxElementNode(
                    name,
                    nameSpan,
                    attributes,
                    children,
                    TextSpan.FromBounds(start, _pos)
                );
            }

            if (StartsWith("/>"))
            {
                _pos += 2;
                return new JsxElementNode(
                    name,
                    nameSpan,
                    attributes,
                    children,
                    TextSpan.FromBounds(start, _pos)
                );
            }

            if (Current == '>')
            {
                _pos++;
                break;
            }

            attributes.Add(ParseAttribute());
        }

        while (!IsEnd)
        {
            if (StartsWith("</"))
            {
                var closeStart = _pos;
                _pos += 2;
                var (closingName, _) = ReadTagName();
                SkipWhitespaceInJsx();

                if (!StringComparer.Ordinal.Equals(name, closingName))
                {
                    _pos = closeStart;
                    AddError(
                        $"Element '<{name}>' is missing a closing tag before '</{closingName}>'.",
                        TextSpan.FromBounds(start, closeStart)
                    );

                    return new JsxElementNode(
                        name,
                        nameSpan,
                        attributes,
                        children,
                        TextSpan.FromBounds(start, closeStart)
                    );
                }

                Expect('>');

                return new JsxElementNode(
                    name,
                    nameSpan,
                    attributes,
                    children,
                    TextSpan.FromBounds(start, _pos)
                );
            }

            if (Current == '<')
            {
                if (Peek(1) == '/')
                {
                    break;
                }

                if (IsNameStart(Peek(1)))
                {
                    children.Add(ParseElement());
                    continue;
                }
            }

            if (Current == '{')
            {
                var expression = ParseBraceExpression();
                children.Add(
                    new JsxExpressionNode(
                        expression.Expression,
                        expression.ExpressionSpan,
                        expression.Format,
                        expression.ContainsNestedJsx,
                        expression.FullSpan
                    )
                );
                continue;
            }

            var text = ParseTextNode();
            if (ShouldKeepTextNode(text.Text, children.Count > 0))
            {
                children.Add(text);
            }
        }

        AddError($"Element '<{name}>' is missing a closing tag.", TextSpan.FromBounds(start, _pos));
        return new JsxElementNode(
            name,
            nameSpan,
            attributes,
            children,
            TextSpan.FromBounds(start, _pos)
        );
    }

    private JsxAttributeNode ParseAttribute()
    {
        var start = _pos;
        var (name, nameSpan) = ReadAttributeName();
        SkipWhitespaceInJsx();

        if (Current != '=')
        {
            return new JsxAttributeNode(
                name,
                nameSpan,
                null,
                null,
                null,
                IsBoolean: true,
                TextSpan.FromBounds(start, _pos)
            );
        }

        _pos++;
        SkipWhitespaceInJsx();

        if (Current == '"' || Current == '\'')
        {
            var value = ReadJsxQuotedString();
            return new JsxAttributeNode(
                name,
                nameSpan,
                value,
                null,
                null,
                IsBoolean: false,
                TextSpan.FromBounds(start, _pos)
            );
        }

        if (Current == '{')
        {
            var expression = ParseBraceExpression(recoverAttributeBoundary: true);
            return new JsxAttributeNode(
                name,
                nameSpan,
                null,
                expression.Expression,
                expression.ExpressionSpan,
                IsBoolean: false,
                TextSpan.FromBounds(start, _pos)
            );
        }

        AddError(
            "Expected an attribute value: a string literal or a { C# expression }.",
            CurrentSpan()
        );
        return new JsxAttributeNode(
            name,
            nameSpan,
            null,
            null,
            null,
            IsBoolean: false,
            TextSpan.FromBounds(start, _pos)
        );
    }

    private JsxTextNode ParseTextNode()
    {
        var start = _pos;
        while (!IsEnd && Current != '<' && Current != '{')
        {
            _pos++;
        }

        return new JsxTextNode(
            _text.Substring(start, _pos - start),
            TextSpan.FromBounds(start, _pos)
        );
    }

    private (
        string Expression,
        TextSpan ExpressionSpan,
        JsxExpressionFormat? Format,
        bool ContainsNestedJsx,
        TextSpan FullSpan
    ) ParseBraceExpression(bool recoverAttributeBoundary = false)
    {
        var fullStart = _pos;
        Expect('{');
        var contentStart = _pos;
        var depth = 1;

        while (!IsEnd && depth > 0)
        {
            if (
                recoverAttributeBoundary
                && depth == 1
                && IsAttributeExpressionRecoveryBoundary(contentStart)
            )
            {
                break;
            }

            if (StartsWith("//"))
            {
                SkipLineCommentForParser();
                continue;
            }

            if (StartsWith("/*"))
            {
                SkipBlockCommentForParser();
                continue;
            }

            if (Current == '"')
            {
                SkipStringLiteralForParser();
                continue;
            }

            if (Current == '\'')
            {
                SkipCharLiteralForParser();
                continue;
            }

            if (Current == '{')
            {
                depth++;
                _pos++;
                continue;
            }

            if (Current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                _pos++;
                continue;
            }

            _pos++;
        }

        var contentEnd = _pos;
        if (Current == '}')
        {
            _pos++;
        }
        else
        {
            AddError("Expected '}' to close C# expression.", TextSpan.FromBounds(fullStart, _pos));
        }

        var trimmedStart = contentStart;
        var trimmedEnd = contentEnd;
        while (trimmedStart < trimmedEnd && char.IsWhiteSpace(_text[trimmedStart]))
        {
            trimmedStart++;
        }

        while (trimmedEnd > trimmedStart && char.IsWhiteSpace(_text[trimmedEnd - 1]))
        {
            trimmedEnd--;
        }

        if (trimmedStart == trimmedEnd)
        {
            AddError(
                "Expected a C# expression inside '{ ... }'.",
                TextSpan.FromBounds(fullStart, _pos)
            );
        }

        var parts = SplitFormattedExpression(trimmedStart, trimmedEnd);
        var expression = _text.Substring(parts.ExpressionSpan.Start, parts.ExpressionSpan.Length);
        var containsNestedJsx = ContainsLikelyNestedJsx(
            parts.ExpressionSpan.Start,
            parts.ExpressionSpan.End
        );

        return (
            expression,
            parts.ExpressionSpan,
            parts.Format,
            containsNestedJsx,
            TextSpan.FromBounds(fullStart, _pos)
        );
    }

    private readonly record struct FormattedExpressionParts(
        TextSpan ExpressionSpan,
        JsxExpressionFormat? Format
    );

    private FormattedExpressionParts SplitFormattedExpression(int start, int end)
    {
        var comma = FindTopLevelFormatSeparator(start, end, ',');
        var colon = FindTopLevelFormatSeparator(comma >= 0 ? comma + 1 : start, end, ':');
        if (comma < 0 && colon < 0)
        {
            return new FormattedExpressionParts(TextSpan.FromBounds(start, end), null);
        }

        var expressionEnd = comma >= 0 ? comma : colon;
        var expressionSpan = TrimSpan(start, expressionEnd);
        TextSpan? alignmentSpan = null;
        TextSpan? formatSpan = null;

        if (comma >= 0)
        {
            var alignmentEnd = colon >= 0 ? colon : end;
            var trimmedAlignment = TrimSpan(comma + 1, alignmentEnd);
            if (trimmedAlignment.Length > 0)
            {
                alignmentSpan = trimmedAlignment;
            }
        }

        if (colon >= 0)
        {
            var trimmedFormat = TrimSpan(colon + 1, end);
            if (trimmedFormat.Length > 0)
            {
                formatSpan = trimmedFormat;
            }
        }

        return new FormattedExpressionParts(
            expressionSpan,
            new JsxExpressionFormat(
                alignmentSpan is { } alignment
                    ? _text.Substring(alignment.Start, alignment.Length)
                    : null,
                alignmentSpan,
                formatSpan is { } format ? _text.Substring(format.Start, format.Length) : null,
                formatSpan
            )
        );
    }

    private int FindTopLevelFormatSeparator(int start, int end, char separator)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = start; index < end; index++)
        {
            if (CsmxCompat.StartsWithAt(_text, index, "//"))
            {
                while (index < end && _text[index] != '\r' && _text[index] != '\n')
                {
                    index++;
                }
                continue;
            }

            if (CsmxCompat.StartsWithAt(_text, index, "/*"))
            {
                index += 2;
                while (index < end && !CsmxCompat.StartsWithAt(_text, index, "*/"))
                {
                    index++;
                }
                if (index < end)
                {
                    index++;
                }
                continue;
            }

            if (_text[index] == '"')
            {
                var saved = _pos;
                _pos = index;
                SkipStringLiteralForParser();
                index = Math.Min(_pos - 1, end - 1);
                _pos = saved;
                continue;
            }

            if (_text[index] == '\'')
            {
                var saved = _pos;
                _pos = index;
                SkipCharLiteralForParser();
                index = Math.Min(_pos - 1, end - 1);
                _pos = saved;
                continue;
            }

            switch (_text[index])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case var c
                    when c == separator && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    if (separator == ':' && IsLikelyTernaryColon(start, index))
                    {
                        break;
                    }

                    return index;
            }
        }

        return -1;
    }

    private bool IsLikelyTernaryColon(int start, int colon)
    {
        var questionDepth = 0;
        for (var index = start; index < colon; index++)
        {
            if (_text[index] == '?')
            {
                questionDepth++;
            }
            else if (_text[index] == ':' && questionDepth > 0)
            {
                questionDepth--;
            }
        }

        return questionDepth > 0;
    }

    private TextSpan TrimSpan(int start, int end)
    {
        while (start < end && char.IsWhiteSpace(_text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(_text[end - 1]))
        {
            end--;
        }

        return TextSpan.FromBounds(start, end);
    }

    private bool ContainsLikelyNestedJsx(int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (CsmxCompat.StartsWithAt(_text, index, "//"))
            {
                while (index < end && _text[index] != '\r' && _text[index] != '\n')
                {
                    index++;
                }
                continue;
            }

            if (CsmxCompat.StartsWithAt(_text, index, "/*"))
            {
                index += 2;
                while (index < end && !CsmxCompat.StartsWithAt(_text, index, "*/"))
                {
                    index++;
                }
                if (index < end)
                {
                    index++;
                }
                continue;
            }

            if (_text[index] == '"')
            {
                var saved = _pos;
                _pos = index;
                SkipStringLiteralForParser();
                index = Math.Min(_pos - 1, end - 1);
                _pos = saved;
                continue;
            }

            if (_text[index] == '\'')
            {
                var saved = _pos;
                _pos = index;
                SkipCharLiteralForParser();
                index = Math.Min(_pos - 1, end - 1);
                _pos = saved;
                continue;
            }

            if (_text[index] == '<' && IsLikelyNestedJsxStart(index, start))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsLikelyNestedJsxStart(int index, int expressionStart)
    {
        if (
            index < 0
            || index >= _text.Length
            || _text[index] != '<'
            || PeekFrom(index, 1) == '/'
            || !IsNameStart(PeekFrom(index, 1))
        )
        {
            return false;
        }

        var previous = PreviousNonWhitespaceIndexWithin(index - 1, expressionStart);
        if (previous < expressionStart)
        {
            return true;
        }

        var previousChar = _text[previous];
        if (previousChar is '=' or '(' or '[' or '{' or ',' or ':' or '?' or ';')
        {
            return true;
        }

        return previousChar == '>' && previous > 0 && _text[previous - 1] == '=';
    }

    private int PreviousNonWhitespaceIndexWithin(int index, int minIndex)
    {
        for (var i = index; i >= minIndex; i--)
        {
            if (!char.IsWhiteSpace(_text[i]))
            {
                return i;
            }
        }

        return minIndex - 1;
    }

    private char PeekFrom(int index, int offset)
    {
        var target = index + offset;
        return target >= 0 && target < _text.Length ? _text[target] : '\0';
    }

    private bool IsAttributeExpressionRecoveryBoundary(int contentStart)
    {
        if (_pos <= contentStart)
        {
            return false;
        }

        if ((Current == '>' && CharAt(_pos - 1) != '=') || StartsWith("/>"))
        {
            return true;
        }

        if (!char.IsWhiteSpace(Current))
        {
            return false;
        }

        var cursor = _pos;
        while (char.IsWhiteSpace(CharAt(cursor)))
        {
            cursor++;
        }

        if (!IsNameStart(CharAt(cursor)))
        {
            return false;
        }

        cursor++;
        while (IsNamePart(CharAt(cursor)))
        {
            cursor++;
        }

        while (char.IsWhiteSpace(CharAt(cursor)))
        {
            cursor++;
        }

        return (CharAt(cursor) == '=' && CharAt(cursor + 1) is not '=' and not '>')
            || (CharAt(cursor) == '>' && CharAt(cursor - 1) != '=')
            || (CharAt(cursor) == '/' && CharAt(cursor + 1) == '>');
    }

    private bool IsLikelyCSharpStatementStart(int index)
    {
        if (index < 0 || index >= _text.Length || !IsNameStart(_text[index]))
        {
            return false;
        }

        var cursor = index + 1;
        while (IsCSharpIdentifierPart(CharAt(cursor)))
        {
            cursor++;
        }

        var word = _text.Substring(index, cursor - index);
        return word
            is "var"
                or "return"
                or "if"
                or "for"
                or "foreach"
                or "while"
                or "switch"
                or "using"
                or "throw";
    }

    private char CharAt(int index) => index >= 0 && index < _text.Length ? _text[index] : '\0';

    private void SkipLineCommentForParser()
    {
        while (!IsEnd && Current != '\n')
        {
            _pos++;
        }
    }

    private void SkipBlockCommentForParser()
    {
        _pos += 2;
        while (!IsEnd)
        {
            if (StartsWith("*/"))
            {
                _pos += 2;
                return;
            }

            _pos++;
        }
    }

    private void SkipStringLiteralForParser()
    {
        if (StartsWith("\"\"\""))
        {
            _pos += 3;
            while (!IsEnd)
            {
                if (StartsWith("\"\"\""))
                {
                    _pos += 3;
                    return;
                }

                _pos++;
            }

            return;
        }

        var isVerbatim = IsVerbatimStringQuote();
        _pos++;
        while (!IsEnd)
        {
            if (!isVerbatim && Current == '\\')
            {
                _pos = Math.Min(_pos + 2, _text.Length);
                continue;
            }

            if (Current == '"')
            {
                _pos++;
                if (isVerbatim && Current == '"')
                {
                    _pos++;
                    continue;
                }

                return;
            }

            _pos++;
        }
    }

    private void SkipCharLiteralForParser()
    {
        _pos++;
        while (!IsEnd)
        {
            if (Current == '\\')
            {
                _pos = Math.Min(_pos + 2, _text.Length);
                continue;
            }

            if (Current == '\'')
            {
                _pos++;
                return;
            }

            _pos++;
        }
    }

    private string ReadJsxQuotedString()
    {
        var quote = Current;
        _pos++;
        var builder = new StringBuilder();

        while (!IsEnd && Current != quote)
        {
            if (Current == '\\' && _pos + 1 < _text.Length)
            {
                _pos++;
                builder.Append(
                    Current switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        '\'' => '\'',
                        var c => c,
                    }
                );
                _pos++;
                continue;
            }

            builder.Append(Current);
            _pos++;
        }

        if (Current == quote)
        {
            _pos++;
        }
        else
        {
            AddError($"Expected closing quote {quote}.", CurrentSpan());
        }

        return builder.ToString();
    }

    private (string Name, TextSpan Span) ReadTagName()
    {
        if (!IsNameStart(Current))
        {
            var span = CurrentSpan();
            AddError("Expected JSX tag name.", span);
            return ("MissingTag", span);
        }

        var start = _pos;
        _pos++;
        while (IsNamePart(Current))
        {
            _pos++;
        }

        return (_text.Substring(start, _pos - start), TextSpan.FromBounds(start, _pos));
    }

    private (string Name, TextSpan Span) ReadAttributeName()
    {
        if (!IsNameStart(Current))
        {
            var span = CurrentSpan();
            AddError("Expected JSX attribute name.", span);
            if (!IsEnd)
            {
                _pos++;
            }

            return ("missing", span);
        }

        var start = _pos;
        _pos++;
        while (IsNamePart(Current))
        {
            _pos++;
        }

        return (_text.Substring(start, _pos - start), TextSpan.FromBounds(start, _pos));
    }

    private void Expect(char c)
    {
        if (Current == c)
        {
            _pos++;
            return;
        }

        AddError($"Expected '{c}'.", CurrentSpan());
    }

    private bool SkipWhitespaceInJsx()
    {
        var skippedNewline = false;
        while (char.IsWhiteSpace(Current))
        {
            skippedNewline |= Current is '\r' or '\n';
            _pos++;
        }

        return skippedNewline;
    }

    private bool IsVerbatimStringQuote()
    {
        var previous = _pos - 1;
        if (previous >= 0 && _text[previous] == '@')
        {
            return true;
        }

        return previous >= 1 && _text[previous] == '$' && _text[previous - 1] == '@';
    }

    private bool StartsWith(string value) => CsmxCompat.StartsWithAt(_text, _pos, value);

    private char Peek(int offset)
    {
        var index = _pos + offset;
        return index >= 0 && index < _text.Length ? _text[index] : '\0';
    }

    private TextSpan CurrentSpan() => new(CsmxCompat.Clamp(_pos, 0, _text.Length), IsEnd ? 0 : 1);

    private void AddError(string message, TextSpan span) =>
        _diagnostics.Add(new CsmxDiagnostic(message, span));

    private bool IsEnd => _pos >= _text.Length;

    private char Current => _pos < _text.Length ? _text[_pos] : '\0';

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

    private static bool ShouldKeepTextNode(string text, bool hasPreviousChild)
    {
        if (!string.IsNullOrWhiteSpace(NormalizeText(text)))
        {
            return true;
        }

        return hasPreviousChild && !text.Contains('\n') && !text.Contains('\r');
    }

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsNamePart(char c) =>
        c == '_' || c == '-' || c == ':' || c == '.' || char.IsLetterOrDigit(c);

    private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
