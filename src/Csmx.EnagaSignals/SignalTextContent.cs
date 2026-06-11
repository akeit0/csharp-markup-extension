namespace Csmx.EnagaSignals;

internal static class SignalTextContent
{
    public static string GetText(UiElement element) =>
        string.Concat(element.TextContentItems.Select(item => item.GetText()));

    public static TextEvaluation Evaluate(UiElement element, bool captureSignals)
    {
        var hasDynamicContent = element.TextContentItems.Any(static item =>
            item is UiDynamicTextContent
        );
        if (!captureSignals || !hasDynamicContent)
        {
            return new TextEvaluation(
                string.Concat(element.TextContentItems.Select(item => item.GetText())),
                [],
                hasDynamicContent
            );
        }

        using var capture = new SignalCapture();
        var text = string.Concat(element.TextContentItems.Select(item => item.GetText()));
        return new TextEvaluation(text, capture.Signals.ToArray(), hasDynamicContent);
    }
}

internal readonly record struct TextEvaluation(
    string Text,
    IReadOnlyCollection<ISignal> Signals,
    bool HasDynamicContent
);
