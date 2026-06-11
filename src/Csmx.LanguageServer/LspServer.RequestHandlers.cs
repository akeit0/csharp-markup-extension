using System.Text.Json;
using Csmx.Compiler;

namespace Csmx.LanguageServer;

internal sealed partial class LspServer
{
    private static readonly object EmptyCompletionResult = new
    {
        isIncomplete = false,
        items = Array.Empty<object>(),
    };

    private object HandleCompletion(JsonElement parameters)
    {
        if (
            !TryReadDocumentPosition(parameters, out var uri, out var line, out var character)
            || !_documents.TryGetValue(uri, out var document)
        )
        {
            return EmptyCompletionResult;
        }

        var index = document.LineMap.GetIndex(line, character);
        var isTagCompletion = IsTagCompletion(document.Text, index);
        var isAttributeCompletion = IsAttributeCompletion(document.Text, index);
        if (!isTagCompletion && !isAttributeCompletion)
        {
            if (TryCreateDocumentSnapshot(document, out var csharpSnapshot))
            {
                var csharpItems = new Dictionary<string, object>(StringComparer.Ordinal);
                AddRoslynCompletionItems(
                    csharpItems,
                    _roslynWorkspace.GetCSharpCompletions(csharpSnapshot, index)
                );
                return CompletionList(csharpItems.Values.ToArray());
            }

            return EmptyCompletionResult;
        }

        var completionFacts = CsmxSourceFacts.GetCompletionFacts(document.Text);
        var items = new Dictionary<string, object>(StringComparer.Ordinal);

        if (isTagCompletion)
        {
            AddTagCompletionItems(items, completionFacts);
            if (TryCreateDocumentSnapshot(document, out var snapshot))
            {
                AddRoslynCompletionItems(
                    items,
                    _roslynWorkspace.GetTagCompletions(snapshot, index)
                );
            }

            return CompletionList(items.Values.ToArray());
        }

        var attributes = GetAttributeCompletions(
            completionFacts,
            GetCurrentOpeningTagName(document.Text, index)
        );
        foreach (var attribute in attributes)
        {
            AddCompletionItem(
                items,
                attribute,
                new
                {
                    label = attribute,
                    kind = 10,
                    detail = "CSMX attribute from this file",
                }
            );
        }

        if (TryCreateDocumentSnapshot(document, out var attributeSnapshot))
        {
            AddRoslynCompletionItems(
                items,
                _roslynWorkspace.GetAttributeCompletions(
                    attributeSnapshot,
                    index,
                    GetCurrentOpeningTagName(document.Text, index)
                )
            );
        }

        return CompletionList(items.Values.ToArray());
    }

    private static void AddTagCompletionItems(
        Dictionary<string, object> items,
        CsmxSourceCompletionFacts facts
    )
    {
        foreach (
            var component in facts.Components.OrderBy(
                component => component.Name,
                StringComparer.Ordinal
            )
        )
        {
            AddCompletionItem(
                items,
                component.Name,
                new
                {
                    label = component.Name,
                    kind = 3,
                    detail = component.PropsType is null
                        ? "CSMX component from this file"
                        : $"CSMX component props: {component.PropsType}",
                }
            );
        }

        foreach (var element in facts.IntrinsicElements)
        {
            AddCompletionItem(
                items,
                element,
                new
                {
                    label = element,
                    kind = 7,
                    detail = "CSMX intrinsic element from this file",
                }
            );
        }
    }

    private static void AddRoslynCompletionItems(
        Dictionary<string, object> items,
        IReadOnlyList<RoslynCompletionItem> roslynItems
    )
    {
        foreach (var item in roslynItems)
        {
            AddCompletionItem(
                items,
                item.Label,
                new
                {
                    label = item.Label,
                    kind = item.Kind,
                    detail = item.Detail,
                }
            );
        }
    }

    private static void AddCompletionItem(
        Dictionary<string, object> items,
        string label,
        object item
    )
    {
        if (!items.ContainsKey(label))
        {
            items.Add(label, item);
        }
    }

    private static IEnumerable<string> GetAttributeCompletions(
        CsmxSourceCompletionFacts facts,
        string? elementName
    )
    {
        var attributes = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(elementName))
        {
            var elementFacts = facts.ElementAttributes.FirstOrDefault(item =>
                string.Equals(item.ElementName, elementName, StringComparison.Ordinal)
            );
            if (elementFacts is not null)
            {
                foreach (var attribute in elementFacts.Attributes)
                {
                    attributes.Add(attribute);
                }
            }
        }

        foreach (var attribute in facts.Attributes)
        {
            attributes.Add(attribute);
        }

        return attributes;
    }

    private static object CompletionList(IReadOnlyList<object> items) =>
        new { isIncomplete = false, items };

    private object? HandleHover(JsonElement parameters)
    {
        if (
            !TryReadDocumentPosition(parameters, out var uri, out var line, out var character)
            || !_documents.TryGetValue(uri, out var document)
        )
        {
            return null;
        }

        var index = document.LineMap.GetIndex(line, character);
        if (TryCreateDocumentSnapshot(document, out var snapshot))
        {
            var roslynHover = _roslynWorkspace.GetHover(snapshot, index);
            if (roslynHover is not null)
            {
                return new { contents = new { kind = "markdown", value = roslynHover.Markdown } };
            }
        }

        var value = GetSyntaxHover(document.Text, index);

        return value is null ? null : new { contents = new { kind = "markdown", value } };
    }

    private object HandleDefinition(JsonElement parameters)
    {
        if (
            !TryReadDocumentPosition(parameters, out var uri, out var line, out var character)
            || !_documents.TryGetValue(uri, out var document)
            || !TryCreateDocumentSnapshot(document, out var snapshot)
        )
        {
            return Array.Empty<object>();
        }

        var index = document.LineMap.GetIndex(line, character);
        return _roslynWorkspace
            .GetDefinitions(snapshot, index)
            .Select(definition => new
            {
                uri = definition.Uri,
                range = new LspRange(
                    new LspPosition(definition.StartLine, definition.StartCharacter),
                    new LspPosition(definition.EndLine, definition.EndCharacter)
                ),
            })
            .Cast<object>()
            .ToArray();
    }

    private object HandleGetGeneratedCSharp(JsonElement parameters)
    {
        if (
            !TryReadTextDocumentUri(parameters, out var uri)
            || !_documents.TryGetValue(uri, out var document)
        )
        {
            return new { code = string.Empty };
        }

        document = RefreshProjectContextIfNeeded(uri, document);

        return new
        {
            code = document.Transform.Code,
            projectFilePath = document.ProjectContext?.ProjectFilePath,
            generatedFilePath = document.ProjectContext?.GeneratedFilePath,
            projectContextDependencies = ToDependencyResponse(
                document.ProjectContext?.CacheDependencies
            ),
            mappings = document
                .Transform.Mappings.Select(mapping => new
                {
                    originalStart = mapping.OriginalSpan.Start,
                    originalLength = mapping.OriginalSpan.Length,
                    generatedStart = mapping.GeneratedSpan.Start,
                    generatedLength = mapping.GeneratedSpan.Length,
                    kind = mapping.Kind.ToString(),
                })
                .ToArray(),
        };
    }

    private object HandleInspectProjectBinding(JsonElement parameters)
    {
        if (!TryReadTextDocumentUri(parameters, out var uri))
        {
            return new
            {
                uri = string.Empty,
                hasProject = false,
                messages = new[] { "Missing textDocument.uri." },
            };
        }

        var sourcePath = TryGetFilePathFromUri(uri);
        var context = CsmxProjectContext.Inspect(sourcePath, _projectContextOptions);
        if (context is null)
        {
            return new
            {
                uri,
                sourceFilePath = sourcePath,
                hasProject = false,
                messages = new[] { "No containing .csproj was found." },
            };
        }

        return new
        {
            uri,
            sourceFilePath = context.SourceFilePath,
            hasProject = true,
            projectFilePath = context.ProjectFilePath,
            projectDirectory = context.ProjectDirectory,
            relativeSourcePath = context.RelativeSourcePath,
            evaluationKind = context.EvaluationKind,
            requestedConfiguration = _projectContextOptions.Configuration,
            requestedTargetFramework = _projectContextOptions.TargetFramework,
            configuration = context.Configuration,
            targetFramework = context.TargetFramework,
            generatedDirectory = context.GeneratedDirectory,
            generatedFilePath = context.GeneratedFilePath,
            generatedFileExists = File.Exists(context.GeneratedFilePath),
            compileIncludesGeneratedFile = context.CompileIncludesGeneratedFile,
            compileItemCount = context.CompileItemCount,
            projectContextDependencies = ToDependencyResponse(context.CacheDependencies),
            transform = new
            {
                compileMode = context.TransformOptions.CompileMode.ToString(),
                elementFactory = context.TransformOptions.ElementFactory,
                attributeFactory = context.TransformOptions.AttributeFactory,
                textFactory = context.TransformOptions.TextFactory,
                childrenFactory = context.TransformOptions.ChildrenFactory,
                componentLowering = context.TransformOptions.ComponentLowering.ToString(),
            },
            messages = context.Messages,
        };
    }

    private static object[] ToDependencyResponse(
        IReadOnlyList<CsmxProjectDependency>? dependencies
    ) =>
        dependencies
            ?.Select(dependency => new
            {
                path = dependency.Path,
                exists = dependency.Exists,
                lastWriteUtc = dependency.LastWriteUtc == DateTime.MinValue
                    ? null
                    : dependency.LastWriteUtc.ToString("O"),
                lastWriteUtcMilliseconds = dependency.LastWriteUtcMilliseconds,
            })
            .Cast<object>()
            .ToArray()
        ?? Array.Empty<object>();

    private object HandleReloadProjectContext(JsonElement parameters)
    {
        if (!TryReadTextDocumentUri(parameters, out var uri))
        {
            return new
            {
                uri = string.Empty,
                reloaded = false,
                clearedEntries = 0,
                messages = new[] { "Missing textDocument.uri." },
            };
        }

        var sourcePath = TryGetFilePathFromUri(uri);
        var clearedEntries = CsmxProjectContext.ClearCache(sourcePath);
        _roslynWorkspace.Clear();
        var documentWasOpen = _documents.TryGetValue(uri, out var document);
        if (documentWasOpen && document is not null)
        {
            UpdateDocument(uri, document.Text, document.Version);
        }

        return new
        {
            uri,
            sourceFilePath = sourcePath,
            reloaded = true,
            documentWasOpen,
            clearedEntries,
            requestedConfiguration = _projectContextOptions.Configuration,
            requestedTargetFramework = _projectContextOptions.TargetFramework,
        };
    }

    private object HandleSetProjectContextOptions(JsonElement parameters)
    {
        var nextOptions = ReadProjectContextOptions(parameters);
        var changed = nextOptions != _projectContextOptions;
        var clearedEntries = changed ? CsmxProjectContext.ClearCache() : 0;
        _projectContextOptions = nextOptions;
        if (changed)
        {
            _roslynWorkspace.Clear();
        }

        var refreshedDocuments = 0;
        if (changed)
        {
            foreach (var document in _documents.Values.ToArray())
            {
                UpdateDocument(document.Uri, document.Text, document.Version);
                refreshedDocuments++;
            }
        }

        return new
        {
            configuration = _projectContextOptions.Configuration,
            targetFramework = _projectContextOptions.TargetFramework,
            changed,
            clearedEntries,
            refreshedDocuments,
        };
    }

    private object HandleSemanticTokens(JsonElement parameters)
    {
        if (
            !TryReadTextDocumentUri(parameters, out var uri)
            || !_documents.TryGetValue(uri, out var document)
        )
        {
            return new { data = Array.Empty<int>() };
        }

        IReadOnlyList<RoslynSemanticToken>? roslynTokens = null;
        if (
            document.ProjectContext is not null
            && TryCreateDocumentSnapshot(document, out var snapshot)
        )
        {
            roslynTokens = _roslynWorkspace.GetSemanticTokens(snapshot);
        }

        return new { data = BuildSemanticTokenData(document.Text, document.LineMap, roslynTokens) };
    }

    private static bool TryReadDocumentPosition(
        JsonElement parameters,
        out string uri,
        out int line,
        out int character
    )
    {
        line = 0;
        character = 0;
        if (
            !TryReadTextDocumentUri(parameters, out uri)
            || !parameters.TryGetProperty(JsonPosition, out var position)
            || !position.TryGetProperty(JsonLine, out var lineElement)
            || !lineElement.TryGetInt32(out line)
            || !position.TryGetProperty(JsonCharacter, out var characterElement)
            || !characterElement.TryGetInt32(out character)
        )
        {
            return false;
        }

        return true;
    }

    private static bool TryReadTextDocumentUri(JsonElement parameters, out string uri)
    {
        uri = string.Empty;
        if (
            !parameters.TryGetProperty(JsonTextDocument, out var textDocument)
            || !textDocument.TryGetProperty(JsonUri, out var uriElement)
        )
        {
            return false;
        }

        uri = uriElement.GetString() ?? string.Empty;
        return uri.Length > 0;
    }

    private static CsmxProjectContextOptions ReadProjectContextOptions(JsonElement parameters)
    {
        var configuration = ReadNullableStringProperty(parameters, "configuration");
        var targetFramework = ReadNullableStringProperty(parameters, "targetFramework");
        return CsmxProjectContextOptions.Create(configuration, targetFramework);
    }

    private static string? ReadNullableStringProperty(JsonElement parameters, string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
