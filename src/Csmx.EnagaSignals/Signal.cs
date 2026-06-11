namespace Csmx.EnagaSignals;

public interface ISignal
{
    event Action? Changed;
}

public sealed class Signal<T> : ISignal
{
    private T value;

    public Signal(T value)
    {
        this.value = value;
    }

    public event Action? Changed;

    public T Value
    {
        get
        {
            SignalTracker.Track(this);
            return value;
        }
        set
        {
            if (EqualityComparer<T>.Default.Equals(this.value, value))
            {
                return;
            }

            this.value = value;
            SignalBatch.Notify(this, NotifyChanged);
        }
    }

    public void Update(Func<T, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Value = update(value);
    }

    public void Set(T value)
    {
        Value = value;
    }

    public override string ToString() => Value?.ToString() ?? string.Empty;

    private void NotifyChanged() => Changed?.Invoke();
}

public static class Signals
{
    public static (Signal<T> Signal, Action<Func<T, T>> SetSignal) CreateSignal<T>(T value)
    {
        var signal = SignalRenderScope.Current is { } scope
            ? scope.GetOrCreateSignal(value)
            : new Signal<T>(value);
        return (signal, signal.Update);
    }

    public static IDisposable BeginBatch() => SignalBatch.Enter();

    public static void Batch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using (BeginBatch())
        {
            action();
        }
    }

    public static UiElement Component(string callSite, Func<UiElement> render)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callSite);
        ArgumentNullException.ThrowIfNull(render);

        return SignalRenderScope.Current is { } scope
            ? scope.CreateComponentHost(SignalComponentKey.FromCallSite(callSite), render)
            : render();
    }

    public static UiElement Component<TKey>(string callSite, TKey key, Func<UiElement> render)
        where TKey : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callSite);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(render);

        return SignalRenderScope.Current is { } scope
            ? scope.CreateComponentHost(SignalComponentKey.FromKey(callSite, key), render)
            : render();
    }

    public static IReadOnlyList<UiElement> KeyedChildren<TItem, TKey>(
        string callSite,
        IEnumerable<TItem> items,
        Func<TItem, TKey> keySelector,
        Func<TItem, UiElement> render
    )
        where TKey : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callSite);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(render);

        return items
            .Select(item => Component(callSite, keySelector(item), () => render(item)))
            .ToArray();
    }
}

internal sealed class SignalBatch : IDisposable
{
    [ThreadStatic]
    private static SignalBatch? current;

    private readonly SignalBatch? previous;
    private readonly Dictionary<ISignal, Action> pending = [];
    private bool disposed;

    private SignalBatch()
    {
        previous = current;
        current = this;
    }

    public static IDisposable Enter() => new SignalBatch();

    public static void Notify(ISignal signal, Action notify)
    {
        if (current is null)
        {
            notify();
            return;
        }

        current.pending[signal] = notify;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        current = previous;

        if (previous is not null)
        {
            foreach (var item in pending)
            {
                previous.pending[item.Key] = item.Value;
            }

            return;
        }

        foreach (var notify in pending.Values)
        {
            notify();
        }
    }
}

internal sealed class SignalRenderState
{
    private readonly List<object> slots = [];
    private readonly Dictionary<SignalComponentKey, SignalRenderState> childStates = [];
    private readonly HashSet<SignalComponentKey> activeChildKeys = [];
    private readonly HashSet<ISignal> renderSubscriptions = [];
    private int cursor;
    private bool rendering;
    private Action? renderInvalidated;

    public void BeginRender()
    {
        if (rendering)
        {
            throw new InvalidOperationException("Signal component render state is already active.");
        }

        rendering = true;
        cursor = 0;
        activeChildKeys.Clear();
    }

    public void EndRender()
    {
        if (!rendering)
        {
            return;
        }

        rendering = false;
        if (childStates.Count == activeChildKeys.Count)
        {
            return;
        }

        foreach (var key in childStates.Keys.ToArray())
        {
            if (!activeChildKeys.Contains(key))
            {
                childStates[key].Dispose();
                childStates.Remove(key);
            }
        }
    }

    public Signal<T> GetOrCreateSignal<T>(T value)
    {
        if (cursor == slots.Count)
        {
            var signal = new Signal<T>(value);
            slots.Add(signal);
            cursor++;
            return signal;
        }

        var slot = slots[cursor++];
        return slot is Signal<T> existing
            ? existing
            : throw new InvalidOperationException(
                "CreateSignal call order changed between renders."
            );
    }

    public SignalRenderState GetOrCreateChild(SignalComponentKey key)
    {
        if (!rendering)
        {
            throw new InvalidOperationException(
                "Nested signal components can only render inside an active signal render scope."
            );
        }

        if (!activeChildKeys.Add(key))
        {
            throw new InvalidOperationException(
                $"Signal component key '{key.DisplayName}' was used more than once in the same parent render."
            );
        }

        if (childStates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var child = new SignalRenderState();
        childStates.Add(key, child);
        return child;
    }

    public void ReplaceRenderSubscriptions(IEnumerable<ISignal> nextSignals, Action invalidated)
    {
        if (renderInvalidated is { } previous)
        {
            foreach (var signal in renderSubscriptions)
            {
                signal.Changed -= previous;
            }
        }

        renderSubscriptions.Clear();
        renderInvalidated = invalidated ?? throw new ArgumentNullException(nameof(invalidated));

        foreach (var signal in nextSignals)
        {
            if (renderSubscriptions.Add(signal))
            {
                signal.Changed += renderInvalidated;
            }
        }
    }

    public UiElement Render(Func<UiElement> render, Action invalidated)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(invalidated);

        IReadOnlyCollection<ISignal> renderSignals;
        UiElement node;
        using (var capture = new SignalCapture())
        using (SignalRenderScope.Enter(this, invalidated))
        {
            node = render();
            renderSignals = capture.Signals.ToArray();
        }

        ReplaceRenderSubscriptions(renderSignals, invalidated);
        return node;
    }

    public void Dispose()
    {
        if (renderInvalidated is { } invalidated)
        {
            foreach (var signal in renderSubscriptions)
            {
                signal.Changed -= invalidated;
            }
        }

        renderSubscriptions.Clear();
        renderInvalidated = null;

        foreach (var child in childStates.Values)
        {
            child.Dispose();
        }

        childStates.Clear();
        activeChildKeys.Clear();
        slots.Clear();
    }
}

internal readonly record struct SignalComponentKey(string CallSite, Type? KeyType, object? Key)
{
    public string DisplayName => KeyType is null ? CallSite : $"{CallSite}:{KeyType.Name}:{Key}";

    public static SignalComponentKey FromCallSite(string callSite) => new(callSite, null, null);

    public static SignalComponentKey FromKey<TKey>(string callSite, TKey key)
        where TKey : notnull => new(callSite, typeof(TKey), key);
}

internal sealed class SignalRenderScope : IDisposable
{
    [ThreadStatic]
    private static SignalRenderScope? current;

    private readonly SignalRenderScope? previous;
    private readonly SignalRenderState state;
    private readonly Action componentInvalidated;

    private SignalRenderScope(SignalRenderState state, Action componentInvalidated)
    {
        this.state = state;
        this.componentInvalidated =
            componentInvalidated ?? throw new ArgumentNullException(nameof(componentInvalidated));
        previous = current;
        state.BeginRender();
        current = this;
    }

    public static SignalRenderScope? Current => current;

    public static SignalRenderScope Enter(SignalRenderState state, Action componentInvalidated) =>
        new(state, componentInvalidated);

    public Signal<T> GetOrCreateSignal<T>(T value) => state.GetOrCreateSignal(value);

    public UiElement CreateComponentHost(SignalComponentKey key, Func<UiElement> render)
    {
        var child = state.GetOrCreateChild(key);
        return new UiComponentHost(child, render, componentInvalidated);
    }

    public void Dispose()
    {
        current = previous;
        state.EndRender();
    }
}

internal sealed class SignalCapture : IDisposable
{
    private readonly SignalCapture? previous;
    private readonly HashSet<ISignal> signals = [];

    public SignalCapture()
    {
        previous = SignalTracker.Current;
        SignalTracker.Current = this;
    }

    public IReadOnlyCollection<ISignal> Signals => signals;

    public void Add(ISignal signal) => signals.Add(signal);

    public void Dispose()
    {
        SignalTracker.Current = previous;
    }
}

internal static class SignalTracker
{
    [ThreadStatic]
    private static SignalCapture? current;

    public static SignalCapture? Current
    {
        get => current;
        set => current = value;
    }

    public static void Track(ISignal signal)
    {
        current?.Add(signal);
    }
}
