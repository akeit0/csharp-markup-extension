namespace Csmx.Compiler;

internal interface ICsmxLoweringBackend
{
    CsmxChildrenStrategy ChildrenStrategy { get; }

    void EmitElement(CsmxElementIr element);
}
