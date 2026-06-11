namespace Csmx.Compiler;

public sealed record CsmxComponentDescriptor(string Name, string? PropsType);

public sealed class CsmxComponentRegistry
{
    private readonly IReadOnlyList<CsmxComponentDescriptor> descriptors;

    private CsmxComponentRegistry(IReadOnlyList<CsmxComponentDescriptor> descriptors)
    {
        this.descriptors = descriptors;
    }

    public IReadOnlyList<CsmxComponentDescriptor> Descriptors => descriptors;

    public static CsmxComponentRegistry Empty { get; } = new([]);

    public static CsmxComponentRegistry Parse(string value)
    {
        var descriptors = SplitList(value)
            .Where(IsValidDescriptor)
            .Select(ParseDescriptor)
            .Where(descriptor => descriptor is not null)
            .Cast<CsmxComponentDescriptor>()
            .ToArray();

        return descriptors.Length == 0 ? Empty : new CsmxComponentRegistry(descriptors);
    }

    public static CsmxComponentRegistry FromSource(string text, string configuredComponents)
    {
        var descriptors = Parse(configuredComponents)
            .descriptors.Concat(CsmxComponentDiscovery.Discover(text ?? string.Empty))
            .GroupBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return descriptors.Length == 0 ? Empty : new CsmxComponentRegistry(descriptors);
    }

    public static bool IsValidList(string value) =>
        SplitList(value).Any() && SplitList(value).All(IsValidDescriptor);

    public bool IsComponent(string name) =>
        descriptors.Any(descriptor =>
            string.Equals(descriptor.Name, name, StringComparison.Ordinal)
        );

    public string? GetPropsType(string name) =>
        descriptors
            .FirstOrDefault(descriptor =>
                !string.IsNullOrWhiteSpace(descriptor.PropsType)
                && string.Equals(descriptor.Name, name, StringComparison.Ordinal)
            )
            ?.PropsType;

    private static bool IsValidDescriptor(string value)
    {
        var descriptor = ParseDescriptor(value);
        return descriptor is not null
            && IsValidComponentExpression(descriptor.Name)
            && (descriptor.PropsType is null || IsValidComponentExpression(descriptor.PropsType));
    }

    private static CsmxComponentDescriptor? ParseDescriptor(string value)
    {
        var separator = value.IndexOfAny(new[] { '=', ':' });
        if (separator < 0)
        {
            var name = value.Trim();
            return string.IsNullOrWhiteSpace(name) || !IsValidComponentExpression(name)
                ? null
                : new CsmxComponentDescriptor(name, null);
        }

        var mappedName = value.Substring(0, separator).Trim();
        var propsType = value.Substring(separator + 1).Trim();
        return string.IsNullOrWhiteSpace(mappedName) || string.IsNullOrWhiteSpace(propsType)
            ? null
            : new CsmxComponentDescriptor(mappedName, propsType);
    }

    private static IEnumerable<string> SplitList(string value) =>
        CsmxCompat.SplitAndTrim(value, ';', ',');

    private static bool IsValidComponentExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Sum(part => part.Length) + parts.Length - 1 != value.Length)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (!IsNameStart(part[0]) || part.Skip(1).Any(c => !IsCSharpIdentifierPart(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
