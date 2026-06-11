using Csmx.Compiler;

namespace Csmx.LanguageServer;

internal sealed partial class LspServer
{
    private static readonly string[] SemanticTokenTypes =
    [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method",
        "macro",
        "keyword",
        "modifier",
        "comment",
        "string",
        "number",
        "regexp",
        "operator",
        "decorator",
    ];

    private static readonly Dictionary<string, int> SemanticTokenTypeIndexes = SemanticTokenTypes
        .Select((tokenType, index) => (tokenType, index))
        .ToDictionary(item => item.tokenType, item => item.index, StringComparer.Ordinal);

    private static int[] BuildSemanticTokenData(
        string text,
        LineMap lineMap,
        IReadOnlyList<RoslynSemanticToken>? roslynTokens = null
    )
    {
        var tokens = new List<SemanticToken>();
        var scanner = new SemanticScanner(text, lineMap, tokens);
        scanner.Scan();
        AddRoslynTokens(tokens, lineMap, roslynTokens);

        var data = new List<int>(tokens.Count * 5);
        var previousLine = 0;
        var previousCharacter = 0;

        foreach (var token in tokens.OrderBy(t => t.Line).ThenBy(t => t.Character))
        {
            if (token.Length <= 0)
            {
                continue;
            }

            var deltaLine = token.Line - previousLine;
            var deltaStart = deltaLine == 0 ? token.Character - previousCharacter : token.Character;

            if (deltaLine < 0 || deltaStart < 0)
            {
                continue;
            }

            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Length);
            data.Add(token.TypeIndex);
            data.Add(0);

            previousLine = token.Line;
            previousCharacter = token.Character;
        }

        return data.ToArray();
    }

    private static void AddRoslynTokens(
        List<SemanticToken> tokens,
        LineMap lineMap,
        IReadOnlyList<RoslynSemanticToken>? roslynTokens
    )
    {
        if (roslynTokens is null || roslynTokens.Count == 0)
        {
            return;
        }

        foreach (var token in roslynTokens)
        {
            if (
                token.Length <= 0
                || !SemanticTokenTypeIndexes.TryGetValue(token.TokenType, out var tokenTypeIndex)
            )
            {
                continue;
            }

            var start = lineMap.GetLinePosition(token.Start);
            var end = lineMap.GetLinePosition(token.Start + token.Length);
            if (start.Line != end.Line || end.Character <= start.Character)
            {
                continue;
            }

            var semanticToken = new SemanticToken(
                start.Line,
                start.Character,
                end.Character - start.Character,
                tokenTypeIndex
            );
            tokens.RemoveAll(existing => TokensOverlap(existing, semanticToken));
            tokens.Add(semanticToken);
        }
    }

    private static bool TokensOverlap(SemanticToken left, SemanticToken right)
    {
        if (left.Line != right.Line)
        {
            return false;
        }

        var leftEnd = left.Character + left.Length;
        var rightEnd = right.Character + right.Length;
        return left.Character < rightEnd && right.Character < leftEnd;
    }

    private readonly record struct SemanticToken(
        int Line,
        int Character,
        int Length,
        int TypeIndex
    );

    private sealed class SemanticScanner
    {
        private readonly string _text;
        private readonly LineMap _lineMap;
        private readonly List<SemanticToken> _tokens;
        private int _pos;

        public SemanticScanner(string text, LineMap lineMap, List<SemanticToken> tokens)
        {
            _text = text;
            _lineMap = lineMap;
            _tokens = tokens;
        }

        public void Scan()
        {
            while (!IsEnd)
            {
                if (char.IsWhiteSpace(Current))
                {
                    _pos++;
                    continue;
                }

                if (StartsWith("//"))
                {
                    ScanLineComment();
                    continue;
                }

                if (StartsWith("/*"))
                {
                    ScanBlockComment();
                    continue;
                }

                if (Current == '<' && IsLikelyJsxStart(_text, _pos))
                {
                    ScanJsxElementFacts();
                    continue;
                }

                ScanCSharpToken();
            }
        }

        private void ScanCSharpToken()
        {
            if (IsInterpolatedStringStart())
            {
                ScanInterpolatedString();
                return;
            }

            if (Current == '"' || Current == '\'' || StartsWith("\"\"\""))
            {
                ScanStringLike();
                return;
            }

            if (char.IsDigit(Current))
            {
                var start = _pos;
                _pos++;
                while (IsIdentifierPart(Current) || Current == '.')
                {
                    _pos++;
                }

                AddToken(start, _pos, "number");
                return;
            }

            if (IsIdentifierStart(Current))
            {
                var start = _pos;
                _pos++;
                while (IsIdentifierPart(Current))
                {
                    _pos++;
                }

                var word = _text.AsSpan(start, _pos - start);
                var tokenType = GetIdentifierTokenType(word, start);
                if (tokenType is not null)
                {
                    AddToken(start, _pos, tokenType);
                }
                return;
            }

            var compoundOperator = ReadCompoundOperator();
            if (compoundOperator > 0)
            {
                AddToken(_pos, _pos + compoundOperator, "operator");
                _pos += compoundOperator;
                return;
            }

            _pos++;
        }

        private void ScanJsxElementFacts()
        {
            var start = _pos;
            var facts = CsmxFacts.ParseElement(_text, start, CsmxTransformOptions.Default);
            AddJsxFactTokens(facts.Element);
            _pos = Math.Max(facts.Position, start + 1);
        }

        private void AddJsxFactTokens(CsmxElementFact element)
        {
            AddToken(element.NameSpan.Start, element.NameSpan.End, "class");
            AddClosingTagToken(element);

            foreach (var attribute in element.Attributes)
            {
                AddToken(attribute.NameSpan.Start, attribute.NameSpan.End, "property");
                if (attribute.ExpressionSpan is { } expressionSpan)
                {
                    ScanCSharpRange(expressionSpan);
                }
            }

            foreach (var child in element.Children)
            {
                switch (child)
                {
                    case CsmxElementFact childElement:
                        AddJsxFactTokens(childElement);
                        break;

                    case CsmxExpressionFact expression:
                        ScanExpressionFact(expression);
                        break;
                }
            }
        }

        private void ScanExpressionFact(CsmxExpressionFact expression)
        {
            if (!expression.ContainsNestedJsx)
            {
                ScanCSharpRange(expression.ExpressionSpan);
                return;
            }

            var cursor = expression.ExpressionSpan.Start;
            foreach (
                var parsed in CsmxSourceFacts.ParseElementsInExpression(
                    _text,
                    expression.ExpressionSpan,
                    CsmxTransformOptions.Default
                )
            )
            {
                if (parsed.Element.Span.Start > cursor)
                {
                    ScanCSharpRange(TextSpan.FromBounds(cursor, parsed.Element.Span.Start));
                }

                AddJsxFactTokens(parsed.Element);
                cursor = Math.Max(cursor, parsed.Position);
            }

            if (cursor < expression.ExpressionSpan.End)
            {
                ScanCSharpRange(TextSpan.FromBounds(cursor, expression.ExpressionSpan.End));
            }
        }

        private void AddClosingTagToken(CsmxElementFact element)
        {
            var searchStart = Math.Max(element.NameSpan.End, element.Span.Start);
            var searchLength = Math.Max(0, element.Span.End - searchStart);
            var relative = LastIndexOfClosingTag(
                _text.AsSpan(searchStart, searchLength),
                element.Name.AsSpan()
            );
            if (relative < 0)
            {
                return;
            }

            var closeNameStart = searchStart + relative + 2;
            var closeNameEnd = closeNameStart + element.Name.Length;
            AddToken(closeNameStart, closeNameEnd, "class");
        }

        private static int LastIndexOfClosingTag(ReadOnlySpan<char> text, ReadOnlySpan<char> name)
        {
            for (var i = text.Length - name.Length - 2; i >= 0; i--)
            {
                if (
                    text[i] == '<'
                    && text[i + 1] == '/'
                    && text.Slice(i + 2, name.Length).SequenceEqual(name)
                )
                {
                    return i;
                }
            }

            return -1;
        }

        private void ScanCSharpRange(TextSpan span)
        {
            var saved = _pos;
            _pos = Math.Clamp(span.Start, 0, _text.Length);
            var end = Math.Clamp(span.End, _pos, _text.Length);

            while (_pos < end && !IsEnd)
            {
                if (char.IsWhiteSpace(Current))
                {
                    _pos++;
                    continue;
                }

                if (StartsWith("//"))
                {
                    ScanLineComment();
                    continue;
                }

                if (StartsWith("/*"))
                {
                    ScanBlockComment();
                    continue;
                }

                ScanCSharpToken();
            }

            _pos = saved;
        }

        private void ScanLineComment()
        {
            var start = _pos;
            while (!IsEnd && Current != '\r' && Current != '\n')
            {
                _pos++;
            }

            AddToken(start, _pos, "comment");
        }

        private void ScanBlockComment()
        {
            var start = _pos;
            _pos += 2;
            while (!IsEnd && !StartsWith("*/"))
            {
                _pos++;
            }

            if (StartsWith("*/"))
            {
                _pos += 2;
            }

            AddToken(start, _pos, "comment");
        }

        private void ScanStringLike()
        {
            var start = _pos;
            if (StartsWith("\"\"\""))
            {
                _pos += 3;
                while (!IsEnd && !StartsWith("\"\"\""))
                {
                    _pos++;
                }

                if (StartsWith("\"\"\""))
                {
                    _pos += 3;
                }

                AddToken(start, _pos, "string");
                return;
            }

            var quote = Current;
            _pos++;
            while (!IsEnd)
            {
                if (Current == '\\')
                {
                    _pos = Math.Min(_pos + 2, _text.Length);
                    continue;
                }

                var c = Current;
                _pos++;
                if (c == quote)
                {
                    break;
                }
            }

            AddToken(start, _pos, "string");
        }

        private bool IsInterpolatedStringStart()
        {
            if (Current == '$')
            {
                var cursor = _pos;
                while (cursor < _text.Length && _text[cursor] == '$')
                {
                    cursor++;
                }

                return cursor < _text.Length
                    && (
                        _text[cursor] == '"'
                        || (
                            _text[cursor] == '@'
                            && cursor + 1 < _text.Length
                            && _text[cursor + 1] == '"'
                        )
                    );
            }

            return Current == '@' && Peek(1) == '$' && Peek(2) == '"';
        }

        private void ScanInterpolatedString()
        {
            var start = _pos;
            var prefix = ReadInterpolatedStringPrefix();
            if (prefix is null)
            {
                _pos++;
                return;
            }

            if (prefix.Value.RawQuoteCount >= 3)
            {
                ScanRawInterpolatedString(start, prefix.Value);
                return;
            }

            var quote = prefix.Value.QuoteStart;
            if (quote < 0)
            {
                _pos++;
                return;
            }

            var verbatim = prefix.Value.Verbatim;
            _pos = quote + 1;
            var literalStart = start;

            while (!IsEnd)
            {
                if (!verbatim && Current == '\\')
                {
                    _pos = Math.Min(_pos + 2, _text.Length);
                    continue;
                }

                if (verbatim && Current == '"' && Peek(1) == '"')
                {
                    _pos += 2;
                    continue;
                }

                if (Current == '{' && Peek(1) == '{')
                {
                    _pos += 2;
                    continue;
                }

                if (Current == '}' && Peek(1) == '}')
                {
                    _pos += 2;
                    continue;
                }

                if (Current == '{')
                {
                    var expressionStart = _pos + 1;
                    var expressionEnd = FindInterpolationExpressionEnd(expressionStart);
                    if (expressionEnd < 0)
                    {
                        break;
                    }

                    AddToken(literalStart, _pos, "string");
                    ScanInterpolationExpression(expressionStart, expressionEnd);
                    _pos = expressionEnd + 1;
                    literalStart = _pos;
                    continue;
                }

                var current = Current;
                _pos++;
                if (current == '"')
                {
                    AddToken(literalStart, _pos, "string");
                    return;
                }
            }

            AddToken(literalStart, _pos, "string");
        }

        private readonly record struct InterpolatedStringPrefix(
            int DollarCount,
            int QuoteStart,
            int RawQuoteCount,
            bool Verbatim
        );

        private InterpolatedStringPrefix? ReadInterpolatedStringPrefix()
        {
            var cursor = _pos;
            var dollarCount = 0;
            var verbatim = false;

            if (cursor < _text.Length && _text[cursor] == '@')
            {
                verbatim = true;
                cursor++;
                while (cursor < _text.Length && _text[cursor] == '$')
                {
                    dollarCount++;
                    cursor++;
                }
            }
            else
            {
                while (cursor < _text.Length && _text[cursor] == '$')
                {
                    dollarCount++;
                    cursor++;
                }

                if (cursor < _text.Length && _text[cursor] == '@')
                {
                    verbatim = true;
                    cursor++;
                }
            }

            if (dollarCount == 0 || cursor >= _text.Length || _text[cursor] != '"')
            {
                return null;
            }

            var quoteCount = CountRun(cursor, '"');
            return new InterpolatedStringPrefix(dollarCount, cursor, quoteCount, verbatim);
        }

        private void ScanRawInterpolatedString(int start, InterpolatedStringPrefix prefix)
        {
            _pos = prefix.QuoteStart + prefix.RawQuoteCount;
            var literalStart = start;

            while (!IsEnd)
            {
                if (IsRawStringDelimiter(prefix.RawQuoteCount))
                {
                    _pos += prefix.RawQuoteCount;
                    AddToken(literalStart, _pos, "string");
                    return;
                }

                if (IsRawInterpolationStart(prefix.DollarCount))
                {
                    var expressionStart = _pos + prefix.DollarCount;
                    var expressionEnd = FindRawInterpolationExpressionEnd(
                        expressionStart,
                        prefix.DollarCount
                    );
                    if (expressionEnd < 0)
                    {
                        break;
                    }

                    AddToken(literalStart, _pos, "string");
                    ScanInterpolationExpression(expressionStart, expressionEnd);
                    _pos = expressionEnd + prefix.DollarCount;
                    literalStart = _pos;
                    continue;
                }

                _pos++;
            }

            AddToken(literalStart, _pos, "string");
        }

        private void ScanInterpolationExpression(int expressionStart, int expressionEnd)
        {
            var contentEnd = FindInterpolationExpressionContentEnd(expressionStart, expressionEnd);
            if (contentEnd > expressionStart)
            {
                ScanCSharpRange(new TextSpan(expressionStart, contentEnd - expressionStart));
            }

            if (contentEnd < expressionEnd)
            {
                AddToken(contentEnd, expressionEnd, "string");
            }
        }

        private int FindInterpolationExpressionEnd(int start)
        {
            var depth = 0;
            for (var index = start; index < _text.Length; index++)
            {
                if (_text.AsSpan(index).StartsWith("//".AsSpan(), StringComparison.Ordinal))
                {
                    while (index < _text.Length && _text[index] != '\r' && _text[index] != '\n')
                    {
                        index++;
                    }
                    continue;
                }

                if (_text.AsSpan(index).StartsWith("/*".AsSpan(), StringComparison.Ordinal))
                {
                    index += 2;
                    while (
                        index < _text.Length
                        && !_text.AsSpan(index).StartsWith("*/".AsSpan(), StringComparison.Ordinal)
                    )
                    {
                        index++;
                    }
                    if (index < _text.Length)
                    {
                        index++;
                    }
                    continue;
                }

                if (_text[index] == '"' || _text[index] == '\'')
                {
                    index = SkipQuotedString(index);
                    continue;
                }

                if (_text[index] == '{')
                {
                    depth++;
                    continue;
                }

                if (_text[index] != '}')
                {
                    continue;
                }

                if (depth == 0)
                {
                    return index;
                }

                depth--;
            }

            return -1;
        }

        private int FindRawInterpolationExpressionEnd(int start, int dollarCount)
        {
            var depth = 0;
            for (var index = start; index < _text.Length; index++)
            {
                if (_text.AsSpan(index).StartsWith("//".AsSpan(), StringComparison.Ordinal))
                {
                    while (index < _text.Length && _text[index] != '\r' && _text[index] != '\n')
                    {
                        index++;
                    }
                    continue;
                }

                if (_text.AsSpan(index).StartsWith("/*".AsSpan(), StringComparison.Ordinal))
                {
                    index += 2;
                    while (
                        index < _text.Length
                        && !_text.AsSpan(index).StartsWith("*/".AsSpan(), StringComparison.Ordinal)
                    )
                    {
                        index++;
                    }
                    if (index < _text.Length)
                    {
                        index++;
                    }
                    continue;
                }

                if (_text[index] == '"' || _text[index] == '\'')
                {
                    index = SkipQuotedString(index);
                    continue;
                }

                if (_text[index] == '{')
                {
                    depth++;
                    continue;
                }

                if (_text[index] != '}')
                {
                    continue;
                }

                var closeBraceCount = CountRun(index, '}');
                if (depth == 0 && closeBraceCount >= dollarCount)
                {
                    return index;
                }

                if (depth > 0)
                {
                    depth--;
                }
            }

            return -1;
        }

        private int FindInterpolationExpressionContentEnd(int start, int end)
        {
            var depth = 0;
            for (var index = start; index < end; index++)
            {
                if (_text[index] == '"' || _text[index] == '\'')
                {
                    index = Math.Min(SkipQuotedString(index), end - 1);
                    continue;
                }

                if (_text[index] is '(' or '[' or '{')
                {
                    depth++;
                    continue;
                }

                if (_text[index] is ')' or ']' or '}')
                {
                    depth = Math.Max(0, depth - 1);
                    continue;
                }

                if (depth == 0 && _text[index] is ':' or ',')
                {
                    return index;
                }
            }

            return end;
        }

        private bool IsRawStringDelimiter(int quoteCount)
        {
            if (_pos + quoteCount > _text.Length)
            {
                return false;
            }

            for (var index = 0; index < quoteCount; index++)
            {
                if (_text[_pos + index] != '"')
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsRawInterpolationStart(int dollarCount)
        {
            if (_pos + dollarCount > _text.Length)
            {
                return false;
            }

            for (var index = 0; index < dollarCount; index++)
            {
                if (_text[_pos + index] != '{')
                {
                    return false;
                }
            }

            return _pos + dollarCount >= _text.Length || _text[_pos + dollarCount] != '{';
        }

        private int CountRun(int start, char value)
        {
            var count = 0;
            while (start + count < _text.Length && _text[start + count] == value)
            {
                count++;
            }

            return count;
        }

        private int SkipQuotedString(int start)
        {
            var quote = _text[start];
            var verbatim = quote == '"' && start > 0 && _text[start - 1] == '@';
            for (var index = start + 1; index < _text.Length; index++)
            {
                if (
                    verbatim
                    && _text[index] == '"'
                    && index + 1 < _text.Length
                    && _text[index + 1] == '"'
                )
                {
                    index++;
                    continue;
                }

                if (!verbatim && _text[index] == '\\')
                {
                    index++;
                    continue;
                }

                if (_text[index] == quote)
                {
                    return index;
                }
            }

            return _text.Length - 1;
        }

        private string? GetIdentifierTokenType(ReadOnlySpan<char> word, int start)
        {
            if (IsCSharpKeyword(word))
            {
                return IsCSharpModifier(word) ? "modifier" : "keyword";
            }

            var previous = PreviousNonWhitespace(start - 1);
            var next = NextNonWhitespace(_pos);
            if (TryGetUsingStaticTerminalIdentifierTokenType(start, out var usingStaticTokenType))
            {
                return usingStaticTokenType;
            }

            if (IsUsingDirectiveIdentifier(start) || IsNamespaceDeclarationIdentifier(start))
            {
                return null;
            }

            if (previous >= 0 && (_text[previous] == '.' || _text[previous] == ':'))
            {
                return next < _text.Length && _text[next] == '(' ? "method" : "property";
            }

            var previousWord = PreviousWord(start);
            if (previousWord is "class" or "record")
            {
                return "class";
            }

            if (previousWord == "struct")
            {
                return "struct";
            }

            if (previousWord == "interface")
            {
                return "interface";
            }

            if (previousWord == "enum")
            {
                return "enum";
            }

            if (next < _text.Length && _text[next] == '(')
            {
                return "method";
            }

            if (next < _text.Length && (_text[next] == '.' || _text[next] == ':'))
            {
                return char.IsUpper(word[0]) ? "type" : "variable";
            }

            if (next < _text.Length && (_text[next] == '{' || _text[next] == '='))
            {
                return null;
            }

            if (char.IsUpper(word[0]))
            {
                return "type";
            }

            return "variable";
        }

        private bool TryGetUsingStaticTerminalIdentifierTokenType(int start, out string? tokenType)
        {
            tokenType = null;

            var directiveStart = FindDirectiveStart(start);
            var cursor = SkipWhitespace(directiveStart);
            if (
                !TryReadWord(cursor, out var usingStart, out var usingEnd)
                || !SpanEquals(usingStart, usingEnd, "using")
            )
            {
                return false;
            }

            cursor = SkipWhitespace(usingEnd);
            if (
                !TryReadWord(cursor, out var staticStart, out var staticEnd)
                || !SpanEquals(staticStart, staticEnd, "static")
            )
            {
                return false;
            }

            cursor = SkipWhitespace(staticEnd);
            if (start < cursor || !TryReadWord(start, out _, out var wordEnd))
            {
                return false;
            }

            var semicolon = _text.IndexOf(';', cursor);
            if (semicolon >= 0 && start >= semicolon)
            {
                return false;
            }

            var next = NextNonWhitespace(wordEnd);
            if (next < _text.Length && _text[next] == '.')
            {
                return false;
            }

            if (semicolon >= 0 && next > semicolon)
            {
                return false;
            }

            tokenType = "class";
            return true;
        }

        private bool IsUsingDirectiveIdentifier(int start)
        {
            var directiveStart = FindDirectiveStart(start);
            var cursor = SkipWhitespace(directiveStart);
            if (!TryReadWord(cursor, out var wordStart, out var wordEnd))
            {
                return false;
            }

            if (!SpanEquals(wordStart, wordEnd, "using"))
            {
                return false;
            }

            cursor = SkipWhitespace(wordEnd);
            if (
                cursor < start
                && TryReadWord(cursor, out var modifierStart, out var modifierEnd)
                && SpanEquals(modifierStart, modifierEnd, "static")
            )
            {
                cursor = SkipWhitespace(modifierEnd);
            }

            if (start < cursor)
            {
                return false;
            }

            var semicolon = _text.IndexOf(';', cursor);
            return semicolon < 0 || start < semicolon;
        }

        private bool IsNamespaceDeclarationIdentifier(int start)
        {
            var declarationStart = FindDeclarationStart(start);
            var cursor = SkipWhitespace(declarationStart);
            if (
                !TryReadWord(cursor, out var namespaceStart, out var namespaceEnd)
                || !SpanEquals(namespaceStart, namespaceEnd, "namespace")
            )
            {
                return false;
            }

            cursor = SkipWhitespace(namespaceEnd);
            if (start < cursor)
            {
                return false;
            }

            var boundary = FindNamespaceDeclarationBoundary(cursor);
            return boundary < 0 || start < boundary;
        }

        private int FindDeclarationStart(int start)
        {
            var cursor = start - 1;
            while (cursor >= 0)
            {
                if (_text[cursor] is ';' or '{' or '}')
                {
                    return cursor + 1;
                }

                cursor--;
            }

            return 0;
        }

        private int FindNamespaceDeclarationBoundary(int start)
        {
            for (var cursor = start; cursor < _text.Length; cursor++)
            {
                if (_text[cursor] is ';' or '{')
                {
                    return cursor;
                }

                if (_text[cursor] is '\r' or '\n')
                {
                    return cursor;
                }
            }

            return -1;
        }

        private int FindDirectiveStart(int start)
        {
            var cursor = start - 1;
            while (cursor >= 0)
            {
                if (_text[cursor] is ';' or '{' or '}')
                {
                    return cursor + 1;
                }

                if (_text[cursor] is '\n' or '\r')
                {
                    return cursor + 1;
                }

                cursor--;
            }

            return 0;
        }

        private int SkipWhitespace(int start)
        {
            var cursor = start;
            while (cursor < _text.Length && char.IsWhiteSpace(_text[cursor]))
            {
                cursor++;
            }

            return cursor;
        }

        private bool TryReadWord(int start, out int wordStart, out int wordEnd)
        {
            wordStart = start;
            wordEnd = start;
            if (start >= _text.Length || !IsIdentifierStart(_text[start]))
            {
                return false;
            }

            wordEnd++;
            while (wordEnd < _text.Length && IsIdentifierPart(_text[wordEnd]))
            {
                wordEnd++;
            }

            return true;
        }

        private bool SpanEquals(int start, int end, string value) =>
            end - start == value.Length
            && _text.AsSpan(start, value.Length).Equals(value.AsSpan(), StringComparison.Ordinal);

        private ReadOnlySpan<char> PreviousWord(int before)
        {
            var i = before - 1;
            while (i >= 0 && char.IsWhiteSpace(_text[i]))
            {
                i--;
            }

            while (
                i >= 0 && (_text[i] == '.' || _text[i] == '<' || _text[i] == '>' || _text[i] == ',')
            )
            {
                i--;
                while (i >= 0 && char.IsWhiteSpace(_text[i]))
                {
                    i--;
                }
            }

            var end = i + 1;
            while (i >= 0 && IsIdentifierPart(_text[i]))
            {
                i--;
            }

            var start = i + 1;
            return start < end ? _text.AsSpan(start, end - start) : ReadOnlySpan<char>.Empty;
        }

        private static bool IsCSharpKeyword(ReadOnlySpan<char> word) =>
            word
                is "abstract"
                    or "as"
                    or "base"
                    or "bool"
                    or "break"
                    or "byte"
                    or "case"
                    or "catch"
                    or "char"
                    or "checked"
                    or "class"
                    or "const"
                    or "continue"
                    or "decimal"
                    or "default"
                    or "delegate"
                    or "do"
                    or "double"
                    or "else"
                    or "enum"
                    or "event"
                    or "explicit"
                    or "extern"
                    or "false"
                    or "finally"
                    or "fixed"
                    or "float"
                    or "for"
                    or "foreach"
                    or "goto"
                    or "if"
                    or "implicit"
                    or "in"
                    or "int"
                    or "interface"
                    or "internal"
                    or "is"
                    or "lock"
                    or "long"
                    or "namespace"
                    or "new"
                    or "null"
                    or "object"
                    or "operator"
                    or "out"
                    or "override"
                    or "params"
                    or "private"
                    or "protected"
                    or "public"
                    or "readonly"
                    or "record"
                    or "ref"
                    or "return"
                    or "sbyte"
                    or "sealed"
                    or "short"
                    or "sizeof"
                    or "stackalloc"
                    or "static"
                    or "string"
                    or "struct"
                    or "switch"
                    or "this"
                    or "throw"
                    or "true"
                    or "try"
                    or "typeof"
                    or "uint"
                    or "ulong"
                    or "unchecked"
                    or "unsafe"
                    or "ushort"
                    or "using"
                    or "var"
                    or "virtual"
                    or "void"
                    or "volatile"
                    or "while"
                    or "yield";

        private static bool IsCSharpModifier(ReadOnlySpan<char> word) =>
            word
                is "abstract"
                    or "async"
                    or "const"
                    or "extern"
                    or "internal"
                    or "new"
                    or "override"
                    or "private"
                    or "protected"
                    or "public"
                    or "readonly"
                    or "required"
                    or "sealed"
                    or "static"
                    or "unsafe"
                    or "virtual"
                    or "volatile";

        private int NextNonWhitespace(int start)
        {
            var i = start;
            while (i < _text.Length && char.IsWhiteSpace(_text[i]))
            {
                i++;
            }

            return i;
        }

        private int PreviousNonWhitespace(int start)
        {
            var i = start;
            while (i >= 0 && char.IsWhiteSpace(_text[i]))
            {
                i--;
            }

            return i;
        }

        private void AddToken(int start, int end, string tokenType)
        {
            if (
                end <= start
                || !SemanticTokenTypeIndexes.TryGetValue(tokenType, out var tokenTypeIndex)
            )
            {
                return;
            }

            var startPosition = _lineMap.GetLinePosition(start);
            var endPosition = _lineMap.GetLinePosition(end);

            if (startPosition.Line != endPosition.Line)
            {
                AddMultilineToken(start, end, tokenTypeIndex);
                return;
            }

            _tokens.Add(
                new SemanticToken(
                    startPosition.Line,
                    startPosition.Character,
                    endPosition.Character - startPosition.Character,
                    tokenTypeIndex
                )
            );
        }

        private void AddMultilineToken(int start, int end, int tokenTypeIndex)
        {
            var index = start;
            while (index < end)
            {
                var position = _lineMap.GetLinePosition(index);
                var lineEnd = index;
                while (
                    lineEnd < end
                    && lineEnd < _text.Length
                    && _text[lineEnd] != '\r'
                    && _text[lineEnd] != '\n'
                )
                {
                    lineEnd++;
                }

                if (lineEnd > index)
                {
                    _tokens.Add(
                        new SemanticToken(
                            position.Line,
                            position.Character,
                            lineEnd - index,
                            tokenTypeIndex
                        )
                    );
                }

                index = lineEnd;
                while (
                    index < end
                    && index < _text.Length
                    && (_text[index] == '\r' || _text[index] == '\n')
                )
                {
                    index++;
                }
            }
        }

        private bool StartsWith(string value) =>
            _text.AsSpan(_pos).StartsWith(value.AsSpan(), StringComparison.Ordinal);

        private int ReadCompoundOperator()
        {
            if (_pos + 1 >= _text.Length)
            {
                return 0;
            }

            var two = _text.AsSpan(_pos, 2);
            return
                two
                    is "=>"
                        or "??"
                        or "?."
                        or "=="
                        or "!="
                        or "<="
                        or ">="
                        or "&&"
                        or "||"
                        or "++"
                        or "--"
                ? 2
                : 0;
        }

        private bool IsEnd => _pos >= _text.Length;

        private char Current => _pos < _text.Length ? _text[_pos] : '\0';

        private char Peek(int offset)
        {
            var index = _pos + offset;
            return index < _text.Length ? _text[index] : '\0';
        }

        private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
