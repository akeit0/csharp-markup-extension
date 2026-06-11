namespace Csmx.Tests;

internal static class TestSource
{
    public const string Text = """
using Csmx.SampleRuntime;

namespace Csmx.Samples.HelloApp.Components;

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
}

internal static class ComponentPolicyTestSource
{
    public const string Text = """
    namespace Csmx.Tests;

    public sealed record ViewProps
    {
        public string Name { get; init; } = string.Empty;
    }

public static class ComponentPolicy
{
    public static object Render(string name)
    {
        Func<ViewProps, VNode[], VNode> View = static (_, _) => throw new NotImplementedException();
        return <View name={name}/>;
    }
}
""";
}
