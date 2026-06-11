using System.Text;

namespace Csmx.Compiler;

/// <summary>
/// Transforms a mostly-normal C# file that contains JSX-like expression syntax into valid C#.
///
/// MVP rule: only JSX element expressions are transformed. The rest of the file is copied as-is.
/// Examples:
///   return <text>Hello</text>;
///   var node = <button disabled={count == 0}>Click</button>;
/// </summary>
public static class CsmxTransformer
{
    public static CsmxTransformResult Transform(string text, string? sourcePath = null) =>
        Transform(text, sourcePath, CsmxTransformOptions.Default);

    public static CsmxTransformResult Transform(
        string text,
        string? sourcePath,
        CsmxTransformOptions? options
    ) => new Transformer(text, sourcePath, options ?? CsmxTransformOptions.Default).Transform();

    private sealed class Transformer
    {
        private readonly string _text;
        private readonly string? _sourcePath;
        private readonly StringBuilder _builder = new();
        private readonly List<CsmxDiagnostic> _diagnostics = new();
        private readonly List<SourceMapEntry> _mappings = new();
        private readonly CsmxLoweringContext _loweringContext;
        private readonly CsmxIrBuilder _irBuilder;
        private readonly ICsmxLoweringBackend _backend;
        private CsmxTransformOptions _options;
        private int _pos;

        public Transformer(string text, string? sourcePath, CsmxTransformOptions options)
        {
            _text = text ?? string.Empty;
            _sourcePath = sourcePath;
            _options = CsmxSourceOptions.Apply(_text, options);
            _options = _options with
            {
                SourceIdentity = _options.SourceIdentity ?? CreateSourceIdentity(_sourcePath),
            };
            _loweringContext = new CsmxLoweringContext(_options, _builder, _mappings, _diagnostics);
            _irBuilder = new CsmxIrBuilder(
                _options,
                CsmxComponentRegistry.FromSource(_text, _options.ComponentNames)
            );
            _backend = CreateLoweringBackend();
            _loweringContext.SetExpressionEmitter(EmitExpression);
        }

        public CsmxTransformResult Transform()
        {
            if (!string.IsNullOrWhiteSpace(_sourcePath))
            {
                _builder
                    .Append("#line 1 \"")
                    .Append(EscapeLineDirectivePath(_sourcePath!))
                    .Append("\"\n");
            }

            while (!IsEnd)
            {
                if (StartsWith("//"))
                {
                    CopyLineCommentWithMapping();
                    continue;
                }

                if (StartsWith("/*"))
                {
                    CopyBlockCommentWithMapping();
                    continue;
                }

                if (Current == '"')
                {
                    CopyStringLiteralWithMapping();
                    continue;
                }

                if (Current == '\'')
                {
                    CopyCharLiteralWithMapping();
                    continue;
                }

                if (Current == '<' && IsLikelyJsxStart())
                {
                    var parsed = CsmxParser.ParseElement(_text, _pos);
                    _pos = parsed.Position;
                    _diagnostics.AddRange(parsed.Diagnostics);
                    _backend.EmitElement(_irBuilder.Build(parsed.Element));
                    continue;
                }

                CopyCSharpText();
            }

            if (!string.IsNullOrWhiteSpace(_sourcePath))
            {
                if (_builder.Length > 0 && _builder[_builder.Length - 1] != '\n')
                {
                    _builder.Append('\n');
                }

                _builder.Append("#line default\n");
            }

            return new CsmxTransformResult(_builder.ToString(), _diagnostics, _mappings);
        }

        private bool IsLikelyJsxStart()
        {
            if (Current != '<' || Peek(1) == '/' || !IsNameStart(Peek(1)))
            {
                return false;
            }

            var previous = PreviousNonWhitespaceIndex(_pos - 1);
            if (previous < 0)
            {
                return true;
            }

            var previousChar = _text[previous];
            if (previousChar is '=' or '(' or '[' or '{' or ',' or ':' or '?' or ';')
            {
                return true;
            }

            if (previousChar == '>' && previous > 0 && _text[previous - 1] == '=')
            {
                return true; // lambda arrow: x => <text />
            }

            var previousWord = ReadPreviousWord(previous);
            return previousWord is "return" or "throw" or "case" or "yield";
        }

        private bool IsLikelyJsxStartAt(int index, int expressionStart)
        {
            if (
                index < 0
                || index >= _text.Length
                || _text[index] != '<'
                || CharAt(index + 1) == '/'
                || !IsNameStart(CharAt(index + 1))
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

            if (previousChar == '>' && previous > 0 && _text[previous - 1] == '=')
            {
                return true;
            }

            var previousWord = ReadPreviousWord(previous);
            return previousWord is "return" or "throw" or "case" or "yield";
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

        private ICsmxLoweringBackend CreateLoweringBackend() =>
            _options.CompileMode switch
            {
                CsmxCompileMode.Fluent => new CsmxFluentLoweringBackend(_loweringContext),
                CsmxCompileMode.Factory => new CsmxFactoryLoweringBackend(_loweringContext),
                _ => new CsmxFactoryLoweringBackend(_loweringContext),
            };

        private static string CreateSourceIdentity(string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return "<memory>";
            }

            return sourcePath!.Replace('\\', '/');
        }

        private void EmitExpression(string expression, TextSpan expressionSpan, ProjectionKind kind)
        {
            var end = Math.Min(expressionSpan.End, _text.Length);
            var pos = CsmxCompat.Clamp(expressionSpan.Start, 0, end);
            var rawStart = pos;

            while (pos < end)
            {
                if (StartsWithAt(pos, "//"))
                {
                    pos = SkipLineComment(pos, end);
                    continue;
                }

                if (StartsWithAt(pos, "/*"))
                {
                    pos = SkipBlockComment(pos, end);
                    continue;
                }

                if (_text[pos] == '"')
                {
                    pos = SkipStringLiteral(pos, end);
                    continue;
                }

                if (_text[pos] == '\'')
                {
                    pos = SkipCharLiteral(pos, end);
                    continue;
                }

                if (_text[pos] == '<' && IsLikelyJsxStartAt(pos, expressionSpan.Start))
                {
                    AppendExpressionSegment(rawStart, pos, kind);
                    var parsed = CsmxParser.ParseElement(_text, pos);
                    _diagnostics.AddRange(parsed.Diagnostics);

                    if (parsed.Position <= pos)
                    {
                        AppendExpressionSegment(pos, pos + 1, kind);
                        pos++;
                        rawStart = pos;
                        continue;
                    }

                    _backend.EmitElement(_irBuilder.Build(parsed.Element));
                    pos = Math.Min(parsed.Position, end);
                    rawStart = pos;
                    continue;
                }

                pos++;
            }

            AppendExpressionSegment(rawStart, end, kind);
        }

        private void AppendExpressionSegment(int start, int end, ProjectionKind kind)
        {
            if (end <= start)
            {
                return;
            }

            var generatedStart = _builder.Length;
            _builder.Append(_text.Substring(start, end - start));
            _mappings.Add(
                new SourceMapEntry(
                    TextSpan.FromBounds(start, end),
                    TextSpan.FromBounds(generatedStart, _builder.Length),
                    kind
                )
            );
        }

        private void CopyCSharpText()
        {
            var originalStart = _pos;
            var generatedStart = _builder.Length;

            while (!IsEnd)
            {
                if (
                    StartsWith("//")
                    || StartsWith("/*")
                    || Current == '"'
                    || Current == '\''
                    || (Current == '<' && IsLikelyJsxStart())
                )
                {
                    break;
                }

                _pos++;
            }

            if (_pos > originalStart)
            {
                _builder.Append(_text.Substring(originalStart, _pos - originalStart));
                AddCSharpMapping(originalStart, generatedStart);
            }
        }

        private void CopyLineCommentWithMapping()
        {
            var originalStart = _pos;
            var generatedStart = _builder.Length;
            CopyLineComment();
            AddCSharpMapping(originalStart, generatedStart);
        }

        private void CopyLineComment()
        {
            while (!IsEnd)
            {
                var c = Current;
                _builder.Append(c);
                _pos++;
                if (c == '\n')
                {
                    break;
                }
            }
        }

        private void CopyBlockCommentWithMapping()
        {
            var originalStart = _pos;
            var generatedStart = _builder.Length;
            CopyBlockComment();
            AddCSharpMapping(originalStart, generatedStart);
        }

        private void CopyBlockComment()
        {
            _builder.Append("/*");
            _pos += 2;

            while (!IsEnd)
            {
                if (StartsWith("*/"))
                {
                    _builder.Append("*/");
                    _pos += 2;
                    return;
                }

                _builder.Append(Current);
                _pos++;
            }
        }

        private void CopyStringLiteralWithMapping()
        {
            var originalStart = _pos;
            var generatedStart = _builder.Length;
            CopyStringLiteral();
            AddCSharpMapping(originalStart, generatedStart);
        }

        private void CopyStringLiteral()
        {
            if (StartsWith("\"\"\""))
            {
                CopyRawStringLiteral();
                return;
            }

            var isVerbatim = IsVerbatimStringQuote();
            _builder.Append(Current);
            _pos++;

            while (!IsEnd)
            {
                if (!isVerbatim && Current == '\\')
                {
                    _builder.Append(Current);
                    _pos++;
                    if (!IsEnd)
                    {
                        _builder.Append(Current);
                        _pos++;
                    }

                    continue;
                }

                if (Current == '"')
                {
                    _builder.Append(Current);
                    _pos++;

                    if (isVerbatim && Current == '"')
                    {
                        _builder.Append(Current);
                        _pos++;
                        continue;
                    }

                    return;
                }

                _builder.Append(Current);
                _pos++;
            }
        }

        private void CopyRawStringLiteral()
        {
            _builder.Append("\"\"\"");
            _pos += 3;

            while (!IsEnd)
            {
                if (StartsWith("\"\"\""))
                {
                    _builder.Append("\"\"\"");
                    _pos += 3;
                    return;
                }

                _builder.Append(Current);
                _pos++;
            }
        }

        private void CopyCharLiteralWithMapping()
        {
            var originalStart = _pos;
            var generatedStart = _builder.Length;
            CopyCharLiteral();
            AddCSharpMapping(originalStart, generatedStart);
        }

        private void CopyCharLiteral()
        {
            _builder.Append(Current);
            _pos++;

            while (!IsEnd)
            {
                if (Current == '\\')
                {
                    _builder.Append(Current);
                    _pos++;
                    if (!IsEnd)
                    {
                        _builder.Append(Current);
                        _pos++;
                    }

                    continue;
                }

                _builder.Append(Current);
                var c = Current;
                _pos++;
                if (c == '\'')
                {
                    return;
                }
            }
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

        private bool IsVerbatimStringQuoteAt(int quoteIndex)
        {
            var previous = quoteIndex - 1;
            if (previous >= 0 && _text[previous] == '@')
            {
                return true;
            }

            return previous >= 1 && _text[previous] == '$' && _text[previous - 1] == '@';
        }

        private int SkipLineComment(int pos, int end)
        {
            while (pos < end && _text[pos] != '\n')
            {
                pos++;
            }

            return pos;
        }

        private int SkipBlockComment(int pos, int end)
        {
            pos += 2;
            while (pos < end)
            {
                if (StartsWithAt(pos, "*/"))
                {
                    return Math.Min(pos + 2, end);
                }

                pos++;
            }

            return pos;
        }

        private int SkipStringLiteral(int pos, int end)
        {
            if (StartsWithAt(pos, "\"\"\""))
            {
                pos += 3;
                while (pos < end)
                {
                    if (StartsWithAt(pos, "\"\"\""))
                    {
                        return Math.Min(pos + 3, end);
                    }

                    pos++;
                }

                return pos;
            }

            var isVerbatim = IsVerbatimStringQuoteAt(pos);
            pos++;
            while (pos < end)
            {
                if (!isVerbatim && _text[pos] == '\\')
                {
                    pos = Math.Min(pos + 2, end);
                    continue;
                }

                if (_text[pos] == '"')
                {
                    pos++;
                    if (isVerbatim && pos < end && _text[pos] == '"')
                    {
                        pos++;
                        continue;
                    }

                    return pos;
                }

                pos++;
            }

            return pos;
        }

        private int SkipCharLiteral(int pos, int end)
        {
            pos++;
            while (pos < end)
            {
                if (_text[pos] == '\\')
                {
                    pos = Math.Min(pos + 2, end);
                    continue;
                }

                if (_text[pos] == '\'')
                {
                    return Math.Min(pos + 1, end);
                }

                pos++;
            }

            return pos;
        }

        private void AddCSharpMapping(int originalStart, int generatedStart)
        {
            if (_pos <= originalStart || _builder.Length <= generatedStart)
            {
                return;
            }

            _mappings.Add(
                new SourceMapEntry(
                    new TextSpan(originalStart, _pos - originalStart),
                    TextSpan.FromBounds(generatedStart, _builder.Length),
                    ProjectionKind.CSharp
                )
            );
        }

        private bool StartsWith(string value) => CsmxCompat.StartsWithAt(_text, _pos, value);

        private bool StartsWithAt(int index, string value) =>
            index >= 0 && index <= _text.Length && CsmxCompat.StartsWithAt(_text, index, value);

        private char Peek(int offset)
        {
            var index = _pos + offset;
            return index >= 0 && index < _text.Length ? _text[index] : '\0';
        }

        private char CharAt(int index) => index >= 0 && index < _text.Length ? _text[index] : '\0';

        private bool IsEnd => _pos >= _text.Length;

        private char Current => _pos < _text.Length ? _text[_pos] : '\0';

        private int PreviousNonWhitespaceIndex(int index)
        {
            for (var i = index; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(_text[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private string ReadPreviousWord(int endIndex)
        {
            var end = endIndex + 1;
            var start = endIndex;
            while (start >= 0 && IsCSharpIdentifierPart(_text[start]))
            {
                start--;
            }

            start++;
            return start < end ? _text.Substring(start, end - start) : string.Empty;
        }

        private static string EscapeLineDirectivePath(string path) =>
            CsmxCompat.ReplaceOrdinal(CsmxCompat.ReplaceOrdinal(path, "\\", "/"), "\"", "\\\"");

        private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

        private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
