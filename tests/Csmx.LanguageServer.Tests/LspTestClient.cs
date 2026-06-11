using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Csmx.Tests;

sealed class LspTestClient : IAsyncDisposable
{
    private static readonly SemaphoreSlim StartLock = new(1, 1);

    private readonly Process process;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly object notificationLock = new();
    private readonly List<PublishedDiagnosticSet> publishedDiagnostics = new();
    private TaskCompletionSource<PublishedDiagnosticSet>? diagnosticsCompletion;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task reader;
    private int nextId;

    private LspTestClient(Process process)
    {
        this.process = process;
        reader = Task.Run(ReadLoopAsync);
    }

    public static async Task<LspTestClient> StartAsync()
    {
        await StartLock.WaitAsync();
        try
        {
            var root = FindRepositoryRoot();
            var serverDll = GetBuiltServerPath(root);
            var process = Process.Start(
                new ProcessStartInfo("dotnet", $"\"{serverDll}\"")
                {
                    WorkingDirectory = root,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            )!;

            return new LspTestClient(process);
        }
        finally
        {
            StartLock.Release();
        }
    }

    private static string GetBuiltServerPath(string root)
    {
        var configuration =
            typeof(LspTestClient)
                .Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
                ?.Configuration
            ?? "Debug";
        var serverDll = Path.Combine(
            root,
            "src",
            "Csmx.LanguageServer",
            "bin",
            configuration,
            "net10.0",
            "Csmx.LanguageServer.dll"
        );
        if (!File.Exists(serverDll))
        {
            throw new InvalidOperationException(
                $"Built language server not found at '{serverDll}'. Build Csmx.LanguageServer before running LSP tests."
            );
        }

        return serverDll;
    }

    public async Task InitializeAsync()
    {
        await RequestAsync(
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = "file:///" + FindRepositoryRoot().Replace('\\', '/'),
                capabilities = new { },
            }
        );
        Notify("initialized", new { });
    }

    public void DidOpen(string uri, string text) =>
        Notify(
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri,
                    languageId = "csmx",
                    version = 1,
                    text,
                },
            }
        );

    public void DidChange(string uri, string text, int version = 2) =>
        Notify(
            "textDocument/didChange",
            new { textDocument = new { uri, version }, contentChanges = new[] { new { text } } }
        );

    public async Task<string?> HoverAsync(string uri, Position position)
    {
        var response = await RequestAsync(
            "textDocument/hover",
            new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character },
            }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }

        return result.GetProperty("contents").GetProperty("value").GetString();
    }

    public async Task<IReadOnlyList<DefinitionLocationResult>> DefinitionAsync(
        string uri,
        Position position
    )
    {
        var response = await RequestAsync(
            "textDocument/definition",
            new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character },
            }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return Array.Empty<DefinitionLocationResult>();
        }

        if (result.ValueKind == JsonValueKind.Object)
        {
            return [ReadDefinitionLocation(result)];
        }

        if (result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DefinitionLocationResult>();
        }

        return result.EnumerateArray().Select(ReadDefinitionLocation).ToArray();
    }

    public async Task<IReadOnlyList<DecodedSemanticToken>> SemanticTokensAsync(string uri)
    {
        var response = await RequestAsync(
            "textDocument/semanticTokens/full",
            new { textDocument = new { uri } }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return Array.Empty<DecodedSemanticToken>();
        }

        var data = result
            .GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .ToArray();
        var tokens = new List<DecodedSemanticToken>();
        var line = 0;
        var character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            tokens.Add(new DecodedSemanticToken(line, character, data[i + 2], data[i + 3]));
        }

        return tokens;
    }

    public async Task<IReadOnlyList<CompletionItemResult>> CompletionAsync(
        string uri,
        Position position
    )
    {
        var response = await RequestAsync(
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character },
            }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return Array.Empty<CompletionItemResult>();
        }

        var items = result.GetProperty("items");
        return items
            .EnumerateArray()
            .Select(item => new CompletionItemResult(
                item.GetProperty("label").GetString() ?? string.Empty,
                item.TryGetProperty("detail", out var detail) ? detail.GetString() : null
            ))
            .ToArray();
    }

    public async Task<GeneratedCSharpResult> GeneratedCSharpAsync(string uri)
    {
        var response = await RequestAsync(
            "csmx/getGeneratedCSharp",
            new { textDocument = new { uri } }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return new GeneratedCSharpResult(string.Empty, null, null);
        }

        return new GeneratedCSharpResult(
            result.TryGetProperty("code", out var code)
                ? code.GetString() ?? string.Empty
                : string.Empty,
            result.TryGetProperty("projectFilePath", out var projectFilePath)
                ? projectFilePath.GetString()
                : null,
            result.TryGetProperty("generatedFilePath", out var generatedFilePath)
                ? generatedFilePath.GetString()
                : null
        );
    }

    public async Task<ProjectContextOptionsResult> SetProjectContextOptionsAsync(
        string? configuration = null,
        string? targetFramework = null
    )
    {
        var response = await RequestAsync(
            "csmx/setProjectContextOptions",
            new { configuration, targetFramework }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return new ProjectContextOptionsResult(null, null, false, 0, 0);
        }

        return new ProjectContextOptionsResult(
            result.TryGetProperty("configuration", out var configurationElement)
                ? configurationElement.GetString()
                : null,
            result.TryGetProperty("targetFramework", out var targetFrameworkElement)
                ? targetFrameworkElement.GetString()
                : null,
            result.TryGetProperty("changed", out var changed)
                && changed.ValueKind == JsonValueKind.True,
            result.TryGetProperty("clearedEntries", out var clearedEntries)
            && clearedEntries.ValueKind != JsonValueKind.Null
                ? clearedEntries.GetInt32()
                : 0,
            result.TryGetProperty("refreshedDocuments", out var refreshedDocuments)
            && refreshedDocuments.ValueKind != JsonValueKind.Null
                ? refreshedDocuments.GetInt32()
                : 0
        );
    }

    public async Task<ProjectBindingResult> ProjectBindingAsync(string uri)
    {
        var response = await RequestAsync(
            "csmx/inspectProjectBinding",
            new { textDocument = new { uri } }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return new ProjectBindingResult(false, null, null, null, null, null, null, null, []);
        }

        var dependencies =
            result.TryGetProperty("projectContextDependencies", out var dependencyArray)
            && dependencyArray.ValueKind == JsonValueKind.Array
                ? dependencyArray
                    .EnumerateArray()
                    .Select(item => new ProjectContextDependencyResult(
                        item.TryGetProperty("path", out var path)
                            ? path.GetString() ?? string.Empty
                            : string.Empty,
                        item.TryGetProperty("exists", out var exists)
                            && exists.ValueKind == JsonValueKind.True,
                        item.TryGetProperty("lastWriteUtcMilliseconds", out var lastWrite)
                        && lastWrite.ValueKind != JsonValueKind.Null
                            ? lastWrite.GetInt64()
                            : null
                    ))
                    .ToArray()
                : [];

        return new ProjectBindingResult(
            result.TryGetProperty("hasProject", out var hasProject)
                && hasProject.ValueKind == JsonValueKind.True,
            result.TryGetProperty("evaluationKind", out var evaluationKind)
                ? evaluationKind.GetString()
                : null,
            result.TryGetProperty("projectFilePath", out var projectFilePath)
                ? projectFilePath.GetString()
                : null,
            result.TryGetProperty("requestedConfiguration", out var requestedConfiguration)
                ? requestedConfiguration.GetString()
                : null,
            result.TryGetProperty("requestedTargetFramework", out var requestedTargetFramework)
                ? requestedTargetFramework.GetString()
                : null,
            result.TryGetProperty("generatedFilePath", out var generatedFilePath)
                ? generatedFilePath.GetString()
                : null,
            result.TryGetProperty("compileIncludesGeneratedFile", out var compileIncludes)
            && compileIncludes.ValueKind != JsonValueKind.Null
                ? compileIncludes.GetBoolean()
                : null,
            result.TryGetProperty("compileItemCount", out var compileItemCount)
            && compileItemCount.ValueKind != JsonValueKind.Null
                ? compileItemCount.GetInt32()
                : null,
            dependencies
        );
    }

    public async Task<ReloadProjectContextResult> ReloadProjectContextAsync(string uri)
    {
        var response = await RequestAsync(
            "csmx/reloadProjectContext",
            new { textDocument = new { uri } }
        );

        if (
            !response.TryGetProperty("result", out var result)
            || result.ValueKind == JsonValueKind.Null
        )
        {
            return new ReloadProjectContextResult(false, false, 0);
        }

        return new ReloadProjectContextResult(
            result.TryGetProperty("reloaded", out var reloaded)
                && reloaded.ValueKind == JsonValueKind.True,
            result.TryGetProperty("documentWasOpen", out var documentWasOpen)
                && documentWasOpen.ValueKind == JsonValueKind.True,
            result.TryGetProperty("clearedEntries", out var clearedEntries)
            && clearedEntries.ValueKind != JsonValueKind.Null
                ? clearedEntries.GetInt32()
                : 0
        );
    }

    public Task<IReadOnlyList<PublishedDiagnostic>> DiagnosticsAsync(string uri) =>
        WaitForDiagnosticsAsync(uri, _ => true);

    public async Task<IReadOnlyList<PublishedDiagnostic>> WaitForDiagnosticsAsync(
        string uri,
        Func<IReadOnlyList<PublishedDiagnostic>, bool> predicate
    )
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        lock (notificationLock)
        {
            var existing = publishedDiagnostics.LastOrDefault(item =>
                item.Uri == uri && predicate(item.Diagnostics)
            );
            if (existing is not null)
            {
                return existing.Diagnostics;
            }
        }

        while (true)
        {
            Task<PublishedDiagnosticSet> waitTask;
            lock (notificationLock)
            {
                var existing = publishedDiagnostics.LastOrDefault(item =>
                    item.Uri == uri && predicate(item.Diagnostics)
                );
                if (existing is not null)
                {
                    return existing.Diagnostics;
                }

                diagnosticsCompletion = new TaskCompletionSource<PublishedDiagnosticSet>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                waitTask = diagnosticsCompletion.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateDiagnosticTimeout(uri);
            }

            PublishedDiagnosticSet published;
            try
            {
                published = await waitTask.WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                throw CreateDiagnosticTimeout(uri);
            }

            if (published.Uri == uri && predicate(published.Diagnostics))
            {
                return published.Diagnostics;
            }
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        pending[id] = completion;
        await SendAsync(
            new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            }
        );
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void Notify(string method, object parameters) =>
        SendAsync(
                new
                {
                    jsonrpc = "2.0",
                    method,
                    @params = parameters,
                }
            )
            .GetAwaiter()
            .GetResult();

    private async Task SendAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";
        await process.StandardInput.WriteAsync(header);
        await process.StandardInput.WriteAsync(json);
        await process.StandardInput.FlushAsync();
    }

    private async Task ReadLoopAsync()
    {
        var headerBuffer = new List<byte>();
        while (!cancellation.IsCancellationRequested)
        {
            headerBuffer.Clear();
            while (true)
            {
                var value = process.StandardOutput.BaseStream.ReadByte();
                if (value < 0)
                {
                    return;
                }

                headerBuffer.Add((byte)value);
                if (
                    headerBuffer.Count >= 4
                    && headerBuffer[^4] == '\r'
                    && headerBuffer[^3] == '\n'
                    && headerBuffer[^2] == '\r'
                    && headerBuffer[^1] == '\n'
                )
                {
                    break;
                }
            }

            var header = Encoding.ASCII.GetString(headerBuffer.ToArray());
            var length = header
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts =>
                    parts.Length == 2
                    && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                )
                .Select(parts => int.Parse(parts[1].Trim()))
                .First();

            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var current = await process.StandardOutput.BaseStream.ReadAsync(
                    body.AsMemory(read, length - read),
                    cancellation.Token
                );
                if (current == 0)
                {
                    return;
                }

                read += current;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                if (pending.Remove(id, out var completion))
                {
                    completion.SetResult(root);
                }
            }
            else if (
                root.TryGetProperty("method", out var method)
                && method.GetString() == "textDocument/publishDiagnostics"
                && root.TryGetProperty("params", out var parameters)
            )
            {
                HandlePublishDiagnostics(parameters);
            }
        }
    }

    private void HandlePublishDiagnostics(JsonElement parameters)
    {
        var uri = parameters.TryGetProperty("uri", out var uriElement)
            ? uriElement.GetString() ?? string.Empty
            : string.Empty;
        var diagnostics = parameters.TryGetProperty("diagnostics", out var diagnosticsElement)
            ? diagnosticsElement
                .EnumerateArray()
                .Select(item => new PublishedDiagnostic(
                    item.TryGetProperty("code", out var code)
                        ? code.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("source", out var source)
                        ? source.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("message", out var message)
                        ? message.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("severity", out var severity) ? severity.GetInt32() : 0,
                    ReadDiagnosticPosition(item, "start"),
                    ReadDiagnosticPosition(item, "end")
                ))
                .ToArray()
            : Array.Empty<PublishedDiagnostic>();
        var published = new PublishedDiagnosticSet(uri, diagnostics);

        lock (notificationLock)
        {
            publishedDiagnostics.Add(published);
            diagnosticsCompletion?.TrySetResult(published);
            diagnosticsCompletion = null;
        }
    }

    private static Position ReadDiagnosticPosition(JsonElement diagnostic, string name)
    {
        if (
            diagnostic.TryGetProperty("range", out var range)
            && range.TryGetProperty(name, out var position)
            && position.TryGetProperty("line", out var line)
            && position.TryGetProperty("character", out var character)
        )
        {
            return new Position(line.GetInt32(), character.GetInt32());
        }

        return new Position(0, 0);
    }

    private static DefinitionLocationResult ReadDefinitionLocation(JsonElement location)
    {
        var uri = location.TryGetProperty("uri", out var uriElement)
            ? uriElement.GetString() ?? string.Empty
            : string.Empty;
        if (
            location.TryGetProperty("range", out var range)
            && range.TryGetProperty("start", out var start)
            && range.TryGetProperty("end", out var end)
        )
        {
            return new DefinitionLocationResult(uri, ReadPosition(start), ReadPosition(end));
        }

        return new DefinitionLocationResult(uri, new Position(0, 0), new Position(0, 0));
    }

    private static Position ReadPosition(JsonElement position)
    {
        var line = position.TryGetProperty("line", out var lineElement)
            ? lineElement.GetInt32()
            : 0;
        var character = position.TryGetProperty("character", out var characterElement)
            ? characterElement.GetInt32()
            : 0;
        return new Position(line, character);
    }

    private static string FormatDiagnostics(IReadOnlyList<PublishedDiagnostic> diagnostics) =>
        string.Join(
            " | ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Source}:{diagnostic.Code}@{diagnostic.Start.Line}:{diagnostic.Start.Character}-{diagnostic.End.Line}:{diagnostic.End.Character} {diagnostic.Message}"
            )
        );

    private TimeoutException CreateDiagnosticTimeout(string uri)
    {
        IReadOnlyList<PublishedDiagnosticSet> matchingDiagnostics;
        lock (notificationLock)
        {
            matchingDiagnostics = publishedDiagnostics.Where(item => item.Uri == uri).ToArray();
        }

        return new TimeoutException(
            $"Timed out waiting for diagnostics for '{uri}'. Published diagnostics: {FormatDiagnosticSets(matchingDiagnostics)}"
        );
    }

    private static string FormatDiagnosticSets(IReadOnlyList<PublishedDiagnosticSet> diagnostics) =>
        string.Join(
            " || ",
            diagnostics.Select(
                (item, index) => $"#{index + 1}: {FormatDiagnostics(item.Diagnostics)}"
            )
        );

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Test cleanup best effort.
            }
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        process.Dispose();
        cancellation.Dispose();
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Csmx.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}

readonly record struct Position(int Line, int Character);

readonly record struct DecodedSemanticToken(int Line, int Character, int Length, int TypeIndex);

readonly record struct CompletionItemResult(string Label, string? Detail);

readonly record struct DefinitionLocationResult(string Uri, Position Start, Position End);

readonly record struct GeneratedCSharpResult(
    string Code,
    string? ProjectFilePath,
    string? GeneratedFilePath
);

readonly record struct ProjectContextOptionsResult(
    string? Configuration,
    string? TargetFramework,
    bool Changed,
    int ClearedEntries,
    int RefreshedDocuments
);

readonly record struct ProjectBindingResult(
    bool HasProject,
    string? EvaluationKind,
    string? ProjectFilePath,
    string? RequestedConfiguration,
    string? RequestedTargetFramework,
    string? GeneratedFilePath,
    bool? CompileIncludesGeneratedFile,
    int? CompileItemCount,
    IReadOnlyList<ProjectContextDependencyResult> ProjectContextDependencies
);

readonly record struct ProjectContextDependencyResult(
    string Path,
    bool Exists,
    long? LastWriteUtcMilliseconds
);

readonly record struct ReloadProjectContextResult(
    bool Reloaded,
    bool DocumentWasOpen,
    int ClearedEntries
);

sealed record PublishedDiagnosticSet(string Uri, IReadOnlyList<PublishedDiagnostic> Diagnostics);

readonly record struct PublishedDiagnostic(
    string Code,
    string Source,
    string Message,
    int Severity,
    Position Start,
    Position End
);
