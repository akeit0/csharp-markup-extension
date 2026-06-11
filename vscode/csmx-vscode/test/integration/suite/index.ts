import * as assert from 'assert/strict';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';

export async function run(): Promise<void> {
  await testDocumentFormatting();
  await testSemanticTokens();
  await testUsingStaticSemanticDelegation();
  await testNestedExpressionCompletionDelegation();
  await testOwnedRoslynHover();
}

async function testDocumentFormatting(): Promise<void> {
  const document = await openWorkspaceDocument(
    'FormattingSmoke.csmx',
    `public static UiNode Render(CounterState state)
{
return <Column
Padding={24}
OnClick={() => state.Count.Update(value => value + 1)}
>
<Text>
Count: {state.Count.Value}
</Text>
</Column>;
}
`
  );

  const editor = await vscode.window.showTextDocument(document);
  await vscode.commands.executeCommand('editor.action.formatDocument');
  await waitFor(() => editor.document.getText().includes('        Padding={24}'));

  assert.equal(
    editor.document.getText(),
    `public static UiNode Render(CounterState state)
{
    return <Column
        Padding={24}
        OnClick={() => state.Count.Update(value => value + 1)}
    >
        <Text>
            Count: {state.Count.Value}
        </Text>
    </Column>;
}
`
  );
}

async function testSemanticTokens(): Promise<void> {
  const document = await openWorkspaceDocument(
    'SemanticSmoke.csmx',
    `using Csmx.EnagaSignals;

public static class CounterView
{
    public static UiNode Render(CounterState state)
    {
        var label = $"""Count: {state.Count.Value:N2}""";
        Console.WriteLine(label);
        return <Text Color="#111">Count: {state.Count.Value}</Text>;
    }
}
`
  );

  await vscode.window.showTextDocument(document);
  await waitForExtensionActivation();
  const tokens = await waitForSemanticTokens(document.uri);
  assert.ok(tokens.data.length > 0, 'Expected semantic tokens from CSMX extension.');

  const decoded = decodeSemanticTokens(tokens);
  const textPosition = document.positionAt(document.getText().indexOf('Text Color'));
  assert.ok(
    decoded.some(token =>
      token.line === textPosition.line
      && token.character === textPosition.character
      && token.length === 'Text'.length
    ),
    'Expected semantic token covering the CSMX Text element.'
  );
}

async function testOwnedRoslynHover(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder, 'Integration test requires a workspace folder.');

  await fs.writeFile(
    path.join(folder.uri.fsPath, 'HoverSmoke.csproj'),
    `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <CsmxCompileMode>fluent</CsmxCompileMode>
  </PropertyGroup>
</Project>
`,
    'utf8'
  );
  await fs.writeFile(
    path.join(folder.uri.fsPath, 'Runtime.cs'),
    `namespace HoverSmoke;

public abstract class UiNode
{
    public UiNode Content(object? value) => this;
}

public sealed class Column : UiNode;

public sealed class Button : UiNode;

public static class Signals
{
    public static (Signal<T> Value, Action<Func<T, T>> Set) CreateSignal<T>(T value) =>
        (new Signal<T>(value), _ => { });
    public static (Signal<T> Value, Action<Func<T, T>> Set) MakeSignal<T>(T value) =>
        (new Signal<T>(value), _ => { });
}

public sealed class Signal<T>(T value)
{
    public T Value { get; } = value;
}
`,
    'utf8'
  );

  const document = await openWorkspaceDocument(
    'OwnedRoslynHoverSmoke.csmx',
    `using static HoverSmoke.Signals;

namespace HoverSmoke;

public static class CounterView
{
    public static UiNode Render()
    {
        var (count, setCount) = CreateSignal(0);
        return <Column><Button>Count: {count.Value}</Button></Column>;
    }
}
`
  );

  await vscode.window.showTextDocument(document);
  await waitForExtensionActivation();

  const uiNodeHover = await waitForHoverContaining(document, 'UiNode Render', 'HoverSmoke.UiNode');
  assert.ok(uiNodeHover.includes('Kind: `NamedType`'), `Expected Roslyn named type hover. Got: ${uiNodeHover}`);

  const createSignalHover = await waitForHoverContaining(document, 'CreateSignal(0)', 'CreateSignal');
  assert.ok(
    createSignalHover.includes('HoverSmoke.Signals.CreateSignal'),
    `Expected Roslyn method hover. Got: ${createSignalHover}`
  );

  let decoded = decodeSemanticTokens(await waitForSemanticTokens(document.uri));
  let createSignal = document.positionAt(document.getText().indexOf('CreateSignal(0)'));
  assertHasToken(decoded, createSignal, 'CreateSignal'.length, 13, 'project CreateSignal method token');

  const edit = new vscode.WorkspaceEdit();
  edit.replace(
    document.uri,
    new vscode.Range(createSignal, createSignal.translate(0, 'CreateSignal'.length)),
    'MakeSignal'
  );
  assert.ok(await vscode.workspace.applyEdit(edit), 'Expected workspace edit to replace CreateSignal.');

  await waitFor(async () => {
    const tokens = decodeSemanticTokens(await waitForSemanticTokens(document.uri));
    const text = document.getText();
    const makeSignal = document.positionAt(text.indexOf('MakeSignal(0)'));
    return tokens.some(token =>
      token.line === makeSignal.line
      && token.character === makeSignal.character
      && token.length === 'MakeSignal'.length
      && token.typeIndex === 13
    );
  });

  decoded = decodeSemanticTokens(await waitForSemanticTokens(document.uri));
  createSignal = document.positionAt(document.getText().indexOf('MakeSignal(0)'));
  assertHasToken(decoded, createSignal, 'MakeSignal'.length, 13, 'project MakeSignal method token after edit');

  const columnDefinition = await waitFor(async () => {
    const column = document.positionAt(document.getText().indexOf('<Column') + 1);
    const definitions = await vscode.commands.executeCommand<vscode.Location[]>(
      'vscode.executeDefinitionProvider',
      document.uri,
      column
    );
    return definitions?.some(definition =>
      definition.uri.fsPath === path.join(folder.uri.fsPath, 'Runtime.cs')
      && definition.range.start.line === 7
      && definition.range.start.character === 'public sealed class '.length
    )
      ? definitions
      : undefined;
  });
  assert.ok(columnDefinition.length > 0, 'Expected Column tag definition from CSMX Roslyn provider.');

  const completionPosition = document.positionAt(document.getText().indexOf('count.Value') + 'count.'.length);
  await vscode.commands.executeCommand<vscode.CompletionList>(
    'vscode.executeCompletionItemProvider',
    document.uri,
    completionPosition
  );
  assert.equal(
    await pathExists(path.join(folder.uri.fsPath, 'Generated')),
    false,
    'Project-backed completion must not create physical generated files.'
  );
}

async function testNestedExpressionCompletionDelegation(): Promise<void> {
  const delegatedLabels: string[] = [];
  const delegatedPrefixes: string[] = [];
  const provider = vscode.languages.registerCompletionItemProvider(
    { scheme: 'csmx-csharp' },
    {
      provideCompletionItems(document, position) {
        const offset = document.offsetAt(position);
        const prefix = document.getText().slice(Math.max(0, offset - 12), offset);
        delegatedPrefixes.push(prefix);

        const item = new vscode.CompletionItem(
          'SelectFromGeneratedProvider',
          vscode.CompletionItemKind.Method
        );
        item.detail = 'delegated generated C# completion';
        delegatedLabels.push(item.label.toString());
        return [item];
      }
    },
    '.'
  );

  try {
    const document = await openWorkspaceDocument(
      'NestedCompletionSmoke.csmx',
      `using System.Linq;

public static class NestedCompletionHost
{
    public static object Render(string[] items)
    {
        return <Panel>{items.Sel}</Panel>;
    }
}
`
    );

    await vscode.window.showTextDocument(document);
    await waitForExtensionActivation();

    const position = document.positionAt(document.getText().indexOf('items.Sel') + 'items.Sel'.length);
    const completions = await waitFor(async () => {
      const result = await vscode.commands.executeCommand<vscode.CompletionList>(
        'vscode.executeCompletionItemProvider',
        document.uri,
        position
      );
      return result?.items.some(item => item.label === 'SelectFromGeneratedProvider') ? result : undefined;
    });

    assert.ok(
      completions.items.some(item =>
        item.label === 'SelectFromGeneratedProvider'
        && item.detail === 'delegated generated C# completion'
      ),
      'Expected nested expression completion to be delegated through the generated C# document.'
    );
    assert.deepEqual(
      delegatedLabels,
      ['SelectFromGeneratedProvider'],
      'Expected exactly one generated completion delegation.'
    );
    assert.ok(
      delegatedPrefixes.some(prefix => prefix.includes('items.Sel')),
      `Expected completion delegation to include the generated nested expression. Prefixes: ${delegatedPrefixes.join(', ')}`
    );
  } finally {
    provider.dispose();
  }
}

async function testUsingStaticSemanticDelegation(): Promise<void> {
  const generatedSemanticLegend = new vscode.SemanticTokensLegend(['namespace', 'type', 'method'], []);
  let delegatedTokenRequests = 0;
  const provider = vscode.languages.registerDocumentSemanticTokensProvider(
    { scheme: 'csmx-csharp' },
    {
      provideDocumentSemanticTokens(document) {
        const text = document.getText();
        const usingStaticStart = text.indexOf('using static');
        if (usingStaticStart < 0) {
          return new vscode.SemanticTokens(new Uint32Array());
        }

        delegatedTokenRequests++;
        const builder = new vscode.SemanticTokensBuilder(generatedSemanticLegend);
        pushToken(builder, document, text.indexOf('Csmx', usingStaticStart), 'Csmx'.length, 'namespace');
        pushToken(builder, document, text.indexOf('EnagaSignals', usingStaticStart), 'EnagaSignals'.length, 'namespace');
        pushToken(builder, document, text.indexOf('Signals;', usingStaticStart), 'Signals'.length, 'type');
        pushToken(builder, document, text.indexOf('UiNode Render'), 'UiNode'.length, 'type');
        pushToken(builder, document, text.indexOf('CreateSignal'), 'CreateSignal'.length, 'method');
        return builder.build();
      }
    },
    generatedSemanticLegend
  );

  try {
    const document = await openWorkspaceDocument(
      'UsingStaticSemanticSmoke.csmx',
      `using Csmx.EnagaSignals;
using static Csmx.EnagaSignals.Signals;

namespace Csmx.Samples.SignalDashboardApp.Components;

public static class CounterView
{
    public static UiNode Render()
    {
        var (count, setCount) = CreateSignal(0);
        return <Text>Count: {count, 3}</Text>;
    }
}
`
    );

    await vscode.window.showTextDocument(document);
    await waitForExtensionActivation();

    const decoded = decodeSemanticTokens(await waitForSemanticTokens(document.uri));
    const text = document.getText();
    const usingStaticStart = text.indexOf('using static');
    const csmx = document.positionAt(text.indexOf('Csmx', usingStaticStart));
    const enagaSignals = document.positionAt(text.indexOf('EnagaSignals', usingStaticStart));
    const signals = document.positionAt(text.indexOf('Signals;', usingStaticStart));
    const namespaceStart = text.indexOf('namespace');
    const namespaceCsmx = document.positionAt(text.indexOf('Csmx', namespaceStart));
    const namespaceSamples = document.positionAt(text.indexOf('Samples', namespaceStart));
    const namespaceDashboard = document.positionAt(text.indexOf('SignalDashboardApp', namespaceStart));
    const namespaceComponents = document.positionAt(text.indexOf('Components', namespaceStart));
    const uiNode = document.positionAt(text.indexOf('UiNode Render'));
    const createSignal = document.positionAt(text.indexOf('CreateSignal'));

    assert.ok(delegatedTokenRequests > 0, 'Expected generated C# semantic provider to be used.');
    assertHasToken(decoded, csmx, 'Csmx'.length, 0, 'using static Csmx namespace token');
    assertHasToken(decoded, enagaSignals, 'EnagaSignals'.length, 0, 'using static EnagaSignals namespace token');
    assertNoToken(decoded, signals, 'Signals'.length, 0, 'using static Signals namespace token');
    assertNoToken(decoded, csmx, 'Csmx'.length, 1, 'using static Csmx type token');
    assertNoToken(decoded, enagaSignals, 'EnagaSignals'.length, 1, 'using static EnagaSignals type token');
    assertHasToken(decoded, signals, 'Signals'.length, 1, 'using static Signals type token');
    assertNoToken(decoded, csmx, 'Csmx'.length, 8, 'using static Csmx variable token');
    assertNoToken(decoded, enagaSignals, 'EnagaSignals'.length, 8, 'using static EnagaSignals variable token');
    assertNoToken(decoded, signals, 'Signals'.length, 8, 'using static Signals variable token');
    assertNoOverlappingTokens(decoded);
    assertNoToken(decoded, namespaceCsmx, 'Csmx'.length, 0, 'dashboard namespace Csmx token');
    assertNoToken(decoded, namespaceSamples, 'Samples'.length, 0, 'dashboard namespace Samples token');
    assertNoToken(decoded, namespaceDashboard, 'SignalDashboardApp'.length, 0, 'dashboard namespace SignalDashboardApp token');
    assertNoToken(decoded, namespaceComponents, 'Components'.length, 0, 'dashboard namespace Components token');
    assertNoToken(decoded, namespaceCsmx, 'Csmx'.length, 1, 'dashboard namespace Csmx type token');
    assertNoToken(decoded, namespaceSamples, 'Samples'.length, 1, 'dashboard namespace Samples type token');
    assertNoToken(decoded, namespaceDashboard, 'SignalDashboardApp'.length, 1, 'dashboard namespace SignalDashboardApp type token');
    assertNoToken(decoded, namespaceComponents, 'Components'.length, 1, 'dashboard namespace Components type token');
    assertNoToken(decoded, namespaceSamples, 'Samples'.length, 9, 'dashboard namespace Samples property token');
    assertNoToken(decoded, namespaceDashboard, 'SignalDashboardApp'.length, 8, 'dashboard namespace SignalDashboardApp variable token');
    assertHasToken(decoded, uiNode, 'UiNode'.length, 1, 'UiNode delegated type token');
    assertHasToken(decoded, createSignal, 'CreateSignal'.length, 13, 'CreateSignal delegated method token');
  } finally {
    provider.dispose();
  }
}

function pushToken(
  builder: vscode.SemanticTokensBuilder,
  document: vscode.TextDocument,
  start: number,
  length: number,
  tokenType: string
): void {
  assert.ok(start >= 0, `Expected generated document to contain token '${tokenType}'.`);
  const tokenStart = document.positionAt(start);
  const tokenEnd = document.positionAt(start + length);
  builder.push(new vscode.Range(tokenStart, tokenEnd), tokenType, []);
}

async function openWorkspaceDocument(fileName: string, content: string): Promise<vscode.TextDocument> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder, 'Integration test requires a workspace folder.');

  const filePath = path.join(folder.uri.fsPath, fileName);
  await fs.writeFile(filePath, content, 'utf8');
  return await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
}

async function pathExists(filePath: string): Promise<boolean> {
  try {
    await fs.stat(filePath);
    return true;
  } catch {
    return false;
  }
}

async function waitForExtensionActivation(): Promise<void> {
  const extension = vscode.extensions.getExtension('local.csmx-vscode');
  assert.ok(extension, 'CSMX extension was not found in the extension host.');
  if (!extension.isActive) {
    await extension.activate();
  }
}

async function waitForSemanticTokens(uri: vscode.Uri): Promise<vscode.SemanticTokens> {
  return await waitFor(async () => {
    const tokens = await vscode.commands.executeCommand<vscode.SemanticTokens>(
      'vscode.provideDocumentSemanticTokens',
      uri
    );
    return tokens && tokens.data.length > 0 ? tokens : undefined;
  });
}

async function waitForHoverContaining(
  document: vscode.TextDocument,
  text: string,
  expected: string
): Promise<string> {
  const offset = document.getText().indexOf(text);
  assert.ok(offset >= 0, `Document should contain '${text}'.`);
  return await waitFor(async () => {
    const hovers = await vscode.commands.executeCommand<vscode.Hover[]>(
      'vscode.executeHoverProvider',
      document.uri,
      document.positionAt(offset)
    );
    const markdown = hovers
      ?.flatMap(hover => hover.contents)
      .map(content => {
        if (content instanceof vscode.MarkdownString) {
          return content.value;
        }

        return typeof content === 'string' ? content : content.value;
      })
      .join('\n\n');
    return markdown?.includes(expected) ? markdown : undefined;
  });
}

async function waitFor<T>(
  action: () => T | undefined | false | Promise<T | undefined | false>,
  timeoutMs = 10000
): Promise<T> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    const value = await action();
    if (value) {
      return value;
    }

    await new Promise(resolve => setTimeout(resolve, 100));
  }

  throw new Error('Timed out waiting for integration test condition.');
}

type DecodedSemanticToken = {
  line: number;
  character: number;
  length: number;
  typeIndex: number;
};

function decodeSemanticTokens(tokens: vscode.SemanticTokens): DecodedSemanticToken[] {
  const decoded: DecodedSemanticToken[] = [];
  let line = 0;
  let character = 0;
  for (let index = 0; index + 4 < tokens.data.length; index += 5) {
    line += tokens.data[index];
    character = tokens.data[index] === 0 ? character + tokens.data[index + 1] : tokens.data[index + 1];
    decoded.push({
      line,
      character,
      length: tokens.data[index + 2],
      typeIndex: tokens.data[index + 3]
    });
  }

  return decoded;
}

function assertHasToken(
  tokens: DecodedSemanticToken[],
  position: vscode.Position,
  length: number,
  typeIndex: number,
  label: string
): void {
  assert.ok(
    tokens.some(token =>
      token.line === position.line
      && token.character === position.character
      && token.length === length
      && token.typeIndex === typeIndex
    ),
    `${label} missing at ${position.line}:${position.character} length ${length} type ${typeIndex}.`
  );
}

function assertNoToken(
  tokens: DecodedSemanticToken[],
  position: vscode.Position,
  length: number,
  typeIndex: number,
  label: string
): void {
  assert.ok(
    !tokens.some(token =>
      token.line === position.line
      && token.character === position.character
      && token.length === length
      && token.typeIndex === typeIndex
    ),
    `${label} should not exist at ${position.line}:${position.character} length ${length} type ${typeIndex}.`
  );
}

function assertNoOverlappingTokens(tokens: DecodedSemanticToken[]): void {
  const sorted = [...tokens].sort((left, right) =>
    left.line - right.line || left.character - right.character || left.length - right.length
  );
  for (let index = 1; index < sorted.length; index++) {
    const previous = sorted[index - 1];
    const current = sorted[index];
    if (previous.line !== current.line) {
      continue;
    }

    assert.ok(
      previous.character + previous.length <= current.character,
      `Semantic tokens overlap on line ${current.line}: ${previous.character}+${previous.length} and ${current.character}+${current.length}.`
    );
  }
}
