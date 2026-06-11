using Enaga.Input;
using Enaga.Layout;
using Enaga.Rendering;
using Enaga.Scene;

namespace Csmx.EnagaSignals;

internal sealed class SignalSceneBuilder
{
    private readonly LayoutCalculator layoutCalculator;
    private readonly IRuntimeTextServices textServices;
    private readonly List<(SceneScreenBounds Bounds, Action Handler)> clickTargets;
    private readonly List<DynamicTextDescriptor> dynamicTextDescriptors;
    private readonly int width;
    private readonly int height;
    private readonly SceneNodeIdAllocator ids = new();
    private readonly SceneNodeMap<SceneGraphNode> nodes = new();
    private readonly SceneNodeMap<SceneLayoutBox> layout = new();
    private readonly Dictionary<SceneNodeId, Action> elementHandlers = [];
    private readonly Dictionary<UiElement, TextEvaluation> textEvaluations = new(
        ReferenceEqualityComparer.Instance
    );

    public SignalSceneBuilder(
        LayoutCalculator layoutCalculator,
        IRuntimeTextServices textServices,
        List<(SceneScreenBounds Bounds, Action Handler)> clickTargets,
        List<DynamicTextDescriptor> dynamicTextDescriptors,
        int width,
        int height
    )
    {
        this.layoutCalculator = layoutCalculator;
        this.textServices = textServices;
        this.clickTargets = clickTargets;
        this.dynamicTextDescriptors = dynamicTextDescriptors;
        this.width = width;
        this.height = height;
    }

    public SceneLayoutCommit Build(UiNode root)
    {
        clickTargets.Clear();
        dynamicTextDescriptors.Clear();
        textEvaluations.Clear();
        var rootElement =
            root as UiElement
            ?? throw new InvalidOperationException("Signal UI root must be an element.");
        rootElement = ResolveElement(rootElement);
        var rootId = EmitElement(rootElement, null, 0, 0, width, height);
        var commit = SceneLayoutCommitFactory.Create(
            rootId,
            new SceneViewport(width, height),
            nodes,
            layout
        );
        BuildClickTargets(commit);
        return commit;
    }

    private SceneNodeId EmitElement(
        UiElement element,
        SceneNodeId? parentId,
        float left,
        float top,
        float width,
        float height
    )
    {
        element = ResolveElement(element);
        var id = ids.Allocate();
        var children = NormalizeChildren(element);
        var childIds = Array.Empty<SceneNodeId>();
        if (element.ClickHandler is { } handler)
        {
            elementHandlers[id] = handler;
        }

        nodes[id] = new SceneGraphNode(
            GetNodeKind(element),
            parentId,
            childIds,
            element.GetType().Name
        );
        layout[id] = CreateLayoutBox(id, element, left, top, width, height);

        if (element is Text)
        {
            return id;
        }

        if (children.Count > 0)
        {
            childIds = EmitChildren(element, children, id, left, top, width, height);
            nodes[id] = nodes[id] with { Children = childIds };
        }

        return id;
    }

    private SceneNodeId[] EmitChildren(
        UiElement parent,
        IReadOnlyList<UiElement> children,
        SceneNodeId parentId,
        float left,
        float top,
        float width,
        float height
    )
    {
        var contentWidth = Math.Max(1, width - parent.PaddingLeftValue - parent.PaddingRightValue);
        var contentHeight = Math.Max(
            1,
            height - parent.PaddingTopValue - parent.PaddingBottomValue
        );
        var requests = new LayoutChildRequest[children.Count];
        var frames = new LayoutFrameData?[children.Count];
        for (var index = 0; index < children.Count; index++)
        {
            requests[index] = CreateLayoutRequest(
                children[index],
                parent,
                contentWidth,
                contentHeight
            );
        }

        layoutCalculator.ComputeFlexLayout(
            LayoutInput.Definite(width, height),
            new LayoutContainerStyle(
                parent.DirectionValue,
                LayoutDirection.Ltr,
                FlexWrap.NoWrap,
                RowGap: parent.GapValue,
                ColumnGap: parent.GapValue,
                AlignItems: CrossAlignment.Stretch,
                JustifyContent: MainAxisJustification.Start,
                Padding: new LayoutBoxEdges(
                    parent.PaddingLeftValue,
                    parent.PaddingTopValue,
                    parent.PaddingRightValue,
                    parent.PaddingBottomValue
                )
            ),
            requests,
            frames
        );

        var childIds = new SceneNodeId[children.Count];
        for (var index = 0; index < children.Count; index++)
        {
            var frame =
                frames[index]
                ?? new LayoutFrameData(parent.PaddingLeftValue, parent.PaddingTopValue, 0, 0);
            childIds[index] = EmitElement(
                children[index],
                parentId,
                left + frame.Left,
                top + frame.Top,
                Math.Max(0, frame.Width),
                Math.Max(0, frame.Height)
            );
        }

        return childIds;
    }

    private LayoutChildRequest CreateLayoutRequest(
        UiElement child,
        UiElement parent,
        float availableWidth,
        float availableHeight
    )
    {
        var width =
            child.WidthValue
            ?? (
                parent.DirectionValue == FlexDirection.Column
                    ? availableWidth
                    : MeasureWidth(child, availableWidth)
            );
        var height = child.HeightValue ?? MeasureHeight(child, width, availableHeight);
        return new LayoutChildRequest(
            LayoutChildKind.Element,
            Width: width,
            Height: height,
            Text: child is Text ? GetTextEvaluation(child).Text : null,
            FontSize: child.FontSizeValue,
            FontWeight: child.FontWeightValue,
            Wrap: child.WrapTextValue,
            FlexGrow: child.FlexGrowValue,
            FlexShrink: child.FlexShrinkValue,
            PaddingLeft: child.PaddingLeftValue,
            PaddingTop: child.PaddingTopValue,
            PaddingRight: child.PaddingRightValue,
            PaddingBottom: child.PaddingBottomValue
        );
    }

    private float MeasureWidth(UiElement element, float availableWidth)
    {
        if (element.WidthValue is { } width)
        {
            return width;
        }

        if (element is Text)
        {
            var text = GetTextEvaluation(element).Text;
            return Math.Min(
                availableWidth,
                Math.Max(1, textServices.MeasureTextWidth(text, CreateTextStyle(element)))
            );
        }

        if (element is Button)
        {
            var textWidth = textServices.MeasureTextWidth(
                SignalTextContent.GetText(element),
                CreateTextStyle(element)
            );
            return Math.Max(72, textWidth + element.PaddingLeftValue + element.PaddingRightValue);
        }

        return Math.Max(1, availableWidth);
    }

    private float MeasureHeight(UiElement element, float width, float availableHeight)
    {
        if (element.HeightValue is { } height)
        {
            return height;
        }

        if (element is Text)
        {
            var style = CreateTextStyle(element);
            return Math.Max(
                1,
                textServices.MeasureTextHeight(
                    GetTextEvaluation(element).Text,
                    Math.Max(1, width),
                    style
                )
            );
        }

        if (element is Button)
        {
            return 38;
        }

        var children = NormalizeChildren(element);
        if (children.Count == 0)
        {
            return Math.Min(availableHeight, 1);
        }

        var total = element.PaddingTopValue + element.PaddingBottomValue;
        if (element.DirectionValue == FlexDirection.Row)
        {
            total += children.Max(child =>
                MeasureHeight(child, MeasureWidth(child, width), availableHeight)
            );
        }
        else
        {
            total += children.Sum(child => MeasureHeight(child, width, availableHeight));
            total += Math.Max(0, children.Count - 1) * element.GapValue;
        }

        return Math.Max(1, Math.Min(availableHeight, total));
    }

    private SceneLayoutBox CreateLayoutBox(
        SceneNodeId id,
        UiElement element,
        float left,
        float top,
        float width,
        float height
    )
    {
        var textEvaluation = element is Text ? GetTextEvaluation(element) : default;
        var text = element is Text ? textEvaluation.Text : null;
        if (textEvaluation.HasDynamicContent)
        {
            dynamicTextDescriptors.Add(
                new DynamicTextDescriptor(id, element, textEvaluation.Text, textEvaluation.Signals)
            );
        }

        var nodeKind = GetNodeKind(element);
        return new SceneLayoutBox(
            nodeKind,
            left,
            top,
            width,
            height,
            BackgroundColor: element.BackgroundColorValue,
            BorderColor: element.BorderColorValue,
            BorderWidth: element.BorderWidthValue,
            BorderRadius: element.BorderRadiusValue,
            TextContent: text,
            TextStyle: nodeKind == SceneNodeKind.Text ? CreateTextStyle(element) : null,
            PaddingLeft: element.PaddingLeftValue,
            PaddingTop: element.PaddingTopValue,
            PaddingRight: element.PaddingRightValue,
            PaddingBottom: element.PaddingBottomValue,
            LineHeight: text is null ? 0 : textServices.MeasureLineHeight(element.FontSizeValue),
            ControlKind: element.ClickHandler is null
                ? SceneControlKind.None
                : SceneControlKind.Button
        );
    }

    private void BuildClickTargets(SceneLayoutCommit commit)
    {
        foreach (var (id, box) in layout)
        {
            if (box.ControlKind != SceneControlKind.Button)
            {
                continue;
            }

            if (!SceneScreenGeometry.TryGetNodeScreenBounds(commit, id, out var bounds))
            {
                continue;
            }

            if (elementHandlers.TryGetValue(id, out var handler))
            {
                clickTargets.Add((bounds, handler));
            }
        }
    }

    private static SceneNodeKind GetNodeKind(UiElement element) =>
        ResolveElement(element) switch
        {
            Text => SceneNodeKind.Text,
            _ => SceneNodeKind.View,
        };

    private static UiElement ResolveElement(UiElement element) =>
        element switch
        {
            UiComponentHost host => host.Resolve(),
            _ => element,
        };

    private TextEvaluation GetTextEvaluation(UiElement element)
    {
        if (!textEvaluations.TryGetValue(element, out var evaluation))
        {
            evaluation = SignalTextContent.Evaluate(element, captureSignals: true);
            textEvaluations.Add(element, evaluation);
        }

        return evaluation;
    }

    private IReadOnlyList<UiElement> NormalizeChildren(UiElement element)
    {
        if (element is Text)
        {
            return [];
        }

        var children = new List<UiElement>();
        var inlineText = new List<UiTextContent>();
        foreach (var child in element.Children)
        {
            FlushInlineText();
            children.Add(ResolveElement(child));
        }

        foreach (var textContent in element.TextContentItems)
        {
            inlineText.Add(textContent);
        }

        FlushInlineText();
        return children;

        void FlushInlineText()
        {
            if (inlineText.Count == 0)
            {
                return;
            }

            var text = new Text()
                .FontSize(element.FontSizeValue)
                .FontWeight(element.FontWeightValue)
                .Color(element.TextColorValue ?? "#111111")
                .Wrap(element.WrapTextValue);
            foreach (var item in inlineText)
            {
                text.AddTextContent(item);
            }

            children.Add(text);
            inlineText.Clear();
        }
    }

    private static SceneTextStyle CreateTextStyle(UiElement element) =>
        new(
            element.FontSizeValue,
            element.TextColorValue ?? "#111111",
            FontWeight: element.FontWeightValue,
            WrapText: element.WrapTextValue
        );
}
