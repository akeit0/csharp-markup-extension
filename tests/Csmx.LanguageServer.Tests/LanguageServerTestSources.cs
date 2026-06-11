namespace Csmx.Tests;

internal static class TestSource
{
    public const string Text = """
using Csmx.Tests.Runtime;

namespace Csmx.Tests.Fixtures.Components;

public sealed record CounterViewProps
{
    public string Name { get; init; } = string.Empty;

    public int Count { get; init; }
}

public static class Counter
{
    public static VNode Render(string name, int count)
    {
        var isSmall = count < 3;
        Func<CounterViewProps, VNode[], VNode> View = (props, children) =>
            <stack class="counter">
                <text>Hello {props.Name}</text>
                <text> Count: {props.Count}</text>
                <button disabled={props.Count == 0}>Click</button>
            </stack>;

        return <View name={name} count={count}/>;
    }
}
""";

    public static Position PositionOf(string needle, int offset = 0) =>
        SourcePosition.Find(Text, needle, offset);
}

internal static class BlockRenderTestSource
{
    public const string Text = """""
using Csmx.EnagaSignals;

namespace Csmx.Tests;

public sealed class CounterState
{
    public Signal<int> Count { get; } = new(0);
}

public static class CounterView
{
    public static UiNode Render(CounterState state)
    {
        var children = new[] { "A", "B" };
        var label = $"Panel({string.Join(" | ", children)})";
        var rawLabel = $"""Raw {state.Count.Value:N2}""";
        Console.WriteLine($"Render CounterView");
        Console.WriteLine(label);
        Console.WriteLine(rawLabel);
        return <Column Padding={24} Gap={14} Background="#f5f7fb">
            <Text FontSize={28} FontWeight={700} Color="#152033">CSMX Signals</Text>
            <Text FontSize={18} Color="#44546a">Count: {state.Count.Value}</Text>
        </Column>;
    }
}
""""";

    public static Position PositionOf(string needle, int offset = 0) =>
        SourcePosition.Find(Text, needle, offset);
}

internal static class UsingStaticSemanticTestSource
{
    public const string Text = """
using Csmx.EnagaSignals;
using static Csmx.EnagaSignals.Signals;

namespace Csmx.Tests.Fixtures.Components;

public static class CounterView
{
    public static UiNode Render()
    {
        var (count, setCount) = CreateSignal(0);
        return <Text>Count: {count, 3}</Text>;
    }
}
""";

    public static Position PositionOf(string needle, int offset = 0) =>
        SourcePosition.Find(Text, needle, offset);
}

internal static class CompletionTestSource
{
    public const string Text = """
using Csmx.Tests.Runtime;

namespace Csmx.Tests;

public sealed record FactoryViewProps
{
    public string Name { get; init; } = string.Empty;
}

public static class CompletionHost
{
    static VNode FactoryView(FactoryViewProps props, VNode[] children) =>
        <panel class="box">
            <action disabled={true}>Run</action>
        </panel>;

    public static VNode Render()
    {
        return <
    }
}
""";

    public static Position PositionOf(string needle, int offset = 0) =>
        SourcePosition.Find(Text, needle, offset);
}

internal static class NestedExpressionLspTestSource
{
    public const string Text = """
using Csmx.Tests.Runtime;

namespace Csmx.Tests;

public sealed record Item(string Name);

public static class NestedExpressionHost
{
    public static VNode Render(Item[] items, bool condition)
    {
        var choice = condition ? <choice selected={true}>Yes</choice> : <empty>No</empty>;
        return <panel class="list">
            {items.Select(item => <row data-id={item.Name}><label>{item.Name}</label></row>)}
        </panel>;
    }
}
""";

    public static Position PositionOf(string needle, int offset = 0) =>
        SourcePosition.Find(Text, needle, offset);
}

internal static class SourcePosition
{
    public static Position Find(string text, string needle, int offset = 0)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Missing text '{needle}'.");
        }

        index += offset;
        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                character = 0;
            }
            else if (text[i] != '\r')
            {
                character++;
            }
        }

        return new Position(line, character);
    }
}
