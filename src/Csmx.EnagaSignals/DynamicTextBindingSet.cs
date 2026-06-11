using Enaga.Rendering;
using Enaga.Scene;

namespace Csmx.EnagaSignals;

internal sealed class DynamicTextBindingSet : IDisposable
{
    private const float LayoutTolerance = 0.5f;

    private readonly IRuntimeTextServices textServices;
    private readonly Action requestRenderWake;
    private readonly List<DynamicTextBinding> bindings = [];

    public DynamicTextBindingSet(IRuntimeTextServices textServices, Action requestRenderWake)
    {
        this.textServices = textServices;
        this.requestRenderWake = requestRenderWake;
    }

    public bool HasDirty { get; private set; }

    public void Replace(IReadOnlyList<DynamicTextDescriptor> descriptors)
    {
        ClearSubscriptions();
        bindings.Clear();
        HasDirty = false;

        foreach (var descriptor in descriptors)
        {
            var binding = new DynamicTextBinding(
                descriptor.NodeId,
                descriptor.Element,
                descriptor.Text
            );
            binding.SignalChanged = () => OnSignalChanged(binding);
            ReplaceSubscriptions(binding, descriptor.Signals);
            bindings.Add(binding);
        }
    }

    public DynamicTextFrameUpdate Render(SceneLayoutCommit commit)
    {
        var layoutOverlay = SceneNodeMap<SceneLayoutBox>.CreateOverlay(
            commit.Layout,
            bindings.Count
        );
        var dirtyRects = new List<SceneDamageRect>();
        HasDirty = false;

        foreach (var binding in bindings)
        {
            if (!binding.Dirty)
            {
                continue;
            }

            binding.Dirty = false;
            var evaluation = SignalTextContent.Evaluate(binding.Element, captureSignals: true);
            ReplaceSubscriptions(binding, evaluation.Signals);
            if (string.Equals(binding.Text, evaluation.Text, StringComparison.Ordinal))
            {
                continue;
            }

            if (!commit.Layout.TryGetValue(binding.NodeId, out var box))
            {
                return DynamicTextFrameUpdate.RequiresRebuild;
            }

            if (!CanPatchInPlace(box, evaluation.Text))
            {
                binding.Text = evaluation.Text;
                return DynamicTextFrameUpdate.RequiresRebuild;
            }

            binding.Text = evaluation.Text;
            layoutOverlay[binding.NodeId] = box with
            {
                TextContent = evaluation.Text,
                LineHeight = textServices.MeasureLineHeight(
                    box.TextStyle?.Font ?? new SceneFont(16)
                ),
            };
            dirtyRects.Add(ToDirtyRect(box));
        }

        if (dirtyRects.Count == 0)
        {
            return DynamicTextFrameUpdate.NoDamage(commit);
        }

        var nextCommit = commit with { Layout = layoutOverlay };
        return DynamicTextFrameUpdate.Patched(
            new SceneFrameResult(nextCommit, dirtyRects.ToArray(), SceneDamageReason.FragmentDamage)
        );
    }

    public void Dispose()
    {
        ClearSubscriptions();
        bindings.Clear();
        HasDirty = false;
    }

    private bool CanPatchInPlace(SceneLayoutBox box, string text)
    {
        var style = box.TextStyle ?? new SceneTextStyle(16);
        var lineHeight = textServices.MeasureLineHeight(style.Font);
        var measuredHeight = Math.Max(
            1,
            textServices.MeasureTextHeight(text, Math.Max(1, box.Width), style)
        );

        if (
            MathF.Abs(measuredHeight - box.Height) > LayoutTolerance
            || MathF.Abs(lineHeight - box.LineHeight) > LayoutTolerance
        )
        {
            return false;
        }

        if (!style.WrapText)
        {
            var measuredWidth = textServices.MeasureTextWidth(text, style);
            if (measuredWidth - box.Width > LayoutTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private void OnSignalChanged(DynamicTextBinding binding)
    {
        binding.Dirty = true;
        HasDirty = true;
        requestRenderWake();
    }

    private static SceneDamageRect ToDirtyRect(SceneLayoutBox box)
    {
        var left = (int)MathF.Floor(box.AbsLeft);
        var top = (int)MathF.Floor(box.AbsTop);
        var right = (int)MathF.Ceiling(box.AbsLeft + box.Width);
        var bottom = (int)MathF.Ceiling(box.AbsTop + box.Height);
        return new SceneDamageRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void ReplaceSubscriptions(
        DynamicTextBinding binding,
        IReadOnlyCollection<ISignal> nextSignals
    )
    {
        foreach (var signal in binding.Signals)
        {
            if (!nextSignals.Contains(signal))
            {
                signal.Changed -= binding.SignalChanged;
            }
        }

        foreach (var signal in nextSignals)
        {
            if (!binding.Signals.Contains(signal))
            {
                signal.Changed += binding.SignalChanged;
            }
        }

        binding.Signals.Clear();
        foreach (var signal in nextSignals)
        {
            binding.Signals.Add(signal);
        }
    }

    private void ClearSubscriptions()
    {
        foreach (var binding in bindings)
        {
            foreach (var signal in binding.Signals)
            {
                signal.Changed -= binding.SignalChanged;
            }
        }
    }

    private sealed class DynamicTextBinding
    {
        public DynamicTextBinding(SceneNodeId nodeId, UiElement element, string text)
        {
            NodeId = nodeId;
            Element = element;
            Text = text;
        }

        public SceneNodeId NodeId { get; }
        public UiElement Element { get; }
        public HashSet<ISignal> Signals { get; } = [];
        public Action SignalChanged { get; set; } = null!;
        public string Text { get; set; }
        public bool Dirty { get; set; }
    }
}

internal readonly record struct DynamicTextDescriptor(
    SceneNodeId NodeId,
    UiElement Element,
    string Text,
    IReadOnlyCollection<ISignal> Signals
);

internal readonly record struct DynamicTextFrameUpdate(
    DynamicTextFrameUpdateKind Kind,
    SceneFrameResult? Frame
)
{
    public static DynamicTextFrameUpdate RequiresRebuild { get; } =
        new(DynamicTextFrameUpdateKind.RequiresRebuild, null);

    public static DynamicTextFrameUpdate NoDamage(SceneLayoutCommit commit) =>
        new(DynamicTextFrameUpdateKind.NoDamage, SceneFrameResult.NoDamage(commit));

    public static DynamicTextFrameUpdate Patched(SceneFrameResult frame) =>
        new(DynamicTextFrameUpdateKind.Patched, frame);
}

internal enum DynamicTextFrameUpdateKind
{
    NoDamage,
    Patched,
    RequiresRebuild,
}
