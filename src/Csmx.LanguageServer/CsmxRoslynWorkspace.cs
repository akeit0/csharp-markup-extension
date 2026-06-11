using System.Collections.Concurrent;
using Csmx.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csmx.LanguageServer;

internal sealed class CsmxRoslynWorkspace
{
    private readonly ConcurrentDictionary<string, CsmxRoslynProject> cache = new(
        StringComparer.OrdinalIgnoreCase
    );

    public RoslynHover? GetHover(OpenDocumentSnapshot document, int sourceIndex)
    {
        if (!TryCreateProjection(document, out var projection))
        {
            return null;
        }

        if (!projection.TryMapSourcePositionToGenerated(sourceIndex, out var generatedIndex))
        {
            return null;
        }

        var root = projection.Root;
        var token = root.FindToken(Math.Min(generatedIndex, Math.Max(0, root.FullSpan.End - 1)));
        if (token.RawKind == 0)
        {
            return null;
        }

        var node = token.Parent;
        if (node is null)
        {
            return null;
        }

        var symbol = GetSymbol(projection.SemanticModel, node);
        if (symbol is null)
        {
            return null;
        }

        var display = GetHoverDisplay(symbol);
        var kind = symbol.Kind.ToString();
        var containing = symbol.ContainingAssembly?.Name;
        var value = string.IsNullOrWhiteSpace(containing)
            ? $"```csharp\n{display}\n```\n\nKind: `{kind}`"
            : $"```csharp\n{display}\n```\n\nKind: `{kind}`. Assembly: `{containing}`.";
        return new RoslynHover(value);
    }

    public IReadOnlyList<RoslynSemanticToken> GetSemanticTokens(OpenDocumentSnapshot document)
    {
        if (!TryCreateProjection(document, out var projection))
        {
            return Array.Empty<RoslynSemanticToken>();
        }

        var model = projection.SemanticModel;
        var root = projection.Root;
        var tokens = new List<RoslynSemanticToken>();

        foreach (
            var token in root.DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
        )
        {
            if (IsContextualKeywordToken(token))
            {
                continue;
            }

            var node = token.Parent;
            if (node is null)
            {
                continue;
            }

            var symbol = GetSymbol(model, node);
            var tokenType = GetSemanticTokenType(symbol);
            if (tokenType is null)
            {
                continue;
            }

            if (
                !projection.TryMapGeneratedSymbolSpanToSource(
                    token.Span,
                    out var originalStart,
                    out var originalLength
                )
            )
            {
                continue;
            }

            tokens.Add(new RoslynSemanticToken(originalStart, originalLength, tokenType));
        }

        return tokens;
    }

    public IReadOnlyList<RoslynDiagnostic> GetDiagnostics(OpenDocumentSnapshot document)
    {
        if (!TryCreateProjection(document, out var projection))
        {
            return Array.Empty<RoslynDiagnostic>();
        }

        var diagnostics = new List<RoslynDiagnostic>();
        foreach (
            var diagnostic in projection
                .SyntaxTree.GetDiagnostics()
                .Concat(projection.SemanticModel.GetDiagnostics())
        )
        {
            if (
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden
                || !diagnostic.Location.IsInSource
                || !ReferenceEquals(diagnostic.Location.SourceTree, projection.SyntaxTree)
            )
            {
                continue;
            }

            if (
                !projection.TryMapGeneratedDiagnosticSpanToSource(
                    diagnostic.Location.SourceSpan,
                    out var sourceStart,
                    out var sourceLength
                )
            )
            {
                continue;
            }

            diagnostics.Add(
                new RoslynDiagnostic(
                    sourceStart,
                    sourceLength,
                    diagnostic.Id,
                    diagnostic.GetMessage(),
                    ToLspDiagnosticSeverity(diagnostic.Severity)
                )
            );
        }

        return diagnostics
            .DistinctBy(diagnostic => new
            {
                diagnostic.Start,
                diagnostic.Length,
                diagnostic.Code,
                diagnostic.Message,
            })
            .ToArray();
    }

    public IReadOnlyList<RoslynDefinitionLocation> GetDefinitions(
        OpenDocumentSnapshot document,
        int sourceIndex
    )
    {
        if (!TryCreateProjection(document, out var projection))
        {
            return Array.Empty<RoslynDefinitionLocation>();
        }

        if (!projection.TryMapSourcePositionToGenerated(sourceIndex, out var generatedIndex))
        {
            return Array.Empty<RoslynDefinitionLocation>();
        }

        var token = projection.Root.FindToken(
            Math.Min(generatedIndex, Math.Max(0, projection.Root.FullSpan.End - 1))
        );
        if (token.RawKind == 0 || token.Parent is null)
        {
            return Array.Empty<RoslynDefinitionLocation>();
        }

        var symbol = GetSymbol(projection.SemanticModel, token.Parent);
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            symbol = constructor.ContainingType;
        }

        return symbol is null
            ? Array.Empty<RoslynDefinitionLocation>()
            : GetSourceDefinitionLocations(projection, symbol).ToArray();
    }

    public IReadOnlyList<RoslynCompletionItem> GetTagCompletions(
        OpenDocumentSnapshot document,
        int sourceIndex
    )
    {
        if (!TryCreateProjection(document, out var projection))
        {
            return Array.Empty<RoslynCompletionItem>();
        }

        var lookupPosition = GetLookupGeneratedPosition(projection, sourceIndex);
        var visibleTypes = projection
            .SemanticModel.LookupNamespacesAndTypes(lookupPosition)
            .OfType<INamedTypeSymbol>()
            .ToArray();
        var baseTypes = visibleTypes
            .Where(type => type.Name is "UiNode" or "UiElement" or "VNode")
            .ToArray();
        var candidates = visibleTypes.Where(type => IsTagCompletionCandidate(type, baseTypes));

        return candidates
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var type = group.First();
                return new RoslynCompletionItem(
                    type.Name,
                    "class " + type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    7
                );
            })
            .ToArray();
    }

    public IReadOnlyList<RoslynCompletionItem> GetAttributeCompletions(
        OpenDocumentSnapshot document,
        int sourceIndex,
        string? tagName
    )
    {
        if (
            string.IsNullOrWhiteSpace(tagName) || !TryCreateProjection(document, out var projection)
        )
        {
            return Array.Empty<RoslynCompletionItem>();
        }

        var lookupPosition = GetLookupGeneratedPosition(projection, sourceIndex);
        var tagType = ResolveTagType(projection, sourceIndex, tagName, lookupPosition);
        if (tagType is null)
        {
            return Array.Empty<RoslynCompletionItem>();
        }

        return GetAttributeCompletionSymbols(tagType)
            .GroupBy(item => item.Label, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<RoslynCompletionItem> GetCSharpCompletions(
        OpenDocumentSnapshot document,
        int sourceIndex
    )
    {
        if (!TryCreateProjection(document, out var projection))
        {
            return Array.Empty<RoslynCompletionItem>();
        }

        if (
            !projection.TryMapSourceCompletionPositionToGenerated(
                sourceIndex,
                out var generatedIndex
            )
        )
        {
            return Array.Empty<RoslynCompletionItem>();
        }

        generatedIndex = Math.Clamp(generatedIndex, 0, projection.GeneratedText.Length);
        if (
            TryGetSourceMemberAccessReceiverType(
                projection,
                sourceIndex,
                out var sourceReceiverType
            )
            && sourceReceiverType is not null
        )
        {
            return GetMemberCompletionItems(projection, generatedIndex, sourceReceiverType);
        }

        if (
            TryGetMemberAccessReceiverType(projection, generatedIndex, out var receiverType)
            && receiverType is not null
        )
        {
            return GetMemberCompletionItems(projection, generatedIndex, receiverType);
        }

        if (IsAfterVarKeyword(projection, generatedIndex))
        {
            return Array.Empty<RoslynCompletionItem>();
        }

        return projection
            .SemanticModel.LookupSymbols(generatedIndex)
            .Concat(projection.SemanticModel.LookupNamespacesAndTypes(generatedIndex))
            .Where(symbol =>
                !symbol.IsImplicitlyDeclared
                && IsCompletionSymbolAccessible(projection, generatedIndex, symbol)
            )
            .Select(ToCompletionItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Label, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<RoslynCompletionItem> GetMemberCompletionItems(
        CsmxProjectedDocument projection,
        int generatedIndex,
        ITypeSymbol receiverType
    ) =>
        GetMemberCompletionSymbols(projection, generatedIndex, receiverType)
            .GroupBy(item => item.Label, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    public void Clear() => cache.Clear();

    private bool TryCreateProjection(
        OpenDocumentSnapshot document,
        out CsmxProjectedDocument projection
    )
    {
        projection = null!;
        if (document.ProjectContext is null)
        {
            return false;
        }

        var project = GetOrCreateProject(document.ProjectContext);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in project.Sources)
        {
            sources[source.Path] = source.Text;
        }

        sources[document.SourceFilePath] = document.Transform.Code;

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTrees = sources
            .Select(source =>
                CSharpSyntaxTree.ParseText(source.Value, parseOptions, path: source.Key)
            )
            .ToArray();
        var tree = syntaxTrees.FirstOrDefault(tree =>
            PathsEqual(tree.FilePath, document.SourceFilePath)
        );
        if (tree is null)
        {
            return false;
        }

        var compilation = CSharpCompilation.Create(
            project.AssemblyName,
            syntaxTrees,
            project.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        projection = new CsmxProjectedDocument(document, compilation, tree);
        return true;
    }

    private CsmxRoslynProject GetOrCreateProject(CsmxProjectContext context)
    {
        var key =
            context.ProjectFilePath + "|" + context.Configuration + "|" + context.TargetFramework;
        if (
            cache.TryGetValue(key, out var cached)
            && cached.Dependencies.All(dependency => dependency.IsCurrent())
        )
        {
            return cached;
        }

        var created = CsmxRoslynProjectLoader.Load(context);
        cache[key] = created;
        return created;
    }

    private static int ToLspDiagnosticSeverity(
        Microsoft.CodeAnalysis.DiagnosticSeverity severity
    ) =>
        severity switch
        {
            Microsoft.CodeAnalysis.DiagnosticSeverity.Error => 1,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => 2,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Info => 3,
            _ => 4,
        };

    private static int GetLookupGeneratedPosition(CsmxProjectedDocument projection, int sourceIndex)
    {
        for (
            var candidate = Math.Min(sourceIndex, Math.Max(0, projection.SourceText.Length - 1));
            candidate >= 0 && candidate >= sourceIndex - 256;
            candidate--
        )
        {
            if (projection.TryMapSourcePositionToGenerated(candidate, out var generatedIndex))
            {
                return Math.Min(generatedIndex, Math.Max(0, projection.Root.FullSpan.End - 1));
            }
        }

        return Math.Max(0, projection.Root.FullSpan.End - 1);
    }

    private static bool IsTagCompletionCandidate(
        INamedTypeSymbol type,
        IReadOnlyList<INamedTypeSymbol> baseTypes
    )
    {
        if (
            type.TypeKind != TypeKind.Class
            || type.IsAbstract
            || type.IsStatic
            || type.IsGenericType
            || !HasAccessibleParameterlessConstructor(type)
        )
        {
            return false;
        }

        return baseTypes.Count == 0
            || baseTypes.Any(baseType => SymbolEqualityComparer.Default.Equals(type, baseType))
            || baseTypes.Any(baseType => InheritsFrom(type, baseType));
    }

    private static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type) =>
        type.Constructors.Any(constructor =>
            !constructor.IsStatic
            && constructor.Parameters.Length == 0
            && constructor.DeclaredAccessibility
                is Accessibility.Public
                    or Accessibility.Internal
                    or Accessibility.ProtectedOrInternal
        );

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static INamedTypeSymbol? ResolveTagType(
        CsmxProjectedDocument projection,
        int sourceIndex,
        string tagName,
        int lookupPosition
    )
    {
        if (
            TryFindCurrentOpeningTagNameSpan(
                projection.SourceText,
                sourceIndex,
                out var tagNameSpan
            )
            && projection.TryMapSourcePositionToGenerated(
                tagNameSpan.Start,
                out var generatedTagIndex
            )
        )
        {
            var token = projection.Root.FindToken(
                Math.Min(generatedTagIndex, Math.Max(0, projection.Root.FullSpan.End - 1))
            );
            if (token.RawKind != 0 && token.Parent is not null)
            {
                var symbol = GetSymbol(projection.SemanticModel, token.Parent);
                if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
                {
                    return constructor.ContainingType;
                }

                if (symbol is INamedTypeSymbol typeSymbol)
                {
                    return typeSymbol;
                }
            }
        }

        return projection
            .SemanticModel.LookupNamespacesAndTypes(lookupPosition, name: tagName)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(type => string.Equals(type.Name, tagName, StringComparison.Ordinal));
    }

    private static IEnumerable<RoslynCompletionItem> GetAttributeCompletionSymbols(
        INamedTypeSymbol tagType
    )
    {
        for (INamedTypeSymbol? current = tagType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol method when IsAttributeMethodCandidate(method):
                        yield return new RoslynCompletionItem(
                            method.Name,
                            method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                            2
                        );
                        break;

                    case IPropertySymbol property when IsAttributePropertyCandidate(property):
                        yield return new RoslynCompletionItem(
                            property.Name,
                            property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                            10
                        );
                        break;

                    case IFieldSymbol field when IsAttributeFieldCandidate(field):
                        yield return new RoslynCompletionItem(
                            field.Name,
                            field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                            5
                        );
                        break;
                }
            }
        }

        foreach (var type in tagType.AllInterfaces)
        {
            foreach (var member in GetAttributeCompletionSymbols(type))
            {
                yield return member;
            }
        }
    }

    private static bool TryGetMemberAccessReceiverType(
        CsmxProjectedDocument projection,
        int generatedIndex,
        out ITypeSymbol? receiverType
    )
    {
        receiverType = null;
        var tokenIndex = Math.Max(
            0,
            Math.Min(generatedIndex - 1, projection.Root.FullSpan.End - 1)
        );
        var token = projection.Root.FindToken(tokenIndex);
        if (token.RawKind == 0)
        {
            return false;
        }

        var memberAccess = token
            .Parent
            ?.AncestorsAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault(access =>
                access.OperatorToken.Span.End <= generatedIndex
                && generatedIndex <= Math.Max(access.Name.Span.End, access.OperatorToken.Span.End)
            );
        if (memberAccess is null)
        {
            return false;
        }

        var typeInfo = projection.SemanticModel.GetTypeInfo(memberAccess.Expression);
        receiverType = typeInfo.Type ?? typeInfo.ConvertedType;
        return receiverType is not null;
    }

    private static bool TryGetSourceMemberAccessReceiverType(
        CsmxProjectedDocument projection,
        int sourceIndex,
        out ITypeSymbol? receiverType
    )
    {
        receiverType = null;
        var dotIndex = FindSourceMemberAccessDot(projection.SourceText, sourceIndex);
        if (
            dotIndex <= 0
            || !projection.TryMapSourcePositionToGenerated(dotIndex - 1, out var generatedReceiverIndex)
        )
        {
            return false;
        }

        var token = projection.Root.FindToken(
            Math.Min(generatedReceiverIndex, Math.Max(0, projection.Root.FullSpan.End - 1))
        );
        if (token.RawKind == 0 || token.Parent is null)
        {
            return false;
        }

        receiverType = GetSymbolType(GetSymbol(projection.SemanticModel, token.Parent));
        return receiverType is not null;
    }

    private static int FindSourceMemberAccessDot(string text, int sourceIndex)
    {
        var cursor = Math.Min(sourceIndex, text.Length);
        while (cursor > 0 && IsCSharpIdentifierPart(text[cursor - 1]))
        {
            cursor--;
        }

        if (cursor <= 0 || text[cursor - 1] != '.')
        {
            return -1;
        }

        var dotIndex = cursor - 1;
        var receiverEnd = dotIndex;
        while (receiverEnd > 0 && char.IsWhiteSpace(text[receiverEnd - 1]))
        {
            receiverEnd--;
        }

        return receiverEnd == dotIndex ? dotIndex : -1;
    }

    private static bool IsAfterVarKeyword(CsmxProjectedDocument projection, int generatedIndex)
    {
        var token = FindTokenBefore(projection.Root, generatedIndex);
        if (
            token.RawKind == 0
            || token.ValueText != "var"
            || token.Parent is not IdentifierNameSyntax identifier
            || identifier.Parent is not VariableDeclarationSyntax declaration
            || !ReferenceEquals(declaration.Type, identifier)
        )
        {
            return false;
        }

        return generatedIndex >= identifier.Span.End
            && generatedIndex <= declaration.Span.End;
    }

    private static SyntaxToken FindTokenBefore(SyntaxNode root, int index)
    {
        if (root.FullSpan.Length == 0)
        {
            return default;
        }

        var boundedIndex = Math.Max(0, Math.Min(index, root.FullSpan.End));
        var token = root.FindToken(boundedIndex);
        if (
            token.RawKind != 0
            && token.Span.Length == 0
            && boundedIndex > 0
        )
        {
            token = root.FindToken(boundedIndex - 1);
        }

        return token.RawKind != 0 ? token : root.FindToken(Math.Max(0, boundedIndex - 1));
    }

    private static IEnumerable<RoslynCompletionItem> GetMemberCompletionSymbols(
        CsmxProjectedDocument projection,
        int generatedIndex,
        ITypeSymbol type
    )
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (
                    member.IsStatic
                    || member.IsImplicitlyDeclared
                    || member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }
                    || !IsCompletionSymbolAccessible(projection, generatedIndex, member, type)
                )
                {
                    continue;
                }

                var item = ToCompletionItem(member);
                if (item is not null)
                {
                    yield return item;
                }
            }
        }

        foreach (var typeInterface in type.AllInterfaces)
        {
            foreach (var member in typeInterface.GetMembers())
            {
                if (
                    member.IsStatic
                    || member.IsImplicitlyDeclared
                    || member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }
                    || !IsCompletionSymbolAccessible(projection, generatedIndex, member, type)
                )
                {
                    continue;
                }

                var item = ToCompletionItem(member);
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    private static bool IsCompletionSymbolAccessible(
        CsmxProjectedDocument projection,
        int generatedIndex,
        ISymbol symbol,
        ITypeSymbol? throughType = null
    )
    {
        if (
            symbol is ILocalSymbol
                or IParameterSymbol
                or ILabelSymbol
                or IRangeVariableSymbol
        )
        {
            return true;
        }

        var within = projection.SemanticModel.GetEnclosingSymbol(generatedIndex)
            ?? projection.Compilation.Assembly.GlobalNamespace;
        var withinType = within.ContainingType ?? within as INamedTypeSymbol;
        return IsSymbolAccessibleFromType(symbol, withinType);
    }

    private static bool IsSymbolAccessibleFromType(ISymbol symbol, INamedTypeSymbol? withinType)
    {
        if (symbol.DeclaredAccessibility == Accessibility.NotApplicable)
        {
            return true;
        }

        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Internal => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Protected =>
                IsSameOrDerivedType(withinType, symbol.ContainingType),
            Accessibility.ProtectedAndInternal =>
                IsSameOrDerivedType(withinType, symbol.ContainingType),
            Accessibility.Private =>
                SymbolEqualityComparer.Default.Equals(withinType, symbol.ContainingType),
            _ => false,
        };
    }

    private static bool IsSameOrDerivedType(
        INamedTypeSymbol? type,
        INamedTypeSymbol? candidateBaseType
    )
    {
        if (type is null || candidateBaseType is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, candidateBaseType))
            {
                return true;
            }
        }

        return false;
    }

    private static RoslynCompletionItem? ToCompletionItem(ISymbol symbol)
    {
        var kind = symbol switch
        {
            IMethodSymbol => 2,
            INamedTypeSymbol { TypeKind: TypeKind.Class } => 7,
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => 8,
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => 22,
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => 13,
            INamespaceSymbol => 9,
            IPropertySymbol => 10,
            IFieldSymbol => 5,
            IEventSymbol => 23,
            ILocalSymbol or IParameterSymbol => 6,
            _ => 0,
        };

        return kind == 0
            ? null
            : new RoslynCompletionItem(
                symbol.Name,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                kind
            );
    }

    private static bool IsAttributeMethodCandidate(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary
        && !method.IsStatic
        && method.Parameters.Length > 0
        && IsPublicApi(method)
        && method.ContainingType.SpecialType != SpecialType.System_Object;

    private static bool IsAttributePropertyCandidate(IPropertySymbol property) =>
        !property.IsStatic && property.SetMethod is not null && IsPublicApi(property);

    private static bool IsAttributeFieldCandidate(IFieldSymbol field) =>
        !field.IsStatic && !field.IsReadOnly && IsPublicApi(field);

    private static bool IsPublicApi(ISymbol symbol) =>
        symbol.DeclaredAccessibility
            is Accessibility.Public
                or Accessibility.Internal
                or Accessibility.ProtectedOrInternal;

    private static bool TryFindCurrentOpeningTagNameSpan(
        string text,
        int index,
        out TextSpan nameSpan
    )
    {
        nameSpan = default;
        var searchStart = Math.Clamp(index - 1, 0, Math.Max(0, text.Length - 1));
        var open = text.LastIndexOf('<', searchStart);
        var close = text.LastIndexOf('>', searchStart);
        if (open < 0 || open <= close || open + 1 >= text.Length || text[open + 1] == '/')
        {
            return false;
        }

        var cursor = open + 1;
        if (cursor >= text.Length || !IsNameStart(text[cursor]))
        {
            return false;
        }

        var start = cursor;
        while (cursor < text.Length && IsNamePart(text[cursor]))
        {
            cursor++;
        }

        nameSpan = TextSpan.FromBounds(start, cursor);
        return nameSpan.Length > 0;
    }

    private static IEnumerable<RoslynDefinitionLocation> GetSourceDefinitionLocations(
        CsmxProjectedDocument projection,
        ISymbol symbol
    )
    {
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource || location.SourceTree is null)
            {
                continue;
            }

            if (ReferenceEquals(location.SourceTree, projection.SyntaxTree))
            {
                if (
                    projection.TryMapGeneratedSymbolSpanToSource(
                        location.SourceSpan,
                        out var sourceStart,
                        out var sourceLength
                    )
                )
                {
                    var lineMap = LineMap.FromText(projection.SourceText);
                    var start = lineMap.GetLinePosition(sourceStart);
                    var end = lineMap.GetLinePosition(sourceStart + sourceLength);
                    yield return new RoslynDefinitionLocation(
                        projection.Source.Uri,
                        start.Line,
                        start.Character,
                        end.Line,
                        end.Character
                    );
                }

                continue;
            }

            var filePath = location.SourceTree.FilePath;
            if (
                string.IsNullOrWhiteSpace(filePath)
                || filePath.EndsWith(".csmx", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var lineSpan = location.GetLineSpan();
            if (!lineSpan.IsValid)
            {
                continue;
            }

            yield return new RoslynDefinitionLocation(
                new Uri(Path.GetFullPath(filePath)).AbsoluteUri,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character
            );
        }
    }

    private static string? GetSemanticTokenType(ISymbol? symbol) =>
        symbol switch
        {
            INamespaceSymbol => "namespace",
            INamedTypeSymbol { TypeKind: TypeKind.Class } => "class",
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
            ITypeParameterSymbol => "typeParameter",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => "enumMember",
            IFieldSymbol => "variable",
            ILocalSymbol => "variable",
            IParameterSymbol => "parameter",
            IEventSymbol => "event",
            _ => null,
        };

    private static ISymbol? GetSymbol(SemanticModel model, SyntaxNode node)
    {
        foreach (var current in node.AncestorsAndSelf())
        {
            var declaredSymbol = model.GetDeclaredSymbol(current);
            if (declaredSymbol is not null)
            {
                return declaredSymbol;
            }

            var symbolInfo = model.GetSymbolInfo(current);
            if (symbolInfo.Symbol is not null)
            {
                return symbolInfo.Symbol;
            }

            if (symbolInfo.CandidateSymbols.Length == 1)
            {
                return symbolInfo.CandidateSymbols[0];
            }

            var typeInfo = model.GetTypeInfo(current);
            if (typeInfo.Type is not null)
            {
                return typeInfo.Type;
            }

            if (typeInfo.ConvertedType is not null)
            {
                return typeInfo.ConvertedType;
            }
        }

        return null;
    }

    private static ITypeSymbol? GetSymbolType(ISymbol? symbol) =>
        symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol eventSymbol => eventSymbol.Type,
            IMethodSymbol method => method.ReturnType,
            ITypeSymbol type => type,
            _ => null,
        };

    private static string GetHoverDisplay(ISymbol symbol) =>
        symbol switch
        {
            ILocalSymbol local => string.Concat(
                GetQualifiedTypeDisplay(local.Type),
                " ",
                local.Name
            ),
            IParameterSymbol parameter => string.Concat(
                GetQualifiedTypeDisplay(parameter.Type),
                " ",
                parameter.Name
            ),
            ITypeSymbol typeSymbol => GetQualifiedTypeDisplay(typeSymbol),
            _ => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
        };

    private static string GetQualifiedTypeDisplay(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsTupleType: true } tupleType)
        {
            return string.Concat(
                "(",
                string.Join(
                    ", ",
                    tupleType.TupleElements.Select(element =>
                        string.Concat(GetQualifiedTypeDisplay(element.Type), " ", element.Name)
                    )
                ),
                ")"
            );
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return string.Concat(GetQualifiedTypeDisplay(arrayType.ElementType), "[]");
        }

        var display = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        if (
            type is not INamedTypeSymbol namedType
            || namedType.SpecialType != SpecialType.None
            || namedType.ContainingNamespace.IsGlobalNamespace
        )
        {
            return display;
        }

        var namespaceName = namedType.ContainingNamespace.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat
        );
        return display.StartsWith(namespaceName + ".", StringComparison.Ordinal)
            ? display
            : string.Concat(namespaceName, ".", display);
    }

    private static bool IsContextualKeywordToken(SyntaxToken token) =>
        token.ValueText == "var"
        && token.Parent is IdentifierNameSyntax identifier
        && identifier.Parent
            is VariableDeclarationSyntax
                or DeclarationExpressionSyntax
                or ForEachStatementSyntax
                or ForEachVariableStatementSyntax;

    private static bool IsNameStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsNamePart(char c) =>
        c == '_' || c == '-' || c == ':' || c == '.' || char.IsLetterOrDigit(c);

    private static bool IsCSharpIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase
        );
}

internal sealed record RoslynHover(string Markdown);

internal sealed record RoslynSemanticToken(int Start, int Length, string TokenType);

internal sealed record RoslynCompletionItem(string Label, string Detail, int Kind);

internal sealed record RoslynDefinitionLocation(
    string Uri,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter
);

internal sealed record RoslynDiagnostic(
    int Start,
    int Length,
    string Code,
    string Message,
    int Severity
);
