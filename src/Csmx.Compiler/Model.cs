namespace Csmx.Compiler;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));
}

public enum CsmxDiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
}

public sealed record CsmxDiagnostic(
    string Message,
    TextSpan Span,
    CsmxDiagnosticSeverity Severity = CsmxDiagnosticSeverity.Error,
    string Code = CsmxDiagnostic.DefaultCode
)
{
    public const string DefaultCode = "CSMX0001";
    public const string DefaultWarningCode = "CSMX1001";
}

public enum ProjectionKind
{
    CSharp,
    JsxElement,
    ComponentReference,
    ElementReference,
    AttributeReference,
    ChildExpression,
    AttributeExpression,
}

public enum CsmxCompileMode
{
    Factory,
    Fluent,
}

public enum CsmxChildrenStrategy
{
    Materialized,
    BackendOwned,
}

public enum CsmxComponentLowering
{
    DirectCall,
    FactoryCall,
}

public enum CsmxElementFactKind
{
    Intrinsic,
    Component,
}

public enum CsmxStaticFactKind
{
    Static,
    Dynamic,
}

public enum CsmxAttributeValueFactKind
{
    Boolean,
    String,
    Expression,
    Missing,
}

public sealed record SourceMapEntry(
    TextSpan OriginalSpan,
    TextSpan GeneratedSpan,
    ProjectionKind Kind
);

public sealed record CsmxTransformResult(
    string Code,
    IReadOnlyList<CsmxDiagnostic> Diagnostics,
    IReadOnlyList<SourceMapEntry> Mappings
);

public sealed record CsmxFactsResult(
    CsmxElementFact Element,
    IReadOnlyList<CsmxDiagnostic> Diagnostics,
    int Position
);

public sealed record CsmxParsedElementFact(CsmxElementFact Element, int Position);

public abstract record CsmxChildFact(TextSpan Span, CsmxStaticFactKind StaticKind);

public sealed record CsmxElementFact(
    string Name,
    TextSpan NameSpan,
    CsmxElementFactKind Kind,
    CsmxStaticFactKind StaticKind,
    string? PropsType,
    IReadOnlyList<CsmxAttributeFact> Attributes,
    IReadOnlyList<CsmxChildFact> Children,
    TextSpan Span
) : CsmxChildFact(Span, StaticKind);

public sealed record CsmxTextFact(string Text, CsmxStaticFactKind StaticKind, TextSpan Span)
    : CsmxChildFact(Span, StaticKind);

public sealed record CsmxExpressionFact(
    string Expression,
    CsmxStaticFactKind StaticKind,
    TextSpan ExpressionSpan,
    CsmxExpressionFormatFact? Format,
    bool ContainsNestedJsx,
    TextSpan Span
) : CsmxChildFact(Span, StaticKind);

public sealed record CsmxExpressionFormatFact(
    string? Alignment,
    TextSpan? AlignmentSpan,
    string? Format,
    TextSpan? FormatSpan
);

public sealed record CsmxAttributeFact(
    string Name,
    TextSpan NameSpan,
    CsmxAttributeValueFactKind ValueKind,
    CsmxStaticFactKind StaticKind,
    string? StringValue,
    string? ExpressionValue,
    TextSpan? ExpressionSpan,
    bool IsBoolean,
    TextSpan Span
);

public sealed record CsmxTransformOptions
{
    public static CsmxTransformOptions Default { get; } = new();

    public CsmxCompileMode CompileMode { get; init; } = CsmxCompileMode.Factory;

    public string ElementFactory { get; init; } = "Element";

    public string AttributeFactory { get; init; } = "Attr";

    public string TextFactory { get; init; } = "Text";

    public string FormattedTextChild { get; init; } = "{TextFactory}({Value}, {Format})";

    public string AlignedFormattedTextChild { get; init; } =
        "{TextFactory}({Value}, {Format}, {Alignment})";

    public string PropsFactory { get; init; } = "Props";

    public string ChildrenFactory { get; init; } = "Children";

    public string ChildSequenceFactory { get; init; } = "ChildSequence";

    public string KeyedSequenceElement { get; init; } = string.Empty;

    public string KeyedSequenceItemsAttribute { get; init; } = "Items";

    public string KeyedSequenceKeyAttribute { get; init; } = "Key";

    public string KeyedSequenceTemplate { get; init; } = string.Empty;

    public string ComponentNames { get; init; } = string.Empty;

    public CsmxComponentLowering ComponentLowering { get; init; } =
        CsmxComponentLowering.DirectCall;

    public string ComponentTemplate { get; init; } = string.Empty;

    public string? SourceIdentity { get; init; }

    public string FluentCreate { get; init; } = "new {Element}()";

    public string FluentAttribute { get; init; } = ".{Name}({Value})";

    public string FluentTextChild { get; init; } = ".Content({Value})";

    public string FluentExpressionChild { get; init; } = ".Content({Value})";

    public string FluentFormattedExpressionChild { get; init; } =
        ".Content({Value}, {Format}, {Alignment})";

    public string FluentElementChild { get; init; } = ".Content({Value})";

    public string FluentComponentTemplate { get; init; } = string.Empty;
}
