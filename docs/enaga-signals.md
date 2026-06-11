# CSMX Enaga Signals

`Csmx.EnagaSignals` is a minimal C# UI runtime for CSMX. It is intentionally not a JavaScript bridge.

## Shape

CSMX uses generic fluent lowering:

```csharp
var (count, setCount) = CreateSignal(0);

<Text>Count: {count}</Text>
<Button Width={88} OnClick={() => setCount(value => value + 1)}>+1</Button>
<Button Width={88} OnClick={() => count.Set(0)}>Reset</Button>
```

Generated C#:

```csharp
var (count, setCount) = CreateSignal(0);

new Text()
    .Content("Count: ")
    .Content(count);

new Button()
    .Width(88)
    .OnClick(() => setCount(value => value + 1))
    .Content("+1");

new Button()
    .Width(88)
    .OnClick(() => count.Set(0))
    .Content("Reset");
```

The compiler does not know `Button`, `Column`, `Signal`, or Enaga. Those are framework/runtime types.

## Runtime

The runtime has three small parts:

- `Signal<T>` tracks reads during render and dynamic text evaluation, then raises invalidation on writes.
- `CreateSignal` stores local signal slots for the current frame source, so local state survives rebuilds without moving state outside the view.
- `Signals.Component(...)` creates a nested component scope with its own signal slots, so inserted or removed siblings do not shift another component's local state.
- Fluent UI nodes such as `Column`, `Row`, `Text`, `Button`, and `Panel` build a C# tree.
- Elements and text are separate: element content goes to `Children`, while strings, primitives, `Signal<T>`, and `Func<string>` become text content.
- `SignalSceneFrameSource` implements Enaga `ISceneFrameSource`, `IInputSink`, `IPointerCursorSource`, and `IRenderWakeSource`.

On render, `SignalSceneFrameSource` captures render-time signal reads, subscribes to those signals, builds an Enaga scene commit, and asks the host for a new frame when a signal changes. Dynamic text signals are tracked separately: if the new text fits the existing layout box, the runtime patches only that text node; if measurement changes the layout, it rebuilds the scene.

Wake requests are coalesced between frames. Multiple signal writes before the host calls `RenderFrame(...)` produce one `RenderWakeRequested` event, and the next frame observes the latest values.

Nested component scopes are explicit today:

```csharp
static UiElement Counter(string label)
{
    var (count, setCount) = CreateSignal(0);

    return new Row()
        .Content(new Text().Content(label).Content(": ").Content(count))
        .Content(new Button().OnClick(() => setCount(value => value + 1)).Content("+1"));
}

static UiNode Dashboard(bool showExtra)
{
    var root = new Column()
        .Content(Signals.Component("left-counter", () => Counter("Left")));

    if (showExtra)
    {
        root.Content(Signals.Component("extra-counter", () => Counter("Extra")));
    }

    return root.Content(Signals.Component("right-counter", () => Counter("Right")));
}
```

Each `callSite` is a stable identity inside its parent component. Future compiler lowering can generate source-position call-site IDs for component calls, while keyed list rendering can use `Signals.Component(callSite, key, render)`.

Keyed component scopes preserve local state through reorder and dispose state after removal:

```csharp
return new Column().Content(
    items.Select(item =>
        Signals.Component("todo-row", item.Id, () => TodoRow(item))));
```

If item `2` moves before item `1`, both rows keep their own signal slots. If item `1` is removed, its component state is discarded; adding item `1` later creates a fresh scope.

## Enaga Integration

The runtime uses:

- `Enaga.Layout.LayoutCalculator` for flex-style child placement.
- `Enaga.Scene.SceneLayoutCommit` and scene nodes for retained render output.
- `Enaga.Rendering.SceneFrameResult` as the render frame boundary.

The `samples/SignalApp` executable adds:

- `Enaga.Rendering.Skia.SceneRenderRoot`
- `Enaga.Hosting.NativeWindowApp`
- Host-selected native platform integration:
  - `Enaga.Platforms.Windows.WindowsNativeWindowPlatformIntegration` on Windows.
  - `Enaga.Platforms.Mac.MacNativeWindowPlatformIntegration` on macOS.
  - `Enaga.Hosting.DefaultNativeWindowPlatformIntegration` elsewhere.

## Sample

Run a non-window verification frame:

```bash
dotnet run --project samples/SignalApp/SignalApp.csproj -c Release -- --once
```

Open the native window:

```bash
dotnet run --project samples/SignalApp/SignalApp.csproj -c Release
```

The sample view lives in `samples/SignalApp/Components/CounterView.csmx`.
