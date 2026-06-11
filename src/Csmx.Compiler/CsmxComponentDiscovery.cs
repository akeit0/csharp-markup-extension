namespace Csmx.Compiler;

internal static class CsmxComponentDiscovery
{
    public static IEnumerable<CsmxComponentDescriptor> Discover(string text)
    {
        text ??= string.Empty;
        var index = 0;

        while (index < text.Length)
        {
            if (TrySkipTriviaOrLiteral(text, index, out var skipped))
            {
                index = skipped;
                continue;
            }

            if (!IsNameStart(text[index]))
            {
                index++;
                continue;
            }

            if (!TryReadQualifiedName(text, index, out var typeName, out var afterTypeName))
            {
                index++;
                continue;
            }

            if (IsFuncTypeName(typeName))
            {
                if (
                    TryDiscoverFuncComponent(
                        text,
                        afterTypeName,
                        out var descriptor,
                        out var afterComponentName
                    )
                )
                {
                    yield return descriptor;
                    index = afterComponentName;
                    continue;
                }

                index = afterTypeName;
                continue;
            }

            if (
                TryDiscoverMethodComponent(
                    text,
                    afterTypeName,
                    out var methodDescriptor,
                    out var afterMethodName
                )
            )
            {
                yield return methodDescriptor;
                index = afterMethodName;
                continue;
            }

            index = afterTypeName;
        }
    }

    private static bool TryDiscoverFuncComponent(
        string text,
        int afterTypeName,
        out CsmxComponentDescriptor descriptor,
        out int end
    )
    {
        descriptor = default!;
        end = afterTypeName;

        var genericStart = SkipWhitespace(text, afterTypeName);
        if (
            genericStart >= text.Length
            || text[genericStart] != '<'
            || !TryReadGenericArguments(
                text,
                genericStart,
                out var typeArguments,
                out var afterGeneric
            )
            || typeArguments.Count < 3
        )
        {
            return false;
        }

        var componentNameStart = SkipWhitespace(text, afterGeneric);
        if (
            componentNameStart >= text.Length
            || !IsNameStart(text[componentNameStart])
            || !TryReadIdentifier(
                text,
                componentNameStart,
                out var componentName,
                out var afterComponentName
            )
        )
        {
            return false;
        }

        var assignmentStart = SkipWhitespace(text, afterComponentName);
        if (assignmentStart >= text.Length || text[assignmentStart] != '=')
        {
            end = afterComponentName;
            return false;
        }

        var propsType = NormalizePropsType(typeArguments[0]);
        if (string.IsNullOrWhiteSpace(propsType))
        {
            end = afterComponentName;
            return false;
        }

        descriptor = new CsmxComponentDescriptor(componentName, propsType);
        end = afterComponentName;
        return true;
    }

    private static bool TryDiscoverMethodComponent(
        string text,
        int afterReturnType,
        out CsmxComponentDescriptor descriptor,
        out int end
    )
    {
        descriptor = default!;
        end = afterReturnType;

        var componentNameStart = SkipWhitespace(text, afterReturnType);
        if (
            componentNameStart >= text.Length
            || !IsNameStart(text[componentNameStart])
            || !TryReadIdentifier(
                text,
                componentNameStart,
                out var componentName,
                out var afterComponentName
            )
            || !IsUppercaseComponentName(componentName)
        )
        {
            return false;
        }

        var parametersStart = SkipWhitespace(text, afterComponentName);
        if (
            parametersStart >= text.Length
            || text[parametersStart] != '('
            || !TryReadParenthesized(
                text,
                parametersStart,
                out var parametersText,
                out var afterParameters
            )
        )
        {
            return false;
        }

        var parameters = SplitTopLevelParameters(parametersText).ToArray();
        if (
            parameters.Length < 2
            || !TryReadParameter(parameters[0], out var propsType, out var propsName)
            || !TryReadParameter(parameters[1], out _, out var childrenName)
            || !string.Equals(propsName, "props", StringComparison.Ordinal)
            || !string.Equals(childrenName, "children", StringComparison.Ordinal)
        )
        {
            end = afterParameters;
            return false;
        }

        propsType = NormalizePropsType(propsType);
        if (string.IsNullOrWhiteSpace(propsType))
        {
            end = afterParameters;
            return false;
        }

        descriptor = new CsmxComponentDescriptor(componentName, propsType);
        end = afterComponentName;
        return true;
    }

    private static bool TryReadParenthesized(string text, int start, out string value, out int end)
    {
        var index = start;
        var depth = 0;
        while (index < text.Length)
        {
            if (TrySkipTriviaOrLiteral(text, index, out var skipped))
            {
                index = skipped;
                continue;
            }

            var c = text[index];
            if (c == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    value = text.Substring(start + 1, index - start - 1);
                    end = index + 1;
                    return true;
                }

                if (depth < 0)
                {
                    break;
                }
            }

            index++;
        }

        value = string.Empty;
        end = start;
        return false;
    }

    private static IEnumerable<string> SplitTopLevelParameters(string text)
    {
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                    {
                        angleDepth--;
                    }
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && bracketDepth == 0:
                    yield return text.Substring(start, index - start).Trim();
                    start = index + 1;
                    break;
            }
        }

        var tail = text.Substring(start).Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }

    private static bool TryReadParameter(string parameter, out string type, out string name)
    {
        parameter = parameter.Trim();
        var equals = parameter.IndexOf('=');
        if (equals >= 0)
        {
            parameter = parameter.Substring(0, equals).TrimEnd();
        }

        var end = parameter.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(parameter[end]))
        {
            end--;
        }

        var nameEnd = end + 1;
        while (end >= 0 && IsNamePart(parameter[end]))
        {
            end--;
        }

        var nameStart = end + 1;
        if (nameStart >= nameEnd || !IsNameStart(parameter[nameStart]))
        {
            type = string.Empty;
            name = string.Empty;
            return false;
        }

        name = parameter.Substring(nameStart, nameEnd - nameStart);
        type = StripParameterModifiers(parameter.Substring(0, nameStart).TrimEnd());
        return type.Length > 0;
    }

    private static string StripParameterModifiers(string type)
    {
        while (true)
        {
            var trimmed = type.TrimStart();
            foreach (var modifier in new[] { "ref", "out", "in", "params", "scoped" })
            {
                if (
                    trimmed.StartsWith(modifier, StringComparison.Ordinal)
                    && trimmed.Length > modifier.Length
                    && char.IsWhiteSpace(trimmed[modifier.Length])
                )
                {
                    type = trimmed.Substring(modifier.Length).TrimStart();
                    goto Next;
                }
            }

            return trimmed;

            Next:
            continue;
        }
    }

    private static bool TryReadQualifiedName(string text, int start, out string name, out int end)
    {
        var index = start;
        if (!TryReadIdentifier(text, index, out var first, out index))
        {
            name = string.Empty;
            end = start;
            return false;
        }

        var parts = new List<string> { first };
        while (index < text.Length)
        {
            if (text[index] == '.')
            {
                var next = index + 1;
                if (!TryReadIdentifier(text, next, out var part, out index))
                {
                    break;
                }

                parts.Add(".");
                parts.Add(part);
                continue;
            }

            if (index + 1 < text.Length && text[index] == ':' && text[index + 1] == ':')
            {
                var next = index + 2;
                if (!TryReadIdentifier(text, next, out var part, out index))
                {
                    break;
                }

                parts.Add("::");
                parts.Add(part);
                continue;
            }

            break;
        }

        name = string.Concat(parts);
        end = index;
        return true;
    }

    private static bool TryReadIdentifier(
        string text,
        int start,
        out string identifier,
        out int end
    )
    {
        if (start >= text.Length || !IsNameStart(text[start]))
        {
            identifier = string.Empty;
            end = start;
            return false;
        }

        var index = start + 1;
        while (index < text.Length && IsNamePart(text[index]))
        {
            index++;
        }

        identifier = text.Substring(start, index - start);
        end = index;
        return true;
    }

    private static bool TryReadGenericArguments(
        string text,
        int start,
        out IReadOnlyList<string> arguments,
        out int end
    )
    {
        var values = new List<string>();
        var argumentStart = start + 1;
        var depth = 0;
        var index = start;

        while (index < text.Length)
        {
            var c = text[index];
            if (TrySkipTriviaOrLiteral(text, index, out var skipped))
            {
                index = skipped;
                continue;
            }

            if (c == '<')
            {
                depth++;
                index++;
                continue;
            }

            if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    values.Add(text.Substring(argumentStart, index - argumentStart).Trim());
                    arguments = values;
                    end = index + 1;
                    return values.All(value => value.Length > 0);
                }

                if (depth < 0)
                {
                    break;
                }

                index++;
                continue;
            }

            if (c == ',' && depth == 1)
            {
                values.Add(text.Substring(argumentStart, index - argumentStart).Trim());
                argumentStart = index + 1;
            }

            index++;
        }

        arguments = Array.Empty<string>();
        end = start;
        return false;
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
            end = SkipWhitespace(text, start);
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

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static string NormalizePropsType(string value)
    {
        value = value.Trim();
        while (value.EndsWith("?", StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - 1).TrimEnd();
        }

        return value;
    }

    private static bool IsFuncTypeName(string name) =>
        string.Equals(name, "Func", StringComparison.Ordinal)
        || string.Equals(name, "System.Func", StringComparison.Ordinal)
        || string.Equals(name, "global::System.Func", StringComparison.Ordinal);

    private static bool IsUppercaseComponentName(string name) =>
        !string.IsNullOrWhiteSpace(name) && char.IsUpper(name[0]);

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsNamePart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
