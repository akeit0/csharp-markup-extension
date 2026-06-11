# CSMX Roadmap

This document is the long-term implementation plan for making CSMX a practical C# JSX system with good editor UX, flexible framework lowering, and a Signal runtime that scales beyond toy samples.

## North Star

CSMX should feel like C# with JSX expression islands, not a separate language.

The compiler should parse JSX once, keep target-neutral syntax and IR, and lower through backend strategies. Runtime projects should stay strongly typed and framework-owned. Flexibility belongs in compiler lowering, backend templates, and framework conventions, not in permissive catch-all child arrays.

The Signal runtime should be acceptable for a larger UI project:

- nested components should keep local signal state stable
- signal updates should avoid rebuilding unrelated subtrees
- text updates should patch text when layout does not change
- layout-changing updates should rebuild only the smallest necessary region
- event handlers should not duplicate or retain stale state
- generated code should remain readable C#
- editor features should stay responsive on large files

## Design Rules

1. CSMX compiler and runtime remain separate.
2. `samples/Csmx.SampleRuntime` is a sample target, not the default language shape.
3. Lowering backends own their child strategy.
4. Do not solve framework flexibility by adding broad untyped runtime APIs.
5. LSP should own a Roslyn workspace for C# semantics instead of forwarding to another extension.
6. Performance work needs budgets and tests, not only manual observation.
7. Backward compatibility can be broken while the project is experimental if the old shape blocks the desired architecture.

## Project Tracks

### Track A: Compiler Core

Goal: support richer C# + JSX expression islands without making the parser ad-hoc.

Current state:

- JSX parser and syntax model are separated from lowering.
- Target-neutral IR exists.
- Factory and fluent lowering are separate.
- Formatted child expressions such as `{value:F2}` and `{value,8:F2}` are represented in IR.
- Nested JSX in C# expression holes is recursively lowered for cases such as ternary branches and lambda bodies.
- Factory mode routes expression children that contain nested JSX through `CsmxChildSequenceFactory`.
- The parser recovers when a nested element sees an ancestor closing tag, preserving following C# source for transform and LSP.
- Fluent lowering templates can emit source-position call-site IDs through `{CallSite}`.
- Component classification uses configured or discovered component symbols, leaving uppercase builder element types available to fluent frameworks.
- Keyed dynamic child sequence lowering is opt-in through configured element and template options such as `<For Items=... Key=...>`.
- Incomplete keyed sequence configuration reports compiler diagnostics for missing template, items, key, or render expression.
- Keyed sequence template placeholders for items, key, and render expressions have source-map coverage for semantic delegation.
- Parser recovery preserves later attributes after broken attribute expressions and keeps following C# after missing opening tag terminators.
- Signal component scopes support keyed children and preserve state through reorder, while removed keys are disposed.
- Signal keyed dynamic children can flow through typed `UiElement` child sequences while preserving keyed state.
- `samples/SignalDashboardApp` covers nested keyed rows, formatted text, event handlers, filtering/reordering, and row-local signal state.
- Signal dashboard headless mode reports first-frame time, node count, dirty regions, and render calls.
- Nested Signal component scopes own their structural render signal subscriptions and dispose them when the component scope is removed.
- Signal components render through retained host elements, allowing child structural signal updates to rebuild from the retained tree without calling the root render.
- Signal runtime tests include a dashboard-style keyed row update budget that guards root and sibling rerenders.
- Signals support explicit batching, deferring repeated writes until the batch closes and coalescing frame wake requests.
- Signal wake requests are coalesced between frames so repeated writes schedule one render wake.
- Signal scene builds cache dynamic text evaluation per text element, keeping text dependencies separate from structural render dependencies during measurement and layout.
- LSP completion facts cover nested JSX attribute names, and C# expression holes are left open for generated-document delegation.

Planned work:

1. Replace expression-start heuristics with a small C# lexical context model.
   - Recognize JSX after `return`, `throw`, `yield return`, assignment, lambda body, argument position, collection element position, and conditional branches.
   - Avoid interpreting normal C# operators such as `<`, `<=`, and `=>` as tags.
   - Keep trivia and source spans precise.

2. Broaden nested JSX inside C# expressions.
   - Examples:

     ```csharp
     var child = condition ? <Text>Yes</Text> : <Text>No</Text>;
     return <Panel>{items.Select(item => <Text>{item.Name}</Text>)}</Panel>;
     ```

   - The first expression scanner handles nested element starts in expression spans.
   - Next work should improve recovery, interpolated-string holes, and source-map coverage for more complex nested expressions.

3. Introduce parser recovery.
   - Unterminated tag should return a partial tree and diagnostics.
   - Mismatched nested closing tags should return control to the owning ancestor element.
   - Attribute expression parse failure should preserve following attributes when possible.
   - Missing opening tag terminators should not consume following C# statements as attributes or text.
   - LSP should still provide tokens and completions in broken code.

4. Expand source maps.
   - Keep exact mappings for unchanged C# spans.
   - Map JSX tag names, attribute names, attribute values, child expressions, formatted child value, alignment, and format.
   - Add generated-to-source and source-to-generated query APIs with explicit bias modes.

5. Add compiler performance budgets.
   - Track transform time for 1 KB, 10 KB, 100 KB, and 1 MB files.
   - Track allocations for parser, IR builder, and lowering.
   - Add benchmark tests once behavior is stable enough.

Acceptance criteria:

- A file with nested components, lambdas, formatted child expressions, and normal C# comparison operators transforms correctly.
- Syntax errors do not destroy the entire file's LSP features.
- Generated mappings are good enough for hover, definitions, semantic tokens, and diagnostics.

### Track B: Backend Lowering

Goal: make CSMX framework-extensible without per-element manifests.

Current state:

- Factory backend materializes children.
- Fluent backend owns children through chained calls.
- Component lowering can be direct or factory-based.
- Project properties and source pragmas can tune lowering templates.
- Factory component calls can be wrapped through `CsmxComponentTemplate` with `{CallSite}`, `{Component}`, `{Props}`, and `{Children}` placeholders.
- Backend-owned fluent component calls can be wrapped through `CsmxFluentComponentTemplate` while intrinsic elements remain fluent builders.

Planned work:

1. Stabilize backend contract.
   - Keep a small `ICsmxLoweringBackend` surface.
   - Pass complete element IR, not pre-shaped child expressions.
   - Let the backend choose materialized children, chained calls, builder calls, static templates, or retained node construction.

2. Add backend capabilities.
   - `SupportsComponentReferences`
   - `SupportsFormattedText`
   - `SupportsDynamicChildren`
   - `SupportsStaticTemplateHoisting`
   - `RequiresStableChildIdentity`

   Capabilities should validate project configuration and improve diagnostics. They should not become a verbose framework manifest.

3. Add compiled signal backend.
   - Lower static element shape into a build function.
   - Lower dynamic text into tracked bindings.
   - Lower dynamic attributes into tracked property bindings.
   - Lower event attributes into stable handlers.
   - Lower child element arrays through typed child slots.

4. Add template hoisting.
   - Static element trees should be hoisted or cached where the backend can reuse them.
   - Dynamic holes should be represented as slots.
   - The IR should describe static tree shape without assuming HTML.

5. Improve diagnostics.
   - Unknown lowering token should point to the property.
   - Unsupported formatted expression should suggest configuring the formatted template or using a backend that supports it.
   - Component props construction errors should mention the inferred props type and element name.

Acceptance criteria:

- A backend can implement React-like factory, fluent builder, and signal retained UI without changing parser or IR.
- A framework can opt into typed conventions rather than declaring every element in a manifest.
- Unsupported backend features fail with clear diagnostics.

### Track C: Signal Runtime Scale

Goal: make `Csmx.EnagaSignals` viable for a larger UI project with nested components and frequent signal updates.

Current state:

- `CreateSignal` stores local signal slots for the current render frame.
- Text can bind to `Signal<T>`.
- Dynamic text updates can patch text when layout does not need a rebuild.
- Enaga layout/render integration exists.

The current model is still too coarse for a large nested app because render state is scoped mainly by call order. That works for simple stable render functions, but it is fragile for conditional and repeated nested components.

Planned architecture:

```text
SignalSceneFrameSource
  -> RootComponentInstance
      -> ComponentInstance
          -> Signal slots
          -> Memo slots
          -> Effect slots
          -> Child component instances
          -> Rendered UiElement subtree
```

#### Component Identity

Nested components need stable identity independent of raw call order.

Plan:

1. Introduce `ComponentInstance`.
   - Owns signal slots for one component render.
   - Owns child component instances.
   - Tracks subscriptions created during render.
   - Tracks the last rendered subtree root.

2. Add keyed child instance lookup.
   - Unkeyed children can use stable source position plus ordinal.
   - Repeated children need explicit keys when order can change.

   Example future shape:

   ```csharp
   <For Items={items} Key={item => item.Id}>
       {item => <TodoRow Item={item} />}
   </For>
   ```

3. Add source-position identity from compiler output.
   - The compiler can pass stable call-site IDs to retained or signal backends through lowering templates.
   - IDs should be deterministic from source path and span, not generated line number.

Acceptance criteria:

- Moving a signal inside a nested component does not reset when an unrelated parent signal changes.
- Reordering keyed children preserves each child's state.
- Removing a child disposes subscriptions and handlers for that child.

#### Fine-Grained Invalidations

Large apps cannot rebuild the whole tree for every local update.

Plan:

1. Classify signal reads.
   - Render-time read: can change element structure or layout.
   - Text read: can patch text or trigger local layout.
   - Attribute/style read: can patch property or trigger local layout.
   - Event handler capture: should not subscribe by itself.

2. Store dependency sets per binding.
   - Text binding owns text dependencies.
   - Attribute binding owns attribute dependencies.
   - Component render owns structural dependencies.

3. Route invalidation to the smallest owner.
   - Text same measurement: patch render frame.
   - Text new measurement: relayout nearest layout boundary.
   - Style/layout attribute: relayout nearest layout boundary.
   - Structural read: rerender component instance only.

4. Add layout boundaries.
   - Default boundary can be the component root.
   - Later, components can opt into explicit boundaries for performance.

Acceptance criteria:

- Updating `Count: {count}` does not call the parent `Render` if the text binding is independent.
- Updating a nested row's signal does not rerender sibling rows.
- Layout-changing text rerenders or relayouts only the affected component subtree.

#### Nested Case

The nested case must be a first-class target, not an afterthought.

Example target:

```csharp
public static UiNode Dashboard()
{
    var (filter, setFilter) = CreateSignal("");

    return <Column>
        <Toolbar Filter={filter} OnFilter={setFilter} />
        <TodoList Filter={filter} />
    </Column>;
}

static UiNode TodoList(Signal<string> filter)
{
    var (items, setItems) = CreateSignal(LoadItems());

    return <Column>
        {items.Value
            .Where(item => item.Title.Contains(filter.Value))
            .Select(item => <TodoRow Key={item.Id} Item={item} />)}
    </Column>;
}

static UiNode TodoRow(TodoItem item)
{
    var (editing, setEditing) = CreateSignal(false);

    return <Row>
        <Text>{item.Title}</Text>
        <Button OnClick={() => setEditing(value => !value)}>Edit</Button>
        {editing.Value ? <Editor Item={item} /> : null}
    </Row>;
}
```

Required behavior:

- Changing `filter` should rerender `TodoList` and preserve keyed `TodoRow` state for rows that remain.
- Toggling one row's `editing` signal should not rerender the dashboard, toolbar, or sibling rows.
- Removing a row should dispose that row's `editing` signal and dynamic bindings.
- Reordering rows should preserve state by key.

#### Typed Children

Large signal UI should keep typed children:

```csharp
IReadOnlyList<UiElement>
```

or an internal builder-owned equivalent. Children should not be represented as a loose content bag.

Plan:

1. Keep element children and text content separate.
2. Add typed child sequence support for dynamic lists.
3. Add `Fragment` or `Children` node only if needed to represent multiple roots.
4. Keep text content as string, primitive overloads, signal overloads, and explicit formatter overloads.

Acceptance criteria:

- `<A><B/><C/></A>` lowers into two element children, not one append path that mixes text and elements.
- Dynamic child lists preserve identity and disposal behavior.
- Text content and element children remain separate in runtime APIs.

#### Scheduling

Plan:

1. Batch signal writes in the same input event.
2. Coalesce render wakes.
3. Prevent reentrant frame builds.
4. Add deterministic test scheduler for runtime tests.

Acceptance criteria:

- Multiple signal updates in one event produce one frame request.
- Event handlers cannot trigger overlapping render commits.
- Tests can assert frame counts without timing sleeps.

### Track D: LSP And VS Code UX

Goal: keep editor feedback good while compiler/runtime grow.

Current state:

- Build integration uses `Csmx.SourceGenerator`; samples build `.csmx` files as Roslyn `AdditionalFiles` and generated C# is contributed by the compiler pipeline.
- `Csmx.SourceGenerator` now targets `netstandard2.0` and is self-contained for analyzer loading.
- CSMX LSP owns JSX facts and simple hovers.
- VS Code extension can still delegate C# hover, completion, definitions, and semantic tokens through generated mappings only for scratch/no-project files; this remains legacy prototype infrastructure.
- Project-file C# semantics are moving to an owned Roslyn workspace that can see source-generator output, project references, unsaved documents, and diagnostics directly.
- Roslyn-backed hover, semantic tokens, definitions, and C# diagnostics now resolve project-backed C# identifiers and errors inside `.csmx`.
- Files outside a project fall back to `csmx-csharp:` virtual generated documents.
- Project-context cache entries track the project file, evaluated MSBuild imports, ancestor `Directory.Build.props` / `.targets` candidates, and `obj/project.assets.json`.
- VS Code settings `csmx.project.configuration` and `csmx.project.targetFramework` select the active editor MSBuild context for transforms, project binding inspection, and open-document refresh.
- `CSMX: Inspect Project Binding` reports detected project, evaluated options, and project-context dependencies.
- `CSMX: Reload Project Context` clears the active document's project-context cache entry and refreshes open-document analysis as a manual fallback.
- Project-backed generated-document forwarding is disabled; hover, semantic tokens, definitions, and diagnostics use the CSMX Roslyn workspace.
- Scratch/no-project delegation shares one virtual generated projection per `.csmx` document version and invalidates on document close or project-context reload.
- Delegation failures are logged to the `CSMX Scratch C# Delegation` output channel.
- TextMate grammar handles core mixed syntax.
- Formatter exists and reads `.editorconfig`.
- Semantic tokens and completion facts include JSX nested inside expression children.
- VS Code integration smoke covers formatting, semantic tokens, and generated C# projection behavior.

Planned work:

1. Finish replacing editor delegation with an owned Roslyn workspace.
    - Load projects with MSBuildWorkspace or the closest stable project-system API.
    - Feed open `.csmx` documents through the same compiler projection used by the source generator.
   - Query Roslyn for diagnostics, hover, completion, definitions, semantic tokens, and symbols.
   - Current implementation covers project-backed hover, semantic tokens, definitions, and diagnostics.
- Project-backed generated-document forwarding is intentionally disabled. Tag and attribute completion now use owned Roslyn where project-backed; expression completion still needs owned Roslyn support.
   - Map Roslyn spans back to `.csmx` through source maps.

2. Expand semantic projection coverage.
   - Nested JSX expression holes.
   - Interpolated raw strings and format clauses.
   - Attribute lambda bodies.
   - Component props construction.

3. Expand completion sources on top of Roslyn.
   - C# completions in expression holes.
   - Attribute completions from inferred component props type.
   - Broader component completions from project symbols where feasible.
   - Avoid hardcoded tag lists.

4. Add code actions.
   - Create missing props property.
   - Convert text expression to formatted expression.
   - Show generated C# projection.

5. Formatter milestones.
   - Stable formatting for nested JSX in ternaries and lambdas.
   - Attribute wrapping based on `.editorconfig` line length.
   - Preserve intentional blank lines and comments.
   - Format generated-like fluent chains without flattening C# methods.

Acceptance criteria:

- Hover on C# symbols inside `.csmx` returns C# symbol info, not CSMX lowering explanations.
- Tag/attribute/text semantic colors remain stable across TextMate and semantic token passes.
- Completion is framework/type-driven where possible and never based on hardcoded sample tags.

### Track E: Build, Incrementality, And Packaging

Goal: make daily iteration fast.

Current state:

- `Csmx.SourceGenerator` transforms `.csmx` `AdditionalFiles` into Roslyn generated source.
- The generator is IDE-loadable as a `netstandard2.0` analyzer and does not rely on a separate compiler dependency next to the analyzer DLL.
- Sample projects build through `Csmx.SourceGenerator` and do not create physical `Generated/Csmx` projections.
- `.cs` files can bind to `.csmx` members through the normal compiler generated-source pipeline.
- `samples/csmx-local.targets` exposes CSMX MSBuild properties through `CompilerVisibleProperty`.
- The old build-task/generator-tool physical projection path has been removed from the repository.
- Extension package includes local language server.
- `eng/dev-check.mjs` and the extension server publish script isolate .NET CLI and NuGet state under the repository.

Planned work:

1. Package `Csmx.SourceGenerator` cleanly as the default build integration.
2. Add source-generator unit/integration tests that compile sample `.csmx` AdditionalFiles without physical generated roots.
3. Keep language server packaging reproducible.
4. Add CI scripts for:
   - .NET tests
   - extension unit tests
   - extension integration smoke
   - package check
   - sample run checks

Acceptance criteria:

- A clean sample build succeeds after deleting all `Generated/Csmx` directories and does not recreate them.
- A normal `.cs` file can reference a type/member declared only in `.csmx`.
- Extension packaging cannot include test harness output.
- A clean checkout can build, test, package, and run samples with one documented command set.

### Track F: Tests

Goal: make regressions visible before manual VS Code checks.

Test layers:

1. Compiler unit tests.
   - Parser recovery.
   - Nested JSX expressions.
   - Formatted child expressions.
   - Backend lowering.
   - Source maps.

2. Build/source-generator smoke tests.
   - Sample Debug/Release builds.
   - Generated item exclusion.
   - No physical generated roots.
   - Imported MSBuild property evaluation.

3. Signal runtime tests.
   - Local signal state.
   - Nested component state.
   - Keyed list preservation.
   - Disposal.
   - Fine-grained text patch.
   - Layout-changing invalidation.
   - Batched scheduling.

4. LSP tests.
   - Roslyn hover.
   - Roslyn definition.
   - Semantic token mapping.
   - Completion in expression holes.
   - Broken-document recovery.

5. VS Code integration tests.
   - Formatter smoke.
   - Semantic token smoke.
   - Hover smoke.
   - Generated C# projection smoke.

## Milestones

### Milestone 1: Stabilize Current Surface

- Add roadmap and update docs.
- Add tests for no-hardcoded completion behavior.
- Add incremental skip tests for samples.
- Tighten public runtime APIs that still expose broad value sinks.
- Keep extension packaging clean and repeatable.

### Milestone 2: Nested Parser

- Implement nested JSX in expression holes.
- Add recovery diagnostics.
- Add source map coverage for nested holes.
- Update formatter and semantic token tests.

### Milestone 3: Signal Component Instances

- Add `ComponentInstance`.
- Add component render scopes.
- Preserve nested component local state.
- Dispose removed instances.
- Add nested component tests.

### Milestone 4: Fine-Grained Signal Runtime

- Separate render, text, attribute, and event dependency tracking.
- Patch text without parent rerender.
- Relayout nearest boundary for layout-changing updates.
- Batch event-triggered signal writes.

### Milestone 5: Dynamic Children

- Add typed dynamic child sequence support.
- Add keyed list identity.
- Add fragment/multiple-root support if required.
- Verify `<A><B/><C/></A>` and dynamic list cases stay typed.

### Milestone 6: Compiled Signal Backend

- Add backend-owned retained UI lowering.
- Pass stable source-position call-site IDs.
- Emit binding objects for dynamic text and attributes.
- Reduce runtime reflection/configuration needs.

### Milestone 7: Large Sample App

Create a larger `samples/SignalDashboardApp` or expand `SignalApp` into separate projects:

- shell layout
- toolbar/filter input
- nested list
- keyed rows
- row-local state
- conditional editor
- formatted numeric text
- event-heavy controls
- enough nodes to expose performance issues

Acceptance criteria:

- One row update does not rerender all rows.
- Filtering preserves state for stable keyed rows.
- Fast repeated clicks produce stable count changes and one coalesced frame per batch.
- No stale event handlers after list changes.
- Startup and first frame time are measured.

## Immediate Next Work

1. Broaden parser recovery for unterminated root elements and malformed nested closing tags beyond the current targeted cases.
2. Add performance budget tests for dashboard first frame and layout-heavy update paths.
3. Expand the Roslyn-workspace LSP replacement from hover to completion, definitions, diagnostics, and semantic tokens.

## Open Questions

- Should component identity be compiler-generated by source span, explicit by runtime call, or both?
- Should keyed lists be a runtime component such as `<For>` or a compiler-recognized pattern?
- Should signal text formatting use culture from runtime context or invariant/default culture?
- How much of the signal backend should be a compiler backend versus ordinary fluent lowering plus smarter runtime?
- Should fragments be exposed as a public element or only as an internal lowering construct?

## Non-Negotiables

- Do not make the sample runtime the language default.
- Do not require comment directives to describe normal components.
- Do not reintroduce catch-all child arrays to fake framework flexibility.
- Do not put hardcoded sample tags into LSP behavior.
- Do not let full-app rerender be the final Signal runtime model.
