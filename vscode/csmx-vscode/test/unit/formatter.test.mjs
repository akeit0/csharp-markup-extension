import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { test } from 'node:test';
import Module from 'node:module';

const require = createRequire(import.meta.url);
const originalLoad = Module._load;

Module._load = function load(request, parent, isMain) {
  if (request === 'vscode') {
    return {
      workspace: {
        getConfiguration() {
          return {
            get() {
              return undefined;
            }
          };
        }
      },
      Uri: {
        file(fsPath) {
          return { fsPath };
        }
      }
    };
  }

  return originalLoad.call(this, request, parent, isMain);
};

const { formatCsjsxText } = require('../../out/formatter.js');

const formatterOptions = { indent: '    ' };

test('formats mixed C# blocks and CSMX elements', () => {
  const input = `namespace Csmx.Samples.FluentApp.Components;

public static class FluentCounter
{
public static Panel Render(int size) =>
<Panel>
<Button Size={size}>Click</Button>
</Panel>;
}

public sealed class Panel
{
private readonly List<object> children = [];

public Panel Content(object value)
{
children.Add(value);
return this;
}

public override string ToString() => $"Panel({string.Join(", ", children)})";
}
`;

  const expected = `namespace Csmx.Samples.FluentApp.Components;

public static class FluentCounter
{
    public static Panel Render(int size) =>
        <Panel>
            <Button Size={size}>Click</Button>
        </Panel>;
}

public sealed class Panel
{
    private readonly List<object> children = [];

    public Panel Content(object value)
    {
        children.Add(value);
        return this;
    }

    public override string ToString() => $"Panel({string.Join(", ", children)})";
}
`;

  assert.equal(formatCsjsxText(input, formatterOptions), expected);
});

test('formats multiline CSMX attributes and indents child text', () => {
  const input = `public static UiNode Render(CounterState state)
{
return <Column
Padding={24}
Gap={14}
OnClick={() => state.Count.Update(value => value + 1)}
>
<Text>
Count: {state.Count.Value}
</Text>
</Column>;
}
`;

  const expected = `public static UiNode Render(CounterState state)
{
    return <Column
        Padding={24}
        Gap={14}
        OnClick={() => state.Count.Update(value => value + 1)}
    >
        <Text>
            Count: {state.Count.Value}
        </Text>
    </Column>;
}
`;

  assert.equal(formatCsjsxText(input, formatterOptions), expected);
});

test('formats chained fluent calls without changing structural depth', () => {
  const input = `public static Panel Render(int size)
{
return new Panel()
.Content(
<Button Size={size}>Click</Button>
)
.Content("Done");
}
`;

  const expected = `public static Panel Render(int size)
{
    return new Panel()
        .Content(
            <Button Size={size}>Click</Button>
        )
        .Content("Done");
}
`;

  assert.equal(formatCsjsxText(input, formatterOptions), expected);
});

test('keeps lambda attributes balanced inside tags', () => {
  const input = `public static UiNode Render(CounterState state)
{
return <Button
Width={88}
OnClick={() => state.Count.Update(value => value + 1)}
>+1</Button>;
}
`;

  const expected = `public static UiNode Render(CounterState state)
{
    return <Button
        Width={88}
        OnClick={() => state.Count.Update(value => value + 1)}
    >+1</Button>;
}
`;

  assert.equal(formatCsjsxText(input, formatterOptions), expected);
});
