namespace Csmx.Compiler;

internal interface ICsmxComponentLoweringBackend
{
    void EmitCallStart(CsmxElementIr element);
}

internal sealed class CsmxDirectComponentLoweringBackend(CsmxLoweringContext context)
    : ICsmxComponentLoweringBackend
{
    public void EmitCallStart(CsmxElementIr element)
    {
        var nameGeneratedStart = context.Position;
        context.Append(element.Name);
        context.AddMapping(element.NameSpan, nameGeneratedStart, ProjectionKind.ComponentReference);
        context.Append('(');
    }
}

internal sealed class CsmxFactoryComponentLoweringBackend(CsmxLoweringContext context)
    : ICsmxComponentLoweringBackend
{
    public void EmitCallStart(CsmxElementIr element)
    {
        var nameGeneratedStart = context.Position;
        context.Append(context.Options.ElementFactory);
        context.Append('(');
        context.Append(element.Name);
        context.AddMapping(
            element.NameSpan,
            nameGeneratedStart + context.Options.ElementFactory.Length + 1,
            ProjectionKind.ComponentReference
        );
        context.Append(", ");
    }
}
