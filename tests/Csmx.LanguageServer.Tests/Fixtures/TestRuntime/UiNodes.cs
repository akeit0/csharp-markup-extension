namespace Csmx.Tests.LanguageServerFixtures;

public abstract class UiNode;

public class UiElement : UiNode
{
    public UiElement Content(UiNode node) => this;

    public UiElement Content(object? value) => this;

    public UiElement Padding(float value) => this;

    public UiElement Height(float value) => this;
}

public sealed class Column : UiElement;

public sealed class Text : UiElement;

public sealed class Button : UiElement;

public sealed class Panel : UiElement;

public sealed class Signal<T>(T value)
{
    public T Value { get; } = value;
}

public static class Runtime
{
    public static UiNode Element(string name, object? props, object? children) => new Text();

    public static object? Props(params object?[] attributes) => attributes;

    public static object? Attr(string name, object? value) => value;

    public static object? Children(params object?[] children) => children;

    public static UiNode Text(object? value) => new Text();
}

public static class Signals
{
    public static (Signal<int> Count, Action<Func<int, int>> SetCount) CreateSignal(int value) =>
        (new Signal<int>(value), _ => { });

    public static string CreateSignal(string value) => value;

    public static (Signal<int> Count, Action<Func<int, int>> SetCount) MakeSignal(int value) =>
        (new Signal<int>(value), _ => { });

    public static int Measure(int value) => value;

    public static int MeasureEdited(int value) => value;
}
