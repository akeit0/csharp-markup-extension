using Csmx.Compiler;
using TUnit.Core;

namespace Csmx.Tests;

public sealed class CompilerTransformTests
{
    [Test]
    public Task TestCompilerMappings()
    {
        var result = CsmxTransformer.Transform(TestSource.Text, "Counter.csmx");
        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Transform should not produce errors."
        );
        AssertContains(result.Code, "Element(\"stack\"", "default factory lowering");
        Assert(
            !result.Code.Contains("global::Csmx.Runtime", StringComparison.Ordinal),
            "Compiler defaults should not bake in the sample runtime."
        );

        var helloOffset =
            TestSource.Text.IndexOf("Hello {props.Name}", StringComparison.Ordinal) + 1;
        Assert(
            FindMapping(result.Mappings, helloOffset, ProjectionKind.CSharp) is null,
            "Plain JSX text must not have a CSharp mapping."
        );
        Assert(
            FindDelegationMapping(result.Mappings, helloOffset) is null,
            "Plain JSX text must not have a delegation mapping."
        );

        var propsNameOffset = TestSource.Text.IndexOf("props.Name", StringComparison.Ordinal);
        var propsNameMapping =
            FindMapping(result.Mappings, propsNameOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException("Expected props.Name child expression mapping.");
        AssertEqual(
            "props.Name",
            Slice(result.Code, propsNameMapping.GeneratedSpan),
            "props.Name generated mapping"
        );

        var propsCountOffset = TestSource.Text.IndexOf("props.Count", StringComparison.Ordinal);
        var propsCountMapping =
            FindMapping(result.Mappings, propsCountOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException(
                "Expected props.Count child expression mapping."
            );
        AssertEqual(
            "props.Count",
            Slice(result.Code, propsCountMapping.GeneratedSpan),
            "props.Count generated mapping"
        );

        var viewOffset = TestSource.Text.IndexOf("<View name", StringComparison.Ordinal) + 1;
        var viewMapping =
            FindMapping(result.Mappings, viewOffset, ProjectionKind.ComponentReference)
            ?? throw new InvalidOperationException("Expected <View> component reference mapping.");
        AssertEqual(
            "View",
            Slice(result.Code, viewMapping.GeneratedSpan),
            "View generated mapping"
        );
        AssertContains(result.Code, "props.Count == 0", "attribute expression equality operator");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestNestedChildExpressionMappingTargetsExpressionPayload()
    {
        const string source = """
public static class NestedCompletionHost
{
    public static object Render(string[] items)
    {
        return <Panel>{items.Sel}</Panel>;
    }
}
""";

        var result = CsmxTransformer.Transform(source, "NestedCompletionSmoke.csmx");
        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Transform should not produce errors."
        );

        var expressionOffset = source.IndexOf("items.Sel", StringComparison.Ordinal);
        var mapping =
            FindMapping(result.Mappings, expressionOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException("Expected child expression mapping.");
        AssertEqual(
            "items.Sel",
            Slice(source, mapping.OriginalSpan),
            "nested child expression original mapping"
        );
        AssertEqual(
            "items.Sel",
            Slice(result.Code, mapping.GeneratedSpan),
            "nested child expression generated mapping"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestComponentLoweringPolicies()
    {
        var direct = CsmxTransformer.Transform(
            ComponentPolicyTestSource.Text,
            "ComponentPolicy.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "CreateElement",
                PropsFactory = "CreateProps",
                AttributeFactory = "CreateAttr",
                ChildrenFactory = "CreateChildren",
                TextFactory = "CreateText",
                ComponentLowering = CsmxComponentLowering.DirectCall,
            }
        );

        AssertContains(
            direct.Code,
            "return View(new ViewProps { Name = name }, CreateChildren())",
            "direct component lowering"
        );

        var factory = CsmxTransformer.Transform(
            ComponentPolicyTestSource.Text,
            "ComponentPolicy.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "CreateElement",
                PropsFactory = "CreateProps",
                AttributeFactory = "CreateAttr",
                ChildrenFactory = "CreateChildren",
                TextFactory = "CreateText",
                ComponentLowering = CsmxComponentLowering.FactoryCall,
            }
        );

        AssertContains(
            factory.Code,
            "return CreateElement(View, new ViewProps { Name = name }, CreateChildren())",
            "factory component lowering"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestComponentTemplateUsesCallSite()
    {
        var source = """
            namespace Csmx.Tests;

            public sealed record ViewProps
            {
                public string Name { get; init; } = string.Empty;
            }

            public static class ComponentTemplateHost
            {
                static object View(ViewProps props, object[] children) =>
                    throw new NotImplementedException();

                public static object Render(string name) => <View name={name}>Hi</View>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "src/Views/ComponentTemplate.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "CreateElement",
                PropsFactory = "CreateProps",
                AttributeFactory = "CreateAttr",
                ChildrenFactory = "CreateChildren",
                TextFactory = "CreateText",
                SourceIdentity = "Views/ComponentTemplate.csmx",
                ComponentTemplate = "Scoped({CallSite}, () => {Component}({Props}, {Children}))",
            }
        );

        var viewStart = source.IndexOf("<View", StringComparison.Ordinal);
        var viewEnd = source.IndexOf("</View>", StringComparison.Ordinal) + "</View>".Length;

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Component template transform should not produce errors."
        );
        AssertContains(
            result.Code,
            $"Scoped(\"Views/ComponentTemplate.csmx#{viewStart}:{viewEnd - viewStart}\", () => View(new ViewProps {{ Name = name }}, CreateChildren(CreateText(\"Hi\"))))",
            "component template lowering"
        );

        var viewOffset = source.IndexOf("<View", StringComparison.Ordinal) + 1;
        var viewMapping =
            FindMapping(result.Mappings, viewOffset, ProjectionKind.ComponentReference)
            ?? throw new InvalidOperationException(
                "Expected component template reference mapping."
            );
        AssertEqual("View", Slice(result.Code, viewMapping.GeneratedSpan), "component mapping");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFluentComponentTemplateUsesCallSite()
    {
        var source = """
            namespace Csmx.Tests;

            public sealed record ViewProps
            {
                public string Name { get; init; } = string.Empty;
            }

            public static class ComponentTemplateHost
            {
                static object View(ViewProps props, object[] children) =>
                    throw new NotImplementedException();

                public static object Render(string name) =>
                    <View name={name}><Panel>Hi</Panel></View>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "src/Views/FluentComponentTemplate.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                SourceIdentity = "Views/FluentComponentTemplate.csmx",
                TextFactory = "Text",
                ChildrenFactory = "Children",
                FluentComponentTemplate =
                    "Scoped({CallSite}, () => {Component}({Props}, {Children}))",
            }
        );

        var viewStart = source.IndexOf("<View", StringComparison.Ordinal);
        var viewEnd = source.IndexOf("</View>", StringComparison.Ordinal) + "</View>".Length;

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Fluent component template transform should not produce errors."
        );
        AssertContains(
            result.Code,
            $"Scoped(\"Views/FluentComponentTemplate.csmx#{viewStart}:{viewEnd - viewStart}\", () => View(new ViewProps {{ Name = name }}, Children(new Panel().Content(\"Hi\"))))",
            "fluent component template lowering"
        );

        var viewOffset = source.IndexOf("<View", StringComparison.Ordinal) + 1;
        var viewMapping =
            FindMapping(result.Mappings, viewOffset, ProjectionKind.ComponentReference)
            ?? throw new InvalidOperationException(
                "Expected fluent component template reference mapping."
            );
        AssertEqual(
            "View",
            Slice(result.Code, viewMapping.GeneratedSpan),
            "fluent component mapping"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFluentLowering()
    {
        var source = """
            namespace Csmx.Tests;

            public static class FluentHost
            {
                public static object Render(int size, object child)
                {
                    return <Button Size={size} title="Run">Click {child}</Button>;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "Fluent.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Fluent transform should not produce errors."
        );
        AssertContains(
            result.Code,
            "return new Button().Size(size).Title(\"Run\").Content(\"Click \").Content(child);",
            "default fluent lowering"
        );
        var buttonOffset = source.IndexOf("Button Size", StringComparison.Ordinal);
        var buttonMapping = FindMapping(
            result.Mappings,
            buttonOffset,
            ProjectionKind.ElementReference
        );
        Assert(
            buttonMapping is not null,
            "Fluent tag name should map to generated element reference."
        );
        AssertEqual(
            "Button",
            result.Code.Substring(
                buttonMapping!.GeneratedSpan.Start,
                buttonMapping.GeneratedSpan.Length
            ),
            "fluent element reference generated span"
        );

        var withoutNew = CsmxTransformer.Transform(
            source,
            "Fluent.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                FluentCreate = "{Element}()",
            }
        );

        AssertContains(
            withoutNew.Code,
            "return Button().Size(size).Title(\"Run\").Content(\"Click \").Content(child);",
            "fluent lowering without new"
        );

        var nested = CsmxTransformer.Transform(
            """
            namespace Csmx.Tests;

            public static class FluentHost
            {
                public static object Render() => <Panel><Button Size={10}>Click</Button></Panel>;
            }
            """,
            "FluentNested.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
            }
        );

        AssertContains(
            nested.Code,
            "new Panel().Content(new Button().Size(10).Content(\"Click\"))",
            "nested fluent element child"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSourceDirectivesOverrideProjectOptions()
    {
        var source = """
            // @csmxCompileMode fluent
            // @csmxCompileMode invalid
            // @csmxFluentCreate First({Element})
            // @csmxFluentCreate Second({Element})

            public static class DirectiveHost
            {
                public static object Render() => <Text>Hello</Text>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "DirectiveHost.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Factory,
                ElementFactory = "ShouldNotAppear",
                FluentCreate = "Ignored({Element})",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Source directive transform should not produce errors."
        );
        AssertContains(
            result.Code,
            "Second(Text).Content(\"Hello\")",
            "last valid source directive should override project/default options"
        );
        Assert(
            !result.Code.Contains("ShouldNotAppear", StringComparison.Ordinal),
            "source compile mode directive should override project compile mode"
        );

        var invalidFactory = CsmxTransformer.Transform(
            """
            // @jsx Bad Factory

            public static class InvalidFactoryDirectiveHost
            {
                public static object Render() => <text>Hello</text>;
            }
            """,
            "InvalidFactoryDirectiveHost.csmx",
            CsmxTransformOptions.Default with
            {
                ElementFactory = "ConfiguredElement",
            }
        );

        AssertContains(
            invalidFactory.Code,
            "ConfiguredElement(\"text\"",
            "invalid factory directive should be ignored"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestUnknownTemplatePlaceholdersProduceWarningsAndPassThrough()
    {
        var source = """
            public sealed record ViewProps;

            public static class UnknownTemplateHost
            {
                static object View(ViewProps props, object[] children) =>
                    throw new NotImplementedException();

                public static object Render() => <View />;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "UnknownTemplateHost.csmx",
            CsmxTransformOptions.Default with
            {
                ComponentTemplate = "Wrap({Componnet}, {Props}, {Children})",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Unknown template placeholder should not be fatal in the current lenient policy."
        );
        Assert(
            result.Diagnostics.Any(d =>
                d.Severity == CsmxDiagnosticSeverity.Warning
                && d.Message.Contains("{Componnet}", StringComparison.Ordinal)
                && d.Message.Contains(
                    nameof(CsmxTransformOptions.ComponentTemplate),
                    StringComparison.Ordinal
                )
            ),
            "Unknown template placeholder should produce a warning."
        );
        AssertContains(result.Code, "{Componnet}", "unknown placeholder pass-through");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFluentTemplateWarningsUseAppliedOptionName()
    {
        var source = """
            public static class FluentTemplateWarningHost
            {
                public static object Render(int count) => <Text>{count}</Text>;
            }
            """;
        const string sharedTemplate = ".Content({Value}, {Unknown})";

        var result = CsmxTransformer.Transform(
            source,
            "FluentTemplateWarningHost.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                FluentTextChild = sharedTemplate,
                FluentExpressionChild = sharedTemplate,
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Unknown fluent template placeholder should remain lenient."
        );
        var warning =
            result.Diagnostics.SingleOrDefault(d => d.Severity == CsmxDiagnosticSeverity.Warning)
            ?? throw new InvalidOperationException("Expected one fluent template warning.");
        Assert(
            warning.Message.Contains("{Unknown}", StringComparison.Ordinal)
                && warning.Message.Contains(
                    nameof(CsmxTransformOptions.FluentExpressionChild),
                    StringComparison.Ordinal
                ),
            "Fluent expression child warning should name the applied option, not another option with the same template text."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestKeyedSequenceComponentConflictProducesWarning()
    {
        var source = """
            public sealed record ForProps;
            public sealed record Item(int Id, string Name);

            public static class KeyedConflictHost
            {
                public static object Render(Item[] items) =>
                    <For Items={items} Key={item => item.Id}>
                        {item => <Text>{item.Name}</Text>}
                    </For>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "KeyedConflictHost.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                ComponentNames = "For=ForProps",
                KeyedSequenceElement = "For",
                KeyedSequenceTemplate = "Keyed({CallSite}, {Items}, {Key}, {Render})",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Keyed/component conflict should remain a warning while keyed lowering wins."
        );
        Assert(
            result.Diagnostics.Any(d =>
                d.Severity == CsmxDiagnosticSeverity.Warning
                && d.Message.Contains("keyed sequence lowering wins", StringComparison.Ordinal)
            ),
            "Keyed/component name conflict should be visible."
        );
        AssertContains(result.Code, "Keyed(", "keyed sequence lowering should still win");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestSourceMapExactSpansWithCrLfAndUnicodeTag()
    {
        var source = string.Join(
            "\r\n",
            "public static class UnicodeHost",
            "{",
            "    public static object Render(int count, string name) => <日本語 data-id={count}>Hi {name}</日本語>;",
            "}"
        );

        var result = CsmxTransformer.Transform(source, "UnicodeHost.csmx");

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Unicode tag transform should not produce errors."
        );
        AssertContains(result.Code, "Element(\"日本語\"", "unicode intrinsic tag");
        AssertContains(result.Code, "Attr(\"data-id\", count)", "kebab attribute name");

        var countOffset = source.IndexOf("count}>", StringComparison.Ordinal);
        var countMapping =
            FindMapping(result.Mappings, countOffset, ProjectionKind.AttributeExpression)
            ?? throw new InvalidOperationException("Expected count attribute mapping.");
        AssertEqual("count", Slice(source, countMapping.OriginalSpan), "count source span");
        AssertEqual(
            "count",
            Slice(result.Code, countMapping.GeneratedSpan),
            "count generated span"
        );

        var nameOffset = source.IndexOf("name}</", StringComparison.Ordinal);
        var nameMapping =
            FindMapping(result.Mappings, nameOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException("Expected name child mapping.");
        AssertEqual("name", Slice(source, nameMapping.OriginalSpan), "name source span");
        AssertEqual("name", Slice(result.Code, nameMapping.GeneratedSpan), "name generated span");

        var textOffset = source.IndexOf("Hi ", StringComparison.Ordinal);
        Assert(
            FindDelegationMapping(result.Mappings, textOffset) is null,
            "Plain text child should not have a C# delegation mapping."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFluentAttributeReferencesMapNameTokens()
    {
        var source = """
            public static class FluentAttributeHost
            {
                public static object Render(int count) => <Panel Padding={24} data-id={count} />;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "FluentAttributeHost.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Fluent attribute reference transform should not produce errors."
        );

        var paddingOffset = source.IndexOf("Padding={24}", StringComparison.Ordinal);
        var paddingMapping =
            FindMapping(result.Mappings, paddingOffset, ProjectionKind.AttributeReference)
            ?? throw new InvalidOperationException("Expected Padding attribute reference mapping.");
        AssertEqual("Padding", Slice(source, paddingMapping.OriginalSpan), "Padding source span");
        AssertEqual(
            "Padding",
            Slice(result.Code, paddingMapping.GeneratedSpan),
            "Padding generated span"
        );

        var dataIdOffset = source.IndexOf("data-id={count}", StringComparison.Ordinal);
        var dataIdMapping =
            FindMapping(result.Mappings, dataIdOffset, ProjectionKind.AttributeReference)
            ?? throw new InvalidOperationException("Expected data-id attribute reference mapping.");
        AssertEqual("data-id", Slice(source, dataIdMapping.OriginalSpan), "data-id source span");
        AssertEqual(
            "DataId",
            Slice(result.Code, dataIdMapping.GeneratedSpan),
            "data-id generated member span"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFluentCallSiteTemplateUsesSourceIdentityAndSpan()
    {
        var source = """
            namespace Csmx.Tests;

            public static class FluentHost
            {
                public static object Render() => <Panel><Button>Click</Button></Panel>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "src/Views/CallSites.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                SourceIdentity = "Views/CallSites.csmx",
                FluentCreate = "Create({Element}, {CallSite})",
            }
        );

        var panelStart = source.IndexOf("<Panel>", StringComparison.Ordinal);
        var panelEnd = source.IndexOf("</Panel>", StringComparison.Ordinal) + "</Panel>".Length;
        var buttonStart = source.IndexOf("<Button>", StringComparison.Ordinal);
        var buttonEnd = source.IndexOf("</Button>", StringComparison.Ordinal) + "</Button>".Length;

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Fluent call-site transform should not produce errors."
        );
        AssertContains(
            result.Code,
            $"Create(Panel, \"Views/CallSites.csmx#{panelStart}:{panelEnd - panelStart}\")",
            "parent element call-site"
        );
        AssertContains(
            result.Code,
            $"Create(Button, \"Views/CallSites.csmx#{buttonStart}:{buttonEnd - buttonStart}\")",
            "nested element call-site"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFluentKeyedSequenceLoweringTemplate()
    {
        var source = """
            namespace Csmx.Tests;

            public sealed record Item(int Id, string Name);

            public static class FluentHost
            {
                public static object Render(Item[] items) =>
                    <Column>
                        <For Items={items} Key={item => item.Id}>
                            {item => <Text>{item.Name}</Text>}
                        </For>
                    </Column>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "src/Views/KeyedSequence.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                SourceIdentity = "Views/KeyedSequence.csmx",
                KeyedSequenceElement = "For",
                KeyedSequenceTemplate = "Keyed({CallSite}, {Items}, {Key}, {Render})",
            }
        );

        var forStart = source.IndexOf("<For", StringComparison.Ordinal);
        var forEnd = source.IndexOf("</For>", StringComparison.Ordinal) + "</For>".Length;

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Keyed sequence transform should not produce errors."
        );
        AssertContains(
            result.Code,
            $"new Column().Content(Keyed(\"Views/KeyedSequence.csmx#{forStart}:{forEnd - forStart}\", items, item => item.Id, item => new Text().Content(item.Name)))",
            "keyed sequence fluent lowering"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestKeyedSequenceTemplateMappings()
    {
        var source = """
            namespace Csmx.Tests;

            public sealed record Item(int Id, string Name);

            public static class FluentHost
            {
                public static object Render(Item[] items) =>
                    <For Items={items} Key={item => item.Id}>
                        {item => <Text>{item.Name}</Text>}
                    </For>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "src/Views/KeyedSequenceMappings.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                SourceIdentity = "Views/KeyedSequenceMappings.csmx",
                KeyedSequenceElement = "For",
                KeyedSequenceTemplate = "Keyed({CallSite}, {Items}, {Key}, {Render})",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Keyed sequence mapping transform should not produce errors."
        );

        var itemsOffset = source.IndexOf("items} Key", StringComparison.Ordinal);
        var itemsMapping =
            FindMapping(result.Mappings, itemsOffset, ProjectionKind.AttributeExpression)
            ?? throw new InvalidOperationException("Expected keyed sequence items mapping.");
        AssertEqual("items", Slice(result.Code, itemsMapping.GeneratedSpan), "items mapping");

        var keyOffset = source.IndexOf("item => item.Id", StringComparison.Ordinal);
        var keyMapping =
            FindMapping(result.Mappings, keyOffset, ProjectionKind.AttributeExpression)
            ?? throw new InvalidOperationException("Expected keyed sequence key mapping.");
        AssertEqual("item => item.Id", Slice(result.Code, keyMapping.GeneratedSpan), "key mapping");

        var renderOffset = source.IndexOf("item => <Text>", StringComparison.Ordinal);
        var renderMapping =
            FindMapping(result.Mappings, renderOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException("Expected keyed sequence render mapping.");
        AssertEqual(
            "item => ",
            Slice(result.Code, renderMapping.GeneratedSpan),
            "render lambda mapping"
        );

        var childOffset = source.IndexOf("item.Name", StringComparison.Ordinal);
        var childMapping =
            FindMapping(result.Mappings, childOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException("Expected keyed sequence nested child mapping.");
        AssertEqual(
            "item.Name",
            Slice(result.Code, childMapping.GeneratedSpan),
            "nested child mapping"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestKeyedSequenceLoweringDiagnostics()
    {
        var source = """
            namespace Csmx.Tests;

            public sealed record Item(int Id, string Name);

            public static class FluentHost
            {
                public static object MissingKey(Item[] items) =>
                    <Column>
                        <For Items={items}>{item => <Text>{item.Name}</Text>}</For>
                    </Column>;

                public static object MissingRender(Item[] items) =>
                    <Column>
                        <For Items={items} Key={item => item.Id}></For>
                    </Column>;
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "KeyedSequenceDiagnostics.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                KeyedSequenceElement = "For",
                KeyedSequenceTemplate = "Keyed({CallSite}, {Items}, {Key}, {Render})",
            }
        );

        AssertContains(
            string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)),
            "requires expression attribute 'Key'",
            "missing keyed sequence key diagnostic"
        );
        AssertContains(
            string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)),
            "requires one child expression render lambda",
            "missing keyed sequence render diagnostic"
        );

        var missingTemplate = CsmxTransformer.Transform(
            """
            namespace Csmx.Tests;

            public sealed record Item(int Id, string Name);

            public static class FluentHost
            {
                public static object Render(Item[] items) =>
                    <Column>
                        <For Items={items} Key={item => item.Id}>{item => <Text>{item.Name}</Text>}</For>
                    </Column>;
            }
            """,
            "KeyedSequenceMissingTemplate.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                KeyedSequenceElement = "For",
            }
        );

        AssertContains(
            string.Join("\n", missingTemplate.Diagnostics.Select(diagnostic => diagnostic.Message)),
            "requires CsmxKeyedSequenceTemplate",
            "missing keyed sequence template diagnostic"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFormattedChildExpressions()
    {
        var source = """
            namespace Csmx.Tests;

            public static class FormatHost
            {
                public static object Render(float value, bool condition, int key, int[] values)
                {
                    return <Text>Value : {value:F2} Aligned: {value,8:F2} Ternary: {condition ? value : values[key]}</Text>;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "Formatted.csmx",
            new CsmxTransformOptions
            {
                TextFactory = "Context",
                FormattedTextChild = "Context({Value}, {Format})",
                AlignedFormattedTextChild = "Context({Value}, {Alignment}, {Format})",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Formatted child transform should not produce errors."
        );
        AssertContains(result.Code, "Context(value, \"F2\")", "formatted child expression");
        AssertContains(
            result.Code,
            "Context(value, 8, \"F2\")",
            "aligned formatted child expression"
        );
        AssertContains(
            result.Code,
            "Context(condition ? value : values[key])",
            "ternary and indexer expression should not be split as format"
        );

        var valueOffset = source.IndexOf("value:F2", StringComparison.Ordinal);
        var valueMapping =
            FindMapping(result.Mappings, valueOffset, ProjectionKind.ChildExpression)
            ?? throw new InvalidOperationException("Expected formatted value mapping.");
        AssertEqual(
            "value",
            Slice(result.Code, valueMapping.GeneratedSpan),
            "formatted value mapping"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestFormattedChildExpressionsInFluentMode()
    {
        var source = """
            namespace Csmx.Tests;

            public static class FluentFormatHost
            {
                public static object Render(float value)
                {
                    return <Text>{value:F2} {value,8:F2}</Text>;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "FluentFormatted.csmx",
            CsmxTransformOptions.Default with
            {
                CompileMode = CsmxCompileMode.Fluent,
                FluentFormattedExpressionChild = ".Content({Value}, {Format}, {Alignment})",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Fluent formatted transform should not produce errors."
        );
        AssertContains(
            result.Code,
            "new Text().Content(value, \"F2\", null).Content(\" \").Content(value, \"F2\", 8)",
            "fluent formatted expression lowering"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestNestedJsxInsideCSharpExpressionHoles()
    {
        var source = """
            namespace Csmx.Tests;

            public static class NestedExpressionHost
            {
                public static object Render(bool condition, Item[] items)
                {
                    var choice = condition ? <text>Yes</text> : <text>No</text>;
                    return <panel>{items.Select(item => <text>{item.Name}</text>)}</panel>;
                }
            }

            public sealed record Item(string Name);
            """;

        var result = CsmxTransformer.Transform(
            source,
            "NestedExpressions.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "Element",
                PropsFactory = "Props",
                AttributeFactory = "Attr",
                ChildrenFactory = "Children",
                ChildSequenceFactory = "ChildSequence",
                TextFactory = "Text",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Nested JSX expression transform should not produce errors."
        );
        AssertContains(
            result.Code,
            "var choice = condition ? Element(\"text\", Props(), Children(Text(\"Yes\"))) : Element(\"text\", Props(), Children(Text(\"No\")));",
            "ternary nested JSX lowering"
        );
        AssertContains(
            result.Code,
            "Children(ChildSequence(items.Select(item => Element(\"text\", Props(), Children(Text(item.Name))))))",
            "lambda nested JSX inside child expression"
        );

        var itemOffset = source.IndexOf("item.Name", StringComparison.Ordinal);
        var itemMapping =
            FindDelegationMapping(result.Mappings, itemOffset)
            ?? throw new InvalidOperationException("Expected nested child expression mapping.");
        AssertEqual(
            "item.Name",
            Slice(result.Code, itemMapping.GeneratedSpan),
            "nested item.Name mapping"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestParserRecoversParentClosingTagFromBrokenNestedElement()
    {
        var source = """
            namespace Csmx.Tests;

            public static class BrokenHost
            {
                public static object Render()
                {
                    var node = <panel><row><label>One</panel>;
                    var after = 1;
                    return after;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "BrokenNested.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "Element",
                PropsFactory = "Props",
                AttributeFactory = "Attr",
                ChildrenFactory = "Children",
                TextFactory = "Text",
            }
        );

        Assert(result.Diagnostics.Count > 0, "Broken nested JSX should produce diagnostics.");
        AssertContains(result.Code, "var after = 1;", "C# after broken JSX should be preserved");
        Assert(
            !result.Code.Contains("</panel>", StringComparison.Ordinal),
            "Recovered parent closing tag should not be copied as C# text."
        );
        Assert(
            !result.Code.Contains("Text(\";", StringComparison.Ordinal),
            "C# after recovered JSX should not be lowered as child text."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestParserRecoversFollowingAttributesAfterBrokenAttributeExpression()
    {
        var source = """
            namespace Csmx.Tests;

            public static class BrokenAttributeHost
            {
                public static object Render(string name)
                {
                    var node = <panel title={name class="box" disabled>Text</panel>;
                    var after = 1;
                    return after;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "BrokenAttribute.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "Element",
                PropsFactory = "Props",
                AttributeFactory = "Attr",
                ChildrenFactory = "Children",
                TextFactory = "Text",
            }
        );

        Assert(result.Diagnostics.Count > 0, "Broken attribute JSX should produce diagnostics.");
        AssertContains(result.Code, "Attr(\"title\", name)", "recovered attribute expression");
        AssertContains(result.Code, "Attr(\"class\", \"box\")", "following string attribute");
        AssertContains(result.Code, "Attr(\"disabled\", true)", "following boolean attribute");
        AssertContains(result.Code, "var after = 1;", "C# after broken attribute JSX");

        return Task.CompletedTask;
    }

    [Test]
    public Task TestAttributeRecoveryDoesNotSplitLambdaExpressions()
    {
        var source = """
            namespace Csmx.Tests;

            public static class LambdaAttributeHost
            {
                public static object Render(Action<int> setCount)
                {
                    return <button onClick={() => setCount(1)} disabled={setCount == null}>Run</button>;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "LambdaAttribute.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "Element",
                PropsFactory = "Props",
                AttributeFactory = "Attr",
                ChildrenFactory = "Children",
                TextFactory = "Text",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Lambda attribute transform should not produce errors."
        );
        AssertContains(
            result.Code,
            "Attr(\"onClick\", () => setCount(1))",
            "lambda attribute expression"
        );
        AssertContains(
            result.Code,
            "Attr(\"disabled\", setCount == null)",
            "equality attribute expression"
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestParserRecoversOpeningTagMissingTerminator()
    {
        var source = """
            namespace Csmx.Tests;

            public static class MissingTagEndHost
            {
                public static object Render(string name)
                {
                    var node = <panel title={name}
                    var after = 1;
                    return after;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "MissingTagEnd.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "Element",
                PropsFactory = "Props",
                AttributeFactory = "Attr",
                ChildrenFactory = "Children",
                TextFactory = "Text",
            }
        );

        Assert(result.Diagnostics.Count > 0, "Missing tag terminator should produce diagnostics.");
        AssertContains(result.Code, "Attr(\"title\", name)", "attribute before missing terminator");
        AssertContains(result.Code, "var after = 1;", "C# after missing tag terminator");
        Assert(
            !result.Code.Contains("Attr(\"var\"", StringComparison.Ordinal),
            "C# after missing tag terminator should not be parsed as JSX attributes."
        );

        return Task.CompletedTask;
    }

    [Test]
    public Task TestComponentDiscovery()
    {
        var source = """
            namespace Csmx.Tests;

            public sealed record QualifiedProps
            {
                public string Name { get; init; } = string.Empty;
            }

            public sealed record NullableProps
            {
                public string Name { get; init; } = string.Empty;
            }

            public sealed record MethodProps
            {
                public string Name { get; init; } = string.Empty;
            }

            public static class ComponentDiscovery
            {
                static object MethodView(MethodProps props, object[] children) =>
                    throw new NotImplementedException();

                static object NotAComponent(string name, int count) =>
                    throw new NotImplementedException();

                public static object Render(string name)
                {
                    // Func<FakeProps, FakeChildren, FakeResult> Fake = null!;
                    var text = "Func<StringProps, StringChildren, StringResult> StringView = null!";
                    System.Func<QualifiedProps, Csmx.SampleRuntime.VNode[], Csmx.SampleRuntime.VNode> Qualified =
                        static (_, _) => throw new NotImplementedException();
                    Func<NullableProps?, Csmx.SampleRuntime.VNode[], Csmx.SampleRuntime.VNode> Nullable =
                        static (_, _) => throw new NotImplementedException();

                    return <Qualified name={name}><Nullable name={name}/><MethodView name={name}/></Qualified>;
                }
            }
            """;

        var result = CsmxTransformer.Transform(
            source,
            "ComponentDiscovery.csmx",
            new CsmxTransformOptions
            {
                ElementFactory = "CreateElement",
                PropsFactory = "CreateProps",
                AttributeFactory = "CreateAttr",
                ChildrenFactory = "CreateChildren",
                TextFactory = "CreateText",
            }
        );

        Assert(
            !result.Diagnostics.Any(d => d.Severity == CsmxDiagnosticSeverity.Error),
            "Component discovery transform should not produce errors."
        );
        AssertContains(
            result.Code,
            "Qualified(new QualifiedProps { Name = name }, CreateChildren(Nullable(new NullableProps { Name = name }, CreateChildren()), MethodView(new MethodProps { Name = name }, CreateChildren())))",
            "syntax-aware component discovery"
        );
        Assert(
            !result.Code.Contains("new String", StringComparison.Ordinal),
            "Method discovery should not treat ordinary methods as component declarations."
        );
        Assert(
            !result.Code.Contains("new FakeProps", StringComparison.Ordinal),
            "Component discovery should ignore comments."
        );
        Assert(
            !result.Code.Contains("new StringProps", StringComparison.Ordinal),
            "Component discovery should ignore string literals."
        );

        return Task.CompletedTask;
    }

    static SourceMapEntry? FindDelegationMapping(
        IReadOnlyList<SourceMapEntry> mappings,
        int offset
    ) =>
        mappings.FirstOrDefault(mapping =>
            mapping.Kind
                is ProjectionKind.CSharp
                    or ProjectionKind.ChildExpression
                    or ProjectionKind.AttributeExpression
                    or ProjectionKind.ComponentReference
                    or ProjectionKind.ElementReference
                    or ProjectionKind.AttributeReference
            && Contains(mapping.OriginalSpan, offset)
        );

    static SourceMapEntry? FindMapping(
        IReadOnlyList<SourceMapEntry> mappings,
        int offset,
        ProjectionKind kind
    ) =>
        mappings.FirstOrDefault(mapping =>
            mapping.Kind == kind && Contains(mapping.OriginalSpan, offset)
        );

    static bool Contains(TextSpan span, int offset) => offset >= span.Start && offset < span.End;

    static string Slice(string text, TextSpan span) => text.Substring(span.Start, span.Length);
}
