namespace Csmx.Compiler;

internal readonly record struct CsmxTemplateDefinition(string Name, string Text);

internal static class CsmxTemplate
{
    public static void Apply(
        CsmxLoweringContext context,
        CsmxTemplateDefinition template,
        TextSpan diagnosticSpan,
        Func<string, bool> emitToken
    )
    {
        for (var index = 0; index < template.Text.Length; )
        {
            if (template.Text[index] != '{')
            {
                context.Append(template.Text[index]);
                index++;
                continue;
            }

            var close = template.Text.IndexOf('}', index + 1);
            if (close < 0)
            {
                context.Append(template.Text[index]);
                index++;
                continue;
            }

            var token = template.Text.Substring(index + 1, close - index - 1);
            if (!emitToken(token))
            {
                context.AddWarning(
                    $"Unknown template placeholder '{{{token}}}' in {template.Name}.",
                    diagnosticSpan
                );
                context.Append('{');
                context.Append(token);
                context.Append('}');
            }

            index = close + 1;
        }
    }
}
