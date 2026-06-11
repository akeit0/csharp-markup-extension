using Csmx.EnagaSignals;
using Enaga.Rendering;
using TUnit.Core;

namespace Csmx.Tests;

public sealed class SignalUiTests
{
    [Test]
    public Task TestSignalUiFrameSource()
    {
        Signal<int>? count = null;
        Action<Func<int, int>>? setCount = null;
        var viewCalls = 0;
        using var source = new SignalSceneFrameSource(() =>
        {
            (count, setCount) = Signals.CreateSignal(0);
            viewCalls++;
            return new Column()
                .Padding(12)
                .Gap(8)
                .Content(new Text().Content("Count: ").Content(count))
                .Content(new Button().OnClick(() => setCount(value => value + 1)).Content("+1"));
        });

        var first = source.RenderFrame(320, 200, TimeSpan.Zero);
        Assert(first.Commit.Nodes.Count >= 4, "Signal UI should emit a non-empty scene.");
        Assert(first.DirtyRects.Length == 1, "Initial signal UI render should dirty the frame.");
        Assert(viewCalls == 1, "Initial signal UI render should call the view once.");

        var second = source.RenderFrame(320, 200, TimeSpan.FromMilliseconds(16));
        Assert(
            second.DirtyRects.Length == 0,
            "Unchanged signal UI render should report no damage."
        );
        Assert(viewCalls == 1, "Unchanged signal UI render should not call the view again.");

        setCount!(_ => 1);
        var third = source.RenderFrame(320, 200, TimeSpan.FromMilliseconds(32));
        Assert(
            third.DirtyRects.Length == 1,
            "Dynamic signal text updates should dirty the text node."
        );
        Assert(viewCalls == 1, "Dynamic signal text updates should not call the view again.");
        Assert(
            third.Commit.Layout.Values.Any(box => box.TextContent == "Count: 1"),
            "Dynamic signal text should update the scene commit."
        );
        Assert(count!.Value == 1, "CreateSignal state should update inside the view scope.");

        var resized = source.RenderFrame(360, 220, TimeSpan.FromMilliseconds(48));
        Assert(resized.DirtyRects.Length == 1, "Resize should rebuild the scene.");
        Assert(viewCalls == 1, "Resize rebuild should reuse the retained view tree.");
        Assert(count.Value == 1, "CreateSignal state should survive a retained layout rebuild.");
        Assert(
            resized.Commit.Layout.Values.Any(box => box.TextContent == "Count: 1"),
            "Rebuilt scene should preserve CreateSignal state."
        );

        var clicks = 0;
        using var clickSource = new SignalSceneFrameSource(() =>
            new Column()
                .Content(new Button().Width(100).Height(40).OnClick(() => clicks++).Content("+1"))
                .Content(new Text().Content("Outside"))
        );
        clickSource.RenderFrame(200, 120, TimeSpan.Zero);

        clickSource.PointerMove(10, 10, 0, synthetic: false);
        clickSource.PointerMove(30, 10, 0, synthetic: false);
        clickSource.PointerUp(0, 0, synthetic: false);
        Assert(
            clicks == 0,
            "Pointer movement and release without press must not invoke button handlers."
        );

        clickSource.PointerMove(10, 10, 0, synthetic: false);
        clickSource.PointerDown(0, 1, synthetic: false);
        clickSource.PointerMove(30, 10, 1, synthetic: false);
        clickSource.PointerUp(0, 0, synthetic: false);
        Assert(clicks == 1, "Pointer press and release on the same button should invoke once.");

        clickSource.PointerMove(10, 10, 0, synthetic: false);
        clickSource.PointerDown(0, 1, synthetic: false);
        clickSource.PointerMove(180, 100, 1, synthetic: false);
        clickSource.PointerUp(0, 0, synthetic: false);
        Assert(clicks == 1, "Dragging away before release should cancel the click.");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiNodeContentModel()
    {
        var (count, _) = Signals.CreateSignal(0);
        var dynamicChildren = new[] { new Text().Content("C"), new Button().Content("D") };
        var container = new Column()
            .Content(new Text().Content("A"))
            .Content(new Button().Content("B"))
            .Content(dynamicChildren);
        Assert(container.Children.Count == 4, "Element content should be stored as children.");
        Assert(
            container.TextContentItems.Count == 0,
            "Element children should not be mixed into text content."
        );

        var text = new Text().Content("Count: ").Content(count);
        Assert(text.Children.Count == 0, "Text content should not create element children.");
        Assert(
            text.TextContentItems.Count == 2,
            "Static and signal text should remain text content."
        );
        Assert(
            text.TextContentItems[1] is UiDynamicTextContent,
            "Signal content should be represented as dynamic text."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiFormattedSignalContent()
    {
        var signal = new Signal<float>(1.234f);
        var text = new Text().Content("Value: ").Content(signal, "F2");

        Assert(
            text.TextContentItems[1] is UiDynamicTextContent,
            "Formatted signal content should stay dynamic."
        );
        AssertEqual(
            "Value: 1.23",
            string.Concat(text.TextContentItems.Select(item => item.GetText())),
            "formatted signal content"
        );

        signal.Value = 2.5f;
        AssertEqual(
            "Value: 2.50",
            string.Concat(text.TextContentItems.Select(item => item.GetText())),
            "updated formatted signal content"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiEvaluatesDynamicTextOncePerFrame()
    {
        Signal<int>? count = null;
        var viewCalls = 0;
        var textEvaluations = 0;

        using var source = new SignalSceneFrameSource(() =>
        {
            (count, _) = Signals.CreateSignal(0);
            viewCalls++;
            return new Column()
                .Padding(4)
                .Content(
                    new Text().Content(() =>
                    {
                        textEvaluations++;
                        return $"Count: {count.Value}";
                    })
                );
        });

        var first = source.RenderFrame(320, 200, TimeSpan.Zero);
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "Count: 0"),
            "Initial dynamic text should render."
        );
        Assert(viewCalls == 1, "Initial frame should call the view once.");
        Assert(textEvaluations == 1, "Dynamic text should be evaluated once during scene build.");

        count!.Value = 1;
        var second = source.RenderFrame(320, 200, TimeSpan.FromMilliseconds(16));
        Assert(
            second.Commit.Layout.Values.Any(box => box.TextContent == "Count: 1"),
            "Dynamic text patch should render the latest value."
        );
        Assert(viewCalls == 1, "Dynamic text patch should not call the view.");
        Assert(textEvaluations == 2, "Dynamic text patch should evaluate the binding once.");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiNestedComponentScopesPreserveSiblingState()
    {
        Signal<int>? left = null;
        Signal<int>? middle = null;
        Signal<int>? right = null;
        Action<Func<bool, bool>>? setShowMiddle = null;
        var rootCalls = 0;

        UiElement Counter(string label, int initial, Action<Signal<int>> capture)
        {
            var (count, _) = Signals.CreateSignal(initial);
            capture(count);
            return new Text().Content(label).Content(": ").Content(count.Value);
        }

        using var source = new SignalSceneFrameSource(() =>
        {
            rootCalls++;
            var (showMiddle, setShowMiddleSignal) = Signals.CreateSignal(false);
            setShowMiddle = setShowMiddleSignal;

            var root = new Column().Content(
                Signals.Component("left-counter", () => Counter("left", 1, value => left = value))
            );

            if (showMiddle.Value)
            {
                root.Content(
                    Signals.Component(
                        "middle-counter",
                        () => Counter("middle", 2, value => middle = value)
                    )
                );
            }

            return root.Content(
                Signals.Component(
                    "right-counter",
                    () => Counter("right", 3, value => right = value)
                )
            );
        });

        var first = source.RenderFrame(320, 240, TimeSpan.Zero);
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "left: 1"),
            "Left counter should render."
        );
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "right: 3"),
            "Right counter should render."
        );
        Assert(rootCalls == 1, "Initial nested component test should call root once.");

        right!.Value = 30;
        var rightUpdated = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(16));
        Assert(
            rightUpdated.Commit.Layout.Values.Any(box => box.TextContent == "right: 30"),
            "Right counter update should render before inserting a sibling."
        );
        Assert(rootCalls == 1, "Child structural signal update should not call root render.");

        setShowMiddle!(_ => true);
        var withMiddle = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(32));
        Assert(
            withMiddle.Commit.Layout.Values.Any(box => box.TextContent == "middle: 2"),
            "Inserted middle component should receive its own signal state."
        );
        Assert(
            withMiddle.Commit.Layout.Values.Any(box => box.TextContent == "right: 30"),
            "Right component state should not shift when a middle sibling is inserted."
        );
        Assert(left!.Value == 1, "Left component state should stay owned by the left component.");
        Assert(middle!.Value == 2, "Middle component should use its own initial state.");
        Assert(
            right.Value == 30,
            "Right component state should stay owned by the right component."
        );
        Assert(rootCalls == 2, "Parent structural signal update should call root render.");

        middle.Value = 20;
        var middleUpdated = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(48));
        Assert(
            middleUpdated.Commit.Layout.Values.Any(box => box.TextContent == "middle: 20"),
            "Middle component state should update while mounted."
        );
        Assert(rootCalls == 2, "Mounted child structural update should not call root render.");

        setShowMiddle!(_ => false);
        var hidden = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(64));
        Assert(
            hidden.Commit.Layout.Values.All(box => box.TextContent != "middle: 20"),
            "Unmounted middle component should be removed from the scene."
        );
        Assert(
            hidden.Commit.Layout.Values.Any(box => box.TextContent == "right: 30"),
            "Right component state should survive after middle is removed."
        );

        setShowMiddle!(_ => true);
        var remounted = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(80));
        Assert(
            remounted.Commit.Layout.Values.Any(box => box.TextContent == "middle: 2"),
            "Remounted middle component should start from a fresh component scope."
        );
        Assert(
            remounted.Commit.Layout.Values.Any(box => box.TextContent == "right: 30"),
            "Right component state should remain stable after middle remounts."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiChildStructuralSubscriptionsAreDisposedWithComponent()
    {
        Action<Func<bool, bool>>? setShowChild = null;
        Signal<bool>? childOpen = null;
        var wakeRequests = 0;
        var rootCalls = 0;

        UiElement Child()
        {
            var (open, _) = Signals.CreateSignal(false);
            childOpen = open;
            return open.Value
                ? new Text().Content("child open")
                : new Text().Content("child closed");
        }

        using var source = new SignalSceneFrameSource(() =>
        {
            rootCalls++;
            var (showChild, setShowChildSignal) = Signals.CreateSignal(true);
            setShowChild = setShowChildSignal;

            var root = new Column().Content(new Text().Content("root"));
            if (showChild.Value)
            {
                root.Content(Signals.Component("child", Child));
            }

            return root;
        });
        source.RenderWakeRequested += () => wakeRequests++;

        var first = source.RenderFrame(320, 200, TimeSpan.Zero);
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "child closed"),
            "Mounted child should render its structural signal state."
        );

        childOpen!.Value = true;
        Assert(wakeRequests == 1, "Mounted child structural signal should request a frame.");
        var opened = source.RenderFrame(320, 200, TimeSpan.FromMilliseconds(16));
        Assert(
            opened.Commit.Layout.Values.Any(box => box.TextContent == "child open"),
            "Mounted child structural signal should rerender the child output."
        );
        Assert(rootCalls == 1, "Child structural signal should not call root render.");

        setShowChild!(_ => false);
        Assert(wakeRequests == 2, "Parent structural signal should request a frame.");
        var removed = source.RenderFrame(320, 200, TimeSpan.FromMilliseconds(32));
        Assert(
            removed.Commit.Layout.Values.All(box => box.TextContent != "child open"),
            "Removed child should leave the scene."
        );
        Assert(rootCalls == 2, "Parent structural signal should call root render.");

        childOpen.Value = false;
        Assert(wakeRequests == 2, "Removed child structural signal should be unsubscribed.");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiKeyedComponentScopesSurviveReorderAndDisposeOnRemove()
    {
        Action<Func<int[], int[]>>? setOrder = null;
        var rowStates = new Dictionary<int, Signal<int>>();

        UiElement Row(int id)
        {
            var (count, _) = Signals.CreateSignal(id * 10);
            rowStates[id] = count;
            return new Text().Content("row ").Content(id).Content(": ").Content(count);
        }

        using var source = new SignalSceneFrameSource(() =>
        {
            var (order, setOrderSignal) = Signals.CreateSignal(new[] { 1, 2 });
            setOrder = setOrderSignal;

            return new Column().Content(
                order.Value.Select(id => Signals.Component("row", id, () => Row(id)))
            );
        });

        var first = source.RenderFrame(320, 240, TimeSpan.Zero);
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "row 1: 10"),
            "Row 1 should render."
        );
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "row 2: 20"),
            "Row 2 should render."
        );

        rowStates[1].Value = 100;
        var updated = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(16));
        Assert(
            updated.Commit.Layout.Values.Any(box => box.TextContent == "row 1: 100"),
            "Keyed row state should update before reorder."
        );

        setOrder!(_ => [2, 1]);
        var reordered = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(32));
        Assert(
            reordered.Commit.Layout.Values.Any(box => box.TextContent == "row 1: 100"),
            "Keyed row state should survive reorder."
        );
        Assert(rowStates[1].Value == 100, "Row 1 signal should remain attached to key 1.");
        Assert(rowStates[2].Value == 20, "Row 2 signal should remain attached to key 2.");

        setOrder!(_ => [2]);
        var removed = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(48));
        Assert(
            removed.Commit.Layout.Values.All(box => box.TextContent != "row 1: 100"),
            "Removed keyed row should leave the scene."
        );

        setOrder!(_ => [2, 1]);
        var remounted = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(64));
        Assert(
            remounted.Commit.Layout.Values.Any(box => box.TextContent == "row 1: 10"),
            "Removed keyed row should remount with fresh state."
        );
        Assert(rowStates[1].Value == 10, "Removed keyed row state should be disposed.");
        Assert(rowStates[2].Value == 20, "Remaining keyed row state should be preserved.");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiKeyedChildrenPreserveStateThroughDynamicContent()
    {
        Action<Func<int[], int[]>>? setOrder = null;
        var rowStates = new Dictionary<int, Signal<int>>();

        UiElement Row(int id)
        {
            var (count, _) = Signals.CreateSignal(id * 10);
            rowStates[id] = count;
            return new Text().Content("child ").Content(id).Content(": ").Content(count);
        }

        using var source = new SignalSceneFrameSource(() =>
        {
            var (order, setOrderSignal) = Signals.CreateSignal(new[] { 1, 2, 3 });
            setOrder = setOrderSignal;

            return new Column().Content(
                Signals.KeyedChildren("dynamic-row", order.Value, id => id, Row)
            );
        });

        var first = source.RenderFrame(320, 240, TimeSpan.Zero);
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "child 1: 10"),
            "Child 1 should render."
        );
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "child 2: 20"),
            "Child 2 should render."
        );
        Assert(
            first.Commit.Layout.Values.Any(box => box.TextContent == "child 3: 30"),
            "Child 3 should render."
        );

        rowStates[2].Value = 200;
        var updated = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(16));
        Assert(
            updated.Commit.Layout.Values.Any(box => box.TextContent == "child 2: 200"),
            "Dynamic child state should update before reorder."
        );

        setOrder!(_ => [3, 2, 1]);
        var reordered = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(32));
        Assert(
            reordered.Commit.Layout.Values.Any(box => box.TextContent == "child 2: 200"),
            "Keyed dynamic child state should survive reorder."
        );
        Assert(rowStates[1].Value == 10, "Child 1 signal should remain attached to key 1.");
        Assert(rowStates[2].Value == 200, "Child 2 signal should remain attached to key 2.");
        Assert(rowStates[3].Value == 30, "Child 3 signal should remain attached to key 3.");

        setOrder!(_ => [3, 1]);
        var removed = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(48));
        Assert(
            removed.Commit.Layout.Values.All(box => box.TextContent != "child 2: 200"),
            "Removed keyed dynamic child should leave the scene."
        );

        setOrder!(_ => [2, 3, 1]);
        var remounted = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(64));
        Assert(
            remounted.Commit.Layout.Values.Any(box => box.TextContent == "child 2: 20"),
            "Removed keyed dynamic child should remount with fresh state."
        );
        Assert(rowStates[2].Value == 20, "Removed keyed dynamic child state should be disposed.");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiKeyedRowStructuralUpdateBudget()
    {
        const int RowCount = 48;
        var ids = Enumerable.Range(1, RowCount).ToArray();
        var rowStates = new Dictionary<int, Signal<bool>>();
        var rowRenderCalls = new Dictionary<int, int>();
        var rootCalls = 0;

        UiElement RowView(int id)
        {
            var (open, _) = Signals.CreateSignal(false);
            rowStates[id] = open;
            rowRenderCalls[id] = rowRenderCalls.GetValueOrDefault(id) + 1;

            return new Row()
                .Gap(4)
                .Content(new Text().Width(72).Content("row ").Content(id))
                .Content(open.Value ? new Text().Content("open") : new Text().Content("closed"));
        }

        using var source = new SignalSceneFrameSource(() =>
        {
            rootCalls++;
            return new Column().Content(
                Signals.KeyedChildren("budget-row", ids, id => id, RowView)
            );
        });

        var first = source.RenderFrame(640, 1200, TimeSpan.Zero);
        Assert(
            first.Commit.Nodes.Count >= RowCount * 3,
            "Budget dashboard should create many nodes."
        );
        Assert(rootCalls == 1, "Initial budget dashboard should call root once.");
        Assert(
            rowRenderCalls.Values.All(count => count == 1),
            "Initial budget dashboard should render each row once."
        );

        rowStates[17].Value = true;
        var updated = source.RenderFrame(640, 1200, TimeSpan.FromMilliseconds(16));
        Assert(
            updated.Commit.Layout.Values.Any(box => box.TextContent == "open"),
            "Updated row should render its structural state."
        );
        Assert(rootCalls == 1, "One row structural update should not call root render.");
        Assert(rowRenderCalls[17] == 2, "Updated row should rerender once.");
        Assert(
            rowRenderCalls.Where(pair => pair.Key != 17).All(pair => pair.Value == 1),
            "One row structural update should not rerender sibling rows."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiCoalescesWakeRequestsBeforeNextFrame()
    {
        Signal<int>? count = null;
        var wakeRequests = 0;

        using var source = new SignalSceneFrameSource(() =>
        {
            (count, _) = Signals.CreateSignal(0);
            return new Column().Content(new Text().Content("Count: ").Content(count));
        });
        source.RenderWakeRequested += () => wakeRequests++;

        source.RenderFrame(320, 240, TimeSpan.Zero);

        count!.Value = 1;
        count.Value = 2;
        count.Value = 3;
        Assert(wakeRequests == 1, "Repeated signal writes before a frame should request one wake.");

        var updated = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(16));
        Assert(
            updated.Commit.Layout.Values.Any(box => box.TextContent == "Count: 3"),
            "Coalesced frame should render the latest signal value."
        );

        count.Value = 4;
        count.Value = 5;
        Assert(
            wakeRequests == 2,
            "Signal writes after a frame should be able to request one new wake."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalBatchDefersAndCoalescesNotifications()
    {
        var signal = new Signal<int>(0);
        var changes = 0;
        signal.Changed += () => changes++;

        using (Signals.BeginBatch())
        {
            signal.Value = 1;
            signal.Value = 2;
            Assert(changes == 0, "Signal batch should defer notifications until dispose.");
        }

        Assert(changes == 1, "Signal batch should notify once for repeated writes.");

        Signals.Batch(() =>
        {
            signal.Value = 3;
            signal.Value = 4;
        });
        Assert(changes == 2, "Signal batch helper should notify once.");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalBatchDefersFrameWakeRequests()
    {
        Signal<int>? left = null;
        Signal<int>? right = null;
        var wakeRequests = 0;

        using var source = new SignalSceneFrameSource(() =>
        {
            (left, _) = Signals.CreateSignal(0);
            (right, _) = Signals.CreateSignal(0);
            return new Column()
                .Content(new Text().Content("Left: ").Content(left))
                .Content(new Text().Content("Right: ").Content(right));
        });
        source.RenderWakeRequested += () => wakeRequests++;

        source.RenderFrame(320, 240, TimeSpan.Zero);

        using (Signals.BeginBatch())
        {
            left!.Value = 1;
            right!.Value = 2;
            Assert(wakeRequests == 0, "Signal batch should defer frame wake requests.");
        }

        Assert(wakeRequests == 1, "Signal batch should produce one coalesced frame wake.");
        var updated = source.RenderFrame(320, 240, TimeSpan.FromMilliseconds(16));
        Assert(
            updated.Commit.Layout.Values.Any(box => box.TextContent == "Left: 1"),
            "Batched left signal should update after the batch."
        );
        Assert(
            updated.Commit.Layout.Values.Any(box => box.TextContent == "Right: 2"),
            "Batched right signal should update after the batch."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSignalUiLayoutChangingText()
    {
        Action<Func<string, string>>? setLabel = null;
        var viewCalls = 0;
        using var source = new SignalSceneFrameSource(() =>
        {
            var (label, setSignal) = Signals.CreateSignal("short");
            setLabel = setSignal;
            viewCalls++;
            return new Column().Padding(4).Content(new Text().FontSize(16).Content(label));
        });

        var first = source.RenderFrame(120, 160, TimeSpan.Zero);
        Assert(first.DirtyRects.Length == 1, "Initial layout-changing text test should render.");
        Assert(viewCalls == 1, "Initial layout-changing text test should call the view once.");

        setLabel!(_ =>
            "this dynamic label is deliberately long enough to wrap over several measured lines"
        );
        var second = source.RenderFrame(120, 160, TimeSpan.FromMilliseconds(16));
        Assert(
            second.DamageReasons.HasFlag(SceneDamageReason.FullFrameFallback),
            "Dynamic text that changes measured layout should rebuild the frame."
        );
        Assert(viewCalls == 1, "Layout-changing dynamic text should not rerender the root view.");
        Assert(
            second.Commit.Layout.Values.Any(box =>
                box.TextContent?.Contains("deliberately long", StringComparison.Ordinal) == true
            ),
            "Rebuilt layout should include the updated dynamic text."
        );

        return Task.CompletedTask;
    }
}
