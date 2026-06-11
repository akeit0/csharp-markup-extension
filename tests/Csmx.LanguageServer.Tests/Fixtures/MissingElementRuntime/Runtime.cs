namespace Csmx.Tests.MissingElementFixtures;

public sealed class UiNode;

public static class Runtime
{
    public static object? Props(params object?[] attributes) => attributes;

    public static object? Attr(string name, object? value) => value;

    public static object? Children(params object?[] children) => children;

    public static UiNode Text(object? value) => new();
}
