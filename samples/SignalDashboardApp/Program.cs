using System.Diagnostics;
using Csmx.EnagaSignals;
using Csmx.Samples;
using Csmx.Samples.SignalDashboardApp.Components;
using Enaga.Hosting;
using Enaga.Rendering;
using Enaga.Rendering.Skia;
using Silk.NET.Maths;

using var source = new SignalSceneFrameSource(Dashboard.Render);

if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
{
    var stopwatch = Stopwatch.StartNew();
    var first = source.RenderFrame(720, 420, TimeSpan.Zero);
    stopwatch.Stop();
    var firstFrameMs = stopwatch.Elapsed.TotalMilliseconds;
    ClickButton(source, first, "Reverse");
    var second = source.RenderFrame(720, 420, TimeSpan.FromMilliseconds(16));
    ClickButton(source, second, "Edit");
    var third = source.RenderFrame(720, 420, TimeSpan.FromMilliseconds(32));
    var summary = third
        .Commit.Layout.Values.Select(box => box.TextContent)
        .Where(text => !string.IsNullOrWhiteSpace(text))
        .Take(8);

    Console.WriteLine(
        $"firstFrameMs={firstFrameMs:F2}; nodes={third.Commit.Nodes.Count}; dirty={third.DirtyRects.Length}; renderCalls={Dashboard.RenderCalls}; text={string.Join(" | ", summary)}"
    );
    return;
}

using var app = NativeWindowApp.Create(
    new SceneRenderRoot(source, requiresFullFramePresentation: true),
    new NativeWindowOptions
    {
        Title = "CSMX Signal Dashboard",
        InitialSize = new Vector2D<int>(760, 460),
        PlatformIntegration = EnagaSamplePlatformIntegration.Create(),
        FramesPerSecond = 60,
    }
);
app.Run();

static void ClickButton(SignalSceneFrameSource source, SceneFrameResult frame, string text)
{
    var (x, y) = FindButtonCenter(frame, text);
    source.PointerMove(x, y, 0, synthetic: false);
    source.PointerDown(0, 1, synthetic: false);
    source.PointerUp(0, 0, synthetic: false);
}

static (float X, float Y) FindButtonCenter(SceneFrameResult frame, string text)
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
