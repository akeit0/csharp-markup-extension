using System.Text.Json;
using Csmx.Compiler;

namespace Csmx.LanguageServer;

internal sealed partial class LspServer
{
    private void HandleDidOpen(JsonElement parameters)
    {
        if (
            !parameters.TryGetProperty(JsonTextDocument, out var textDocument)
            || !textDocument.TryGetProperty(JsonUri, out var uriElement)
            || !textDocument.TryGetProperty(JsonText, out var textElement)
        )
        {
            return;
        }

        var uri = uriElement.GetString() ?? string.Empty;
        if (uri.Length == 0)
        {
            return;
        }

        var text = textElement.GetString() ?? string.Empty;
        var version = textDocument.TryGetProperty(JsonVersion, out var versionElement)
            ? versionElement.GetInt32()
            : 0;

        UpdateDocument(uri, text, version);
    }

    private void HandleDidChange(JsonElement parameters)
    {
        if (
            !parameters.TryGetProperty(JsonTextDocument, out var textDocument)
            || !textDocument.TryGetProperty(JsonUri, out var uriElement)
        )
        {
            return;
        }

        var uri = uriElement.GetString() ?? string.Empty;
        if (uri.Length == 0)
        {
            return;
        }

        var version = textDocument.TryGetProperty(JsonVersion, out var versionElement)
            ? versionElement.GetInt32()
            : 0;

        if (
            !parameters.TryGetProperty(JsonContentChanges, out var changes)
            || changes.GetArrayLength() == 0
            || !changes[0].TryGetProperty(JsonText, out var textElement)
        )
        {
            return;
        }

        var text = textElement.GetString() ?? string.Empty;
        UpdateDocument(uri, text, version);
    }

    private async Task HandleDidCloseAsync(JsonElement parameters)
    {
        if (!TryReadTextDocumentUri(parameters, out var uri))
        {
            return;
        }

        _documents.Remove(uri);
        await PublishDiagnosticsAsync(uri, Array.Empty<object>());
    }

    private void UpdateDocument(string uri, string text, int version)
    {
        var sourcePath = TryGetFilePathFromUri(uri);
        var projectContext = CsmxProjectContext.TryCreate(sourcePath, _projectContextOptions);
        var transformed = CsmxTransformer.Transform(
            text,
            sourcePath,
            projectContext?.TransformOptions ?? CsmxTransformOptions.Default
        );
        var lineMap = LineMap.FromText(text);

        var document = new OpenDocument(uri, text, version, transformed, lineMap, projectContext);
        _documents[uri] = document;
        QueuePublishDocumentDiagnostics(document);
    }

    private OpenDocument RefreshProjectContextIfNeeded(string uri, OpenDocument document)
    {
        var sourcePath = TryGetFilePathFromUri(uri);
        var projectContext = CsmxProjectContext.TryCreate(sourcePath, _projectContextOptions);
        if (ReferenceEquals(projectContext, document.ProjectContext))
        {
            return document;
        }

        if (projectContext is null && document.ProjectContext is null)
        {
            return document;
        }

        var transformed = CsmxTransformer.Transform(
            document.Text,
            sourcePath,
            projectContext?.TransformOptions ?? CsmxTransformOptions.Default
        );
        var refreshed = document with { Transform = transformed, ProjectContext = projectContext };

        _documents[uri] = refreshed;
        QueuePublishDocumentDiagnostics(refreshed);
        return refreshed;
    }

    private void QueuePublishDocumentDiagnostics(OpenDocument document)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishDocumentDiagnosticsAsync(document);
            }
            catch
            {
                // Diagnostics are best-effort; request handlers must keep serving the editor.
            }
        });
    }

    private async Task PublishDocumentDiagnosticsAsync(OpenDocument document)
    {
        var diagnostics = document
            .Transform.Diagnostics.Select(diagnostic =>
                CreateDiagnostic(
                    document.LineMap,
                    diagnostic.Span.Start,
                    diagnostic.Span.Length,
                    ToLspDiagnosticSeverity(diagnostic.Severity),
                    diagnostic.Code,
                    "csmx",
                    diagnostic.Message
                )
            )
            .ToList();

        if (
            document.ProjectContext is not null
            && !document.Transform.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == CsmxDiagnosticSeverity.Error
            )
            && TryCreateDocumentSnapshot(document, out var snapshot)
        )
        {
            var roslynDiagnostics = _roslynWorkspace.GetDiagnostics(snapshot);
            diagnostics.AddRange(
                roslynDiagnostics.Select(diagnostic =>
                    CreateDiagnostic(
                        document.LineMap,
                        diagnostic.Start,
                        diagnostic.Length,
                        diagnostic.Severity,
                        diagnostic.Code,
                        "csharp",
                        diagnostic.Message
                    )
                )
            );
        }

        await PublishDiagnosticsAsync(document.Uri, diagnostics);
    }

    private async Task PublishDiagnosticsAsync(string uri, object diagnostics)
    {
        await SendNotificationAsync("textDocument/publishDiagnostics", new { uri, diagnostics });
    }

    private static string? TryGetFilePathFromUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) || !parsedUri.IsFile)
        {
            return null;
        }

        if (Path.DirectorySeparatorChar == '\\' && string.IsNullOrEmpty(parsedUri.Host))
        {
            var absolutePath = Uri.UnescapeDataString(parsedUri.AbsolutePath).Replace('/', '\\');
            if (
                absolutePath.Length >= 3
                && absolutePath[0] == '\\'
                && char.IsLetter(absolutePath[1])
                && absolutePath[2] == ':'
            )
            {
                return absolutePath[1..];
            }
        }

        return parsedUri.LocalPath;
    }

    private static bool TryCreateDocumentSnapshot(
        OpenDocument document,
        out OpenDocumentSnapshot snapshot
    )
    {
        var sourcePath = TryGetFilePathFromUri(document.Uri);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            snapshot = null!;
            return false;
        }

        snapshot = new OpenDocumentSnapshot(
            document.Uri,
            document.Text,
            sourcePath,
            document.Transform,
            document.ProjectContext
        );
        return true;
    }

    private static int ToLspDiagnosticSeverity(CsmxDiagnosticSeverity severity) =>
        severity switch
        {
            CsmxDiagnosticSeverity.Error => 1,
            CsmxDiagnosticSeverity.Warning => 2,
            CsmxDiagnosticSeverity.Information => 3,
            _ => 1,
        };

    private static LspDiagnostic CreateDiagnostic(
        LineMap lineMap,
        int start,
        int length,
        int severity,
        string code,
        string source,
        string message
    )
    {
        var startPosition = lineMap.GetLinePosition(start);
        var endPosition = lineMap.GetLinePosition(start + Math.Max(0, length));
        return new LspDiagnostic(
            new LspRange(
                new LspPosition(startPosition.Line, startPosition.Character),
                new LspPosition(endPosition.Line, endPosition.Character)
            ),
            severity,
            code,
            source,
            message
        );
    }

    private sealed record OpenDocument(
        string Uri,
        string Text,
        int Version,
        CsmxTransformResult Transform,
        LineMap LineMap,
        CsmxProjectContext? ProjectContext
    );

    private sealed record LspDiagnostic(
        LspRange Range,
        int Severity,
        string Code,
        string Source,
        string Message
    );

    private sealed record LspRange(LspPosition Start, LspPosition End);

    private sealed record LspPosition(int Line, int Character);
}
