using System.Text.Json;

namespace Csmx.LanguageServer;

internal sealed partial class LspServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Dictionary<string, OpenDocument> _documents = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CsmxRoslynWorkspace _roslynWorkspace = new();

    private CsmxProjectContextOptions _projectContextOptions = CsmxProjectContextOptions.Default;
    private bool _exitRequested;

    public LspServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync()
    {
        while (!_exitRequested)
        {
            var message = ReadMessage();
            if (message is null)
            {
                break;
            }

            using (message)
            {
                try
                {
                    await DispatchAsync(message.RootElement);
                }
                catch
                {
                    // Keep the editor session alive after transient malformed input.
                }
            }
        }
    }

    private async Task DispatchAsync(JsonElement root)
    {
        var hasId = root.TryGetProperty(JsonId, out var id);
        if (!root.TryGetProperty(JsonMethod, out var methodElement))
        {
            if (hasId)
            {
                await SendResponseAsync(id, result: null);
            }

            return;
        }

        if (methodElement.ValueEquals(MethodInitialize))
        {
            await SendResponseAsync(
                id,
                new
                {
                    capabilities = new
                    {
                        textDocumentSync = 1,
                        completionProvider = new
                        {
                            resolveProvider = false,
                            triggerCharacters = CompletionTriggerCharacters,
                        },
                        hoverProvider = true,
                        definitionProvider = true,
                        semanticTokensProvider = new
                        {
                            legend = new
                            {
                                tokenTypes = SemanticTokenTypes,
                                tokenModifiers = Array.Empty<string>(),
                            },
                            full = true,
                        },
                    },
                    serverInfo = new { name = "CSMX MVP Language Server", version = "0.2.0" },
                }
            );
            return;
        }

        if (methodElement.ValueEquals(MethodInitialized))
        {
            return;
        }

        if (methodElement.ValueEquals(MethodShutdown))
        {
            if (hasId)
            {
                await SendResponseAsync(id, result: null);
            }

            return;
        }

        if (methodElement.ValueEquals(MethodExit))
        {
            _exitRequested = true;
            return;
        }

        if (!root.TryGetProperty(JsonParams, out var parameters))
        {
            if (hasId)
            {
                await SendResponseAsync(id, result: null);
            }

            return;
        }

        if (methodElement.ValueEquals(MethodDidOpen))
        {
            HandleDidOpen(parameters);
            return;
        }

        if (methodElement.ValueEquals(MethodDidChange))
        {
            HandleDidChange(parameters);
            return;
        }

        if (methodElement.ValueEquals(MethodDidClose))
        {
            await HandleDidCloseAsync(parameters);
            return;
        }

        if (methodElement.ValueEquals(MethodCompletion))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleCompletion(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodHover))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleHover(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodDefinition))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleDefinition(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodSemanticTokensFull))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleSemanticTokens(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodGetGeneratedCSharp))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleGetGeneratedCSharp(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodInspectProjectBinding))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleInspectProjectBinding(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodReloadProjectContext))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleReloadProjectContext(parameters));
            }

            return;
        }

        if (methodElement.ValueEquals(MethodSetProjectContextOptions))
        {
            if (hasId)
            {
                await SendResponseAsync(id, HandleSetProjectContextOptions(parameters));
            }

            return;
        }

        if (hasId)
        {
            await SendResponseAsync(id, result: null);
        }
    }
}
