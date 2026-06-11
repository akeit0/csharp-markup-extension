using System.Net;
using System.Reflection;
using System.Text;

namespace Csmx.SampleRuntime;

public static class Csmx
{
    public static VElement Element(string name, object? props, IReadOnlyList<VNode> children)
    {
        var attributes = GetAttributes(props);
        return new VElement(name, attributes, children);
    }

    public static VNode Element<TProps>(
        Func<TProps, VNode[], VNode> component,
        TProps props,
        VNode[] children
    ) => component(props, children);

    public static CsmxProps Props(params VAttribute[] attributes) => new(attributes);

    public static VNode[] Children(params VChild[] items) =>
        items.SelectMany(item => item.Nodes).ToArray();

    public static VNode[] ChildSequence(IEnumerable<VNode> items) =>
        (items ?? throw new ArgumentNullException(nameof(items))).ToArray();

    public static VAttribute Attr(string name) => new(name, null);

    public static VAttribute Attr<T>(string name, T value) => new(name, value);

    public static VText Text<T>(T value) => new(value?.ToString() ?? string.Empty);

    public static VText Text<T>(T value, string? format) => new(FormatText(value, format, null));

    public static VText Text<T>(T value, string? format, int? alignment) =>
        new(FormatText(value, format, alignment));

    private static string FormatText<T>(T value, string? format, int? alignment)
    {
        var text =
            value is IFormattable formattable && format is not null
                ? formattable.ToString(format, null)
                : value?.ToString() ?? string.Empty;

        return alignment switch
        {
            null => text,
            >= 0 => text.PadLeft(alignment.Value),
            _ => text.PadRight(-alignment.Value),
        };
    }

    private static IReadOnlyList<VAttribute> GetAttributes(object? props)
    {
        if (props is null)
        {
            return Array.Empty<VAttribute>();
        }

        if (props is CsmxProps csmxProps)
        {
            return csmxProps.Attributes;
        }

        return props
            .GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => new VAttribute(
                property.GetCustomAttribute<CsmxAttributeNameAttribute>()?.Name ?? property.Name,
                property.GetValue(props)
            ))
            .ToArray();
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CsmxAttributeNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public abstract class ComponentBase
{
    public VNode Build() => Render();

    protected abstract VNode Render();
}

public abstract record VNode
{
    public abstract string ToMarkup();
}

public sealed record VText(string Value) : VNode
{
    public override string ToMarkup() => WebUtility.HtmlEncode(Value);
}

public readonly record struct VChild
{
    private readonly IEnumerable<VNode>? nodes;

    private VChild(IEnumerable<VNode> nodes)
    {
        this.nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }

    internal IEnumerable<VNode> Nodes => nodes ?? [];

    public static implicit operator VChild(VNode node) =>
        new([node ?? throw new ArgumentNullException(nameof(node))]);

    public static implicit operator VChild(VNode[] nodes) => new(nodes);
}

public sealed record VAttribute(string Name, object? Value);

public sealed record CsmxProps(IReadOnlyList<VAttribute> Attributes);

public sealed record VElement(
    string Name,
    IReadOnlyList<VAttribute> Attributes,
    IReadOnlyList<VNode> Children
) : VNode
{
    public override string ToMarkup()
    {
        var builder = new StringBuilder();
        builder.Append('<').Append(Name);

        foreach (var attribute in Attributes)
        {
            if (attribute.Value is null || attribute.Value is false)
            {
                continue;
            }

            builder.Append(' ').Append(attribute.Name);

            if (attribute.Value is true)
            {
                continue;
            }

            builder
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(attribute.Value.ToString()))
                .Append('"');
        }

        builder.Append('>');

        foreach (var child in Children)
        {
            builder.Append(child.ToMarkup());
        }

        builder.Append("</").Append(Name).Append('>');
        return builder.ToString();
    }
}
