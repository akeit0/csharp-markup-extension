"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.run = run;
const assert = require("assert/strict");
const fs = require("fs/promises");
const path = require("path");
const vscode = require("vscode");
async function run() {
    await testDocumentFormatting();
    await testSemanticTokens();
}
async function testDocumentFormatting() {
    const document = await openWorkspaceDocument('FormattingSmoke.csmx', `public static UiNode Render(CounterState state)
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
`);
    const editor = await vscode.window.showTextDocument(document);
    await vscode.commands.executeCommand('editor.action.formatDocument');
    await waitFor(() => editor.document.getText().includes('        Padding={24}'));
    assert.equal(editor.document.getText(), `public static UiNode Render(CounterState state)
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
`);
}
async function testSemanticTokens() {
    const document = await openWorkspaceDocument('SemanticSmoke.csmx', `using Csmx.EnagaSignals;

public static class CounterView
{
    public static UiNode Render(CounterState state)
    {
        var label = $"""Count: {state.Count.Value:N2}""";
        Console.WriteLine(label);
        return <Text Color="#111">Count: {state.Count.Value}</Text>;
    }
}
`);
    await vscode.window.showTextDocument(document);
    await waitForExtensionActivation();
    const tokens = await waitForSemanticTokens(document.uri);
    assert.ok(tokens.data.length > 0, 'Expected semantic tokens from CSMX extension.');
    const decoded = decodeSemanticTokens(tokens);
    const textPosition = document.positionAt(document.getText().indexOf('Text Color'));
    assert.ok(decoded.some(token => token.line === textPosition.line
        && token.character === textPosition.character
        && token.length === 'Text'.length), 'Expected semantic token covering the CSMX Text element.');
}
async function openWorkspaceDocument(fileName, content) {
    const folder = vscode.workspace.workspaceFolders?.[0];
    assert.ok(folder, 'Integration test requires a workspace folder.');
    const filePath = path.join(folder.uri.fsPath, fileName);
    await fs.writeFile(filePath, content, 'utf8');
    return await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
}
async function waitForExtensionActivation() {
    const extension = vscode.extensions.getExtension('local.csmx-vscode');
    assert.ok(extension, 'CSMX extension was not found in the extension host.');
    if (!extension.isActive) {
        await extension.activate();
    }
}
async function waitForSemanticTokens(uri) {
    return await waitFor(async () => {
        const tokens = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', uri);
        return tokens && tokens.data.length > 0 ? tokens : undefined;
    });
}
async function waitFor(action, timeoutMs = 10000) {
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
function decodeSemanticTokens(tokens) {
    const decoded = [];
    let line = 0;
    let character = 0;
    for (let index = 0; index + 4 < tokens.data.length; index += 5) {
        line += tokens.data[index];
        character = tokens.data[index] === 0 ? character + tokens.data[index + 1] : tokens.data[index + 1];
        decoded.push({
            line,
            character,
            length: tokens.data[index + 2]
        });
    }
    return decoded;
}
//# sourceMappingURL=index.js.map