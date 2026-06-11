# CSMX Features And Architecture

This is a standalone review artifact for the current CSMX experiment. A reviewer should be able to understand the feature set, architecture, contracts, risks, and acceptance scenarios without opening the repository, source files, README, or roadmap. Source file paths and type names are included as labels for orientation only; all review-relevant behavior is described here.

## Summary

CSMX is C# with JSX-like expression islands. A `.csmx` file is mostly normal C#; JSX-like element expressions are transformed into ordinary generated C# before compilation. The goal is to let C# UI/runtime projects use compact element syntax without forking the C# compiler, replacing MSBuild, or running a second C# language server.

Current build integration uses `Csmx.SourceGenerator`. Projects treat `.csmx` files as Roslyn `AdditionalFiles`, and generated C# is contributed by the compiler's source-generator pipeline. The generator is a `netstandard2.0` analyzer with the portable compiler sources compiled into the analyzer assembly. Normal builds do not write physical `Generated/Csmx/**/*.g.cs` projections or CSMX-owned `obj/csmx` directories.

Example source:

```csharp
using Csmx.SampleRuntime;

namespace Demo.Components;

public static class Counter
{
    public static VNode Render(string name, int count)
    {
        return <stack class="counter">
            <text>Hello {name}</text>
            <text>Count: {count}</text>
            <button disabled={count == 0}>Click</button>
        </stack>;
    }
}
```

Representative generated C# shape:

```csharp
#line 1 "Components/Counter.csmx"
using Csmx.SampleRuntime;

namespace Demo.Components;

public static class Counter
{
    public static VNode Render(string name, int count)
    {
        return Element(
            "stack",
            Props(Attr("class", "counter")),
            Children(
                Element("text", Props(), Children(Text("Hello "), Text(name))),
                Element("text", Props(), Children(Text("Count: "), Text(count))),
                Element("button", Props(Attr("disabled", count == 0)), Children(Text("Click")))));
    }
}
#line default
```

The exact generated calls are controlled by project properties. Real projects normally configure fully-qualified factory calls or fluent builder calls instead of relying on short default names.

Generated source is contributed to Roslyn directly, so normal C# compilation can bind project references, `using` directives, `using static` imports, and `.csmx`-defined symbols through generated source without any project-local generated files.

## Review Decision Summary

This artifact is intended to validate the current architecture and the migration away from the first prototype. The important decisions are:

1. CSMX uses Roslyn source generation as the build substrate.
2. CSMX remains a projection/lowering tool, not a C# compiler fork.
3. Runtime behavior is configured through lowering templates and runtime libraries rather than encoded in the parser or IR.
4. Editor semantics should move from VS Code request forwarding to an owned Roslyn workspace. This migration now covers project-backed hover, semantic tokens, and mapped C# diagnostics for `.csmx`.

Current readiness:

| Area | Status | Review decision |
|---|---|---|
| Build integration | Source generator | Accept if samples build after deleting physical `Generated/Csmx` roots, do not recreate them, and `.cs` can bind `.csmx` symbols. |
| Editor delegation | Owned Roslyn hover/definition/tokens/diagnostics/tag completion/attribute completion; scratch/no-project delegation only | Implement remaining project-backed C# expression completion in owned Roslyn-workspace LSP. |
| Syntax | Heuristic | Accept only with explicit unsupported contexts and parser recovery tests. |
| Runtime model | Runtime-neutral projection | Accept while parser/IR stay independent from sample runtimes. |
| Security/trust | Experimental | Do not treat as safe for untrusted workspaces. |
| Multi-TFM editor support | Implemented explicit editor selection | Accept if inspected binding and generated projection switch to requested Configuration/TFM without reopening documents. |
| Source-map projection | Prototype complete with mapping limits | Accept if expression/component round-trip examples stay covered by tests. |

## Design Goals

- Keep `.csmx` close to normal C# so existing types, references, namespaces, `using` directives, nullable context, analyzers, and builds still matter.
- Avoid a C# compiler fork and keep transformation as a source-generation/projection step.
- Let Roslyn provide normal C# intelligence through an owned workspace instead of forwarding requests to another VS Code extension.
- Keep parser and IR runtime-neutral. Enaga, sample VNodes, `Button`, `Signal<T>`, and other runtime concepts belong in project configuration or runtime libraries.
- Support multiple runtime shapes, especially factory-style virtual nodes and fluent native UI builders.
- Make generated paths inspectable and deterministic enough for build/editor parity checks.
- Prefer observable failure modes through commands and output channels over silent delegation failure.

## Non-Goals

- Replacing the C# compiler or implementing a complete C# parser.
- Defining a JavaScript-like component model.
- Making one runtime shape the language default.
- Adding a broad untyped runtime API just to make every framework fit.
- Continuing to depend on VS Code request forwarding as the final C# editor integration.

## Glossary

| Term | Meaning |
|---|---|
| Source file | A user-authored `.csmx` file. |
| Projection | The generated `.cs` view of a `.csmx` file. |
| Physical projection | A legacy `.csmx.g.cs` file written to disk by the old generator path. It is not part of normal builds. |
| Source map | A set of UTF-16 span mappings from `.csmx` source to generated C# projection. |
| Delegation | VS Code extension behavior that asks the installed C# extension for features on generated C# and maps results back. |
| Intrinsic element | An element lowered through runtime/factory/fluent templates rather than component-call lowering. |
| Component element | An element classified through configured or discovered component facts and lowered as a component call/template. |
| Source-fact discovery | Syntax-based scanning of the current `.csmx` text for component-like declarations; it is not Roslyn semantic binding. |

## One-Page Architecture

Build-time flow:

```text
User edits Components/Counter.csmx
  -> MSBuild includes it as a Csmx item and AdditionalFiles item
  -> Csmx.SourceGenerator receives the AdditionalText plus visible CSMX properties
  -> Csmx.Compiler transforms it
  -> Roslyn receives generated C# source from the generator
  -> normal C# compiler compiles ordinary .cs files plus source-generator output
```

Editor-time flow:

```text
User opens Components/Counter.csmx in VS Code
  -> VS Code extension starts Csmx.LanguageServer
  -> language server evaluates containing .csproj with dotnet msbuild
  -> language server transforms the source using evaluated project properties
  -> owned Roslyn workspace resolves project-backed hover, definitions, semantic tokens, and diagnostics
  -> legacy generated-document delegation remains only for scratch/no-project fallbacks
```

Standalone-file fallback:

```text
User opens a .csmx file with no containing .csproj
  -> language server cannot compute a project-local generated path
  -> extension uses an in-memory csmx-csharp: generated document
  -> C# delegation may work for syntax or BCL cases but does not get normal project references
```

Main components:

| Component | Responsibility |
|---|---|
| `Csmx.Compiler` | Scans mostly-normal C#, parses JSX islands, builds target-neutral IR, lowers through configured backends, emits source maps and CSMX diagnostics. |
| `Csmx.SourceGenerator` | Roslyn source generator that transforms `.csmx` `AdditionalFiles` into generated C# for normal compilation. |
| `CsmxProjectedDocument` | Per-open-document projection object that owns source text, generated C# text, source maps, Roslyn syntax tree, semantic model, and source/generated mapping helpers. |
| `CsmxRoslynProjectLoader` | Loads project-backed Roslyn inputs for the language server: project sources, transformed sibling `.csmx` files, direct project-reference sources, metadata references, and cache dependencies. |
| `CsmxRoslynWorkspace` | Language-server-owned Roslyn project context for project-backed `.csmx` files. It builds `CsmxProjectedDocument` instances from open documents and resolves hover, definitions, tag completion, attribute completion, semantic tokens, and diagnostics without asking the external C# extension. |
| `Csmx.LanguageServer` | Owns CSMX diagnostics, project-backed C# diagnostics, tag/attribute completion, syntax hover, semantic tokens, generated projection requests, project binding inspection, and project-backed Roslyn hover/completion/tokens. |
| `vscode/csmx-vscode` | Starts the language server and uses generated-document delegation only for scratch/no-project fallbacks. |

## Correctness Invariants

These invariants are the most important review targets. Changes across compiler, build integration, language server, or extension code should preserve them.

| Area | Invariant | Failure symptom |
|---|---|---|
| Build/editor option parity | Build-time source generation and editor-time Roslyn workspace transforms must use the same evaluated CSMX properties. | Runtime factories, keyed templates, or fluent lowering differ between build and editor. |
| Compile inclusion | `.csmx` inputs must be `AdditionalFiles`; physical `.csmx.g.cs` files must not be included as project `Compile` items. | Duplicate type/member definitions or stale generated code appears in C# diagnostics. |
| C# binding | Project-backed Roslyn workspace must include generated source for open `.csmx` files and project references. | BCL symbols work but project references, `using`, `using static`, or imported members do not resolve. |
| Source maps | Mappings used for owned Roslyn projection and scratch delegation must be UTF-16 offset compatible with VS Code documents and must map expression holes, attribute expressions, attribute references, and component/element references without off-by-one drift. | Hover, definition, completion, diagnostics, or semantic tokens land on the wrong source span. |
| Parser recovery | A malformed JSX island must produce localized CSMX diagnostics and preserve unrelated C# regions after the broken island. | One broken tag kills features for the rest of the file or turns following C# statements into text/attributes. |
| Runtime neutrality | Parser and IR must not depend on Enaga, VNode, `Button`, `Signal<T>`, or any runtime-specific type. | Lowering or facts become coupled to one sample/runtime and block other targets. |
| Determinism | Generated projection content must be deterministic from `.csmx` source plus evaluated project/source options. | Rebuilds churn generated files or editor projections differ from build outputs. |
| Cleanup safety | Clean may delete the stale default `Generated/Csmx` root only inside the project. | Clean deletes user-owned files or files outside the intended stale generated directory. |

## Feature Status

| Feature | Status | Contract notes |
|---|---|---|
| Ordinary C# outside JSX islands | Implemented | Copied as source text and mapped as `CSharp`. Comments and string/char/raw string literals are skipped while scanning for JSX starts. |
| Element expressions | Implemented, heuristic start detection | Recognized when `<` appears in supported expression-start contexts and is followed by a valid JSX name start. |
| Self-closing elements | Implemented | `<View />` and `<text />` parse as elements with no children. |
| Opening/closing element pairs | Implemented | Mismatched or missing close tags produce CSMX diagnostics with recovery. |
| Lowercase intrinsic elements | Implemented | Lowercase is a convention, but actual classification is "not a configured/discovered component". |
| Uppercase fluent intrinsic elements | Implemented by fallback | An uppercase tag with no component fact remains intrinsic, so fluent mode can lower `<Button />` through `{Element}`. |
| Local component facts | Partial | Source-fact discovery recognizes specific `Func<...> Name =` and uppercase method shapes in the same file; imported semantic discovery is not implemented. |
| Configured component names | Implemented | `CsmxComponentNames` can explicitly identify component names and optional props types. |
| String attributes | Implemented | Quoted values become C# string literals; common C-style escapes in quoted JSX attributes are interpreted. |
| Boolean attributes | Implemented | Attribute without `=` lowers as `true`. |
| Expression attributes | Implemented | `{ ... }` content is preserved as C# expression text and recursively transforms nested JSX where detected. |
| Attribute names with `-`, `:`, `.` | Implemented at parse level | Factory mode emits names as strings. Fluent/member templates normalize by default unless `{RawName}` is used. |
| Text children | Implemented with normalization | Newline-containing text is trimmed per line and joined with spaces; indentation-only text is dropped. No XML entity decoding is performed. |
| Expression children | Implemented | Expression text is runtime/backend-owned after lowering; null/bool/sequence semantics are not normalized by the parser. |
| Nested JSX in expression holes | Implemented for tested contexts | Works in ternaries and lambdas/LINQ-like expressions when the nested `<` passes the same JSX-start heuristic. |
| Formatted expression children | Implemented | `{value:F2}` and `{value,8:F2}` split top-level alignment/format for text-child templates. |
| Keyed dynamic children | Prototype, implemented | Opt-in through configured element/attribute names and a raw template. Current validation is structural. |
| Physical generated projections | Removed from normal path | Source-generator builds do not create `Generated/Csmx/**/*.csmx.g.cs` files. |
| MSBuild project-info artifact | Removed | Project context is evaluated directly from MSBuild properties/items; no CSMX `obj/csmx` pipeline is expected. |
| Active editor Configuration/TFM selection | Implemented | `csmx.project.configuration` and `csmx.project.targetFramework` are sent to the language server and used for open-document transforms, generated paths, and project binding inspection. |
| Project-backed hover/semantic tokens/diagnostics/definition | Implemented with strict mapping limits | Uses the owned Roslyn workspace and maps copied C#, child expressions, attribute expressions, component references, and fluent element references. Generated helper diagnostics are suppressed. |
| C# completion | Project-backed tag/attribute completion uses owned Roslyn; scratch/no-project generated fallback remains for C# expression completion | Project-backed request forwarding is disabled to avoid stale physical projections and unstable C# extension state. |
| Scratch/no-project C# diagnostics in `.csmx` | Unsupported | VS Code delegated diagnostics are not pulled back; inspect generated projection or use a project-backed document. |
| JSX fragments | Unsupported | `<>...</>` is not a recognized element opener. |
| Spread attributes | Unsupported | `{...props}` is not an attribute form. |
| XML comments in JSX | Unsupported | `<!-- comment -->` is not parsed as JSX comment syntax. |
| Semantic component discovery from referenced assemblies/imports | Unsupported | C# extension can resolve generated C# symbols, but CSMX component classification is source/config based. |

Representative test traceability:

| Contract claim | Representative automated coverage | Known gap |
|---|---|---|
| Source maps target expression payloads | `TestCompilerMappings`, `TestNestedChildExpressionMappingTargetsExpressionPayload`, `TestKeyedSequenceTemplateMappings` | More exact CRLF/Unicode span tests are needed. |
| Component lowering modes | `TestComponentLoweringPolicies`, `TestComponentTemplateUsesCallSite`, `TestFluentComponentTemplateUsesCallSite` | No semantic/imported component discovery tests because that behavior is unsupported. |
| Fluent lowering | `TestFluentLowering`, `TestFluentCallSiteTemplateUsesSourceIdentityAndSpan` | More invalid-template tests are needed. |
| Keyed dynamic children | `TestFluentKeyedSequenceLoweringTemplate`, `TestKeyedSequenceLoweringDiagnostics`, `TestKeyedSequenceComponentConflictProducesWarning` | Validation is structural; semantic runtime type compatibility is not checked. |
| Formatted expression children | `TestFormattedChildExpressions`, `TestFormattedChildExpressionsInFluentMode` | More nested ternary/format edge cases can be added. |
| Nested JSX in expression holes | `TestNestedJsxInsideCSharpExpressionHoles` | More contexts such as `await (...)` and collection expressions need coverage. |
| Parser recovery | `TestParserRecoversParentClosingTagFromBrokenNestedElement`, `TestParserRecoversFollowingAttributesAfterBrokenAttributeExpression`, `TestParserRecoversOpeningTagMissingTerminator` | Recovery should continue to grow with syntax surface. |
| Component discovery | `TestComponentDiscovery` | Scope/preprocessor false positives are documented but not fully tested. |
| Build/editor project context | `TestGeneratedCSharpUsesProjectContext`, `TestProjectContextUsesEvaluatedMsBuildProperties`, `TestProjectContextAutoRefreshesImportedBuildProperties`, `TestProjectContextUsesRequestedConfigurationAndTargetFramework` | Restore/package dependency tracking is still narrower than full MSBuild workspace state. |
| Generated-file isolation | Sample Debug/Release builds | Multi-TFM isolation should have a dedicated test project. |
| Design-time generation | Source generator analyzer integration | Editor active-TFM switching is explicit, not auto-synchronized with C# extension UI. |
| Language-server diagnostics/completion/hover | `TestLanguageServerDiagnosticsUseStableCodes`, `TestProjectRoslynDiagnosticsMapToCsjsxAndClearAfterEdit`, `TestProjectRoslynDiagnosticsSuppressGeneratedHelperFailures`, `TestLanguageServerCompletions`, `TestLanguageServerHover` | C# diagnostics require project-backed Roslyn workspace context; scratch/no-project files do not get C# diagnostics. |

## Language Syntax Contract

CSMX is not a full C# grammar extension. The transformer scans mostly-normal C# and recognizes JSX only at likely expression starts.

### JSX Start Disambiguation

The transformer considers `<Name` a JSX start only when:

- the current character is `<`;
- the next character is a valid name start (`_` or a Unicode letter);
- the next character is not `/`;
- the previous non-whitespace token context allows an expression start.

Expression-start contexts currently recognized:

- beginning of file/text segment;
- after `=`, `(`, `[`, `{`, `,`, `:`, `?`, or `;`;
- after a lambda arrow `=>`;
- after previous word `return`, `throw`, `case`, or `yield`.

Expected JSX examples:

```csharp
return <Button />;
var view = <Button />;
var view = condition ? <A /> : <B />;
var array = new[] { <A />, <B /> };
items.Select(item => <Text>{item.Name}</Text>);
await RenderAsync(<A />);
return obj switch
{
    X => <A />,
    _ => <B />
};
```

Expected ordinary C# examples:

```csharp
return x < y;
return dict[key] < value;
return Foo<Bar>();
return typeof(List<string>);
return await <A />; // `await` is not currently a JSX-start trigger
```

Known unsupported or fragile contexts:

- type contexts and generic type argument lists;
- C# attribute lists;
- pattern syntax;
- declaration syntax where an expression is not starting;
- ambiguous line breaks where C# itself would need full parser context to decide whether `<` is relational or generic syntax.

If a likely JSX island is malformed, the parser should diagnose the local island and preserve following C# text. If `<` is not recognized as JSX, it is copied as ordinary C#.

Literal scanning examples:

```csharp
var raw = """
<this is not JSX>
""";

var c = '<';
var text = $"not JSX: {x < y}";

return <日本語 name="ok" />;
return <_private />;
```

The transformer skips comments, string literals, raw string literals, char literals, and common interpolated/verbatim string forms while searching for top-level JSX starts. Component discovery has its own lighter scanner and should not be treated as a semantic C# parser.

### Preprocessor Directives

Preprocessor directives outside JSX islands are copied unchanged as ordinary C# text.

Current behavior:

- JSX islands inside inactive `#if false` regions are still transformed because the transformer does not evaluate preprocessor conditions.
- Component discovery does not respect conditional compilation and may discover declarations inside inactive regions.
- Source-level directives inside inactive preprocessor regions can still be recognized if they appear in the first 32 physical lines.
- Generated `#line` directives are emitted around the whole generated file when a source path is available; preprocessor directives from the source are otherwise preserved in place.

Review implication: preprocessor-aware behavior is not part of the current contract. Tests should cover the current projection behavior before any attempt to make discovery or directive scanning conditional-compilation aware.

### Where JSX Can Appear

JSX is intended for C# expression positions, including:

- return expressions;
- assignment right-hand sides;
- lambda expression bodies;
- conditional branches;
- switch expression arms;
- argument expressions;
- collection and array initializer items;
- child expression holes.

JSX is not intended for:

- type names;
- generic argument lists;
- C# attribute lists;
- namespace/type/member declaration syntax;
- pattern syntax;
- XML documentation comments.

### Tag Names

Tag names start with `_` or a letter. After the first character, tag names may contain `_`, `-`, `:`, `.`, letters, or digits.

Examples accepted by the parser:

```csharp
<text />
<_private />
<ui:button />
<data.row />
<data-row />
```

The lowering backend decides whether the resulting generated C# is valid. A tag such as `<data-row />` is safe in factory mode because the element name is emitted as a string. In default fluent mode, `{Element}` is inserted raw into `new {Element}()`, so such names require a custom template or they will generate invalid C#.

### Attribute Syntax

Supported attribute forms:

```csharp
<button disabled />
<button class="primary" />
<button title='single quoted' />
<button onClick={() => setCount(count + 1)} />
```

Attribute names follow the same name-character rule as tag names. `class`, `data-id`, `ui:name`, and `Grid.Row` are parseable attribute names.

`@class` is not a valid attribute name because `@` is not a CSMX name-start character. `class` and `Class` are distinct parsed names. Factory mode preserves that distinction in string attribute names; default member-style lowering normalizes both to `Class`.

Factory mode emits attribute names as strings when no props type is known:

```csharp
Attr("class", "primary")
Attr("data-id", id)
```

Component props mode and default fluent mode convert attribute names to C# member-style names by keeping letters/digits and capitalizing after non-alphanumeric separators:

```text
class      -> Class
data-id    -> DataId
Grid.Row   -> GridRow
```

The default fluent attribute template is `.{Name}({Value})`. `{Name}` uses the normalized member name. `{RawName}` is available in fluent templates when a runtime intentionally wants the raw parsed name, but raw names can produce invalid C# for keywords or kebab-case attributes.

Event handlers are not special. They are normal attributes whose meaning belongs to the configured runtime.

Attribute order and duplicate attributes:

- Attribute order is preserved in the parsed IR and in generated argument/member emission.
- Duplicate attributes are not rejected by CSMX today.
- Duplicate behavior is runtime/C#-compiler owned: factory mode can pass duplicate string attributes to the runtime; props/fluent lowering may generate duplicate property/member assignments or calls.

Child order is preserved in generated output after whitespace-only text children are dropped and newline-containing text nodes are normalized.

### Comments And XML-Like Syntax

C# comments outside JSX islands are copied unchanged:

```csharp
// copied as C#
/* copied as C# */
return <Text>Hello</Text>;
```

C# comments inside expression holes are treated as part of the C# expression scanner:

```csharp
<Button onClick={
    // still inside the expression hole
    () => setCount(count + 1)
} />
```

JSX comment syntax is not defined. In practice:

- `{/* comment */}` is an empty C# expression hole containing only a C# block comment and currently produces a CSMX diagnostic because no expression remains after trimming.
- `<!-- comment -->` is unsupported XML syntax and should not be used.
- `/* comment */` inside an opening tag is unsupported unless it appears inside an attribute expression.

### Text And Whitespace Semantics

Text nodes are compile-time strings. They are not XML text nodes and are not HTML-decoded.

Single-line text is preserved exactly:

```csharp
<Text>Hello {name}</Text>
```

lowers as text `"Hello "` followed by the expression child `name`.

Newline-containing text is normalized:

- `\r\n` and `\r` are normalized to `\n` for processing;
- each line is trimmed;
- empty trimmed lines are removed;
- remaining lines are joined with a single space;
- indentation-only text between child elements is dropped.

Example:

```csharp
<Stack>
    <Text>Hello</Text>
    <Text>World</Text>
</Stack>
```

does not create text children for the indentation/newlines around the child elements.

Example:

```csharp
<Text>Line 1
Line 2</Text>
```

produces one text child with `"Line 1 Line 2"`.

Explicit whitespace should be written as an expression:

```csharp
<Text>{" "}</Text>
```

XML/HTML entities are not decoded:

```csharp
<Text>&lt;</Text>          // text is the four characters &, l, t, ;
<Text>Tom & Jerry</Text>  // ampersand is ordinary text
```

### Child Expression Semantics

The compiler distinguishes only these child shapes:

- static text;
- nested element;
- C# expression;
- formatted C# expression;
- nested JSX sequence expression when an expression contains nested JSX.

The parser does not normalize runtime values such as `null`, `bool`, arrays, or `IEnumerable<T>`. Those semantics belong to the lowering backend and target runtime.

Examples:

```csharp
<Stack>{maybeNull}</Stack>
<Stack>{children}</Stack>
<Stack>{items.Select(item => <Text>{item.Name}</Text>)}</Stack>
```

Factory mode currently wraps ordinary expression children in `TextFactory(...)`. When an expression contains nested JSX, factory mode wraps the whole expression in `ChildSequenceFactory(...)`. Fluent mode passes expression children to `CsmxFluentExpressionChild` or `CsmxFluentFormattedExpressionChild`. Runtimes decide whether nulls disappear, booleans are ignored, sequences are flattened, or values are rendered as text.

## Lowering Contract

The compiler produces target-neutral IR first, then lowers through a backend selected by `CsmxCompileMode`.

### Factory Mode

Factory mode lowers elements into configured calls:

```csharp
<button disabled={count == 0}>Click</button>
```

Representative output:

```csharp
Element("button", Props(Attr("disabled", count == 0)), Children(Text("Click")))
```

Factory mode contracts:

- Intrinsic element names are emitted as C# string literals.
- Attributes without known props type are emitted through `CsmxAttributeFactory`.
- Boolean attributes emit `true`.
- Missing attribute values emit `null` after a diagnostic path.
- Text children emit through `CsmxTextFactory`.
- Expression children without nested JSX emit through `CsmxTextFactory` by default.
- Expression children with nested JSX emit through `CsmxChildSequenceFactory`.
- Component elements lower either as direct calls or through the element factory depending on `CsmxComponentLowering`.

### Fluent Mode

Fluent mode lowers elements into configured builder templates:

```csharp
<Button Width={88}>Click</Button>
```

Representative default output:

```csharp
new Button().Width(88).Content("Click")
```

Fluent mode contracts:

- `CsmxFluentCreate` receives `{Element}` and optionally `{CallSite}`.
- `CsmxFluentAttribute` receives `{Name}`, `{RawName}`, and `{Value}`.
- Text, expression, formatted-expression, and element children each have separate templates.
- Uppercase tags with no component facts are still intrinsic from the compiler's perspective, which is how fluent UI elements such as `<Button />` work.
- If a tag or attribute name cannot become valid C# with the default templates, the project must configure templates that can handle that name.

### Component Lowering

A component is an element whose name appears in the component registry. The registry is built from `CsmxComponentNames` plus source-fact discovery in the same `.csmx` file.

Direct component lowering:

```csharp
<View name={name} />
```

can lower as:

```csharp
View(new ViewProps { Name = name }, Children())
```

Factory component lowering can lower as:

```csharp
Element(View, new ViewProps { Name = name }, Children())
```

Component templates can wrap the whole call:

```xml
<CsmxComponentTemplate>
  global::Runtime.Scope({CallSite}, () => {Component}({Props}, {Children}))
</CsmxComponentTemplate>
```

Template tokens for component templates:

| Token | Meaning |
|---|---|
| `{CallSite}` | C# string literal containing source identity plus original element span. |
| `{Component}` | Component name, mapped as `ComponentReference`. |
| `{Props}` | Generated props object/factory expression. |
| `{Children}` | Generated children expression. |

Unknown tokens are copied back with braces, for example `{Unknown}` stays `{Unknown}`.

### Component Resolution Contract

Component classification is syntax/configuration based, not Roslyn-semantic.

Resolution order:

1. Parse `CsmxComponentNames` descriptors. Descriptors are separated by `;` or `,` and can be `Name` or `Name=PropsType` / `Name:PropsType`.
2. Discover supported local component declarations from source text in the same file.
3. Group descriptors by exact component name and keep the first descriptor.
4. If an element name is in the registry, classify it as `Component`.
5. Otherwise classify it as `Intrinsic`.
6. Keyed sequence matching is a separate lowering-time special case and wins when `element.Name == CsmxKeyedSequenceElement`.

Supported local discovery shapes:

```csharp
Func<ViewProps, VNode[], VNode> View = ...;
System.Func<ViewProps, VNode[], VNode> View = ...;
global::System.Func<ViewProps, VNode[], VNode> View = ...;

static object View(ViewProps props, object[] children) => ...;
static VNode View(ViewProps props, VNode[] children) { ... }
```

Discovery requirements and limits:

- `Func` component discovery requires at least three generic type arguments; the first is treated as props type.
- Method discovery requires an uppercase method name.
- Method discovery requires at least two parameters named exactly `props` and `children`; the first parameter type is treated as props type.
- Nullable suffixes on discovered props types are stripped.
- Discovery skips comments, string literals, verbatim strings, interpolated strings in common forms, and char literals.
- Local discovery does not prove the symbol is in scope at the element use site. It is a source-fact heuristic.

False-positive and false-negative examples:

```csharp
void A()
{
    static VNode View(ViewProps props, VNode[] children) => throw null!;
}

void B()
{
    return <View />; // Discovery may classify View as a component even though C# scope rejects it.
}
```

```csharp
#if false
static VNode View(ViewProps props, VNode[] children) => throw null!;
#endif

return <View />; // Discovery is not preprocessor-aware today.
```

```csharp
static VNode View(OtherProps props, VNode[] children) => throw null!;
static VNode View(ViewProps props, VNode[] children) => throw null!;

return <View />; // No overload resolution; first discovered descriptor for the name wins.
```

These examples are intentionally documented as risks, not desired behavior. The generated C# compiler remains the final authority on whether the lowered component call is legal C#.

Unsupported component discovery:

- components imported from other files or assemblies;
- components available only through `using static`;
- generic component type inference;
- nested component lookup by C# scope rules;
- semantic overload resolution;
- distinguishing fluent intrinsic `<Button>` from component `<Button>` by type information.

Ambiguity rule:

- If a name is configured/discovered as a component, it lowers as a component.
- If not, it lowers as intrinsic, even if the tag is uppercase.
- The C# compiler then validates the generated code.

This rule is intentional for fluent mode because uppercase framework elements such as `<Button>` and `<Column>` should work without being registered as components.

Keyed-sequence conflict rule:

- Current behavior: if `CsmxKeyedSequenceElement` is `For`, then `<For>` uses keyed sequence lowering even when `For` is also configured or discovered as a component.
- Current behavior also emits a CSMX warning for that conflict because silent precedence is surprising.

Example conflict:

```xml
<CsmxComponentNames>For=ForProps</CsmxComponentNames>
<CsmxKeyedSequenceElement>For</CsmxKeyedSequenceElement>
```

### Keyed Dynamic Children

Projects can opt into keyed sequence lowering through configuration:

```xml
<CsmxKeyedSequenceElement>For</CsmxKeyedSequenceElement>
<CsmxKeyedSequenceTemplate>
  global::Runtime.Keyed({CallSite}, {Items}, {Key}, {Render})
</CsmxKeyedSequenceTemplate>
```

Example:

```csharp
<Column>
    <For Items={items} Key={item => item.Id}>
        {item => <Text>{item.Title}</Text>}
    </For>
</Column>
```

Current validation:

- `CsmxKeyedSequenceTemplate` must be non-empty.
- `Items` attribute must exist and must be an expression attribute.
- `Key` attribute must exist and must be an expression attribute.
- There must be at least one expression child; the first expression child is used as `{Render}`.

Current limits:

- Additional children are not part of the keyed template contract.
- The compiler does not validate the runtime delegate types.
- The runtime owns retained-state behavior and key equality semantics.

### Formatted Text Children

Formatted child expressions split only top-level `,` and `:` separators outside parentheses, brackets, braces, strings, chars, and comments.

```csharp
<Text>Total: {total,8:F2}</Text>
```

Factory mode uses:

```xml
<CsmxFormattedTextChild>{TextFactory}({Value}, {Format})</CsmxFormattedTextChild>
<CsmxAlignedFormattedTextChild>{TextFactory}({Value}, {Format}, {Alignment})</CsmxAlignedFormattedTextChild>
```

Fluent mode uses:

```xml
<CsmxFluentFormattedExpressionChild>.Content({Value}, {Format}, {Alignment})</CsmxFluentFormattedExpressionChild>
```

The runtime decides whether formatting is immediate, culture-sensitive, reactive, or metadata-preserving.

### Call-Site Identity

`{CallSite}` emits a C# string literal:

```text
<SourceIdentity>#<OriginalStart>:<OriginalLength>
```

Examples:

```text
Views/Counter.csmx#142:38
<memory>#12:16
```

Source identity rules:

- Build identity is the source path relative to the project directory, using `/`.
- Language-server identity is the relative source path when project context exists, using `/`.
- In-memory/no-path transforms use `<memory>`.
- Absolute paths passed directly to the transformer are normalized by replacing `\` with `/`.

Stability rules:

- Stable across build/editor when both use the same project-relative source path and same source span.
- Stable across target frameworks and configurations for the same source text.
- Not stable under edits that move or resize the element span.
- Not stable under file rename unless the runtime treats renamed source identity specially.
- No collision strategy is implemented beyond source identity plus span.

## Source-Map Contract

Compiler model:

```text
SourceMapEntry:
  OriginalSpan.Start: zero-based .NET string char offset in the .csmx source
  OriginalSpan.Length: .NET string char length
  GeneratedSpan.Start: zero-based .NET string char offset in generated C#
  GeneratedSpan.Length: .NET string char length
  Kind: CSharp | JsxElement | ComponentReference | ElementReference | AttributeReference | ChildExpression | AttributeExpression
```

LSP/extension wire shape:

```text
originalStart: number
originalLength: number
generatedStart: number
generatedLength: number
kind: string
```

Offset and newline contract:

- Offsets are .NET `string` char indexes, which are UTF-16 code-unit offsets and match VS Code/LSP document offsets when converted through `TextDocument.offsetAt` / `positionAt`.
- CRLF and LF are not normalized for source-map offsets. Mappings use offsets into the original source and generated strings as produced.
- Paths are not part of individual `SourceMapEntry` records. The request response carries the generated file path, and the open VS Code document supplies the source URI.
- Windows path comparisons for project binding use case-insensitive full paths.

Mapping kind contract:

| Kind | Meaning | Used for Roslyn projection or scratch delegation? |
|---|---|---|
| `CSharp` | Original C# copied unchanged. | Yes |
| `JsxElement` | Whole JSX element lowered to a generated expression. | No by default |
| `ComponentReference` | Component tag name mapped to generated component symbol. | Yes |
| `ElementReference` | Fluent intrinsic tag name mapped to generated element type/factory identifier. | Yes |
| `AttributeReference` | JSX attribute name mapped to generated member/property symbol. | Yes |
| `ChildExpression` | C# expression child or nested expression segment. | Yes |
| `AttributeExpression` | C# expression attribute or expression-valued template segment. | Yes |

Projection/delegation rules:

- The language server maps Roslyn results only through `CSharp`, `ChildExpression`, `AttributeExpression`, `ComponentReference`, `ElementReference`, and `AttributeReference` mappings.
- The VS Code extension uses the same mapped kinds only for scratch/no-project generated-document fallback.
- Text children intentionally have no C# delegation mapping.
- Generated helper tokens from factories/templates are normally not mapped back unless emitted through one of the mapped expression/component spans.
- Nested JSX inside expression holes can create multiple generated spans for different source spans.
- The same broad JSX element source span may have a `JsxElement` mapping while inner expression/component spans have narrower C# mappings.

Mapping behaviors in the scratch extension fallback:

| Behavior | Use | Rule |
|---|---|---|
| Strict | Hover/definition/general position mapping | Position must be inside a mapping. |
| Inclusive | Range ends and fallback mapping | A position exactly at mapping end may map to target end. |
| Inferred | Defined but not the primary delegation path | Chooses the nearest preceding mapping. |

Zero-width scratch completion behavior:

- Completion first tries strict mapping at the cursor.
- If strict mapping fails, the extension tries the previous original character and advances one generated character.
- If that fails, it tries inclusive mapping.

This is a pragmatic scratch completion-position contract, not a general source-map affinity model.

Round-trip examples:

```csharp
<Button disabled={count == 0}>Click</Button>
```

If `Button` is an intrinsic fluent element, expected C# mappings are:

| Source span | Source offset in snippet | Generated span text | Kind |
|---|---:|---|---|
| `count == 0` | `18+10` | `count == 0` | `AttributeExpression` |
| `Click` | `30+5` | none | no C# delegation mapping |
| `Button` | `1+6` | none | no component mapping because it is intrinsic |

If `Button` is configured or discovered as a component, the tag name also gets a component mapping:

| Source span | Source offset in snippet | Generated span text | Kind |
|---|---:|---|---|
| `Button` | `1+6` | `Button` | `ComponentReference` |

For text plus expression children:

```csharp
<Text>Hello {name}</Text>
```

Expected mappings:

| Source span | Generated span text | Kind |
|---|---|---|
| `Hello ` | none | no C# delegation mapping |
| `name` | `name` | `ChildExpression` |

## MSBuild Contract

Default properties:

```xml
<CsmxEnabled>true</CsmxEnabled>
```

Item contract:

- `**/*.csmx` files are included as `Csmx` items when `CsmxEnabled == true`.
- `.csmx` files are removed from normal `None` and then re-added as `None` for visibility.
- `.csmx` files are removed from `Compile` and added as Roslyn `AdditionalFiles`.
- Stale files under `$(MSBuildProjectDirectory)\Generated\Csmx\**\*.cs` are removed from implicit SDK `Compile` globs.
- `Csmx.SourceGenerator` is attached as an analyzer through a source-tree project reference or packaged analyzer DLL.
- No physical `.csmx.g.cs` files are explicitly included as `Compile`.

Target contract:

- `CleanCsmxGenerated` removes stale `Generated/Csmx` roots after `Clean` if they exist.
- CSMX parse/config errors are reported by the source generator with source `.csmx` file/line/column.

Multi-targeting contract:

- The generated path contains `$(TargetFramework)` when that property is set.
- Inner builds for each target framework generate separate projections.
- During editor-time project evaluation, the language server uses the VS Code `csmx.project.targetFramework` setting when provided. Otherwise it uses `TargetFramework` if MSBuild reports one, then the first entry from `TargetFrameworks`.
- During editor-time project evaluation, the language server uses the VS Code `csmx.project.configuration` setting when provided. Otherwise it uses the project/MSBuild default, normally `Debug`.
- Changing either editor setting sends `csmx/setProjectContextOptions`, clears project-context caches, refreshes open `.csmx` documents, and clears the extension's prepared generated-document cache.
- Active editor Configuration/TFM selection is explicit; it is not automatically synchronized with any active target framework selector from the installed C# extension.
- Conditional references/properties are only as faithful as the MSBuild evaluation performed for the chosen target framework context.

User-visible multi-targeting behavior:

- CSMX currently projects against one active editor target framework.
- If a symbol exists only for another target framework, project-backed Roslyn features may appear missing even though a build for that target framework succeeds.
- `CSMX: Inspect Project Binding` is the source of truth for the editor's requested and evaluated configuration/target framework.

Clean contract:

- `dotnet clean` may remove the stale legacy `Generated/Csmx` root inside the project when it exists.
- Normal builds do not recreate that root.
- Custom physical generated roots are no longer part of the default build contract.

Generated analyzer/debugger interaction:

- Generated source is provided by Roslyn source generation, so analyzers see compiler generated trees rather than project-local `.g.cs` files.
- `#line` directives can influence compiler and analyzer reported locations, but analyzer-specific generated-code behavior may vary.
- Nullable context, `#pragma`, and other preprocessor directives from the source are copied as ordinary C# outside JSX islands.

### Path Safety And Canonicalization

Current behavior:

- Project binding path comparisons use full paths and case-insensitive comparison on Windows.
- Stale generated cleanup is constrained to `$(MSBuildProjectDirectory)\Generated\Csmx\`.
- Normal builds do not accept or create external generated roots.

Desired safety policy before broader use:

1. Keep physical generated roots out of the default path.
2. If a future debug mode reintroduces physical writes, require explicit opt-in and strict path containment.
3. Document how symlinks and UNC paths are handled before allowing any generated-root cleanup beyond the stale project-local root.

## Editor And LSP Contract

Language-server owned features:

- CSMX syntax diagnostics.
- JSX tag and attribute completion from source facts plus project-backed Roslyn symbols where available.
- CSMX syntax hover for elements and attributes.
- Semantic tokens for CSMX syntax and lightweight C# lexical facts.
- Project-backed Roslyn hover, semantic tokens, and mapped C# diagnostics.
- `csmx/getGeneratedCSharp`.
- `csmx/inspectProjectBinding`.
- `csmx/reloadProjectContext`.
- `csmx/setProjectContextOptions`.

VS Code extension owned behavior:

1. Start `Csmx.LanguageServer`.
2. Register formatting and helper commands.
3. Ask the language server for generated C#.
4. Use server-owned Roslyn features for project-backed hover, definitions, semantic tokens, and diagnostics.
5. Use a `csmx-csharp:` virtual generated document for scratch/no-project delegation fallbacks.
6. Map delegated fallback results back with CSMX source maps.

Project-context evaluation kinds:

| Kind | Meaning |
|---|---|
| `msbuild` | MSBuild properties/items were evaluated directly. This is the preferred project-backed path. |
| `xml-fallback` | MSBuild evaluation failed and the server fell back to raw project XML/defaults. Treat as degraded. |

The document formatter edits the source `.csmx` document. It does not format generated files as a separate artifact.

Formatter contract:

- Formatting must be idempotent.
- Formatting must preserve source-level directives.
- Formatting must preserve comments.
- Formatting must not rewrite C# expression text inside `{ ... }` except for whitespace at known JSX boundaries.
- Formatting must preserve attribute order and child order.
- Formatting must not reflow text nodes unless the transformed text remains equivalent under CSMX text normalization.

Custom LSP request contract:

```text
csmx/getGeneratedCSharp:
  input: textDocument.uri
  output:
    code
    projectFilePath?
    generatedFilePath?
    mappings[]

csmx/inspectProjectBinding:
  input: textDocument.uri
  output:
    hasProject
    sourceFilePath
    projectFilePath
    projectDirectory
    relativeSourcePath
    evaluationKind
    requestedConfiguration?
    requestedTargetFramework?
    configuration
    targetFramework
    generatedDirectory
    generatedFilePath
    generatedFileExists
    compileIncludesGeneratedFile
    compileItemCount
    transform summary
    messages[]

csmx/reloadProjectContext:
  input: textDocument.uri
  output:
    reloaded
    documentWasOpen
    clearedEntries
    sourceFilePath?

csmx/setProjectContextOptions:
  input:
    configuration?
    targetFramework?
  output:
    configuration?
    targetFramework?
    changed
    clearedEntries
    refreshedDocuments
```

Editor feature matrix:

| Feature | Owner | Generated C# delegation | Notes |
|---|---|---|---|
| Tag completion | CSMX language server | No | Uses source facts plus owned Roslyn visible constructible UI types for project-backed files. |
| Attribute completion | CSMX language server | No | Uses source facts plus owned Roslyn fluent members/properties/fields for project-backed files. |
| C# expression completion | Not project-backed yet | Scratch/no-project only | Project-backed forwarding is disabled; owned Roslyn expression completion is planned. |
| Hover on CSMX syntax | CSMX language server | No | Elements/attributes have CSMX-specific hover. |
| Hover on C# symbols | CSMX language server | No | Uses owned Roslyn workspace for project-backed documents. |
| Definition | CSMX language server | Scratch/no-project fallback only | Project-backed definitions use owned Roslyn and source-backed direct project references when available. |
| Semantic tokens | CSMX + Roslyn | No for project-backed files | Roslyn tokens replace overlapping CSMX tokens only for precise source-map spans. |
| Diagnostics | CSMX + Roslyn language server | No C# pull | CSMX diagnostics always publish. Project-backed C# diagnostics are produced by the owned Roslyn workspace and mapped through strict source-map spans. |

Debugging commands:

- `CSMX: Show Generated C#`
- `CSMX: Inspect Project Binding`
- `CSMX: Reload Project Context`
- `CSMX: Inspect Token At Cursor`

Output channels:

- `CSMX Project Binding`
- `CSMX Scratch C# Delegation`
- `CSMX Token Inspector`

## Diagnostics And Debugging

Diagnostics matrix:

| Diagnostic kind | Producer | Shown in `.csmx` editor? | Build/CLI location | Notes |
|---|---|---|---|---|
| CSMX parse errors | Language server and source generator | Yes | `.csmx` source path | Examples: missing closing tag, malformed attribute expression. |
| CSMX keyed sequence config errors | Compiler through language server/source generator | Yes | `.csmx` source path | Examples: missing keyed template, missing `Items`, missing `Key`, missing render expression. |
| Invalid factory/template generated C# | C# compiler / installed C# extension | Only when the diagnostic maps through copied C#, expression, attribute, or component-reference spans | Source generator diagnostics use Roslyn generated trees; `#line` helps CLI locations where applicable | Raw template/helper failures outside source-owned spans are intentionally suppressed in editor diagnostics. |
| C# semantic errors in preserved source | Owned Roslyn workspace for project-backed editor; C# compiler for build | Yes for project-backed editor files | `#line` can point CLI diagnostics at `.csmx` for generated source | The language server maps Roslyn diagnostics through source maps; VS Code delegated diagnostics are still unavailable. |
| Project binding failures | Language server/extension command | Output channel | Not a compiler diagnostic | Use `CSMX: Inspect Project Binding`. |
| Scratch delegation failures | VS Code extension | Output channel | Not a compiler diagnostic | Logged once per document/failure kind. |

Generated files include `#line` directives when a source path is available:

```csharp
#line 1 "Components/Counter.csmx"
...
#line default
```

This helps build diagnostics and debugging map back to `.csmx` where the generated code corresponds closely to source. Editor diagnostics use explicit source-map entries rather than relying only on generated line offsets.

Failure-mode matrix:

| Symptom | Likely cause | First check | Expected fix |
|---|---|---|---|
| Hover works for `StringComparison` but not project/runtime symbols | Project detection, references, or Roslyn workspace source set is incomplete. | `CSMX: Inspect Project Binding` | Fix project detection, references, or workspace source collection. |
| `using` or `using static` members are unresolved in `.csmx` | Editor project context is degraded or missing project references. | Inspect evaluation kind and project-context dependencies. | Ensure project-backed Roslyn workspace is used and references are resolved. |
| Build succeeds but editor hover is stale | Editor and build used different evaluated CSMX properties or stale workspace inputs. | Compare Inspect Project Binding dependencies; try `CSMX: Reload Project Context`. | Align MSBuild evaluation and workspace invalidation. |
| Release build reports duplicate symbols from generated files | Stale physical generated files are still being compiled. | Scan for `Generated/Csmx/**/*.cs` and `*.csmx.g.cs` under the project. | Remove stale generated roots; ensure target excludes `Generated/Csmx`. |
| One malformed tag breaks features after it | Parser recovery gap. | Compiler recovery tests and CSMX diagnostics. | Add localized recovery case. |
| Completion appears one character off | Source-map offset or zero-width completion issue. | `CSMX: Inspect Token At Cursor` | Fix mapping span or completion fallback behavior. |
| C# diagnostics appear only in generated projection/build output | File is not project-backed, project binding failed, or the diagnostic is in generated helper code outside source-owned spans. | Inspect Project Binding; inspect generated C# if needed. | Fix project binding or source-map coverage; generated helper failures should stay suppressed unless a source-owned span exists. |
| `dotnet msbuild` times out in editor | Large/slow project evaluation. | Inspect Project Binding messages. | Improve cache/invalidation or reduce evaluation scope. |

## Configuration Reference

Properties are MSBuild properties. Source-level directives in the first 32 lines can override many transform options using `@jsx...` or `@csmx...` names inside comments, but project properties are the primary configuration surface.

### Source-Level Directive Contract

Source-level directives are a file-local override mechanism for transform options. They are intended for experiments and small samples; project properties are still the primary configuration surface.

Directive scan:

- Only the first 32 physical source lines are scanned.
- Lines are scanned independently after trimming spaces, tabs, `/`, `*`, and `\r` from both ends.
- This makes line comments and block-comment lines work, for example `// @csmxCompileMode fluent` and `/* @csmxCompileMode fluent */`.
- A directive must be the first non-trimmed token on the scanned line.
- A directive after code on the same line is ignored.
- The scanner is not preprocessor-aware; directives inside inactive `#if false` regions can still apply if they are in the first 32 physical lines.

Directive grammar:

```text
@directiveName <value>
```

Precedence:

1. Last recognized source directive in the scan window.
2. Evaluated project property.
3. Built-in compiler default.

Diagnostics and invalid values:

- Unknown directives are ignored.
- Invalid compile mode values are ignored.
- Invalid factory-expression values in source directives are ignored.
- Duplicate directives are allowed; later recognized directives win.
- No CSMX diagnostic is currently emitted for ignored/unknown/duplicate directives.

Supported directives:

| Directive | Equivalent option/property | Example |
|---|---|---|
| `@jsxCompileMode`, `@csmxCompileMode` | `CsmxCompileMode` | `// @csmxCompileMode fluent` |
| `@jsx`, `@csmxElementFactory` | `CsmxElementFactory` | `// @jsx global::Runtime.Element` |
| `@jsxAttr`, `@csmxAttributeFactory` | `CsmxAttributeFactory` | `// @jsxAttr global::Runtime.Attr` |
| `@jsxText`, `@csmxTextFactory` | `CsmxTextFactory` | `// @jsxText global::Runtime.Text` |
| `@jsxFormattedText`, `@csmxFormattedTextChild` | `CsmxFormattedTextChild` | `// @csmxFormattedTextChild Text({Value}, {Format})` |
| `@jsxAlignedFormattedText`, `@csmxAlignedFormattedTextChild` | `CsmxAlignedFormattedTextChild` | `// @csmxAlignedFormattedTextChild Text({Value}, {Format}, {Alignment})` |
| `@jsxProps`, `@csmxPropsFactory` | `CsmxPropsFactory` | `// @jsxProps global::Runtime.Props` |
| `@jsxChildren`, `@csmxChildrenFactory` | `CsmxChildrenFactory` | `// @jsxChildren global::Runtime.Children` |
| `@jsxChildSequence`, `@csmxChildSequenceFactory` | `CsmxChildSequenceFactory` | `// @jsxChildSequence global::Runtime.ChildSequence` |
| `@jsxKeyedSequenceElement`, `@csmxKeyedSequenceElement` | `CsmxKeyedSequenceElement` | `// @csmxKeyedSequenceElement For` |
| `@jsxKeyedSequenceItems`, `@csmxKeyedSequenceItemsAttribute` | `CsmxKeyedSequenceItemsAttribute` | `// @csmxKeyedSequenceItems Items` |
| `@jsxKeyedSequenceKey`, `@csmxKeyedSequenceKeyAttribute` | `CsmxKeyedSequenceKeyAttribute` | `// @csmxKeyedSequenceKey Key` |
| `@jsxKeyedSequenceTemplate`, `@csmxKeyedSequenceTemplate` | `CsmxKeyedSequenceTemplate` | `// @csmxKeyedSequenceTemplate Keyed({CallSite}, {Items}, {Key}, {Render})` |
| `@jsxComponentTemplate`, `@csmxComponentTemplate` | `CsmxComponentTemplate` | `// @csmxComponentTemplate Scope({CallSite}, () => {Component}({Props}, {Children}))` |
| `@jsxFluentCreate`, `@csmxFluentCreate` | `CsmxFluentCreate` | `// @csmxFluentCreate new {Element}()` |
| `@jsxFluentAttr`, `@csmxFluentAttribute` | `CsmxFluentAttribute` | `// @jsxFluentAttr .{Name}({Value})` |
| `@jsxFluentText`, `@csmxFluentTextChild` | `CsmxFluentTextChild` | `// @jsxFluentText .Content({Value})` |
| `@jsxFluentExpression`, `@csmxFluentExpressionChild` | `CsmxFluentExpressionChild` | `// @jsxFluentExpression .Content({Value})` |
| `@jsxFluentFormattedExpression`, `@csmxFluentFormattedExpressionChild` | `CsmxFluentFormattedExpressionChild` | `// @jsxFluentFormattedExpression .Content({Value}, {Format}, {Alignment})` |
| `@jsxFluentElement`, `@csmxFluentElementChild` | `CsmxFluentElementChild` | `// @jsxFluentElement .Content({Value})` |
| `@jsxFluentComponent`, `@csmxFluentComponentTemplate` | `CsmxFluentComponentTemplate` | `// @jsxFluentComponent Scope({CallSite}, () => {Component}({Props}, {Children}))` |

Not supported as source directives today:

- `CsmxComponentNames`
- `CsmxComponentLowering`
- `CsmxGeneratedDir` or other build path properties
- package/build integration properties
- `@jsxImportSource`

### Core And Build Properties

| Property | Default | Required | Meaning |
|---|---|---|---|
| `CsmxEnabled` | `true` | No | Enables `.csmx` item inclusion and source-generator integration. |
| `CsmxSourceGeneratorProject` | source-tree path | No | Analyzer project reference used in source-tree builds. |
| `CsmxSourceGeneratorAssembly` | package analyzer path | No | Analyzer DLL used by packaged builds. |
| `CsmxStaleGeneratedRootDir` | `$(MSBuildProjectDirectory)\Generated\Csmx\` | No | Stale legacy root excluded from compile and removed on clean. |

### Factory Mode Properties

| Property | Default | Required | Placeholders | Meaning |
|---|---|---|---|---|
| `CsmxCompileMode` | `factory` | No | n/a | Selects `factory` or `fluent`. |
| `CsmxElementFactory` | `Element` | Runtime-dependent | n/a | Intrinsic element factory expression. |
| `CsmxAttributeFactory` | `Attr` | Runtime-dependent | n/a | Attribute factory expression. |
| `CsmxTextFactory` | `Text` | Runtime-dependent | n/a | Text/expression child factory expression in factory mode. |
| `CsmxPropsFactory` | `Props` | Runtime-dependent | n/a | Props collection factory. |
| `CsmxChildrenFactory` | `Children` | Runtime-dependent | n/a | Children collection factory. |
| `CsmxChildSequenceFactory` | `ChildSequence` | Runtime-dependent for nested JSX sequences | n/a | Wraps dynamic expressions containing nested JSX. |
| `CsmxFormattedTextChild` | `{TextFactory}({Value}, {Format})` | No | `{TextFactory}`, `{Value}`, `{Format}` | Template for `{value:F2}`. |
| `CsmxAlignedFormattedTextChild` | `{TextFactory}({Value}, {Format}, {Alignment})` | No | `{TextFactory}`, `{Value}`, `{Format}`, `{Alignment}` | Template for `{value,8:F2}`. |
| `CsmxComponentLowering` | `direct` | No | n/a | `direct` or `factory`. |
| `CsmxComponentTemplate` | empty | No | `{CallSite}`, `{Component}`, `{Props}`, `{Children}` | Wraps component lowering in factory mode. |

### Fluent Mode Properties

| Property | Default | Required | Placeholders | Meaning |
|---|---|---|---|---|
| `CsmxFluentCreate` | `new {Element}()` | Runtime-dependent | `{Element}`, `{CallSite}` | Creates the fluent receiver. |
| `CsmxFluentAttribute` | `.{Name}({Value})` | Runtime-dependent | `{Name}`, `{RawName}`, `{Value}`, `{Element}`, `{CallSite}` | Emits one attribute call. |
| `CsmxFluentTextChild` | `.Content({Value})` | Runtime-dependent | `{Value}`, `{Element}`, `{CallSite}` | Emits one text child. |
| `CsmxFluentExpressionChild` | `.Content({Value})` | Runtime-dependent | `{Value}`, `{Element}`, `{CallSite}` | Emits one expression child. |
| `CsmxFluentFormattedExpressionChild` | `.Content({Value}, {Format}, {Alignment})` | Runtime-dependent | `{Value}`, `{Format}`, `{Alignment}`, `{Element}`, `{CallSite}` | Emits formatted expression child. |
| `CsmxFluentElementChild` | `.Content({Value})` | Runtime-dependent | `{Value}`, `{Element}`, `{CallSite}` | Emits nested element child. |
| `CsmxFluentComponentTemplate` | empty | No | `{CallSite}`, `{Component}`, `{Props}`, `{Children}` | Wraps component lowering in fluent mode. |

### Component And Keyed Sequence Properties

| Property | Default | Required | Placeholders | Meaning |
|---|---|---|---|---|
| `CsmxComponentNames` | empty | No | n/a | `Name` or `Name=PropsType` / `Name:PropsType` descriptors separated by `;` or `,`. |
| `CsmxKeyedSequenceElement` | empty | No | n/a | Element name that activates keyed sequence lowering. |
| `CsmxKeyedSequenceItemsAttribute` | `Items` | When keyed sequence enabled | n/a | Expression attribute used for `{Items}`. |
| `CsmxKeyedSequenceKeyAttribute` | `Key` | When keyed sequence enabled | n/a | Expression attribute used for `{Key}`. |
| `CsmxKeyedSequenceTemplate` | empty | When keyed sequence enabled | `{CallSite}`, `{Items}`, `{Key}`, `{Render}` | Raw template for keyed sequence expression. |

### Template Validation Rules

- Factory expressions such as `CsmxElementFactory` are lightly validated in language-server project context and source directives to contain identifier-ish characters, `.`, `:`, and braces.
- Source-generator normalization trims values and otherwise follows compiler option parsing.
- Templates are raw C# text replacement. They are not parsed by CSMX before being emitted.
- Unknown placeholders are emitted unchanged, including braces.
- Unmatched `{` is emitted as a literal `{`.
- There is no escape syntax for literal placeholder-looking text other than choosing template text that does not match a known token.
- Templates can contain lambdas, method calls, generics, `await` expressions where valid in the generated expression context, and other expression-level C#.
- Templates that emit statements or semicolons in expression-only positions can produce invalid generated C#.
- Project properties and imported props/targets are trusted input.

Template expression-context contract:

| Template/property | Must produce | How compiler uses it | May contain statements? |
|---|---|---|---|
| `CsmxElementFactory` | callable expression/name | Emits `{ElementFactory}("name", props, children)` | No |
| `CsmxAttributeFactory` | callable expression/name | Emits `{AttributeFactory}("name", value)` | No |
| `CsmxPropsFactory` | callable expression/name | Emits `{PropsFactory}(...)` | No |
| `CsmxChildrenFactory` | callable expression/name | Emits `{ChildrenFactory}(...)` | No |
| `CsmxChildSequenceFactory` | callable expression/name | Emits `{ChildSequenceFactory}(expression)` | No |
| `CsmxTextFactory` | callable expression/name | Emits `{TextFactory}(value)` | No |
| `CsmxFormattedTextChild` | complete child expression | Inserted as one child expression | No |
| `CsmxAlignedFormattedTextChild` | complete child expression | Inserted as one child expression | No |
| `CsmxComponentTemplate` | complete expression | Replaces the whole component expression | No |
| `CsmxKeyedSequenceTemplate` | complete expression | Replaces the whole keyed sequence element | No |
| `CsmxFluentCreate` | complete receiver expression | Starts the fluent chain | No |
| `CsmxFluentAttribute` | receiver suffix | Appended to the current fluent receiver | No |
| `CsmxFluentTextChild` | receiver suffix | Appended to the current fluent receiver | No |
| `CsmxFluentExpressionChild` | receiver suffix | Appended to the current fluent receiver | No |
| `CsmxFluentFormattedExpressionChild` | receiver suffix | Appended to the current fluent receiver | No |
| `CsmxFluentElementChild` | receiver suffix | Appended to the current fluent receiver | No |
| `CsmxFluentComponentTemplate` | complete expression | Replaces the whole component expression in fluent mode | No |

Context examples:

```xml
<CsmxFluentCreate>new {Element}()</CsmxFluentCreate>
<CsmxFluentAttribute>.{Name}({Value})</CsmxFluentAttribute>
```

`CsmxFluentCreate` must be a complete expression. `CsmxFluentAttribute` must be a suffix that can be appended to that expression.

Unknown-placeholder policy:

- Current behavior is warning-and-pass-through: unknown placeholders emit CSMX warnings and remain unchanged in generated output.
- This keeps experimentation lenient while making misspellings such as `{Componnet}` visible before the C# compiler reports invalid generated code.
- A stricter future option can reject unknown placeholders, with an explicit opt-in property for pass-through behavior if needed.

## Trust And Security Model

CSMX treats the project as trusted code.

- The language server shells out to `dotnet msbuild` for the detected containing project to evaluate imported properties and compile items.
- Imported `.props` and `.targets` can affect generated code and editor-time projection.
- Template properties are raw C# generation templates and can produce arbitrary generated C#.
- Normal builds do not write physical generated files.
- Clean only removes stale legacy `Generated/Csmx` roots inside the project.

Current trust gaps:

- There is no explicit VS Code workspace-trust gate around `dotnet msbuild` evaluation.
- Generated-root cleanup has a sentinel guard and project-boundary validation, but symlink/UNC behavior is not yet a formal trust policy.

Reviewer expectation:

- These gaps are acceptable for the current experiment only if documented and visible.
- Before treating CSMX as safe for untrusted workspaces, project evaluation and generated writes need an explicit workspace-trust policy.

Intended untrusted-workspace behavior before broader use:

- Do not run `dotnet msbuild`.
- Do not write physical generated files.
- Use virtual projection only.
- Disable project-backed C# delegation.
- Show a degraded-mode status message explaining that project evaluation is disabled.
- Allow project-backed features only after the user trusts the workspace through the host editor's trust mechanism.

## Performance And Cache Invalidation

Current behavior:

- Single-file transform is in-process and does not require MSBuild once transform options are known.
- Language-server project context uses `dotnet msbuild -getProperty:<properties>` and optionally `-getItem:Compile`.
- MSBuild evaluation timeout is 6000 ms.
- Normal generated-code requests use a project context cache keyed by source path, containing project path, and a dependency snapshot.
- The dependency snapshot includes the project file, MSBuild's evaluated `MSBuildAllProjects` import list, ancestor `Directory.Build.props` / `Directory.Build.targets` candidates, and `obj/project.assets.json`.
- The VS Code extension caches virtual generated C# documents only for scratch/no-project fallback delegation.
- Explicit project binding inspection bypasses the cache and requests compile items.

Current cache invalidation:

| Change | Invalidated today? | Notes |
|---|---|---|
| `.csmx` text edit | Yes | Open document transform uses current text; the extension cache is keyed by source document version. |
| `.csproj` timestamp change | Yes | Project file is a project-context dependency. |
| `Directory.Build.props` / `.targets` change | Yes | Ancestor candidates are tracked, including files that did not exist when the context was cached. |
| Imported props/targets change | Yes when present in `MSBuildAllProjects` | Evaluated imports are tracked from MSBuild. |
| Configuration/TFM setting change | Yes | Extension sends active options to the language server, clears prepared projections, and refreshes open documents. |
| Package restore/reference change | Partially | `obj/project.assets.json` timestamp is tracked; broader restore inputs are not. |
| Source generator assembly change | Build yes, editor no direct cache key | Build target attaches the source generator as an analyzer. |
| Extension version change | VS Code reload/install boundary | Inspector reports extension version. |
| `CSMX: Reload Project Context` command | Yes for active document | Clears language-server project context and the extension's prepared-projection cache for the active document. |

Practical performance targets for review:

| Operation | Target | Status |
|---|---|---|
| Warm single-file transform | Under 50 ms for typical sample files | Not enforced by benchmark. |
| Warm generated projection request after project context cache | Under 100 ms for typical sample files | Not enforced by benchmark. |
| Repeated scratch-file delegation on same edit version | Reuse the prepared virtual projection until the document changes | Implemented in extension cache; not benchmarked. |
| Cold MSBuild project evaluation | Under 1000 ms for small SDK projects | Timeout is 6000 ms; slow evaluations are only visible through messages/logging. |
| Project binding inspection | May be slower because it asks for `Compile` items | Used on demand. |

Important future improvement:

- Cache invalidation should include more restore/package inputs beyond `project.assets.json`, and editor UI should make the active configuration/TFM state more discoverable.

Future project-context cache key:

- source file path;
- project file path and dependency snapshot;
- selected configuration;
- selected target framework;
- restore/package dependency graph beyond `obj/project.assets.json`;
- CSMX source-generator/package version;
- language-server/extension version when it affects projection behavior.

Current manual recovery command:

```text
CSMX: Reload Project Context
```

This command clears the active document's project-context cache entry and refreshes open-document analysis. It remains a manual escape hatch for cases outside the current dependency snapshot.
It also clears the VS Code extension's fallback prepared-projection cache for that document.

## Testing And Acceptance Scenarios

Review commands:

```powershell
dotnet test tests\Csmx.Compiler.Tests\Csmx.Compiler.Tests.csproj
dotnet test tests\Csmx.LanguageServer.Tests\Csmx.LanguageServer.Tests.csproj
dotnet test tests\Csmx.EnagaSignals.Tests\Csmx.EnagaSignals.Tests.csproj
dotnet build samples\SignalDashboardApp\SignalDashboardApp.csproj -c Debug
dotnet build samples\SignalDashboardApp\SignalDashboardApp.csproj -c Release
cd vscode\csmx-vscode
npm run package
code --install-extension csmx-vscode-0.1.00.vsix --force
```

Acceptance scenarios:

| ID | Scenario | Steps | Pass condition | Current automation |
|---|---|---|---|---|
| `ACCEPT-BUILD-001` | No physical generated files | Delete `Generated/Csmx` and CSMX-owned `obj` folders, then build samples. | Build succeeds and does not recreate `Generated/Csmx`, `obj/csmx`, `obj/csmx-task`, or physical `.csmx.g.cs` files. | Sample build verification. |
| `ACCEPT-BIND-001` | Project-backed C# binding | In a sample using `using` and `using static`, hover imported runtime members such as `UiNode` and `CreateSignal`. | Hover/semantic tokens come from the CSMX Roslyn workspace. | `TestRoslynHoverResolvesSampleProjectSymbols`. |
| `ACCEPT-DIAG-001` | Project-backed C# diagnostics | Introduce unresolved names in copied C#, attribute expressions, and child expressions. | Diagnostics appear in the `.csmx` editor at the source-owned spans and clear after the edit fixes them. | `TestProjectRoslynDiagnosticsMapToCsjsxAndClearAfterEdit`. |
| `ACCEPT-DIAG-002` | Generated helper diagnostic suppression | Remove an intrinsic helper such as `Element` from a project runtime and open a JSX element. | Generated helper errors do not get projected onto arbitrary `.csmx` spans. | `TestProjectRoslynDiagnosticsSuppressGeneratedHelperFailures`. |
| `ACCEPT-CONFIG-001` | Debug/Release source-generator build | Build Debug, then build Release. | No stale generated file is compiled; no duplicate type errors. | Sample build verification. |
| `ACCEPT-TFM-001` | Multi-TFM editor selection | Open a multi-targeted project and set active editor TFM. | Generated C# transform and binding inspection use requested Configuration/TFM without physical generated output. | `TestProjectContextUsesRequestedConfigurationAndTargetFramework`. |
| `ACCEPT-MAP-001` | Attribute source-map round trip | Hover or inspect `count == 0` in `disabled={count == 0}`. | C# projection maps to the expression payload, not the whole tag. | Partial: `TestCompilerMappings`. |
| `ACCEPT-MAP-002` | Child source-map round trip | Hover or inspect `name` in `<Text>Hello {name}</Text>`. | C# projection maps to `name`; text `Hello ` has no C# mapping. | `TestCompilerMappings`. |
| `ACCEPT-MAP-003` | Component reference mapping | Hover or token-inspect a configured/discovered component tag name. | Tag name maps to generated component symbol span as `ComponentReference`. | `TestCompilerMappings`, `TestComponentTemplateUsesCallSite`. |
| `ACCEPT-MAP-004` | Attribute reference mapping | Hover, definition, or token-inspect a fluent attribute name such as `Padding`. | Attribute name maps to generated member symbol span as `AttributeReference`. | `TestFluentAttributeReferencesMapNameTokens`, `TestDefinitionResolvesSignalAppFluentTags`. |
| `ACCEPT-RECOVERY-001` | Parser recovery | Break one nested closing tag, then inspect completions or generated code after the broken region. | CSMX diagnostics are local; following C# statements remain C#. | `TestParserRecoversParentClosingTagFromBrokenNestedElement`. |
| `ACCEPT-KEYED-001` | Keyed sequence validation | Remove `Items`, `Key`, render child, or template from a keyed sequence sample. | CSMX diagnostics identify the missing structural requirement. | `TestKeyedSequenceLoweringDiagnostics`. |
| `ACCEPT-MSB-001` | Imported MSBuild properties | Put CSMX properties in `Directory.Build.props`. | Language server and build both use imported values. | `TestProjectContextUsesEvaluatedMsBuildProperties`. |
| `ACCEPT-MSB-002` | Automatic imported property refresh | Change imported CSMX properties after opening a file, then request generated C# again. | Active projection picks up the changed imported property without manual reload. | `TestProjectContextAutoRefreshesImportedBuildProperties`. |
| `ACCEPT-MSB-003` | Active editor Configuration/TFM selection | Open a multi-target `.csmx`, set `csmx.project.configuration=Release` and `csmx.project.targetFramework` to the second TFM, then request generated C# and inspect binding. | Transform and binding inspection use the requested Configuration/TFM without reopening the document. | `TestProjectContextUsesRequestedConfigurationAndTargetFramework`. |
| `ACCEPT-CLEAN-001` | Stale generated cleanup | Run clean on a project with stale `Generated/Csmx`. | Stale project-local root is removed; normal build does not recreate it. | Manual/sample verification. |
| `ACCEPT-DIRECTIVE-001` | Source directive precedence | Put project property and first-32-line directive with different compile modes. | Source directive wins; later duplicate directive wins. | `TestSourceDirectivesOverrideProjectOptions`. |
| `ACCEPT-TEMPLATE-001` | Template unknown placeholder | Misspell a known placeholder in a template. | CSMX warning is emitted, names the applied template option, and generated C# preserves the unknown token. | `TestUnknownTemplatePlaceholdersProduceWarningsAndPassThrough`, `TestFluentTemplateWarningsUseAppliedOptionName`. |
| `ACCEPT-TRUST-001` | Untrusted workspace degraded mode | Open project in untrusted workspace. | No MSBuild shell-out or physical generated write; user sees degraded mode. | Gap: not implemented. |

## Sample Runtimes And Apps

`samples/Csmx.SampleRuntime` is a small VNode target used by simple samples. It is not the language default.

`src/Csmx.EnagaSignals` is an experimental signal UI runtime that uses fluent lowering with Enaga scene/rendering integration. It supports local signal slots, signal-driven render invalidation, dynamic text tracking, keyed child state preservation, coalesced wake requests, and native-window rendering samples.

Representative samples:

| Sample | Purpose |
|---|---|
| `samples/HelloApp` | Simple factory/VNode sample. |
| `samples/FactoryApp` | Factory component lowering sample. |
| `samples/FluentApp` | Fluent builder lowering sample. |
| `samples/SignalApp` | Enaga signal UI sample. |
| `samples/SignalDashboardApp` | Keyed dynamic child and dashboard-style signal sample. |

## Minimal Runtime Configuration Examples

Factory/VNode-style project:

```xml
<PropertyGroup>
  <CsmxCompileMode>factory</CsmxCompileMode>
  <CsmxElementFactory>global::Csmx.SampleRuntime.Csmx.Element</CsmxElementFactory>
  <CsmxAttributeFactory>global::Csmx.SampleRuntime.Csmx.Attr</CsmxAttributeFactory>
  <CsmxPropsFactory>global::Csmx.SampleRuntime.Csmx.Props</CsmxPropsFactory>
  <CsmxChildrenFactory>global::Csmx.SampleRuntime.Csmx.Children</CsmxChildrenFactory>
  <CsmxChildSequenceFactory>global::Csmx.SampleRuntime.Csmx.ChildSequence</CsmxChildSequenceFactory>
  <CsmxTextFactory>global::Csmx.SampleRuntime.Csmx.Text</CsmxTextFactory>
</PropertyGroup>
```

Fluent UI project:

```xml
<PropertyGroup>
  <CsmxCompileMode>fluent</CsmxCompileMode>
  <CsmxFluentCreate>new {Element}()</CsmxFluentCreate>
  <CsmxFluentAttribute>.{Name}({Value})</CsmxFluentAttribute>
  <CsmxFluentTextChild>.Content({Value})</CsmxFluentTextChild>
  <CsmxFluentExpressionChild>.Content({Value})</CsmxFluentExpressionChild>
  <CsmxFluentFormattedExpressionChild>.Content({Value}, {Format}, {Alignment})</CsmxFluentFormattedExpressionChild>
  <CsmxFluentElementChild>.Content({Value})</CsmxFluentElementChild>
</PropertyGroup>
```

Keyed sequence extension:

```xml
<PropertyGroup>
  <CsmxKeyedSequenceElement>For</CsmxKeyedSequenceElement>
  <CsmxKeyedSequenceItemsAttribute>Items</CsmxKeyedSequenceItemsAttribute>
  <CsmxKeyedSequenceKeyAttribute>Key</CsmxKeyedSequenceKeyAttribute>
  <CsmxKeyedSequenceTemplate>
    global::Runtime.Keyed({CallSite}, {Items}, {Key}, {Render})
  </CsmxKeyedSequenceTemplate>
</PropertyGroup>
```

Git ignore guidance:

```gitignore
/Generated/Csmx/
```

This remains useful for stale legacy outputs. Current normal builds should not create this directory.

## Review Focus

The most important review questions are:

- Do build-time source generation and editor-time Roslyn transforms use the same evaluated properties?
- Does project context use evaluated MSBuild state instead of raw `.csproj` text when possible?
- Are physical generated files absent from normal builds and excluded if stale files remain?
- Are parser and IR still runtime-neutral?
- Are source maps precise enough for hover, completion, definitions, and semantic-token mapping?
- Are delegation failures observable without attaching a debugger?
- Is generated cleanup constrained to the intended default generated root?
- Are heuristic language boundaries explicitly tested before expanding syntax?
- Are raw templates documented as trusted C# generation input?

## When Not To Use This Yet

Do not use this experiment as production infrastructure yet if:

- the workspace is untrusted or should not run project build logic during editing;
- you need every C# editor feature to be owned by CSMX without any generated-projection delegation fallback;
- you rely on component discovery from imported files, referenced assemblies, or `using static`;
- you need exact C# diagnostics for generated helper/template code that has no source-owned span;
- you need reliable editor switching across multiple target frameworks;
- your runtime requires complex child normalization to be implemented in the compiler rather than in backend templates/runtime code;
- you need strict template validation instead of raw C# template pass-through;
- generated cleanup must satisfy strict symlink, UNC, home-directory, or repository-root policy today.

## Current Limitations

- JSX parsing uses heuristic expression-start detection instead of Roslyn syntax integration.
- Component discovery is source/configuration based, not semantic.
- Imported components and `using static` components are not discovered as CSMX components, though generated C# can still bind imported C# symbols after lowering.
- C# diagnostics are mapped for project-backed source-owned spans only; scratch/no-project files and generated helper/template spans do not get full C# diagnostic remapping.
- Editor target-framework selection is primitive for multi-targeted projects.
- Project context cache invalidation tracks evaluated imports, ancestor Directory.Build files, and `project.assets.json`, but can still miss broader restore/package inputs.
- Template validation is intentionally light and can allow invalid generated C#.
- Formatting is modest and focused on supported syntax.
- Automatic cleanup has a sentinel guard and project-boundary validation, but still lacks a stricter symlink/UNC/home-directory policy.
- Workspace-trust policy is not implemented.

## Risk Register

| Risk | Impact | Mitigation today | Better future mitigation |
|---|---|---|---|
| MSBuild shell-out latency | Slow editor projection on large projects. | Cache by source/project timestamp; 6000 ms timeout; inspect messages. | Import graph invalidation, background refresh, measured budgets. |
| Fallback project evaluation drift | Editor projection can miss imports/conditions. | XML fallback only after MSBuild failure; inspector reports evaluation kind. | Make fallback visibly degraded and reduce reliance on it. |
| C# diagnostics gap | Helper/template errors or scratch/no-project files may show errors only in generated projection/build output. | Project-backed source-owned spans map through the Roslyn workspace; `#line` directives and generated projection inspection cover the rest. | Broader diagnostic affinity model and generated-helper diagnostics with intentional source ownership. |
| Heuristic parsing | Legal or ambiguous C# contexts can be missed or misclassified. | Conservative JSX-start rule and recovery tests. | Roslyn-assisted scanning or explicit syntax restrictions with more tests. |
| Source-map edge cases | Delegated features can land on wrong spans. | Mapped kinds, strict/inclusive completion fallback, token inspector. | Formal affinity model and more round-trip tests. |
| Runtime coupling | Compiler could accidentally encode sample/runtime behavior. | Target-neutral IR and template-based lowering. | Review invariants and backend boundaries. |
| Cleanup safety | Recursive clean could delete user files if paths are mishandled. | Generated roots must stay below the project; clean only removes the exact default root and requires the sentinel file. | Symlink/UNC policy and stricter cleanup root classification. |
| Generated path mismatch | Editor and build disagree, breaking C# binding. | Shared property contract and inspector. | Shared path computation library or golden tests across build/LSP. |
| Extension version confusion | Normal VS Code can run older installed VSIX while launch host works. | Inspector reports extension version. | Better install/update workflow and extension activation logging. |
