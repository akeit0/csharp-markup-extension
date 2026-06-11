using Csmx.EnagaSignals;
using Csmx.Samples;
using Csmx.Samples.SignalApp.Components;
using Enaga.Hosting;
using Enaga.Rendering.Skia;
using Silk.NET.Maths;

using var source = new SignalSceneFrameSource(CounterView.Render);

if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
{
    var first = source.RenderFrame(420, 260, TimeSpan.Zero);
    var (x, y) = FindButtonCenter(first, "+1");
    source.PointerMove(x, y, 0, synthetic: false);
    source.PointerDown(0, 1, synthetic: false);
    source.PointerUp(0, 0, synthetic: false);
    var second = source.RenderFrame(420, 260, TimeSpan.FromMilliseconds(16));
    var countText =
        second
            .Commit.Layout.Values.Select(box => box.TextContent)
            .FirstOrDefault(text => text?.StartsWith("Count: ", StringComparison.Ordinal) == true)
        ?? "Count: <missing>";
    Console.WriteLine(
        $"nodes={second.Commit.Nodes.Count}; dirty={second.DirtyRects.Length}; {countText}"
    );
    return;
}

using var app = NativeWindowApp.Create(
    new SceneRenderRoot(source, requiresFullFramePresentation: true),
    new NativeWindowOptions
    {
        Title = "CSMX Signals",
        InitialSize = new Vector2D<int>(520, 360),
        PlatformIntegration = EnagaSamplePlatformIntegration.Create(),
        FramesPerSecond = 60,
    }
);
app.Run();

static (float X, float Y) FindButtonCenter(Enaga.Rendering.SceneFrameResult frame, string text)
{
    foreach (var (id, box) in frame.Commit.Layout)
    {
        if (!string.Equals(box.TextContent, text, StringComparison.Ordinal))
        {
            continue;
        }

        if (
            frame.Commit.Nodes.TryGetValue(id, out var textNode)
            && textNode.ParentId is { } parentId
            && frame.Commit.Layout.TryGetValue(parentId, out var parentBox)
        )
        {
            return (
                parentBox.AbsLeft + parentBox.Width / 2,
                parentBox.AbsTop + parentBox.Height / 2
            );
        }
    }

    throw new InvalidOperationException($"Button '{text}' was not found in the rendered scene.");
}
