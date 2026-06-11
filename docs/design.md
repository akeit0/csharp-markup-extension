# CSMX Design

## Goal

CSMX is a C#-first JSX experiment. A `.csmx` file is still mostly C#; only JSX-like element expressions are transformed.

```csharp
public static VNode Render(string name)
{
    return <text>Hello {name}</text>;
}
```

This is lowered to ordinary C# through configured factory calls:

```csharp
public static VNode Render(string name)
{
    return Element(
        "text",
        Props(),
        Children(Text("Hello "), Text(name)));
}
```

`samples/Csmx.SampleRuntime` is only a bundled sample target. Real targets should configure their own element, props, attribute, text, and children factories.

## Non-goals

- No custom `component` declaration syntax.
- No JavaScript-like component syntax.
- No compiler fork.
- No additional C# language server process.

## Architecture

```text
.csmx file
  -> Csmx.Compiler text transformer
  -> generated .g.cs
  -> normal C# compiler / existing project build
```

Editor side:

```text
VS Code extension
  -> starts Csmx.LanguageServer only
  -> handles .csmx diagnostics/completion/hover/definition
  -> uses owned Roslyn projection for project-backed C# hover/definition/tokens/diagnostics
  -> request-forwards C# features only for scratch/no-project virtual projections
  -> does not start a second C# language server
```

Build side:

```text
MSBuild target
  -> .csmx files are AdditionalFiles
  -> Csmx.SourceGenerator contributes generated C# to Roslyn
  -> normal C# compiler compiles source-generator output
```

Normal builds do not write `Generated/Csmx/**/*.csmx.g.cs` files and do not create CSMX-owned `obj/csmx` directories. The target removes stale legacy generated files from SDK implicit `Compile` globs, then lets the source generator provide generated source in memory.

Editor-side project context is evaluated through `dotnet msbuild -getProperty` so imported properties such as `Directory.Build.props` and local target imports match the real build. If MSBuild evaluation fails, the language server falls back to a direct XML read as an editor-only fallback.

## Transform strategy

The transformer scans C# text and copies it unchanged until it finds a likely JSX element expression. The MVP heuristic recognizes `<Tag` after expression-start contexts such as:

- `return <Tag />`
- `var x = <Tag />`
- `Render(<Tag />)`
- `x => <Tag />`
- `condition ? <A /> : <B />`
- `items.Select(item => <text>{item.Name}</text>)`

Nested JSX inside C# expression islands is transformed recursively. In factory mode, an expression child that contains nested JSX is routed through the configured child-sequence factory before it is passed to `Children(...)`.

## Factory Lowering

```csharp
<text>Hello {name}</text>
```

becomes:

```csharp
Element(
    "text",
    Props(),
    Children(Text("Hello "), Text(name)))
```

Attributes become configured prop/attribute calls:

```csharp
<button disabled={count == 0}>Click</button>
```

becomes:

```csharp
Element(
    "button",
    Props(Attr("disabled", count == 0)),
    Children(Text("Click")))
```

Lowercase tags are intrinsic elements. Uppercase tags are component references. A local component delegate can provide the props type:

```csharp
Func<CounterViewProps, VNode[], VNode> View = (props, children) =>
    <stack>{children}</stack>;

return <View name={name} />;
```

With direct component lowering, this becomes:

```csharp
View(new CounterViewProps { Name = name }, Children())
```

With factory component lowering, this becomes:

```csharp
Element(View, new CounterViewProps { Name = name }, Children())
```

## Source-generator build

The target tracks source files through Roslyn `AdditionalFiles`; Roslyn's incremental generator pipeline handles invalidation. `dotnet clean` removes stale legacy `Generated/Csmx` roots if they exist, but a normal build does not recreate them.
