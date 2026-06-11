using Enaga.Layout;

namespace Csmx.EnagaSignals;

public abstract class UiNode;

public abstract class UiTextContent
{
    private protected UiTextContent() { }

    public abstract string GetText();
}

public sealed class UiStaticTextContent : UiTextContent
{
    private readonly string text;

    public UiStaticTextContent(string text)
    {
        this.text = text;
    }

    public override string GetText() => text;
}

public sealed class UiDynamicTextContent : UiTextContent
{
    private readonly Func<string> text;

    public UiDynamicTextContent(Func<string> text)
    {
        this.text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public override string GetText() => text();
}

public class UiElement : UiNode
{
    private readonly List<UiElement> children = [];
    private readonly List<UiTextContent> textContent = [];

    public IReadOnlyList<UiElement> Children => children;

    public IReadOnlyList<UiTextContent> TextContentItems => textContent;

    internal UiElement AddTextContent(UiTextContent value)
    {
        textContent.Add(value ?? throw new ArgumentNullException(nameof(value)));
        return this;
    }

    public float? WidthValue { get; private set; }
    public float? HeightValue { get; private set; }
    public float FlexGrowValue { get; private set; }
    public float FlexShrinkValue { get; private set; } = 1;
    public float PaddingLeftValue { get; private set; }
    public float PaddingTopValue { get; private set; }
    public float PaddingRightValue { get; private set; }
    public float PaddingBottomValue { get; private set; }
    public float GapValue { get; private set; }
    public FlexDirection DirectionValue { get; private set; } = FlexDirection.Column;
    public string? BackgroundColorValue { get; private set; }
    public string? BorderColorValue { get; private set; }
    public float BorderWidthValue { get; private set; }
    public float BorderRadiusValue { get; private set; }
    public string? TextColorValue { get; private set; }
    public float FontSizeValue { get; private set; } = 16;
    public int FontWeightValue { get; private set; } = 400;
    public bool WrapTextValue { get; private set; } = true;
    public Action? ClickHandler { get; private set; }

    public UiElement Content(UiElement value)
    {
        children.Add(value ?? throw new ArgumentNullException(nameof(value)));
        return this;
    }

    public UiElement Content(IEnumerable<UiElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        children.AddRange(values);
        return this;
    }

    public UiElement Content(string? value)
    {
        if (value is null)
        {
            return this;
        }

        textContent.Add(new UiStaticTextContent(value));
        return this;
    }

    public UiElement Content(char value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content(int value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content(long value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content(float value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content(double value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content(decimal value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content(bool value)
    {
        textContent.Add(new UiStaticTextContent(value.ToString()));
        return this;
    }

    public UiElement Content<T>(Signal<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        textContent.Add(new UiDynamicTextContent(() => value.Value?.ToString() ?? string.Empty));
        return this;
    }

    public UiElement Content<T>(T value, string? format)
    {
        textContent.Add(new UiStaticTextContent(FormatText(value, format, null)));
        return this;
    }

    public UiElement Content<T>(T value, string? format, int? alignment)
    {
        textContent.Add(new UiStaticTextContent(FormatText(value, format, alignment)));
        return this;
    }

    public UiElement Content<T>(Signal<T> value, string? format)
    {
        ArgumentNullException.ThrowIfNull(value);
        textContent.Add(new UiDynamicTextContent(() => FormatText(value.Value, format, null)));
        return this;
    }

    public UiElement Content<T>(Signal<T> value, string? format, int? alignment)
    {
        ArgumentNullException.ThrowIfNull(value);
        textContent.Add(new UiDynamicTextContent(() => FormatText(value.Value, format, alignment)));
        return this;
    }

    public UiElement Content(Func<string> value)
    {
        textContent.Add(new UiDynamicTextContent(value));
        return this;
    }

    public UiElement Width(float value)
    {
        WidthValue = value;
        return this;
    }

    public UiElement Height(float value)
    {
        HeightValue = value;
        return this;
    }

    public UiElement Size(float value)
    {
        WidthValue = value;
        HeightValue = value;
        return this;
    }

    public UiElement Flex(float value)
    {
        FlexGrowValue = value;
        return this;
    }

    public UiElement FlexGrow(float value)
    {
        FlexGrowValue = value;
        return this;
    }

    public UiElement FlexShrink(float value)
    {
        FlexShrinkValue = value;
        return this;
    }

    public UiElement Padding(float value)
    {
        PaddingLeftValue = value;
        PaddingTopValue = value;
        PaddingRightValue = value;
        PaddingBottomValue = value;
        return this;
    }

    public UiElement Padding(float vertical, float horizontal)
    {
        PaddingLeftValue = horizontal;
        PaddingTopValue = vertical;
        PaddingRightValue = horizontal;
        PaddingBottomValue = vertical;
        return this;
    }

    public UiElement Gap(float value)
    {
        GapValue = value;
        return this;
    }

    public UiElement Direction(FlexDirection value)
    {
        DirectionValue = value;
        return this;
    }

    public UiElement Background(string value)
    {
        BackgroundColorValue = value;
        return this;
    }

    public UiElement Border(string value)
    {
        BorderColorValue = value;
        BorderWidthValue = Math.Max(1, BorderWidthValue);
        return this;
    }

    public UiElement BorderWidth(float value)
    {
        BorderWidthValue = value;
        return this;
    }

    public UiElement Radius(float value)
    {
        BorderRadiusValue = value;
        return this;
    }

    public UiElement Color(string value)
    {
        TextColorValue = value;
        return this;
    }

    public UiElement FontSize(float value)
    {
        FontSizeValue = value;
        return this;
    }

    public UiElement FontWeight(int value)
    {
        FontWeightValue = value;
        return this;
    }

    public UiElement Wrap(bool value)
    {
        WrapTextValue = value;
        return this;
    }

    public UiElement OnClick(Action handler)
    {
        ClickHandler = handler;
        return this;
    }

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
}

internal sealed class UiComponentHost(
    SignalRenderState state,
    Func<UiElement> render,
    Action requestFrame
) : UiElement
{
    private UiElement? current;
    private bool dirty = true;

    public UiElement Resolve()
    {
        if (!dirty && current is { } existing)
        {
            return existing;
        }

        current = state.Render(render, MarkDirty);
        dirty = false;
        return current;
    }

    private void MarkDirty()
    {
        dirty = true;
        requestFrame();
    }
}

public sealed class Panel : UiElement;

public sealed class Column : UiElement
{
    public Column()
    {
        Direction(FlexDirection.Column);
    }
}

public sealed class Row : UiElement
{
    public Row()
    {
        Direction(FlexDirection.Row);
    }
}

public sealed class Text : UiElement;

public sealed class Button : UiElement
{
    public Button()
    {
        Height(38);
        Padding(8, 14);
        Radius(6);
        Background("#2f6fed");
        Color("#ffffff");
        FontWeight(600);
    }
}
