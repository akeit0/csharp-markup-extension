# CSMX Backend Pipeline

## Goal

CSMX should support factory-based lowering and Solid-like compiled lowering without forcing every target through the same `Element(name, props, children)` call shape.

The compiler should parse JSX once, classify it cheaply, and lower it through a selected backend. The runtime should stay typed and target-specific; it should not regain permissive `object?[]` escape hatches.

## Pipeline

```text
.csmx source
  -> C#-aware JSX scanner/parser
  -> JSX syntax tree
  -> lightweight element facts
  -> target-neutral IR and static/dynamic analysis
  -> backend lowering
  -> generated .g.cs + source map
```

Initial implementation can keep the parser and current syntax tree in `CsmxTransformer`, but emission should move behind a backend-shaped interface first. A later pass can extract the IR once the backend seam is stable.

## Backend Responsibilities

A backend decides:

- how to lower intrinsic elements
- how to lower component elements
- how to represent props
- how to represent children
- whether static content is materialized, deferred, inlined, or backend-owned
- how dynamic text, attributes, events, and child insertions are wired

Current factory backend:

```csharp
Element("Text", props, Children(Text("Hello "), Text(name)))
```

Current fluent backend:

```csharp
new Button().Size(10).Content("Click")
```

Solid-like/custom backend:

```csharp
var node = Target.Create("Text");
Target.AppendStatic(node, "Hello ");
Target.BindText(node, () => name);
return node;
```

The second shape is not just a different factory call. It is a different lowering strategy.

## Children Strategy

Children must be backend-specific.

The sample VNode runtime materializes strict typed children:

```csharp
VNode[]
```

Compiled modes can use backend-owned children:

```text
BackendOwned
```

Backend-owned means the element lowerer consumes child IR directly and may emit builder calls, template markers, render blocks, slots, or no standalone children expression at all.

Do not reintroduce runtime `object?[]` to make this flexible. Flexibility belongs in the compiler backend, not in the runtime type surface.

## Static Template Is Not HTML-Only

Static template analysis should produce target-neutral template IR, not only HTML strings.

Example shape:

```text
TemplateElement("Stack")
  StaticProp("class", "counter")
  TemplateElement("Text")
    StaticText("Hello ")
    DynamicText("props.Name")
```

Possible backend outputs:

- HTML DOM: cached string template plus markers
- native UI: builder/open/set/close calls
- VNode: typed object construction
- custom target: user-provided compile hooks

## LSP Scope

The language server should remain simple. It does not need full backend lowering semantics for normal editor features.

LSP should use:

- parser
- source spans
- lightweight element facts
- simple options such as compile mode, component names, and props pattern

LSP should not run expensive backend-specific template analysis on every keystroke. It can call the compiler for explicit "show generated C#" or generated projection requests, but hover/highlight/completion should stay syntax/facts-based.

The compiler exposes `CsmxFacts.ParseElement(...)` for this path. It returns public facts such as element kind, props type, attributes, children, spans, and static/dynamic classification without running backend lowering or generating C#.

## Implementation Stages

1. Move current emission behind a factory backend object without changing generated output.
2. Add `CsmxCompileMode` options with `factory` as the first supported mode.
3. Add a target-neutral element/children IR.
4. Move static/dynamic analysis onto the IR.
5. Add backend-owned children support through convention-based fluent lowering.
6. Add a second experimental compiled backend.
7. Keep LSP on parser/facts unless the user explicitly asks for generated C#.

## Current Status

The compiler now has the first backend seam and `factory` compile mode plumbing. Parsed JSX is converted into a small IR before factory lowering:

```text
JsxElementNode
  -> CsmxElementIr(kind, staticKind, propsType, attributes, children)
  -> FactoryLoweringBackend
```

The syntax model lives outside the transformer in `CsmxSyntax.cs`, JSX parsing lives in `CsmxParser.cs`, and syntax-to-IR classification lives in `CsmxIrBuilder.cs`. The IR model lives in `CsmxIr.cs`. It carries static/dynamic facts for elements, attributes, text, and expression children. The lowering contract lives in `CsmxLoweringBackend.cs` and exposes a `ChildrenStrategy` so future backends can choose materialized children or backend-owned children without changing the runtime surface.

Public LSP-oriented facts live behind `CsmxFacts.ParseElement(...)`. This API converts parser syntax through the same IR builder and returns public immutable facts instead of backend output.

The language server uses this facts API for opening-tag and attribute hover, and semantic tokens use it for JSX tag and attribute spans. Closing-tag and text-child hover remain lightweight local checks. Source-level factory options are parsed through `CsmxSourceOptions`, so compiler output and LSP facts do not keep separate pragma parsing rules.

Component classification uses configured or discovered component symbols rather than uppercase naming alone. This keeps builder-style fluent elements such as `<Panel>` and `<Button>` available as intrinsic target elements, while local component declarations such as `Func<TProps, VNode[], VNode> View = ...` still provide lightweight props type inference without needing comments.

Component lowering is a project-level policy. `direct` emits `View(props, children)`. `factory` emits `ElementFactory(View, props, children)` for React-style component references. These policies share parser and IR, but they stay separate lowering implementations through `CsmxDirectComponentLoweringBackend` and `CsmxFactoryComponentLoweringBackend`.

Factory lowering can also wrap component calls through `CsmxComponentTemplate`. The template owns the whole component expression and can use `{CallSite}`, `{Component}`, `{Props}`, and `{Children}`. For retained or signal runtimes this gives a stable source-span identity without making component identity a parser feature:

```xml
<CsmxComponentTemplate>
  global::Target.Scope({CallSite}, () => {Component}({Props}, {Children}))
</CsmxComponentTemplate>
```

Backend-owned fluent lowering has the matching `CsmxFluentComponentTemplate` hook. This keeps builder-style intrinsic elements on fluent chains while letting component elements lower to scoped component calls:

```xml
<CsmxFluentComponentTemplate>
  global::Target.Scope({CallSite}, () => {Component}({Props}, {Children}))
</CsmxFluentComponentTemplate>
```

Delegated C# editor features use source-map entries, not generated-line offset guessing. The generated projection carries mappings for unchanged C# spans, JSX elements, attribute expressions, and child expressions. Client-side mapping uses strict, inclusive, or inferred behavior depending on the request surface, following the same shape as Razor's document-mapping model.

In a project, generated source is contributed by `Csmx.SourceGenerator` directly to Roslyn. The language server builds an owned Roslyn workspace over the transformed open document plus project references, so project-backed hover, semantic tokens, and C# diagnostics do not require physical `Generated/Csmx` files. Outside a project, the extension falls back to a `csmx-csharp:` virtual document and does not pull C# diagnostics back through VS Code delegation.

Lowering helpers now live behind `CsmxLoweringContext`, and the current factory backend lives in `CsmxFactoryLoweringBackend.cs`. The transformer still owns C# text scanning and generated-file assembly, but JSX parsing and factory-specific code generation are no longer embedded in the transformer.

Current implemented strategies:

```text
FactoryLoweringBackend -> Materialized children expression
FluentLoweringBackend -> Backend-owned chained calls
```

Fluent lowering is framework-level convention based, not per-element manifest based. The default conventions are:

```xml
<CsmxCompileMode>fluent</CsmxCompileMode>
<CsmxFluentCreate>new {Element}()</CsmxFluentCreate>
<CsmxFluentAttribute>.{Name}({Value})</CsmxFluentAttribute>
<CsmxFluentTextChild>.Content({Value})</CsmxFluentTextChild>
<CsmxFluentExpressionChild>.Content({Value})</CsmxFluentExpressionChild>
<CsmxFluentFormattedExpressionChild>.Content({Value}, {Format}, {Alignment})</CsmxFluentFormattedExpressionChild>
<CsmxFluentElementChild>.Content({Value})</CsmxFluentElementChild>
```

For example, `<Button Size={10}>Click</Button>` lowers to `new Button().Size(10).Content("Click")`. Setting `CsmxFluentCreate` to `{Element}()` switches creation to `Button()` without changing every element.

Factory lowering uses `CsmxChildSequenceFactory` for expression children that contain nested JSX:

```csharp
<list>{items.Select(item => <item>{item}</item>)}</list>
```

becomes:

```csharp
Element("list", Props(), Children(ChildSequence(items.Select(item =>
    Element("item", Props(), Children(Text(item)))))))
```

This keeps ordinary `{value}` text expressions separate from typed child sequences. The target runtime decides what `ChildSequence` accepts, typically `IEnumerable<TNode>`.

Keyed dynamic child sequences are opt-in through a lowering template instead of a built-in runtime dependency. A project can configure a sequence element and template:

```xml
<CsmxKeyedSequenceElement>For</CsmxKeyedSequenceElement>
<CsmxKeyedSequenceTemplate>
  global::Csmx.EnagaSignals.Signals.KeyedChildren({CallSite}, {Items}, {Key}, {Render})
</CsmxKeyedSequenceTemplate>
```

Then:

```csharp
<Column>
    <For Items={items} Key={item => item.Id}>
        {item => <Text>{item.Name}</Text>}
    </For>
</Column>
```

lowers through the configured template. `{CallSite}` is deterministic from source identity and span, `{Items}` and `{Key}` come from configured attributes, and `{Render}` is the child expression. This gives retained or signal runtimes stable keyed child identity without making the sample Signal runtime the language default.

If a configured sequence element is missing its template, items attribute, key attribute, or render expression, the compiler reports a CSMX diagnostic instead of treating the element as an ordinary framework element.

Child expression formatting is modeled in IR instead of being tied to a `Text` element:

```csharp
<Text>Value: {value:F2}</Text>
<Text>Value: {value,8:F2}</Text>
```

The parser splits only top-level interpolation separators, so nested C# such as `condition ? value : values[key]` remains a normal expression. Factory mode uses `CsmxFormattedTextChild` and `CsmxAlignedFormattedTextChild` templates; fluent mode uses `CsmxFluentFormattedExpressionChild`. Frameworks decide whether `{Value}` is an immediate value, a signal, a binding, or another target-specific dynamic text representation.

Future compiled/custom strategies should keep the same backend-owned direction: lower child IR inside the element/backend instead of producing a mandatory children expression.
