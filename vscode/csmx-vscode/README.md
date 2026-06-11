# C# JSX VS Code Extension

Minimal VS Code client for the CSMX MVP language server.

Build integration uses `Csmx.SourceGenerator`: project `.csmx` files compile as Roslyn `AdditionalFiles` with generated source contributed by the compiler pipeline. The generator is a `netstandard2.0` analyzer for IDE loading. The CSMX language server is replacing VS Code request-forwarding with owned Roslyn-backed features for project-backed files.

The extension starts the CSMX language server. Project-backed hover, definitions, semantic tokens, and diagnostics are resolved through the server's owned Roslyn workspace; legacy generated-document delegation is limited to scratch/no-project files.

The CSMX server owns CSMX syntax diagnostics, project-backed C# diagnostics, JSX tag/attribute completion, hover, go-to-definition, and semantic highlighting. Project-backed tag and attribute completion use the owned Roslyn workspace; broader C# expression completion is not forwarded to the external C# extension yet.

Normal builds do not write `Generated/Csmx/**/*.csmx.g.cs` files and do not create CSMX-owned `obj/csmx` directories. Files outside a project fall back to a `csmx-csharp:` virtual document for best-effort C# delegation.

## Local setup

Install dependencies:

```bash
npm ci
```

Build the extension and bundled language server:

```bash
npm run build
```

`npm run build`, `npm run test:integration`, and `npm run package` publish the language server through `scripts/publish-server.mjs`. The script uses repo-local .NET and NuGet state so extension checks do not depend on the current user's NuGet configuration.

From the repository root, VS Code also exposes launch profiles for extension-host debugging, packaging, integration smoke tests, and the full dev check.

Run the unit regression tests:

```bash
npm run test:unit
```

Run the VS Code extension-host smoke test:

```bash
npm run test:integration
```

Package and install the extension:

```bash
npm run package
code --install-extension csmx-vscode-0.1.00.vsix --force
```

`npm run package` runs the formatter, grammar, and VS Code smoke tests before creating the VSIX.

The packaged extension includes a published `server/` language-server directory. To use a custom language server build, publish it first and set:

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

Empty values use project/MSBuild defaults. Setting changes refresh open `.csmx` documents and clear cached project context.

## Commands

Use **CSMX: Show Generated C#** while a `.csmx` file is active to inspect the transformed C#.

Use **CSMX: Inspect Project Binding** to inspect the active document's extension version, detected project, requested/evaluated Configuration and TargetFramework, project-context dependencies, compile mode, and factory/fluent settings.

Use **CSMX: Reload Project Context** to manually clear the active document's language-server project-context cache entry and refresh open-document analysis.

Use **CSMX: Inspect Token At Cursor** to inspect the active TextMate token, LSP semantic token, bracket configuration, and generated C# mapping for the cursor position.

## Custom factories

Project-wide defaults can be set with MSBuild properties:

```xml
<PropertyGroup>
  <CsmxCompileMode>factory</CsmxCompileMode>
  <CsmxElementFactory>MyUi.CreateElement</CsmxElementFactory>
  <CsmxPropsFactory>MyUi.CreateProps</CsmxPropsFactory>
  <CsmxAttributeFactory>MyUi.CreateAttribute</CsmxAttributeFactory>
  <CsmxChildrenFactory>MyUi.CreateChildren</CsmxChildrenFactory>
  <CsmxTextFactory>MyUi.CreateText</CsmxTextFactory>
  <CsmxFormattedTextChild>MyUi.Context({Value}, {Format})</CsmxFormattedTextChild>
  <CsmxAlignedFormattedTextChild>MyUi.Context({Value}, {Alignment}, {Format})</CsmxAlignedFormattedTextChild>
  <CsmxComponentLowering>direct</CsmxComponentLowering>
</PropertyGroup>
```

Convention-based fluent lowering is configured at the framework level:

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

With those defaults, `<Button Size={10}>Click</Button>` lowers to `new Button().Size(10).Content("Click")`. Use `{Element}()` as `CsmxFluentCreate` when creation should omit `new`.

Child expressions support interpolation-style formatting:

```csharp
<Text>Value: {value:F2}</Text>
<Text>Value: {value,8:F2}</Text>
```

Factory formatted child templates can use `{TextFactory}`, `{Value}`, `{Format}`, and `{Alignment}`. Fluent formatted child templates can use `{Value}`, `{Format}`, and `{Alignment}`.

Per-file pragmas override those defaults:

```csharp
// @jsxCompileMode factory
// @jsx MyUi.CreateElement
// @jsxProps MyUi.CreateProps
// @jsxAttr MyUi.CreateAttribute
// @jsxChildren MyUi.CreateChildren
// @jsxText MyUi.CreateText
// @jsxFormattedText MyUi.Context({Value}, {Format})
// @jsxAlignedFormattedText MyUi.Context({Value}, {Alignment}, {Format})
```

The element factory is called as `ElementFactory(string name, TProps props, TChildren children)`. The bundled sample maps those factories to `Csmx.SampleRuntime`, but the compiler does not require that runtime shape.

Lowercase tags are intrinsic elements. Uppercase tags are component references. A local declaration such as `Func<ViewProps, VNode[], VNode> View = ...` lets the compiler infer `ViewProps` for `<View />`. `CsmxComponentLowering=direct` lowers `<View />` to `View(props, children)`. `CsmxComponentLowering=factory` lowers it through the configured element factory as `ElementFactory(View, props, children)`.

## C# delegation

Delegation prepares a generated C# projection only for `.csmx` files that are not inside a project. It uses a read-only `csmx-csharp:` virtual document and maps editor positions through compiler source-map entries. Project-backed files do not use generated-document request forwarding and do not write physical generated files. Project-backed go-to-definition is served by the CSMX language server's owned Roslyn workspace.

VS Code request forwarding still does not expose a diagnostics pull API. Project-backed C# diagnostics are produced by the owned Roslyn workspace and mapped back through strict source-map spans; scratch/no-project C# diagnostics remain best-effort only through generated projection inspection.

Semantic highlighting is implemented by the CSMX language server with standard LSP token types. It classifies C# keywords, modifiers, declarations, likely types/methods/variables, comments, strings, numbers, operators, JSX tag names, and JSX attributes. JSX tag and attribute spans come from the compiler facts API so highlighting and hover use the same parser path. For project-backed files, the owned Roslyn workspace contributes C# semantic tokens and maps them back through strict compiler source mappings. Tag and attribute completions merge source facts with Roslyn-visible element types and fluent members.

Scratch/no-project delegation failures are logged once per document/failure kind to the **CSMX Scratch C# Delegation** output channel.
