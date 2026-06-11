using System.Text;

namespace Csmx.Compiler;

internal sealed class CsmxLoweringContext(
    CsmxTransformOptions options,
    StringBuilder builder,
    List<SourceMapEntry> mappings,
    List<CsmxDiagnostic> diagnostics
)
{
    private Action<string, TextSpan, ProjectionKind>? expressionEmitter;

    public CsmxTransformOptions Options { get; } = options;

    public int Position => builder.Length;

    public void Append(char value) => builder.Append(value);

    public void Append(string value) => builder.Append(value);

    public void SetExpressionEmitter(Action<string, TextSpan, ProjectionKind> emitter)
    {
        expressionEmitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public void AppendStringLiteral(string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            builder.Append(
                c switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => c.ToString(),
                }
            );
        }

        builder.Append('"');
    }

    public void AppendCallSiteLiteral(CsmxElementIr element)
    {
        var sourceIdentity = string.IsNullOrWhiteSpace(Options.SourceIdentity)
            ? "<memory>"
            : Options.SourceIdentity!;
        AppendStringLiteral($"{sourceIdentity}#{element.Span.Start}:{element.Span.Length}");
    }

    public void AddMapping(TextSpan originalSpan, int generatedStart, ProjectionKind kind) =>
        mappings.Add(
            new SourceMapEntry(
                originalSpan,
                TextSpan.FromBounds(generatedStart, builder.Length),
                kind
            )
        );

    public void AddDiagnostic(
        string message,
        TextSpan span,
        CsmxDiagnosticSeverity severity = CsmxDiagnosticSeverity.Error
    ) =>
        diagnostics.Add(
            new CsmxDiagnostic(
                message,
                span,
                severity,
                severity == CsmxDiagnosticSeverity.Warning
                    ? CsmxDiagnostic.DefaultWarningCode
                    : CsmxDiagnostic.DefaultCode
            )
        );

    public void AddWarning(string message, TextSpan span) =>
        AddDiagnostic(message, span, CsmxDiagnosticSeverity.Warning);

    public void EmitExpression(string expression, TextSpan expressionSpan, ProjectionKind kind)
    {
        if (expressionEmitter is not null)
        {
            expressionEmitter(expression, expressionSpan, kind);
            return;
        }

        var generatedStart = Position;
        Append(expression);
        AddMapping(expressionSpan, generatedStart, kind);
    }
}
