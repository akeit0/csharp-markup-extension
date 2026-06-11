import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const oniguruma = require('vscode-oniguruma');
const textmate = require('vscode-textmate');

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const grammarPath = path.join(root, 'syntaxes', 'csmx.tmLanguage.json');
const wasmPath = path.join(root, 'node_modules', 'vscode-oniguruma', 'release', 'onig.wasm');

const sample = `using Csmx.SampleRuntime;

public static class FactoryCounter
{
    public static VNode Render(string name, int count)
    {
        var b = 2 < 3;
        var c = 2 <= 3;
        Func<CounterViewProps, VNode[], VNode> View = (props, children) =>
            <panel class="factory-counter">
                <label>Factory Hello {props.Name}</label>
                <label> Count: {props.Count}</label>
                <action disabled={props.Count == 0}>Run</action>
            </panel>;
    }

    public static UiNode Render(CounterState state)
    {
        Console.WriteLine($"Render CounterView");
        return <Column Padding={24} Gap={14} Background="#f5f7fb">
            <Text FontSize={28} FontWeight={700} Color="#152033">CSMX Signals</Text>
            <Text FontSize={18} Color="#44546a">Count: {state.Count.Value}</Text>
        </Column>;
    }
}`;

test('tokenizes CSMX without treating C# operators as tags', async () => {
  const tokenizedLines = tokenize(sample, await loadGrammar());

  assertTokenTextHasScope(tokenizedLines, 9, '=>', 'keyword.operator.lambda.csharp.csmx');
  assertSubstringLacksScope(tokenizedLines, 7, '<', 'punctuation.definition.tag.begin.csmx');
  assertSubstringLacksScope(tokenizedLines, 8, '<=', 'punctuation.definition.tag.begin.csmx');
  assertSubstringLacksScope(tokenizedLines, 5, 'Render', 'entity.name.type');
  assertSubstringLacksScope(tokenizedLines, 9, 'props', 'entity.name.type');
});

test('tokenizes CSMX tags, attributes, and text distinctly', async () => {
  const tokenizedLines = tokenize(sample, await loadGrammar());

  assertTokenTextHasScope(tokenizedLines, 10, 'panel', 'entity.name.tag.csmx');
  assertTokenTextHasScope(tokenizedLines, 11, 'label', 'entity.name.tag.csmx');
  assertTokenTextHasScope(tokenizedLines, 11, 'label', 'entity.name.tag.csmx', { occurrence: 2 });
  assertTokenTextHasScope(tokenizedLines, 10, 'class', 'entity.other.attribute-name.csmx');
  assertSubstringLacksScope(tokenizedLines, 11, 'Factory', 'entity.name.type');
  assertSubstringLacksScope(tokenizedLines, 13, 'Run', 'entity.name.type');
  assertTokenTextHasScope(tokenizedLines, 20, 'Column', 'entity.name.tag.csmx');
  assertTokenTextHasScope(tokenizedLines, 20, 'Padding', 'entity.other.attribute-name.csmx');
  assertTokenTextHasScope(tokenizedLines, 20, 'Gap', 'entity.other.attribute-name.csmx');
  assertTokenTextHasScope(tokenizedLines, 20, 'Background', 'entity.other.attribute-name.csmx');
  assertTokenTextHasScope(tokenizedLines, 21, 'Text', 'entity.name.tag.csmx');
  assertTokenTextHasScope(tokenizedLines, 21, 'FontSize', 'entity.other.attribute-name.csmx');
  assertTokenTextHasScope(tokenizedLines, 21, 'FontWeight', 'entity.other.attribute-name.csmx');
  assertTokenTextHasScope(tokenizedLines, 21, 'Color', 'entity.other.attribute-name.csmx');
  assertSubstringLacksScope(tokenizedLines, 21, 'CSMX Signals', 'entity.name.type');
});

async function loadGrammar() {
  const wasm = await fs.readFile(wasmPath);
  await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));

  const registry = new textmate.Registry({
    onigLib: Promise.resolve({
      createOnigScanner(patterns) {
        return new oniguruma.OnigScanner(patterns);
      },
      createOnigString(value) {
        return new oniguruma.OnigString(value);
      }
    }),
    loadGrammar: async scopeName => {
      if (scopeName !== 'source.csmx') {
        return null;
      }

      return textmate.parseRawGrammar(await fs.readFile(grammarPath, 'utf8'), grammarPath);
    }
  });

  const grammar = await registry.loadGrammar('source.csmx');
  assert.ok(grammar, 'Failed to load source.csmx grammar.');
  return grammar;
}

function tokenize(text, grammar) {
  let ruleStack = null;
  return text.split(/\r?\n/).map(line => {
    const result = grammar.tokenizeLine(line, ruleStack);
    ruleStack = result.ruleStack;
    return {
      line,
      tokens: result.tokens.map(token => ({
        startIndex: token.startIndex,
        endIndex: token.endIndex,
        text: line.slice(token.startIndex, token.endIndex),
        scopes: token.scopes
      }))
    };
  });
}

function assertTokenTextHasScope(tokenizedLines, lineNumber, tokenText, expectedScope, options = {}) {
  const token = findToken(tokenizedLines, lineNumber, tokenText, options.occurrence ?? 1);
  assert.ok(
    token.scopes.some(scope => scope.includes(expectedScope)),
    `Expected '${tokenText}' on line ${lineNumber} to have scope '${expectedScope}'. Got: ${token.scopes.join(', ')}`
  );
}

function assertSubstringLacksScope(tokenizedLines, lineNumber, value, forbiddenScope) {
  const token = findTokenContaining(tokenizedLines, lineNumber, value);
  assert.ok(
    !token.scopes.some(scope => scope.includes(forbiddenScope)),
    `Expected '${value}' on line ${lineNumber} to lack scope '${forbiddenScope}'. Got token '${token.text}' scopes: ${token.scopes.join(', ')}`
  );
}

function findToken(tokenizedLines, lineNumber, tokenText, occurrence = 1) {
  const line = tokenizedLines[lineNumber - 1];
  assert.ok(line, `Missing line ${lineNumber}.`);

  let seen = 0;
  for (const token of line.tokens) {
    if (token.text === tokenText) {
      seen++;
      if (seen === occurrence) {
        return token;
      }
    }
  }

  assert.fail(
    `Missing token '${tokenText}' occurrence ${occurrence} on line ${lineNumber}. Tokens: ${line.tokens
      .map(token => `'${token.text}' [${token.scopes.join(', ')}]`)
      .join('; ')}`
  );
}

function findTokenContaining(tokenizedLines, lineNumber, value) {
  const line = tokenizedLines[lineNumber - 1];
  assert.ok(line, `Missing line ${lineNumber}.`);

  const token = line.tokens.find(candidate => candidate.text.includes(value));
  assert.ok(
    token,
    `Missing token containing '${value}' on line ${lineNumber}. Tokens: ${line.tokens
      .map(candidate => `'${candidate.text}' [${candidate.scopes.join(', ')}]`)
      .join('; ')}`
  );
  return token;
}
