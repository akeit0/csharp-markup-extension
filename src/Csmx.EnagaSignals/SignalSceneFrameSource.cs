using Enaga.Input;
using Enaga.Layout;
using Enaga.Rendering;
using Enaga.Scene;

namespace Csmx.EnagaSignals;

public sealed class SignalSceneFrameSource
    : ISceneFrameSource,
        IRenderWakeSource,
        IInputSink,
        IPointerCursorSource,
        IDisposable
{
    private readonly Func<UiNode> render;
    private readonly RuntimeBackendServices backendServices;
    private readonly LayoutCalculator layoutCalculator;
    private readonly object sync = new();
    private readonly List<(SceneScreenBounds Bounds, Action Handler)> clickTargets = [];
    private readonly HashSet<ISignal> renderSubscriptions = [];
    private readonly DynamicTextBindingSet dynamicTextBindings;
    private readonly SignalRenderState renderState = new();
    private UiNode? lastRoot;
    private SceneLayoutCommit? lastCommit;
    private bool dirty = true;
    private bool componentDirty;
    private int lastWidth;
    private int lastHeight;
    private float pointerX;
    private float pointerY;
    private PointerCursorKind cursor = PointerCursorKind.Default;
    private Action? pressedClickHandler;
    private SceneScreenBounds pressedClickBounds;
    private bool hasPressedClickTarget;
    private string? lastError;
    private bool renderWakePending;

    public SignalSceneFrameSource(
        Func<UiNode> render,
        RuntimeBackendServices? backendServices = null
    )
    {
        this.render = render ?? throw new ArgumentNullException(nameof(render));
        this.backendServices = backendServices ?? DummyRuntimeBackendServices.Create();
        layoutCalculator = new LayoutCalculator(this.backendServices.Text);
        dynamicTextBindings = new DynamicTextBindingSet(
            this.backendServices.Text,
            OnDynamicTextSignalChanged
        );
    }

    public string? LastError => lastError;

    public RuntimeBackendServices BackendServices => backendServices;

    public PointerCursorKind CurrentCursor => cursor;

    public event Action? RenderWakeRequested;

    public SceneFrameResult RenderFrame(int width, int height, TimeSpan elapsed)
    {
        lock (sync)
        {
            renderWakePending = false;

            if (
                !dirty
                && dynamicTextBindings.HasDirty
                && !componentDirty
                && lastCommit is not null
                && width == lastWidth
                && height == lastHeight
            )
            {
                return RenderDynamicTextFrame();
            }

            if (
                !dirty
                && !componentDirty
                && lastCommit is not null
                && width == lastWidth
                && height == lastHeight
            )
            {
                return SceneFrameResult.NoDamage(lastCommit);
            }

            try
            {
                lastError = null;
                var commit = BuildCommit(Math.Max(1, width), Math.Max(1, height));
                lastCommit = commit;
                lastWidth = width;
                lastHeight = height;
                dirty = false;
                componentDirty = false;
                return SceneFrameResult.FullFrame(
                    commit,
                    width,
                    height,
                    SceneDamageReason.RuntimeReload
                );
            }
            catch (Exception ex)
            {
                lastError = ex.ToString();
                if (lastCommit is not null)
                {
                    return SceneFrameResult.NoDamage(lastCommit);
                }

                var fallback = BuildErrorCommit(
                    Math.Max(1, width),
                    Math.Max(1, height),
                    ex.Message
                );
                lastCommit = fallback;
                return SceneFrameResult.FullFrame(
                    fallback,
                    width,
                    height,
                    SceneDamageReason.ErrorOverlay
                );
            }
        }
    }

    public void PointerMove(float x, float y, int buttons, bool synthetic)
    {
        lock (sync)
        {
            pointerX = x;
            pointerY = y;
            cursor = FindClickTarget(x, y) is null
                ? PointerCursorKind.Default
                : PointerCursorKind.Pointer;
        }
    }

    public void PointerDown(int button, int buttons, bool synthetic)
    {
        if (button != 0)
        {
            return;
        }

        lock (sync)
        {
            var target = FindClickTarget(pointerX, pointerY);
            if (target is { } clickTarget)
            {
                pressedClickHandler = clickTarget.Handler;
                pressedClickBounds = clickTarget.Bounds;
                hasPressedClickTarget = true;
            }
            else
            {
                ClearPressedClickTarget();
            }
        }
    }

    public void PointerUp(int button, int buttons, bool synthetic)
    {
        if (button != 0)
        {
            return;
        }

        Action? handler = null;
        lock (sync)
        {
            if (
                hasPressedClickTarget
                && pressedClickBounds.Contains(pointerX, pointerY)
                && FindClickTarget(pointerX, pointerY) is { } target
                && Equals(target.Handler, pressedClickHandler)
            )
            {
                handler = target.Handler;
            }

            ClearPressedClickTarget();
        }

        handler?.Invoke();
    }

    public void Wheel(float deltaX, float deltaY, bool synthetic, int modifiers = 0) { }

    public void KeyDown(string key, int modifiers, bool repeat, bool synthetic) { }

    public void KeyUp(string key, int modifiers, bool synthetic) { }

    public void TextInput(string text, bool synthetic) { }

    public void Dispose()
    {
        ReplaceRenderSubscriptions([]);
        renderState.Dispose();
        dynamicTextBindings.Dispose();
        backendServices.Dispose();
    }

    private SceneLayoutCommit BuildCommit(int width, int height)
    {
        UiNode root;
        if (dirty || lastRoot is null)
        {
            IReadOnlyCollection<ISignal> renderSignals;
            using (var capture = new SignalCapture())
            using (SignalRenderScope.Enter(renderState, OnComponentSignalChanged))
            {
                root = render();
                renderSignals = capture.Signals.ToArray();
            }

            ReplaceRenderSubscriptions(renderSignals);
            lastRoot = root;
        }
        else
        {
            root = lastRoot;
        }

        var dynamicTextDescriptors = new List<DynamicTextDescriptor>();
        var builder = new SignalSceneBuilder(
            layoutCalculator,
            backendServices.Text,
            clickTargets,
            dynamicTextDescriptors,
            width,
            height
        );
        var commit = builder.Build(root);
        dynamicTextBindings.Replace(dynamicTextDescriptors);
        return commit;
    }

    private SceneFrameResult RenderDynamicTextFrame()
    {
        if (lastCommit is null)
        {
            dirty = true;
            return SceneFrameResult.NoDamage(
                BuildCommit(Math.Max(1, lastWidth), Math.Max(1, lastHeight))
            );
        }

        var update = dynamicTextBindings.Render(lastCommit);
        if (update.Kind == DynamicTextFrameUpdateKind.RequiresRebuild)
        {
            var commit = BuildCommit(Math.Max(1, lastWidth), Math.Max(1, lastHeight));
            lastCommit = commit;
            dirty = false;
            return SceneFrameResult.FullFrame(
                commit,
                lastWidth,
                lastHeight,
                SceneDamageReason.FragmentDamage | SceneDamageReason.FullFrameFallback
            );
        }

        if (update.Frame is null)
        {
            return SceneFrameResult.NoDamage(lastCommit);
        }

        lastCommit = update.Frame.Commit;
        return update.Frame;
    }

    private void ReplaceRenderSubscriptions(IEnumerable<ISignal> nextSignals)
    {
        foreach (var signal in renderSubscriptions)
        {
            signal.Changed -= OnSignalChanged;
        }

        renderSubscriptions.Clear();
        foreach (var signal in nextSignals)
        {
            if (renderSubscriptions.Add(signal))
            {
                signal.Changed += OnSignalChanged;
            }
        }
    }

    private void OnSignalChanged()
    {
        var shouldRequestWake = false;
        lock (sync)
        {
            dirty = true;
            shouldRequestWake = TryMarkRenderWakePending();
        }

        if (shouldRequestWake)
        {
            RenderWakeRequested?.Invoke();
        }
    }

    private void OnComponentSignalChanged()
    {
        var shouldRequestWake = false;
        lock (sync)
        {
            componentDirty = true;
            shouldRequestWake = TryMarkRenderWakePending();
        }

        if (shouldRequestWake)
        {
            RenderWakeRequested?.Invoke();
        }
    }

    private void OnDynamicTextSignalChanged()
    {
        var shouldRequestWake = false;
        lock (sync)
        {
            shouldRequestWake = TryMarkRenderWakePending();
        }

        if (shouldRequestWake)
        {
            RenderWakeRequested?.Invoke();
        }
    }

    private bool TryMarkRenderWakePending()
    {
        if (renderWakePending)
        {
            return false;
        }

        renderWakePending = true;
        return true;
    }

    private void ClearPressedClickTarget()
    {
        pressedClickHandler = null;
        pressedClickBounds = default;
        hasPressedClickTarget = false;
    }

    private (SceneScreenBounds Bounds, Action Handler)? FindClickTarget(float x, float y)
    {
        (SceneScreenBounds Bounds, Action Handler)? target = null;
        var current = default(SceneScreenBounds);
        var currentZOrder = -1;
        for (var index = 0; index < clickTargets.Count; index++)
        {
            var candidate = clickTargets[index];
            if (!candidate.Bounds.Contains(x, y))
            {
                continue;
            }

            if (
                target is null
                || SceneScreenBounds.IsHigherPriority(
                    candidate.Bounds,
                    index,
                    current,
                    currentZOrder
                )
            )
            {
                current = candidate.Bounds;
                currentZOrder = index;
                target = candidate;
            }
        }

        return target;
    }

    private static SceneLayoutCommit BuildErrorCommit(int width, int height, string message)
    {
        var rootId = new SceneNodeId(1);
        var textId = new SceneNodeId(2);
        var nodes = new SceneNodeMap<SceneGraphNode>
        {
            [rootId] = new SceneGraphNode(SceneNodeKind.View, null, [textId], "root"),
            [textId] = new SceneGraphNode(SceneNodeKind.Text, rootId, [], "error"),
        };
        var layout = new SceneNodeMap<SceneLayoutBox>
        {
            [rootId] = new SceneLayoutBox(
                SceneNodeKind.View,
                0,
                0,
                width,
                height,
                BackgroundColor: "#330000"
            ),
            [textId] = new SceneLayoutBox(
                SceneNodeKind.Text,
                24,
                24,
                Math.Max(1, width - 48),
                40,
                TextContent: message,
                TextStyle: new SceneTextStyle(16, "#ffffff"),
                LineHeight: 22
            ),
        };

        return SceneLayoutCommitFactory.Create(
            rootId,
            new SceneViewport(width, height),
            nodes,
            layout
        );
    }
}
