# C\# Markup Extension PoC: C# with JSX-like Expression Syntax

This repository is a minimal proof-of-concept for a C# project that accepts `.csmx` files containing mostly-normal C# plus JSX-like element expressions.

## Quick Start

```bash
git clone https://github.com/akeit0/csharp-markup-extension.git
cd csmx
git submodule update --init Enaga
dotnet build Csmx.slnx
cd vscode/csmx-vscode
npm ci
cd ../..
```

For the quickest editor loop, open the workspace and run **Extension: Launch CSMX** from VS Code's Run and Debug view. This starts an Extension Development Host without installing a VSIX.

```bash
code .
```

To install the extension into your normal VS Code profile instead, package and install the VSIX:

```bash
cd vscode/csmx-vscode
npm run package
code --install-extension csmx-vscode-0.1.0.vsix --force
cd ../..
```

Good starting points:

```text
samples/HelloApp/Components/Counter.csmx
samples/SignalApp/Components/CounterView.csmx
samples/SignalDashboardApp/Components/Dashboard.csmx
```

Run samples:

```bash
dotnet run --project samples/HelloApp/HelloApp.csproj
dotnet run --project samples/SignalApp/SignalApp.csproj
dotnet run --project samples/SignalDashboardApp/SignalDashboardApp.csproj
```

The intended syntax is deliberately small:

```csharp
using Csmx.SampleRuntime;

namespace Csmx.Samples.HelloApp.Components;

public static class Counter
{
    public static VNode Render(string name, int count)
    {
        Func<CounterViewProps, VNode[], VNode> View = (props, children) =>
            <stack class="counter">
                <text>Hello {props.Name}</text>
                <text>Count: {props.Count}</text>
                <button disabled={props.Count == 0}>Click</button>
            </stack>;

        return <View name={name} count={count} />;
    }
}
```

The generated C# is ordinary C#:

```csharp
Func<CounterViewProps, VNode[], VNode> View = (props, children) =>
    global::Csmx.SampleRuntime.Csmx.Element(
        "stack",
        global::Csmx.SampleRuntime.Csmx.Props(global::Csmx.SampleRuntime.Csmx.Attr("class", "counter")),
        global::Csmx.SampleRuntime.Csmx.Children(
            global::Csmx.SampleRuntime.Csmx.Element("text", global::Csmx.SampleRuntime.Csmx.Props(), global::Csmx.SampleRuntime.Csmx.Children(global::Csmx.SampleRuntime.Csmx.Text("Hello "), global::Csmx.SampleRuntime.Csmx.Text(props.Name))),
            global::Csmx.SampleRuntime.Csmx.Element("text", global::Csmx.SampleRuntime.Csmx.Props(), global::Csmx.SampleRuntime.Csmx.Children(global::Csmx.SampleRuntime.Csmx.Text("Count: "), global::Csmx.SampleRuntime.Csmx.Text(props.Count))),
            global::Csmx.SampleRuntime.Csmx.Element("button", global::Csmx.SampleRuntime.Csmx.Props(global::Csmx.SampleRuntime.Csmx.Attr("disabled", props.Count == 0)), global::Csmx.SampleRuntime.Csmx.Children(global::Csmx.SampleRuntime.Csmx.Text("Click")))));

return View(new CounterViewProps { Name = name, Count = count }, global::Csmx.SampleRuntime.Csmx.Children());
```

## Important design choice

Build integration is moving to a Roslyn source generator. `.csmx` files are added as `AdditionalFiles`, transformed by `Csmx.SourceGenerator`, and contributed to the normal C# compilation as generated source. This makes `.cs` files see `.csmx`-defined members through the compiler's official generated-source pipeline instead of through project-local physical `.g.cs` files.

The VS Code extension still starts the CSMX language server. Legacy request-forwarding to the installed C# extension remains in place, but the server has started owning Roslyn-backed C# semantics directly. Hover for project-backed `.csmx` C# identifiers now resolves through the CSMX server's own compilation and source maps.

In a normal VS Code workspace this means:

```text
*.cs      -> existing C# extension / C# language server, if installed
*.csmx    -> this CSMX language server only
compiler generated source -> generated C# contributed by Roslyn source generator
```

The CSMX language server aims to provide CSMX syntax diagnostics, JSX tag/attribute completion, semantic highlighting, hover, generated C# projection data, and project-binding inspection. The VS Code extension request-forwards regular C# completion, hover, go-to-definition, and semantic tokens to the installed C# extension using the project-local generated `.g.cs` file. If a `.csmx` file is not inside a project, the extension falls back to a virtual generated C# document.

Generation defaults to unqualified factory names: `Element`, `Props`, `Attr`, `Children`, `ChildSequence`, and `Text`. Projects configure concrete targets with `CsmxCompileMode`, `CsmxElementFactory`, `CsmxPropsFactory`, `CsmxAttributeFactory`, `CsmxChildrenFactory`, `CsmxChildSequenceFactory`, `CsmxTextFactory`, formatted child templates, `CsmxComponentLowering`, and `CsmxComponentNames`.

Factory mode lowers to an element factory that receives `string name, TProps props, TChildren children`.

Fluent mode is convention based and backend-owned. With the default fluent conventions:

```csharp
<Button Size={10}>Click</Button>
```

lowers to:

```csharp
new Button().Size(10).Content("Click")
```

The fluent conventions are project-level templates such as `CsmxFluentCreate`, `CsmxFluentAttribute`, and `CsmxFluentTextChild`; they are not per-element manifests.

Child expressions support interpolation-style formatting:

```csharp
<Text>Value: {value:F2}</Text>
<Text>Value: {value,8:F2}</Text>
```

Factory mode lowers those through `CsmxFormattedTextChild` or `CsmxAlignedFormattedTextChild`. Fluent mode lowers them through `CsmxFluentFormattedExpressionChild`. Template tokens include `{Value}`, `{Format}`, `{Alignment}`, and in factory mode `{TextFactory}`.

Lowercase tags are intrinsic elements. Uppercase tags are component references. A local declaration such as `Func<ViewProps, VNode[], VNode> View = ...` lets the compiler infer `ViewProps` for `<View />`. `CsmxComponentLowering=direct` lowers components as `View(props, children)`, while `CsmxComponentLowering=factory` lowers them through the configured element factory.

## Project layout

```text
src/Csmx.Compiler        Transformer: mostly-C# + JSX expressions -> C#
src/Csmx.SourceGenerator Roslyn source generator for .csmx AdditionalFiles
src/Csmx.LanguageServer  Minimal C# LSP server over stdio
samples/Csmx.SampleRuntime Tiny virtual-node sample runtime
samples/HelloApp         Direct-call component lowering sample
samples/FactoryApp       Factory-call component lowering sample
samples/FluentApp        Convention-based fluent lowering sample
samples/SignalApp        Enaga-backed signal UI sample
vscode/csmx-vscode       Minimal VS Code extension
```

Design notes and planning live under `docs/`, including the long-term roadmap in `docs/roadmap.md`.
For a reviewable current-state overview, see `docs/features-and-architecture.md`.

## Supported MVP syntax

JSX-like element expressions:

```csharp
return <text>Hello</text>;
var button = <button disabled={count == 0}>Click</button>;
var layout = <stack><text>{name}</text></stack>;
var choice = condition ? <text>A</text> : <text>B</text>;
var projected = <panel>{items.Select(item => <text>{item.Name}</text>)}</panel>;
```

Attributes:

```csharp
<text class="title" />
<button disabled />
<button disabled={count == 0} />
```

Children:

```csharp
<text>Hello</text>
<text>Hello {name}</text>
<stack><text>A</text><text>B</text></stack>
```

## Not supported in this MVP

```csharp
// Not supported yet: JSX fragments
return <>Hello</>;

// Not supported yet: spread attributes
return <button {...props} />;
```

CSMX transforms JSX where a C# expression can appear, including nested JSX inside expression islands such as ternary branches and lambda bodies. In factory mode, a child expression that contains nested JSX is passed through `CsmxChildSequenceFactory` so typed child sequences can be flattened by the target runtime.

## Build integration

The sample projects import `samples/csmx-local.targets`, which wires in `src/Csmx.SourceGenerator` as a Roslyn analyzer.

The current source-generator target:

1. Includes `**/*.csmx` as CSMX inputs.
2. Removes those files from normal C# compilation.
3. Adds them as `AdditionalFiles`.
4. Adds `Csmx.SourceGenerator` as the analyzer input.

`Csmx.SourceGenerator` targets `netstandard2.0` and compiles the portable CSMX compiler sources into the analyzer assembly. That keeps analyzer loading simple for command-line builds and IDE hosts: the generator does not require a separate `Csmx.Compiler.dll` beside it.
5. Exposes CSMX MSBuild properties through `CompilerVisibleProperty`.
6. Lets Roslyn generated source participate in normal compilation, references, diagnostics, and `.cs` to `.csmx` symbol binding.

## Build and smoke-test the samples

```bash
dotnet build Csmx.slnx
dotnet run --project samples/HelloApp/HelloApp.csproj
dotnet run --project samples/FactoryApp/FactoryApp.csproj
dotnet run --project samples/FluentApp/FluentApp.csproj
dotnet run --project samples/SignalApp/SignalApp.csproj -- --once
dotnet run --project samples/SignalDashboardApp/SignalDashboardApp.csproj -- --once
```

The signal samples accept `--once` for non-interactive CLI smoke checks. Omit it to open an Enaga native window.

Expected `HelloApp` output:

```html
<stack class="counter"><text>Hello World</text><text>Count: 3</text><button>Click</button></stack>
```

Expected `FactoryApp` output:

```html
<panel class="factory-counter"><label>Factory Hello Factory!!</label><label> Count: 7</label><action>Run</action></panel>
```

Expected `FluentApp` output:

```text
Panel(Button(Size=10, Content=Click))
```

Expected `SignalApp --once` output:

```text
nodes=10; dirty=1; count=1
```

## VS Code extension

```bash
cd vscode/csmx-vscode
npm ci
npm run package
code --install-extension csmx-vscode-0.1.0.vsix --force
```

For a repo-wide verification pass, run:

```bash
node ./eng/dev-check.mjs
```

This script and the extension server publish script keep .NET CLI and NuGet state under the repository (`.dotnet/`, `.appdata/`, `.nuget-packages/`) so local checks do not depend on machine-level NuGet configuration.

VS Code users can run the tracked workspace tasks and launch profiles:

- `Extension: Launch CSMX` starts an extension-development host after building the extension and bundled language server.
- `Extension: Package VSIX` runs `npm run package`.
- `Dev Check` runs `eng/dev-check.mjs`.

The packaged extension includes a published language server. To use a custom published server directory instead, set:

```json
{
  "csmx.server.directory": "/absolute/path/to/published/server"
}
```

For multi-target or non-Debug editor context, set the active CSMX projection explicitly:

```json
{
  "csmx.project.configuration": "Release",
  "csmx.project.targetFramework": "net10.0-windows"
}
```

Empty values use the project/MSBuild defaults. Changing either setting refreshes open `.csmx` projections and clears the editor delegation cache.

Useful commands while a `.csmx` file is active:

- **CSMX: Show Generated C#** opens the transformed code.
- **CSMX: Inspect Project Binding** reports extension version, project path, requested/evaluated Configuration and TargetFramework, evaluation kind, evaluated generated path, generated file existence, MSBuild `Compile` inclusion, and project-context dependencies.
- **CSMX: Reload Project Context** clears the active document's project-context cache entry, clears the editor-side prepared-projection cache, and refreshes its generated projection as a manual fallback.
- **CSMX: Inspect Token At Cursor** reports TextMate token, semantic token, bracket configuration, and generated mapping data.

## Limitation

- It is not a Roslyn extension, so IDE features must be implemented from scratch.
- Unlike Razor, official ExternalAccess is not provided from Roslyn, so the workspace cannot be shared with normal C# (cannot co-host).

## LICENSE

MIT
