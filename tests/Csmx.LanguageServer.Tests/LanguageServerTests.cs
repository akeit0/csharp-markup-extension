using TUnit.Core;

namespace Csmx.Tests;

public sealed class LanguageServerTests
{
    private const int SemanticNamespace = 0;
    private const int SemanticClass = 2;
    private const int SemanticType = 1;
    private const int SemanticVariable = 8;
    private const int SemanticOperator = 21;
    private const int SemanticProperty = 9;
    private const int SemanticMethod = 13;
    private const int SemanticKeyword = 15;
    private const int SemanticString = 18;

    [Test]
    public async Task TestLanguageServerHover()
    {
        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen("file:///Counter.csmx", TestSource.Text);

        var propsHover = await client.HoverAsync(
            "file:///Counter.csmx",
            TestSource.PositionOf("(props, children)", 1)
        );
        AssertNull(propsHover, "props hover should be delegated, not owned by CSMX LSP.");

        var childrenHover = await client.HoverAsync(
            "file:///Counter.csmx",
            TestSource.PositionOf("children) =>", 1)
        );
        AssertNull(childrenHover, "children hover should be delegated, not owned by CSMX LSP.");

        var helloHover = await client.HoverAsync(
            "file:///Counter.csmx",
            TestSource.PositionOf("Hello {props.Name}", 1)
        );
        AssertNull(helloHover, "plain JSX text should not produce CSMX hover.");

        var textHover = await client.HoverAsync(
            "file:///Counter.csmx",
            TestSource.PositionOf("<text>Hello", 1)
        );
        AssertContains(textHover, "CSMX element `<text>`", "<text> hover");

        var genericHover = await client.HoverAsync(
            "file:///Counter.csmx",
            TestSource.PositionOf("Func<CounterViewProps", 5)
        );
        AssertNull(genericHover, "C# generic type argument must not be parsed as JSX.");

        var tokens = await client.SemanticTokensAsync("file:///Counter.csmx");
        AssertHasSemanticToken(
            tokens,
            TestSource.PositionOf("<text>Hello", 1),
            4,
            SemanticClass,
            "opening text tag token"
        );
        AssertHasSemanticToken(
            tokens,
            TestSource.PositionOf("</text>", 2),
            4,
            SemanticClass,
            "closing text tag token"
        );
        AssertHasSemanticToken(
            tokens,
            TestSource.PositionOf("=>"),
            2,
            SemanticOperator,
            "lambda arrow token"
        );
        AssertNoSemanticToken(
            tokens,
            TestSource.PositionOf("< 3"),
            1,
            SemanticOperator,
            "single less-than operator token"
        );
        AssertNoSemanticToken(
            tokens,
            TestSource.PositionOf("Render("),
            6,
            SemanticClass,
            "method name class token"
        );
        AssertNoSemanticToken(
            tokens,
            TestSource.PositionOf("Render("),
            6,
            SemanticType,
            "method name type token"
        );
        AssertNoSemanticToken(
            tokens,
            TestSource.PositionOf("(props, children)", 1),
            5,
            SemanticClass,
            "lambda parameter class token"
        );
        AssertNoSemanticToken(
            tokens,
            TestSource.PositionOf("Hello {props.Name}"),
            5,
            SemanticClass,
            "JSX text class token"
        );

        client.DidOpen("file:///BlockRender.csmx", BlockRenderTestSource.Text);
        var blockTokens = await client.SemanticTokensAsync("file:///BlockRender.csmx");
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("<Column", 1),
            6,
            SemanticClass,
            "block-bodied return opening Column tag token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("Padding={24}"),
            7,
            SemanticProperty,
            "block-bodied return Padding attribute token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("<Text FontSize", 1),
            4,
            SemanticClass,
            "block-bodied return nested Text tag token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("Console.WriteLine"),
            7,
            SemanticType,
            "qualified Console receiver token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("WriteLine"),
            9,
            SemanticMethod,
            "qualified WriteLine method token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("$\"Panel("),
            8,
            SemanticString,
            "interpolated string literal segment token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("Join"),
            4,
            SemanticMethod,
            "interpolated string expression method token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("\" | \""),
            5,
            SemanticString,
            "interpolated string nested string token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("children)})"),
            8,
            SemanticVariable,
            "interpolated string expression variable token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("$\"\"\"Raw"),
            8,
            SemanticString,
            "raw interpolated string literal segment token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("state.Count.Value:N2"),
            5,
            SemanticVariable,
            "raw interpolated string expression receiver token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("Count.Value:N2"),
            5,
            SemanticProperty,
            "raw interpolated string expression property token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf(":N2"),
            3,
            SemanticString,
            "interpolated string format clause token"
        );
        AssertNoSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("N2"),
            2,
            SemanticVariable,
            "interpolated string format clause variable token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("state.Count.Value"),
            5,
            SemanticVariable,
            "expression receiver state token"
        );
        AssertHasSemanticToken(
            blockTokens,
            BlockRenderTestSource.PositionOf("Count.Value"),
            5,
            SemanticProperty,
            "expression Count property token"
        );

        client.DidOpen("file:///UsingStatic.csmx", UsingStaticSemanticTestSource.Text);
        var usingStaticTokens = await client.SemanticTokensAsync("file:///UsingStatic.csmx");
        var usingStaticStart = UsingStaticSemanticTestSource.Text.IndexOf(
            "using static",
            StringComparison.Ordinal
        );
        var usingStaticCsmx = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("Csmx", usingStaticStart)
        );
        var usingStaticEnagaSignals = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("EnagaSignals", usingStaticStart)
        );
        var usingStaticSignals = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("Signals;", usingStaticStart)
        );
        var namespaceStart = UsingStaticSemanticTestSource.Text.IndexOf(
            "namespace",
            StringComparison.Ordinal
        );
        var namespaceCsmx = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("Csmx", namespaceStart)
        );
        var namespaceTests = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("Tests", namespaceStart)
        );
        var namespaceFixtures = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("Fixtures", namespaceStart)
        );
        var namespaceComponents = PositionAt(
            UsingStaticSemanticTestSource.Text,
            UsingStaticSemanticTestSource.Text.IndexOf("Components", namespaceStart)
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticCsmx,
            4,
            SemanticNamespace,
            "using static root namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticEnagaSignals,
            12,
            SemanticNamespace,
            "using static nested namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticSignals,
            7,
            SemanticNamespace,
            "using static target namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticCsmx,
            4,
            SemanticType,
            "using static root type token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticEnagaSignals,
            12,
            SemanticType,
            "using static nested type token"
        );
        AssertHasSemanticToken(
            usingStaticTokens,
            usingStaticSignals,
            7,
            SemanticClass,
            "using static target class token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticCsmx,
            4,
            SemanticVariable,
            "using static root variable token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticEnagaSignals,
            12,
            SemanticVariable,
            "using static nested variable token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            usingStaticSignals,
            7,
            SemanticVariable,
            "using static target variable token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceCsmx,
            4,
            SemanticNamespace,
            "file-scoped namespace root namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceTests,
            5,
            SemanticNamespace,
            "file-scoped namespace nested namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceFixtures,
            8,
            SemanticNamespace,
            "file-scoped namespace fixtures namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceComponents,
            10,
            SemanticNamespace,
            "file-scoped namespace components namespace token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceCsmx,
            4,
            SemanticType,
            "file-scoped namespace root type token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceTests,
            5,
            SemanticType,
            "file-scoped namespace nested type token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceFixtures,
            8,
            SemanticType,
            "file-scoped namespace fixtures type token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceComponents,
            10,
            SemanticType,
            "file-scoped namespace components type token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceTests,
            5,
            SemanticProperty,
            "file-scoped namespace nested property token"
        );
        AssertNoSemanticToken(
            usingStaticTokens,
            namespaceFixtures,
            8,
            SemanticVariable,
            "file-scoped namespace fixtures variable token"
        );

        client.DidOpen("file:///NestedExpression.csmx", NestedExpressionLspTestSource.Text);
        var nestedTokens = await client.SemanticTokensAsync("file:///NestedExpression.csmx");
        AssertHasSemanticToken(
            nestedTokens,
            NestedExpressionLspTestSource.PositionOf("<choice", 1),
            6,
            SemanticClass,
            "ternary nested choice tag token"
        );
        AssertHasSemanticToken(
            nestedTokens,
            NestedExpressionLspTestSource.PositionOf("selected={true}"),
            8,
            SemanticProperty,
            "ternary nested choice attribute token"
        );
        AssertHasSemanticToken(
            nestedTokens,
            NestedExpressionLspTestSource.PositionOf("<row", 1),
            3,
            SemanticClass,
            "lambda nested row tag token"
        );
        AssertHasSemanticToken(
            nestedTokens,
            NestedExpressionLspTestSource.PositionOf("data-id={item.Name}"),
            7,
            SemanticProperty,
            "lambda nested row attribute token"
        );
        AssertHasSemanticToken(
            nestedTokens,
            NestedExpressionLspTestSource.PositionOf("item.Name", 5),
            4,
            SemanticProperty,
            "lambda nested row attribute expression property token"
        );
        AssertHasSemanticToken(
            nestedTokens,
            NestedExpressionLspTestSource.PositionOf("<label", 1),
            5,
            SemanticClass,
            "lambda nested label tag token"
        );
    }

    [Test]
    public async Task TestLanguageServerDiagnosticsUseStableCodes()
    {
        const string uri = "file:///BrokenDiagnostics.csmx";
        const string source = """
public static class BrokenDiagnostics
{
    public static object Render() => <Panel;
}
""";

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var diagnostics = await client.DiagnosticsAsync(uri);
        Assert(diagnostics.Count > 0, "Malformed CSMX should publish at least one diagnostic.");
        Assert(
            diagnostics.All(diagnostic => diagnostic.Source == "csmx"),
            "CSMX diagnostics should use the csmx source."
        );
        Assert(
            diagnostics.All(diagnostic => diagnostic.Code == "CSMX0001"),
            "CSMX diagnostics should use a stable diagnostic code."
        );
        Assert(
            diagnostics.All(diagnostic => diagnostic.Severity == 1),
            "Malformed CSMX diagnostics should publish LSP error severity."
        );
    }

    [Test]
    public async Task TestProjectRoslynDiagnosticsMapToCsjsxAndClearAfterEdit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Csmx.Tests",
            "language-server",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(tempRoot);
            var projectPath = Path.Combine(tempRoot, "DiagnosticSmoke.csproj");
            var sourcePath = Path.Combine(tempRoot, "View.csmx");
            var uri = new Uri(sourcePath).AbsoluteUri;

            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{GetTestRuntimeProject(repositoryRoot)}}" />
                  </ItemGroup>
                </Project>
                """
            );

            var source = """
                using Csmx.Tests.LanguageServerFixtures;
                using static Csmx.Tests.LanguageServerFixtures.Runtime;

                namespace DiagnosticSmoke;

                public static class View
                {
                    public static UiNode Render()
                    {
                        var label = MissingValue;
                        return <Text FontSize={Signals.UnknownMeasure(0)}>Count: {Signals.MissingSignal(0)}</Text>;
                    }
                }
                """;
            File.WriteAllText(sourcePath, source);

            await using var client = await LspTestClient.StartAsync();
            await client.InitializeAsync();
            client.DidOpen(uri, source);

            var diagnostics = await client.WaitForDiagnosticsAsync(
                uri,
                diagnostics =>
                    diagnostics.Any(diagnostic =>
                        diagnostic.Source == "csharp"
                        && diagnostic.Message.Contains("MissingValue", StringComparison.Ordinal)
                    )
                    && diagnostics.Any(diagnostic =>
                        diagnostic.Source == "csharp"
                        && diagnostic.Message.Contains("UnknownMeasure", StringComparison.Ordinal)
                    )
                    && diagnostics.Any(diagnostic =>
                        diagnostic.Source == "csharp"
                        && diagnostic.Message.Contains("MissingSignal", StringComparison.Ordinal)
                    )
            );

            AssertDiagnosticStartsAt(
                diagnostics,
                "MissingValue",
                PositionAt(source, source.IndexOf("MissingValue", StringComparison.Ordinal)),
                "copied C# diagnostic"
            );
            AssertDiagnosticStartsAt(
                diagnostics,
                "UnknownMeasure",
                PositionAt(source, source.IndexOf("UnknownMeasure", StringComparison.Ordinal)),
                "attribute expression diagnostic"
            );
            AssertDiagnosticStartsAt(
                diagnostics,
                "MissingSignal",
                PositionAt(source, source.IndexOf("MissingSignal", StringComparison.Ordinal)),
                "child expression diagnostic"
            );
            Assert(
                diagnostics
                    .Where(diagnostic =>
                        diagnostic.Message.Contains("Missing", StringComparison.Ordinal)
                        || diagnostic.Message.Contains("Unknown", StringComparison.Ordinal)
                    )
                    .All(diagnostic => diagnostic.Source == "csharp" && diagnostic.Severity == 1),
                "Mapped Roslyn diagnostics should publish as csharp errors."
            );

            var fixedSource = source
                .Replace(
                    "var label = MissingValue;",
                    "var label = 1;\n        _ = label;",
                    StringComparison.Ordinal
                )
                .Replace("UnknownMeasure", "Measure", StringComparison.Ordinal)
                .Replace("MissingSignal", "CreateSignal", StringComparison.Ordinal);
            client.DidChange(uri, fixedSource);

            var clearedDiagnostics = await client.WaitForDiagnosticsAsync(
                uri,
                diagnostics => diagnostics.Count == 0
            );
            Assert(
                clearedDiagnostics.Count == 0,
                "Roslyn diagnostics should clear after the source edit fixes the errors."
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }

    [Test]
    public async Task TestProjectRoslynDiagnosticsSuppressGeneratedHelperFailures()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Csmx.Tests",
            "language-server",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(tempRoot);
            var projectPath = Path.Combine(tempRoot, "GeneratedHelperSmoke.csproj");
            var sourcePath = Path.Combine(tempRoot, "View.csmx");
            var uri = new Uri(sourcePath).AbsoluteUri;

            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{GetMissingElementRuntimeProject(
                    repositoryRoot
                )}}" />
                  </ItemGroup>
                </Project>
                """
            );

            var source = """
                using Csmx.Tests.MissingElementFixtures;
                using static Csmx.Tests.MissingElementFixtures.Runtime;

                namespace GeneratedHelperSmoke;

                public static class View
                {
                    public static UiNode Render() => <panel>Generated helper missing</panel>;
                }
                """;
            File.WriteAllText(sourcePath, source);

            await using var client = await LspTestClient.StartAsync();
            await client.InitializeAsync();
            client.DidOpen(uri, source);

            var diagnostics = await client.WaitForDiagnosticsAsync(
                uri,
                diagnostics => diagnostics.Count == 0
            );
            Assert(
                diagnostics.Count == 0,
                "Generated helper errors should not be projected onto the CSMX document."
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }

    [Test]
    public async Task TestLanguageServerCompletions()
    {
        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen("file:///Completion.csmx", CompletionTestSource.Text);

        var tagCompletions = await client.CompletionAsync(
            "file:///Completion.csmx",
            CompletionTestSource.PositionOf("return <", "return <".Length)
        );
        AssertHasCompletion(tagCompletions, "FactoryView", "component completion");
        AssertHasCompletion(tagCompletions, "panel", "source intrinsic completion");
        AssertNoCompletion(tagCompletions, "Stack", "hardcoded tag completion");
        AssertNoCompletion(tagCompletions, "Button", "hardcoded tag completion");

        var attributeCompletions = await client.CompletionAsync(
            "file:///Completion.csmx",
            CompletionTestSource.PositionOf("<panel ", "<panel ".Length)
        );
        AssertHasCompletion(attributeCompletions, "class", "source attribute completion");
        AssertHasCompletion(attributeCompletions, "disabled", "source attribute completion");
        AssertNoCompletion(attributeCompletions, "onClick", "hardcoded attribute completion");

        client.DidOpen("file:///NestedExpression.csmx", NestedExpressionLspTestSource.Text);
        var nestedTagCompletions = await client.CompletionAsync(
            "file:///NestedExpression.csmx",
            NestedExpressionLspTestSource.PositionOf("return <panel", "return <".Length)
        );
        AssertHasCompletion(nestedTagCompletions, "row", "nested expression intrinsic completion");
        AssertHasCompletion(nestedTagCompletions, "label", "nested child intrinsic completion");
        AssertHasCompletion(nestedTagCompletions, "choice", "ternary nested intrinsic completion");

        var nestedAttributeCompletions = await client.CompletionAsync(
            "file:///NestedExpression.csmx",
            NestedExpressionLspTestSource.PositionOf("<row data-id", "<row ".Length)
        );
        AssertHasCompletion(
            nestedAttributeCompletions,
            "data-id",
            "nested expression element attribute completion"
        );
        AssertHasCompletion(
            nestedAttributeCompletions,
            "selected",
            "ternary nested element attribute completion"
        );
        AssertNoCompletion(
            nestedAttributeCompletions,
            "onClick",
            "hardcoded nested attribute completion"
        );

        var csharpExpressionCompletions = await client.CompletionAsync(
            "file:///NestedExpression.csmx",
            NestedExpressionLspTestSource.PositionOf("item.Name", "item.".Length)
        );
        Assert(
            csharpExpressionCompletions.Count == 0,
            "Project-less C# expression completions should not return Roslyn items."
        );
    }

    [Test]
    public async Task TestGeneratedCSharpUsesProjectContext()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "ProjectContextSmoke",
            "ProjectContextSmoke.csproj"
        );
        var sourcePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "ProjectContextSmoke",
            "Components",
            "CounterView.csmx"
        );
        var source = await File.ReadAllTextAsync(sourcePath);
        var uri = new Uri(sourcePath).AbsoluteUri;

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var generated = await client.GeneratedCSharpAsync(uri);
        AssertEqual(projectPath, generated.ProjectFilePath ?? string.Empty, "project context path");
        AssertEqual(
            Path.Combine(
                Path.GetDirectoryName(projectPath)!,
                "Generated",
                "Csmx",
                "Debug",
                "net10.0",
                "Components",
                "CounterView.csmx.g.cs"
            ),
            generated.GeneratedFilePath ?? string.Empty,
            "generated context path"
        );
        AssertContains(generated.Code, "new Column()", "fluent project projection");
        AssertContains(generated.Code, "CreateSignal(0)", "fluent project projection");
    }

    [Test]
    public async Task TestProjectRoslynCompletionsResolveCSharpSymbols()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "ProjectContextSmoke",
            "Components",
            "CounterView.csmx"
        );
        var source = """
            using static Csmx.Tests.LanguageServerFixtures.Signals;

            namespace Csmx.Tests.ProjectContextSmoke.Components;

            public sealed class SecretBox
            {
                private int Hidden { get; } = 1;

                public int Shown { get; } = 2;
            }

            public static class CounterView
            {
                public static UiNode Render()
                {
                    var (count, setCount) = CreateSignal(0);
                    var box = new SecretBox();
                    return <Column><Text>Count: {count.} Box: {box.}</Text></Column>;
                }
            }
            """;
        var uri = new Uri(sourcePath).AbsoluteUri;

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var memberCompletions = await client.CompletionAsync(
            uri,
            PositionAt(source, source.IndexOf("count.", StringComparison.Ordinal) + "count.".Length)
        );
        AssertHasCompletion(memberCompletions, "Value", "project C# member completion");

        var localCompletions = await client.CompletionAsync(
            uri,
            PositionAt(source, source.IndexOf("{count.", StringComparison.Ordinal) + 1)
        );
        AssertHasCompletion(localCompletions, "count", "project C# local completion");
        AssertHasCompletion(localCompletions, "setCount", "project C# local completion");

        var boxCompletions = await client.CompletionAsync(
            uri,
            PositionAt(source, source.IndexOf("box.", StringComparison.Ordinal) + "box.".Length)
        );
        AssertHasCompletion(boxCompletions, "Shown", "accessible member completion");
        AssertNoCompletion(boxCompletions, "Hidden", "inaccessible private member completion");
    }

    [Test]
    public async Task TestProjectRoslynCompletionsRespectCSharpContext()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "ProjectContextSmoke",
            "Components",
            "CounterView.csmx"
        );
        var source = (await File.ReadAllTextAsync(sourcePath)).Replace(
            "var (count, setCount) = CreateSignal(0);",
            """
                    var 
                    var (count, setCount) = CreateSignal(0);
            """,
            StringComparison.Ordinal
        ).Replace("{count}", "{count.Va}", StringComparison.Ordinal);
        var uri = new Uri(sourcePath).AbsoluteUri;

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var typedMemberCompletions = await client.CompletionAsync(
            uri,
            PositionAt(source, source.IndexOf("count.Va", StringComparison.Ordinal) + "count.Va".Length)
        );
        AssertHasCompletion(typedMemberCompletions, "Value", "typed project C# member completion");

        var varCompletions = await client.CompletionAsync(
            uri,
            PositionAt(source, source.IndexOf("var \n", StringComparison.Ordinal) + "var ".Length)
        );
        Assert(
            varCompletions.Count == 0,
            $"C# completion after var should be context-aware. Got: {string.Join(", ", varCompletions.Select(item => item.Label))}"
        );
    }

    [Test]
    public async Task TestRoslynHoverResolvesProjectSymbols()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "HoverSmoke",
            "Dashboard.csmx"
        );
        var source = await File.ReadAllTextAsync(sourcePath);
        var uri = new Uri(sourcePath).AbsoluteUri;

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var uiNodeHover = await client.HoverAsync(
            uri,
            PositionAt(source, source.IndexOf("UiNode Render", StringComparison.Ordinal))
        );
        AssertContains(
            uiNodeHover,
            "Csmx.Tests.LanguageServerFixtures.UiNode",
            "UiNode Roslyn hover"
        );

        var createSignalHover = await client.HoverAsync(
            uri,
            PositionAt(source, source.IndexOf("CreateSignal(\"\")", StringComparison.Ordinal))
        );
        AssertContains(createSignalHover, "CreateSignal", "CreateSignal Roslyn hover");

        var stringComparisonHover = await client.HoverAsync(
            uri,
            PositionAt(
                source,
                source.IndexOf("StringComparison.OrdinalIgnoreCase", StringComparison.Ordinal)
            )
        );
        AssertContains(stringComparisonHover, "System.StringComparison", "BCL enum Roslyn hover");
    }

    [Test]
    public async Task TestDefinitionResolvesFluentTagsAndAttributesToProjectReferenceSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var runtimePath = GetTestRuntimeSource(repositoryRoot);
        var runtimeSource = await File.ReadAllTextAsync(runtimePath);
        var sourcePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "FluentReferenceApp",
            "CounterView.csmx"
        );
        var source = await File.ReadAllTextAsync(sourcePath);
        var uri = new Uri(sourcePath).AbsoluteUri;

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var columnDefinitions = await client.DefinitionAsync(
            uri,
            PositionAt(source, source.IndexOf("<Column", StringComparison.Ordinal) + 1)
        );
        var columnPosition = PositionAt(
            runtimeSource,
            runtimeSource.IndexOf("Column : UiElement", StringComparison.Ordinal)
        );
        Assert(
            columnDefinitions.Any(item =>
                PathsEqual(new Uri(item.Uri).LocalPath, runtimePath) && item.Start == columnPosition
            ),
            $"Expected Column definition in '{runtimePath}' at {columnPosition}. Got: {FormatDefinitions(columnDefinitions)}"
        );

        var buttonDefinitions = await client.DefinitionAsync(
            uri,
            PositionAt(source, source.IndexOf("<Button", StringComparison.Ordinal) + 1)
        );
        var buttonPosition = PositionAt(
            runtimeSource,
            runtimeSource.IndexOf("Button : UiElement", StringComparison.Ordinal)
        );
        Assert(
            buttonDefinitions.Any(item =>
                PathsEqual(new Uri(item.Uri).LocalPath, runtimePath) && item.Start == buttonPosition
            ),
            $"Expected Button definition in '{runtimePath}' at {buttonPosition}. Got: {FormatDefinitions(buttonDefinitions)}"
        );

        var paddingDefinitions = await client.DefinitionAsync(
            uri,
            PositionAt(source, source.IndexOf("Padding={24}", StringComparison.Ordinal))
        );
        var paddingPosition = PositionAt(
            runtimeSource,
            runtimeSource.IndexOf("Padding(float value)", StringComparison.Ordinal)
        );
        Assert(
            paddingDefinitions.Any(item =>
                PathsEqual(new Uri(item.Uri).LocalPath, runtimePath)
                && item.Start == paddingPosition
            ),
            $"Expected Padding definition in '{runtimePath}' at {paddingPosition}. Got: {FormatDefinitions(paddingDefinitions)}"
        );

        var paddingHover = await client.HoverAsync(
            uri,
            PositionAt(source, source.IndexOf("Padding={24}", StringComparison.Ordinal))
        );
        AssertContains(paddingHover, "Padding(float)", "Padding Roslyn hover");

        var tagCompletions = await client.CompletionAsync(
            uri,
            PositionAt(source, source.IndexOf("<Column", StringComparison.Ordinal) + 1)
        );
        AssertHasCompletion(tagCompletions, "Column", "fluent tag completion");
        AssertHasCompletion(tagCompletions, "Panel", "project-reference tag completion");

        var attributeCompletions = await client.CompletionAsync(
            uri,
            PositionAt(
                source,
                source.IndexOf("<Column Padding", StringComparison.Ordinal) + "<Column ".Length
            )
        );
        AssertHasCompletion(attributeCompletions, "Padding", "fluent attribute completion");
        AssertHasCompletion(
            attributeCompletions,
            "Height",
            "inherited fluent attribute completion"
        );
    }

    [Test]
    public async Task TestProjectReferenceSourcesDoNotConflictWithMetadata()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "ConflictSmoke",
            "View.csmx"
        );
        var source = await File.ReadAllTextAsync(sourcePath);
        var uri = new Uri(sourcePath).AbsoluteUri;

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var diagnostics = await client.WaitForDiagnosticsAsync(
            uri,
            diagnostics => diagnostics.All(diagnostic => diagnostic.Code != "CS0436")
        );
        Assert(
            diagnostics.All(diagnostic => diagnostic.Code != "CS0436"),
            $"Project references should not report source/metadata conflicts. Got: {FormatDiagnostics(diagnostics)}"
        );
    }

    [Test]
    public async Task TestProjectRoslynSemanticTokensTrackEdits()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Csmx.Tests",
            "language-server",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(tempRoot);
            var projectPath = Path.Combine(tempRoot, "HoverSmoke.csproj");
            var sourcePath = Path.Combine(tempRoot, "View.csmx");
            var uri = new Uri(sourcePath).AbsoluteUri;

            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{GetTestRuntimeProject(repositoryRoot)}}" />
                  </ItemGroup>
                </Project>
                """
            );

            var firstSource = """
                using Csmx.Tests.LanguageServerFixtures;
                using static Csmx.Tests.LanguageServerFixtures.Runtime;

                namespace HoverSmoke;

                public static class View
                {
                    public static UiNode Render()
                    {
                        var (count, setCount) = Signals.CreateSignal(0);
                        return <Text FontSize={Signals.Measure(0)}>Count: {Signals.CreateSignal(0)}</Text>;
                    }
                }
                """;
            var editedSource = firstSource
                .Replace("Signals.Measure", "Signals.MeasureEdited")
                .Replace("CreateSignal", "MakeSignal")
                .Replace("return <Text", "// shifted before jsx\n        return <Text");
            File.WriteAllText(sourcePath, firstSource);

            await using var client = await LspTestClient.StartAsync();
            await client.InitializeAsync();
            client.DidOpen(uri, firstSource);

            var textHover = await client.HoverAsync(
                uri,
                PositionAt(firstSource, firstSource.IndexOf("<Text", StringComparison.Ordinal) + 1)
            );
            AssertContains(textHover, "CSMX element `<Text>`", "project JSX tag hover");

            var countHover = await client.HoverAsync(
                uri,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("count, setCount", StringComparison.Ordinal)
                )
            );
            AssertContains(
                countHover,
                "Csmx.Tests.LanguageServerFixtures.Signal<int> count",
                "deconstructed count local Roslyn hover"
            );

            var setCountHover = await client.HoverAsync(
                uri,
                PositionAt(firstSource, firstSource.IndexOf("setCount)", StringComparison.Ordinal))
            );
            AssertContains(
                setCountHover,
                "System.Action<System.Func<int, int>> setCount",
                "deconstructed setCount local Roslyn hover"
            );

            var firstTokens = await client.SemanticTokensAsync(uri);
            AssertHasSemanticToken(
                firstTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("var (count", StringComparison.Ordinal)
                ),
                "var".Length,
                SemanticKeyword,
                "implicit deconstruction var keyword token"
            );
            AssertNoSemanticToken(
                firstTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("var (count", StringComparison.Ordinal)
                ),
                "var".Length,
                SemanticType,
                "implicit deconstruction var type token"
            );
            AssertNoSemanticToken(
                firstTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("var (count", StringComparison.Ordinal)
                ),
                "var".Length,
                SemanticVariable,
                "implicit deconstruction var variable token"
            );
            AssertHasSemanticToken(
                firstTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("Text FontSize", StringComparison.Ordinal)
                ),
                "Text".Length,
                SemanticClass,
                "initial JSX tag token"
            );
            AssertNoSemanticToken(
                firstTokens,
                PositionAt(firstSource, firstSource.IndexOf("<Text", StringComparison.Ordinal)),
                "Element".Length,
                SemanticMethod,
                "generated Element method token should not leak onto JSX"
            );
            AssertHasSemanticToken(
                firstTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("Measure(0)", StringComparison.Ordinal)
                ),
                "Measure".Length,
                SemanticMethod,
                "initial Measure method token"
            );
            AssertHasSemanticToken(
                firstTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("CreateSignal", StringComparison.Ordinal)
                ),
                "CreateSignal".Length,
                SemanticMethod,
                "initial CreateSignal method token"
            );

            client.DidChange(uri, editedSource);
            var makeSignalHover = await client.HoverAsync(
                uri,
                PositionAt(
                    editedSource,
                    editedSource.IndexOf("MakeSignal", StringComparison.Ordinal)
                )
            );
            AssertContains(makeSignalHover, "MakeSignal", "edited MakeSignal Roslyn hover");

            var editedTokens = await client.SemanticTokensAsync(uri);
            AssertHasSemanticToken(
                editedTokens,
                PositionAt(
                    editedSource,
                    editedSource.IndexOf("Text FontSize", StringComparison.Ordinal)
                ),
                "Text".Length,
                SemanticClass,
                "edited JSX tag token"
            );
            AssertNoSemanticToken(
                editedTokens,
                PositionAt(editedSource, editedSource.IndexOf("<Text", StringComparison.Ordinal)),
                "Element".Length,
                SemanticMethod,
                "generated Element method token should not leak after edit"
            );
            AssertHasSemanticToken(
                editedTokens,
                PositionAt(
                    editedSource,
                    editedSource.IndexOf("MeasureEdited(0)", StringComparison.Ordinal)
                ),
                "MeasureEdited".Length,
                SemanticMethod,
                "edited MeasureEdited method token"
            );
            AssertNoSemanticToken(
                editedTokens,
                PositionAt(
                    firstSource,
                    firstSource.IndexOf("CreateSignal", StringComparison.Ordinal)
                ),
                "CreateSignal".Length,
                SemanticMethod,
                "stale CreateSignal method token after edit"
            );
            AssertHasSemanticToken(
                editedTokens,
                PositionAt(
                    editedSource,
                    editedSource.IndexOf("MakeSignal", StringComparison.Ordinal)
                ),
                "MakeSignal".Length,
                SemanticMethod,
                "edited MakeSignal method token"
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }

    [Test]
    public async Task TestProjectContextUsesEvaluatedMsBuildProperties()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Csmx.Tests",
            "language-server",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(tempRoot);
            var targets = GetTestCsmxTargets(repositoryRoot);
            var projectPath = Path.Combine(tempRoot, "TempProject.csproj");
            var sourcePath = Path.Combine(tempRoot, "View.csmx");
            var uri = new Uri(sourcePath).AbsoluteUri;
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <PropertyGroup>
                    <CsmxTextFactory>global::TempProject.Imported.Text</CsmxTextFactory>
                  </PropertyGroup>
                </Project>
                """
            );
            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <CsmxElementFactory>global::TempProject.Ui.Element</CsmxElementFactory>
                    <CsmxAttributeFactory>global::TempProject.Ui.Attr</CsmxAttributeFactory>
                    <CsmxPropsFactory>global::TempProject.Ui.Props</CsmxPropsFactory>
                    <CsmxChildrenFactory>global::TempProject.Ui.Children</CsmxChildrenFactory>
                  </PropertyGroup>
                  <Import Project="{{targets}}" />
                </Project>
                """
            );
            File.WriteAllText(
                sourcePath,
                "public static class View { public static object Render() => <text>Hello</text>; }"
            );

            await using var client = await LspTestClient.StartAsync();
            await client.InitializeAsync();
            client.DidOpen(uri, await File.ReadAllTextAsync(sourcePath));

            var generated = await client.GeneratedCSharpAsync(uri);
            AssertContains(
                generated.Code,
                "global::TempProject.Imported.Text(\"Hello\")",
                "Directory.Build.props text factory"
            );

            var binding = await client.ProjectBindingAsync(uri);
            Assert(binding.HasProject, "Project binding inspection should find a project.");
            AssertEqual("msbuild", binding.EvaluationKind ?? string.Empty, "evaluation kind");
            Assert(
                binding.CompileIncludesGeneratedFile != true,
                "Source-generator projects should not include a physical generated file in Compile items."
            );
            Assert(
                binding.ProjectContextDependencies.Any(dependency =>
                    PathsEqual(dependency.Path, Path.Combine(tempRoot, "Directory.Build.props"))
                    && dependency.Exists
                ),
                "Project binding inspection should report Directory.Build.props as a project-context dependency."
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }

    [Test]
    public async Task TestProjectContextAutoRefreshesImportedBuildProperties()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Csmx.Tests",
            "language-server",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(tempRoot);
            var targets = GetTestCsmxTargets(repositoryRoot);
            var propsPath = Path.Combine(tempRoot, "Directory.Build.props");
            var projectPath = Path.Combine(tempRoot, "TempProject.csproj");
            var sourcePath = Path.Combine(tempRoot, "View.csmx");
            var uri = new Uri(sourcePath).AbsoluteUri;

            WriteDirectoryBuildProps(propsPath, "global::TempProject.First.Text");
            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <CsmxElementFactory>global::TempProject.Ui.Element</CsmxElementFactory>
                    <CsmxAttributeFactory>global::TempProject.Ui.Attr</CsmxAttributeFactory>
                    <CsmxPropsFactory>global::TempProject.Ui.Props</CsmxPropsFactory>
                    <CsmxChildrenFactory>global::TempProject.Ui.Children</CsmxChildrenFactory>
                  </PropertyGroup>
                  <Import Project="{{targets}}" />
                </Project>
                """
            );
            File.WriteAllText(
                sourcePath,
                "public static class View { public static object Render() => <text>Hello</text>; }"
            );

            await using var client = await LspTestClient.StartAsync();
            await client.InitializeAsync();
            client.DidOpen(uri, await File.ReadAllTextAsync(sourcePath));

            var first = await client.GeneratedCSharpAsync(uri);
            AssertContains(
                first.Code,
                "global::TempProject.First.Text(\"Hello\")",
                "initial imported text factory"
            );

            await WaitForTimestampTickAsync();
            WriteDirectoryBuildProps(propsPath, "global::TempProject.Second.Text");

            var refreshed = await client.GeneratedCSharpAsync(uri);
            AssertContains(
                refreshed.Code,
                "global::TempProject.Second.Text(\"Hello\")",
                "changed imported build properties should invalidate project context automatically"
            );

            var reload = await client.ReloadProjectContextAsync(uri);
            Assert(reload.Reloaded, "reload request should remain available.");
            Assert(reload.DocumentWasOpen, "reload request should refresh the open document.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }

    [Test]
    public async Task TestProjectContextUsesRequestedConfigurationAndTargetFramework()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Csmx.Tests",
            "language-server",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(tempRoot);
            var targets = GetTestCsmxTargets(repositoryRoot);
            var projectPath = Path.Combine(tempRoot, "TempProject.csproj");
            var sourcePath = Path.Combine(tempRoot, "View.csmx");
            var uri = new Uri(sourcePath).AbsoluteUri;
            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>
                    <CsmxElementFactory>global::TempProject.Ui.Element</CsmxElementFactory>
                    <CsmxAttributeFactory>global::TempProject.Ui.Attr</CsmxAttributeFactory>
                    <CsmxPropsFactory>global::TempProject.Ui.Props</CsmxPropsFactory>
                    <CsmxChildrenFactory>global::TempProject.Ui.Children</CsmxChildrenFactory>
                  </PropertyGroup>
                  <Import Project="{{targets}}" />
                </Project>
                """
            );
            File.WriteAllText(
                sourcePath,
                "public static class View { public static object Render() => <text>Hello</text>; }"
            );

            await using var client = await LspTestClient.StartAsync();
            await client.InitializeAsync();
            client.DidOpen(uri, await File.ReadAllTextAsync(sourcePath));

            var defaultGenerated = await client.GeneratedCSharpAsync(uri);
            AssertEqual(
                Path.Combine(tempRoot, "Generated", "Csmx", "Debug", "net10.0", "View.csmx.g.cs"),
                defaultGenerated.GeneratedFilePath ?? string.Empty,
                "default editor generated path"
            );

            var options = await client.SetProjectContextOptionsAsync(
                configuration: "Release",
                targetFramework: "net10.0-windows"
            );
            Assert(options.Changed, "Project context option request should report a change.");
            AssertEqual(
                "Release",
                options.Configuration ?? string.Empty,
                "requested configuration"
            );
            AssertEqual(
                "net10.0-windows",
                options.TargetFramework ?? string.Empty,
                "requested target framework"
            );
            Assert(
                options.RefreshedDocuments == 1,
                $"Expected one refreshed document, got {options.RefreshedDocuments}."
            );

            var selectedGenerated = await client.GeneratedCSharpAsync(uri);
            AssertEqual(
                Path.Combine(
                    tempRoot,
                    "Generated",
                    "Csmx",
                    "Release",
                    "net10.0-windows",
                    "View.csmx.g.cs"
                ),
                selectedGenerated.GeneratedFilePath ?? string.Empty,
                "selected editor generated path"
            );

            var binding = await client.ProjectBindingAsync(uri);
            Assert(binding.HasProject, "Project binding inspection should find a project.");
            AssertEqual(
                "Release",
                binding.RequestedConfiguration ?? string.Empty,
                "binding requested configuration"
            );
            AssertEqual(
                "net10.0-windows",
                binding.RequestedTargetFramework ?? string.Empty,
                "binding requested target framework"
            );
            AssertEqual(
                selectedGenerated.GeneratedFilePath ?? string.Empty,
                binding.GeneratedFilePath ?? string.Empty,
                "binding generated path"
            );
            Assert(
                binding.CompileIncludesGeneratedFile != true,
                "Source-generator projects should not include physical generated Compile items for the requested configuration/TFM."
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }

    [Test]
    public async Task TestEncodedWindowsDriveFileUriDoesNotCrashServer()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "csmx-uri-smoke", "EncodedDrive.csmx");
        var uri = ToEncodedDriveFileUri(sourcePath);
        const string source = """
using Csmx.EnagaSignals;

public static class EncodedDrive
{
    public static UiNode Render()
    {
        return <Text>Smoke</Text>;
    }
}
""";

        await using var client = await LspTestClient.StartAsync();
        await client.InitializeAsync();
        client.DidOpen(uri, source);

        var tokens = await client.SemanticTokensAsync(uri);
        Assert(
            tokens.Count > 0,
            "Encoded Windows drive file URIs should keep the language server alive."
        );
    }

    static void AssertHasSemanticToken(
        IReadOnlyList<DecodedSemanticToken> tokens,
        Position position,
        int length,
        int typeIndex,
        string label
    )
    {
        var found = tokens.Any(token =>
            token.Line == position.Line
            && token.Character == position.Character
            && token.Length == length
            && token.TypeIndex == typeIndex
        );
        Assert(
            found,
            $"{label} missing at {position.Line}:{position.Character} length {length} type {typeIndex}."
        );
    }

    static Position PositionAt(string text, int index)
    {
        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                character = 0;
            }
            else if (text[i] != '\r')
            {
                character++;
            }
        }

        return new Position(line, character);
    }

    static void AssertDiagnosticStartsAt(
        IReadOnlyList<PublishedDiagnostic> diagnostics,
        string messageFragment,
        Position position,
        string label
    )
    {
        var found = diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains(messageFragment, StringComparison.Ordinal)
            && diagnostic.Start.Line == position.Line
            && diagnostic.Start.Character == position.Character
        );
        Assert(
            found,
            $"{label} should start at {position.Line}:{position.Character}. Got: {FormatDiagnostics(diagnostics)}"
        );
    }

    static string FormatDiagnostics(IReadOnlyList<PublishedDiagnostic> diagnostics) =>
        string.Join(
            " | ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Source}:{diagnostic.Code}@{diagnostic.Start.Line}:{diagnostic.Start.Character}-{diagnostic.End.Line}:{diagnostic.End.Character} {diagnostic.Message}"
            )
        );

    static string FormatDefinitions(IReadOnlyList<DefinitionLocationResult> definitions) =>
        string.Join(
            ", ",
            definitions.Select(definition =>
                $"{definition.Uri}@{definition.Start.Line}:{definition.Start.Character}"
            )
        );

    static void AssertNoSemanticToken(
        IReadOnlyList<DecodedSemanticToken> tokens,
        Position position,
        int length,
        int typeIndex,
        string label
    )
    {
        var found = tokens.Any(token =>
            token.Line == position.Line
            && token.Character == position.Character
            && token.Length == length
            && token.TypeIndex == typeIndex
        );
        Assert(
            !found,
            $"{label} should not exist at {position.Line}:{position.Character} length {length} type {typeIndex}."
        );
    }

    static void AssertHasCompletion(
        IReadOnlyList<CompletionItemResult> items,
        string label,
        string message
    )
    {
        Assert(
            items.Any(item => string.Equals(item.Label, label, StringComparison.Ordinal)),
            $"{message} should include '{label}'. Got: {string.Join(", ", items.Select(item => item.Label))}"
        );
    }

    static void AssertNoCompletion(
        IReadOnlyList<CompletionItemResult> items,
        string label,
        string message
    )
    {
        Assert(
            !items.Any(item => string.Equals(item.Label, label, StringComparison.Ordinal)),
            $"{message} should not include '{label}'. Got: {string.Join(", ", items.Select(item => item.Label))}"
        );
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

    private static string GetTestCsmxTargets(string repositoryRoot) =>
        Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "csmx-test.targets"
        );

    private static string GetTestRuntimeProject(string repositoryRoot) =>
        Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "TestRuntime",
            "TestRuntime.csproj"
        );

    private static string GetTestRuntimeSource(string repositoryRoot) =>
        Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "TestRuntime",
            "UiNodes.cs"
        );

    private static string GetMissingElementRuntimeProject(string repositoryRoot) =>
        Path.Combine(
            repositoryRoot,
            "tests",
            "Csmx.LanguageServer.Tests",
            "Fixtures",
            "MissingElementRuntime",
            "MissingElementRuntime.csproj"
        );

    private static void DeleteDirectoryBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Test cleanup best effort.
        }
    }

    private static string ToEncodedDriveFileUri(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (
            fullPath.Length >= 3
            && char.IsLetter(fullPath[0])
            && fullPath[1] == ':'
            && (fullPath[2] == '\\' || fullPath[2] == '/')
        )
        {
            return "file:///"
                + char.ToLowerInvariant(fullPath[0])
                + "%3A"
                + fullPath[2..].Replace('\\', '/');
        }

        return new Uri(fullPath).AbsoluteUri;
    }

    private static void WriteDirectoryBuildProps(string path, string textFactory)
    {
        File.WriteAllText(
            path,
            $$"""
            <Project>
              <PropertyGroup>
                <CsmxTextFactory>{{textFactory}}</CsmxTextFactory>
              </PropertyGroup>
            </Project>
            """
        );
    }

    private static Task WaitForTimestampTickAsync() => Task.Delay(TimeSpan.FromMilliseconds(1200));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );
}
