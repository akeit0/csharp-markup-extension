import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';
import * as oniguruma from 'vscode-oniguruma';
import {
  DocumentSemanticsTokensSignature,
  LanguageClient,
  LanguageClientOptions,
  ProvideCompletionItemsSignature,
  ProvideHoverSignature,
  ServerOptions
} from 'vscode-languageclient/node';
import * as textmate from 'vscode-textmate';
import { provideCsjsxFormattingEdits } from './formatter';

let client: LanguageClient | undefined;
let textMateGrammar: Promise<textmate.IGrammar | null> | undefined;
let tokenInspectionOutput: vscode.OutputChannel | undefined;
let projectBindingOutput: vscode.OutputChannel | undefined;
let scratchDelegationOutput: vscode.OutputChannel | undefined;

type GeneratedCSharpResponse = {
  code: string;
  projectFilePath?: string | null;
  generatedFilePath?: string | null;
  projectContextDependencies?: ProjectContextDependencyResponse[];
  mappings?: SourceMapEntryResponse[];
};

type ProjectContextDependencyResponse = {
  path?: string | null;
  exists?: boolean | null;
  lastWriteUtc?: string | null;
  lastWriteUtcMilliseconds?: number | null;
};

type SourceMapEntryResponse = {
  originalStart: number;
  originalLength: number;
  generatedStart: number;
  generatedLength: number;
  kind: string;
};

type ProjectBindingResponse = {
  uri?: string;
  sourceFilePath?: string | null;
  hasProject?: boolean;
  projectFilePath?: string | null;
  projectDirectory?: string | null;
  relativeSourcePath?: string | null;
  evaluationKind?: string | null;
  requestedConfiguration?: string | null;
  requestedTargetFramework?: string | null;
  configuration?: string | null;
  targetFramework?: string | null;
  generatedDirectory?: string | null;
  generatedFilePath?: string | null;
  generatedFileExists?: boolean | null;
  compileIncludesGeneratedFile?: boolean | null;
  compileItemCount?: number | null;
  projectContextDependencies?: ProjectContextDependencyResponse[] | null;
  transform?: {
    compileMode?: string | null;
    elementFactory?: string | null;
    attributeFactory?: string | null;
    textFactory?: string | null;
    childrenFactory?: string | null;
    componentLowering?: string | null;
  } | null;
  messages?: string[] | null;
};

type ReloadProjectContextResponse = {
  uri?: string;
  sourceFilePath?: string | null;
  reloaded?: boolean;
  documentWasOpen?: boolean;
  clearedEntries?: number;
  requestedConfiguration?: string | null;
  requestedTargetFramework?: string | null;
  messages?: string[] | null;
};

type ProjectContextOptionsRequest = {
  configuration?: string | null;
  targetFramework?: string | null;
};

type ProjectContextOptionsResponse = {
  configuration?: string | null;
  targetFramework?: string | null;
  changed?: boolean;
  clearedEntries?: number;
  refreshedDocuments?: number;
};

const csharpDelegationMappingKinds = new Set([
  'CSharp',
  'ChildExpression',
  'AttributeExpression',
  'ComponentReference',
  'ElementReference',
  'AttributeReference'
]);

const generatedScheme = 'csmx-csharp';

const semanticTokenTypes = [
  'namespace',
  'type',
  'class',
  'enum',
  'interface',
  'struct',
  'typeParameter',
  'parameter',
  'variable',
  'property',
  'enumMember',
  'event',
  'function',
  'method',
  'macro',
  'keyword',
  'modifier',
  'comment',
  'string',
  'number',
  'regexp',
  'operator',
  'decorator'
];

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const generatedDocuments = new GeneratedCSharpDocumentProvider();

  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider(generatedScheme, generatedDocuments)
  );

  const configuredServerDirectory = vscode.workspace
    .getConfiguration('csmx')
    .get<string>('server.directory');

  const serverDirectory = configuredServerDirectory && configuredServerDirectory.length > 0
    ? configuredServerDirectory
    : path.join(context.extensionPath, 'server');
  const serverDll = path.join(serverDirectory, 'Csmx.LanguageServer.dll');
  const serverDeps = path.join(serverDirectory, 'Csmx.LanguageServer.deps.json');

  if (!await fileExists(serverDll) || !await fileExists(serverDeps)) {
    void vscode.window.showErrorMessage(`CSMX language server publish output was not found in '${serverDirectory}'.`);
    return;
  }

  const serverOptions: ServerOptions = {
    command: 'dotnet',
    args: [serverDll],
    options: {
      cwd: context.extensionPath
    }
  };

  const scratchDelegation = new ScratchCSharpDelegation(generatedDocuments);

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: 'file', language: 'csmx' }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher('**/*.csmx')
    },
    middleware: {
      provideCompletionItem: async (document, position, completionContext, token, next) => {
        const csmxItems = await next(document, position, completionContext, token);
        if (
          hasCompletionItems(csmxItems)
          || isInsideOpeningTag(document.getText(), document.offsetAt(position))
        ) {
          return csmxItems;
        }

        return await scratchDelegation.provideCompletionItems(document, position, completionContext, token, next);
      },
      provideHover: async (document, position, token, next) => {
        const csmxHover = await next(document, position, token);
        if (csmxHover) {
          return csmxHover;
        }

        if (await hasContainingProject(document.uri)) {
          return undefined;
        }

        return await scratchDelegation.provideHover(document, position, token, next);
      },
      provideDocumentSemanticTokens: async (document, token, next) => {
        return await scratchDelegation.provideSemanticTokens(document, token, next);
      }
    }
  };

  client = new LanguageClient(
    'csmxLanguageServer',
    'C# JSX Language Server',
    serverOptions,
    clientOptions
  );

  await client.start();
  await setProjectContextOptions(scratchDelegation);

  context.subscriptions.push(
    vscode.languages.registerDefinitionProvider(
      { scheme: 'file', language: 'csmx' },
      {
        provideDefinition: async (document, position, token) => {
          if (isInsideOpeningTag(document.getText(), document.offsetAt(position))) {
            return undefined;
          }

          if (await hasContainingProject(document.uri)) {
            return undefined;
          }

          return await scratchDelegation.provideDefinition(document, position, token);
        }
      }
    ),
    vscode.languages.registerDocumentFormattingEditProvider(
      { scheme: 'file', language: 'csmx' },
      {
        provideDocumentFormattingEdits: (document, options) =>
          provideCsjsxFormattingEdits(document, options)
      }
    ),
    vscode.commands.registerCommand('csmx.showGeneratedCSharp', async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.document.languageId !== 'csmx') {
        void vscode.window.showWarningMessage('Open a .csmx document first.');
        return;
      }

      if (!client) {
        void vscode.window.showErrorMessage('CSMX language server is not running.');
        return;
      }

      const generated = await getGeneratedCSharp(editor.document);
      const generatedDocument = await vscode.workspace.openTextDocument({
        language: 'csharp',
        content: generated.code
      });

      await vscode.window.showTextDocument(generatedDocument, { preview: true });
    }),
    vscode.commands.registerCommand('csmx.inspectProjectBinding', async () => {
      await inspectProjectBinding(context);
    }),
    vscode.commands.registerCommand('csmx.reloadProjectContext', async () => {
      await reloadProjectContext(scratchDelegation);
    }),
    vscode.workspace.onDidCloseTextDocument(document => {
      if (document.languageId === 'csmx') {
        scratchDelegation.invalidate(document);
      }
    }),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (
        event.affectsConfiguration('csmx.project.configuration')
        || event.affectsConfiguration('csmx.project.targetFramework')
      ) {
        void setProjectContextOptions(scratchDelegation);
      }
    }),
    vscode.commands.registerCommand('csmx.inspectTokenAtCursor', async () => {
      await inspectTokenAtCursor(context);
    }),
    {
      dispose: () => {
        void client?.stop();
      }
    }
  );
}

export async function deactivate(): Promise<void> {
  await client?.stop();
}

class GeneratedCSharpDocumentProvider implements vscode.TextDocumentContentProvider {
  private readonly contents = new Map<string, string>();
  private readonly virtualToOriginal = new Map<string, vscode.Uri>();
  private readonly onDidChangeEmitter = new vscode.EventEmitter<vscode.Uri>();

  readonly onDidChange = this.onDidChangeEmitter.event;

  provideTextDocumentContent(uri: vscode.Uri): string {
    const originalUri = this.virtualToOriginal.get(uri.toString());
    if (!originalUri) {
      return '';
    }

    return this.contents.get(originalUri.toString()) ?? '';
  }

  set(
    document: vscode.TextDocument,
    code: string,
    mappings: SourceMapEntryResponse[]
  ): GeneratedCSharpDocument {
    const virtualUri = this.toVirtualUri(document.uri);
    this.contents.set(document.uri.toString(), code);
    this.virtualToOriginal.set(virtualUri.toString(), document.uri);
    this.onDidChangeEmitter.fire(virtualUri);
    return { uri: virtualUri, mappings, projectContextDependencies: [] };
  }

  toVirtualUri(originalUri: vscode.Uri): vscode.Uri {
    return vscode.Uri.parse(`${generatedScheme}://generated/${encodeURIComponent(originalUri.toString())}.g.cs`);
  }

  getOriginalUri(virtualUri: vscode.Uri): vscode.Uri | undefined {
    return this.virtualToOriginal.get(virtualUri.toString());
  }
}

type GeneratedCSharpDocument = {
  uri: vscode.Uri;
  mappings: SourceMapEntryResponse[];
  projectContextDependencies: ProjectContextDependencyResponse[];
  textDocument?: vscode.TextDocument;
};

class ScratchCSharpDelegation {
  constructor(private readonly generatedDocuments: GeneratedCSharpDocumentProvider) {}

  private readonly reportedIssues = new Set<string>();
  private readonly generatedCache = new Map<string, ScratchGeneratedCSharpCacheEntry>();

  invalidate(document: vscode.TextDocument): void {
    this.generatedCache.delete(document.uri.toString());
  }

  invalidateAll(): void {
    this.generatedCache.clear();
  }

  async provideCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    completionContext: vscode.CompletionContext,
    token: vscode.CancellationToken,
    next: ProvideCompletionItemsSignature
  ): Promise<vscode.ProviderResult<vscode.CompletionItem[] | vscode.CompletionList>> {
    if (token.isCancellationRequested) {
      return await next(document, position, completionContext, token);
    }

    const generated = await this.prepareGeneratedDocument(document, token, true);
    if (!generated) {
      return await next(document, position, completionContext, token);
    }

    const generatedPosition = toGeneratedCompletionPosition(document, generated, position);
    if (!generatedPosition) {
      return await next(document, position, completionContext, token);
    }

    let completionList: vscode.CompletionList | undefined;
    try {
      completionList = await vscode.commands.executeCommand<vscode.CompletionList>(
        'vscode.executeCompletionItemProvider',
        generated.uri,
        generatedPosition,
        completionContext.triggerCharacter
      );
    } catch (error) {
      this.logIssue(document, 'completion-command', `Completion delegation failed: ${formatError(error)}`);
      return await next(document, position, completionContext, token);
    }

    const translated = translateCompletionList(completionList, document, generated);
    return hasCompletionItems(translated) ? translated : await next(document, position, completionContext, token);
  }

  async provideHover(
    document: vscode.TextDocument,
    position: vscode.Position,
    token: vscode.CancellationToken,
    next: ProvideHoverSignature
  ): Promise<vscode.ProviderResult<vscode.Hover>> {
    if (!(await this.canUseScratchDelegation(document, token))) {
      return await next(document, position, token);
    }

    const generated = await this.prepareGeneratedDocument(document, token);
    if (!generated) {
      return await next(document, position, token);
    }

    const generatedPosition = toGeneratedPosition(document, generated, position);
    if (!generatedPosition) {
      return await next(document, position, token);
    }

    let hovers: vscode.Hover[] | undefined;
    try {
      hovers = await vscode.commands.executeCommand<vscode.Hover[]>(
        'vscode.executeHoverProvider',
        generated.uri,
        generatedPosition
      );
    } catch (error) {
      this.logIssue(document, 'hover-command', `Hover delegation failed: ${formatError(error)}`);
      return await next(document, position, token);
    }

    const hover = hovers?.[0];
    if (!hover) {
      return await next(document, position, token);
    }

    return new vscode.Hover(hover.contents, translateRange(hover.range, document, generated));
  }

  async provideDefinition(
    document: vscode.TextDocument,
    position: vscode.Position,
    token: vscode.CancellationToken
  ): Promise<vscode.ProviderResult<vscode.Definition>> {
    if (!(await this.canUseScratchDelegation(document, token))) {
      return undefined;
    }

    const generated = await this.prepareGeneratedDocument(document, token);
    if (!generated) {
      return undefined;
    }

    const generatedPosition = toGeneratedPosition(document, generated, position);
    if (!generatedPosition) {
      return undefined;
    }

    let definitions: vscode.Location[] | undefined;
    try {
      definitions = await vscode.commands.executeCommand<vscode.Location[]>(
        'vscode.executeDefinitionProvider',
        generated.uri,
        generatedPosition
      );
    } catch (error) {
      this.logIssue(document, 'definition-command', `Definition delegation failed: ${formatError(error)}`);
      return undefined;
    }

    return definitions
      ?.map(definition => translateDefinition(definition, generated, document))
      .filter((definition): definition is vscode.Location => definition !== undefined);
  }

  async provideSemanticTokens(
    document: vscode.TextDocument,
    token: vscode.CancellationToken,
    next: DocumentSemanticsTokensSignature
  ): Promise<vscode.ProviderResult<vscode.SemanticTokens>> {
    const csmxTokens = await next(document, token);
    if (token.isCancellationRequested) {
      return csmxTokens;
    }

    if (!(await this.canUseScratchDelegation(document, token))) {
      return csmxTokens;
    }

    const generated = await this.prepareGeneratedDocument(document, token);
    if (!generated || !generated.textDocument || token.isCancellationRequested) {
      if (!generated) {
        this.logIssue(document, 'semantic-no-generated', 'Semantic token delegation skipped because no generated C# document was available.');
      }
      return csmxTokens;
    }

    const generatedTokens = await getGeneratedSemanticTokens(generated.uri, error => {
      this.logIssue(document, 'semantic-command', `Semantic token delegation failed: ${formatError(error)}`);
    });
    if (!generatedTokens) {
      return csmxTokens;
    }

    const mapping = createDocumentMapping(document, generated);
    if (!mapping) {
      this.logIssue(document, 'semantic-no-mapping', 'Semantic token delegation skipped because no generated mapping was available.');
      return csmxTokens;
    }

    return mergeSemanticTokens(
      decodeSemanticTokens(csmxTokens),
      translateGeneratedSemanticTokens(generatedTokens, generated.textDocument, mapping)
    );
  }

  private async prepareGeneratedDocument(
    document: vscode.TextDocument,
    token: vscode.CancellationToken,
    allowProjectDocuments = false
  ): Promise<GeneratedCSharpDocument | undefined> {
    if (!(await this.canUseScratchDelegation(document, token, allowProjectDocuments))) {
      return undefined;
    }

    if (!client || token.isCancellationRequested) {
      if (!client) {
        this.logIssue(document, 'client-missing', 'CSMX language client is not available.');
      }
      return undefined;
    }

    const cacheKey = document.uri.toString();
    const cached = this.generatedCache.get(cacheKey);
    if (cached?.version === document.version && await areProjectContextDependenciesCurrent(cached.projectContextDependencies)) {
      const generatedDocument = await cached.document;
      return token.isCancellationRequested ? undefined : generatedDocument;
    }

    const generatedDocument = this.createGeneratedDocument(document);
    this.generatedCache.set(cacheKey, {
      version: document.version,
      projectContextDependencies: [],
      document: generatedDocument
    });

    const prepared = await generatedDocument;
    const current = this.generatedCache.get(cacheKey);
    if (prepared && current?.document === generatedDocument) {
      this.generatedCache.set(cacheKey, {
        version: document.version,
        projectContextDependencies: prepared.projectContextDependencies,
        document: generatedDocument
      });
    }
    if (!prepared && this.generatedCache.get(cacheKey)?.document === generatedDocument) {
      this.generatedCache.delete(cacheKey);
    }

    return token.isCancellationRequested ? undefined : prepared;
  }

  private async createGeneratedDocument(
    document: vscode.TextDocument
  ): Promise<GeneratedCSharpDocument | undefined> {
    const generated = await tryGetGeneratedCSharp(document);
    if (!generated) {
      this.logIssue(document, 'generated-request', 'Generated C# request returned no result.');
      return undefined;
    }

    const mappings = generated.mappings ?? [];
    const projectContextDependencies = generated.projectContextDependencies ?? [];
    const virtualDocument = this.generatedDocuments.set(document, generated.code, mappings);
    virtualDocument.projectContextDependencies = projectContextDependencies;
    virtualDocument.textDocument = await vscode.workspace.openTextDocument(virtualDocument.uri);
    return virtualDocument;
  }

  private logIssue(document: vscode.TextDocument, code: string, message: string): void {
    const key = `${document.uri.toString()}:${code}`;
    if (this.reportedIssues.has(key)) {
      return;
    }

    this.reportedIssues.add(key);
    logScratchDelegationIssue(`${document.uri.toString()} - ${message}`);
  }

  private async canUseScratchDelegation(
    document: vscode.TextDocument,
    token: vscode.CancellationToken,
    allowProjectDocuments = false
  ): Promise<boolean> {
    return (
      !token.isCancellationRequested
      && (allowProjectDocuments || !(await hasContainingProject(document.uri)))
    );
  }
}

type ScratchGeneratedCSharpCacheEntry = {
  version: number;
  projectContextDependencies: ProjectContextDependencyResponse[];
  document: Promise<GeneratedCSharpDocument | undefined>;
};

async function areProjectContextDependenciesCurrent(
  dependencies: ProjectContextDependencyResponse[]
): Promise<boolean> {
  for (const dependency of dependencies) {
    const filePath = dependency.path;
    if (!filePath) {
      continue;
    }

    let stat: { mtime: Date } | undefined;
    try {
      stat = await fs.stat(filePath);
    } catch {
      stat = undefined;
    }

    const exists = stat !== undefined;
    if (exists !== Boolean(dependency.exists)) {
      return false;
    }

    if (!stat) {
      continue;
    }

    const expected = dependency.lastWriteUtcMilliseconds;
    if (expected === null || expected === undefined) {
      return false;
    }

    if (Math.abs(stat.mtime.getTime() - expected) > 1) {
      return false;
    }
  }

  return true;
}

function readProjectContextOptions(): ProjectContextOptionsRequest {
  const configuration = normalizeProjectContextSetting(
    vscode.workspace.getConfiguration('csmx').get<string>('project.configuration')
  );
  const targetFramework = normalizeProjectContextSetting(
    vscode.workspace.getConfiguration('csmx').get<string>('project.targetFramework')
  );

  return { configuration, targetFramework };
}

function normalizeProjectContextSetting(value: string | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed && trimmed.length > 0 ? trimmed : null;
}

async function setProjectContextOptions(
  scratchDelegation: ScratchCSharpDelegation
): Promise<void> {
  if (!client) {
    return;
  }

  const options = readProjectContextOptions();
  try {
    const response = await client.sendRequest<ProjectContextOptionsResponse>(
      'csmx/setProjectContextOptions',
      options
    );
    if (response.changed) {
      scratchDelegation.invalidateAll();
      logScratchDelegationIssue(
        `Project context options changed: Configuration=${response.configuration ?? '<project default>'}, TargetFramework=${response.targetFramework ?? '<project default>'}, refreshed=${response.refreshedDocuments ?? 0}, cleared=${response.clearedEntries ?? 0}.`
      );
    }
  } catch (error) {
    logScratchDelegationIssue(`csmx/setProjectContextOptions failed: ${formatError(error)}`);
  }
}

async function getGeneratedCSharp(document: vscode.TextDocument): Promise<GeneratedCSharpResponse> {
  if (!client) {
    throw new Error('CSMX language client is not available.');
  }

  const response = await client.sendRequest<GeneratedCSharpResponse>('csmx/getGeneratedCSharp', {
    textDocument: {
      uri: document.uri.toString()
    }
  });

  return response;
}

async function tryGetGeneratedCSharp(
  document: vscode.TextDocument
): Promise<GeneratedCSharpResponse | undefined> {
  try {
    return await getGeneratedCSharp(document);
  } catch (error) {
    logScratchDelegationIssue(`csmx/getGeneratedCSharp failed for ${document.uri.toString()}: ${formatError(error)}`);
    return undefined;
  }
}

async function getProjectBinding(document: vscode.TextDocument): Promise<ProjectBindingResponse> {
  if (!client) {
    throw new Error('CSMX language client is not available.');
  }

  return await client.sendRequest<ProjectBindingResponse>('csmx/inspectProjectBinding', {
    textDocument: {
      uri: document.uri.toString()
    }
  });
}

async function reloadProjectContextRequest(document: vscode.TextDocument): Promise<ReloadProjectContextResponse> {
  if (!client) {
    throw new Error('CSMX language client is not available.');
  }

  return await client.sendRequest<ReloadProjectContextResponse>('csmx/reloadProjectContext', {
    textDocument: {
      uri: document.uri.toString()
    }
  });
}

async function reloadProjectContext(
  scratchDelegation: ScratchCSharpDelegation
): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'csmx') {
    void vscode.window.showWarningMessage('Open a .csmx document first.');
    return;
  }

  let reload: ReloadProjectContextResponse;
  try {
    reload = await reloadProjectContextRequest(editor.document);
  } catch (error) {
    void vscode.window.showErrorMessage(`CSMX project context reload failed: ${formatError(error)}`);
    return;
  }

  scratchDelegation.invalidate(editor.document);

  const cleared = reload.clearedEntries ?? 0;
  void vscode.window.showInformationMessage(`CSMX project context reloaded (${cleared} cache entr${cleared === 1 ? 'y' : 'ies'} cleared).`);
}

async function inspectProjectBinding(context: vscode.ExtensionContext): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'csmx') {
    void vscode.window.showWarningMessage('Open a .csmx document first.');
    return;
  }

  let binding: ProjectBindingResponse;
  try {
    binding = await getProjectBinding(editor.document);
  } catch (error) {
    void vscode.window.showErrorMessage(`CSMX project binding inspection failed: ${formatError(error)}`);
    return;
  }

  projectBindingOutput ??= vscode.window.createOutputChannel('CSMX Project Binding');
  projectBindingOutput.clear();
  projectBindingOutput.appendLine('CSMX Project Binding');
  projectBindingOutput.appendLine('');
  appendOutputLine(projectBindingOutput, 'Extension', `${context.extension.packageJSON.publisher}.${context.extension.packageJSON.name}@${context.extension.packageJSON.version}`);
  appendOutputLine(projectBindingOutput, 'Document', editor.document.uri.toString());
  appendOutputLine(projectBindingOutput, 'Source file', binding.sourceFilePath);
  appendOutputLine(projectBindingOutput, 'Has project', binding.hasProject);
  appendOutputLine(projectBindingOutput, 'Project', binding.projectFilePath);
  appendOutputLine(projectBindingOutput, 'Evaluation', binding.evaluationKind);
  appendOutputLine(projectBindingOutput, 'Requested configuration', binding.requestedConfiguration);
  appendOutputLine(projectBindingOutput, 'Requested target framework', binding.requestedTargetFramework);
  appendOutputLine(projectBindingOutput, 'Configuration', binding.configuration);
  appendOutputLine(projectBindingOutput, 'Target framework', binding.targetFramework);
  appendOutputLine(projectBindingOutput, 'Relative source', binding.relativeSourcePath);
  appendOutputLine(projectBindingOutput, 'Generated dir', binding.generatedDirectory);
  appendOutputLine(projectBindingOutput, 'Generated file', binding.generatedFilePath);
  appendOutputLine(projectBindingOutput, 'Generated exists', binding.generatedFileExists);
  appendOutputLine(projectBindingOutput, 'Compile includes generated', binding.compileIncludesGeneratedFile);
  appendOutputLine(projectBindingOutput, 'Compile item count', binding.compileItemCount);
  if (binding.projectContextDependencies && binding.projectContextDependencies.length > 0) {
    projectBindingOutput.appendLine('');
    projectBindingOutput.appendLine('Project Context Dependencies');
    for (const dependency of binding.projectContextDependencies) {
      projectBindingOutput.appendLine(
        `  ${dependency.path ?? '<none>'} | exists=${Boolean(dependency.exists)} | mtime=${dependency.lastWriteUtc ?? '<none>'}`
      );
    }
  }

  projectBindingOutput.appendLine('');
  projectBindingOutput.appendLine('Transform');
  appendOutputLine(projectBindingOutput, '  Compile mode', binding.transform?.compileMode);
  appendOutputLine(projectBindingOutput, '  Component lowering', binding.transform?.componentLowering);
  appendOutputLine(projectBindingOutput, '  Element factory', binding.transform?.elementFactory);
  appendOutputLine(projectBindingOutput, '  Attribute factory', binding.transform?.attributeFactory);
  appendOutputLine(projectBindingOutput, '  Text factory', binding.transform?.textFactory);
  appendOutputLine(projectBindingOutput, '  Children factory', binding.transform?.childrenFactory);

  if (binding.messages && binding.messages.length > 0) {
    projectBindingOutput.appendLine('');
    projectBindingOutput.appendLine('Messages');
    for (const message of binding.messages) {
      projectBindingOutput.appendLine(`  ${message}`);
    }
  }

  projectBindingOutput.show(true);
}

function appendOutputLine(
  output: vscode.OutputChannel,
  label: string,
  value: string | number | boolean | null | undefined
): void {
  output.appendLine(`${label}: ${value === undefined || value === null || value === '' ? '<none>' : value}`);
}

function translateCompletionList(
  completionList: vscode.CompletionList | undefined,
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument
): vscode.CompletionList | undefined {
  if (!completionList) {
    return undefined;
  }

  return new vscode.CompletionList(
    completionList.items.map(item => translateCompletionItem(item, originalDocument, generated)),
    completionList.isIncomplete
  );
}

function translateCompletionItem(
  item: vscode.CompletionItem,
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument
): vscode.CompletionItem {
  const translated = item;
  translated.range = translateCompletionRange(item.range, originalDocument, generated);
  translated.textEdit = translateTextEdit(item.textEdit, originalDocument, generated);
  translated.additionalTextEdits = item.additionalTextEdits
    ?.map(edit => translateTextEdit(edit, originalDocument, generated))
    .filter(isTextEdit);
  return translated;
}

function translateCompletionRange(
  range: vscode.Range | { inserting: vscode.Range; replacing: vscode.Range } | undefined,
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument
): vscode.Range | { inserting: vscode.Range; replacing: vscode.Range } | undefined {
  if (!range) {
    return undefined;
  }

  if ('inserting' in range && 'replacing' in range) {
    const inserting = translateRange(range.inserting, originalDocument, generated);
    const replacing = translateRange(range.replacing, originalDocument, generated);
    return inserting && replacing ? { inserting, replacing } : undefined;
  }

  return translateRange(range, originalDocument, generated);
}

function translateDefinition(
  definition: vscode.Location,
  generated: GeneratedCSharpDocument,
  originalDocument: vscode.TextDocument
): vscode.Location | undefined {
  if (definition.uri.toString() !== generated.uri.toString()) {
    return definition;
  }

  const range = translateRange(definition.range, originalDocument, generated);
  return range ? new vscode.Location(originalDocument.uri, range) : undefined;
}

function translateTextEdit<T extends vscode.TextEdit | vscode.SnippetTextEdit | undefined>(
  edit: T,
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument
): T | undefined {
  if (!edit) {
    return undefined;
  }

  const range = translateRange(edit.range, originalDocument, generated);
  if (!range) {
    return undefined;
  }

  if (edit instanceof vscode.SnippetTextEdit) {
    return new vscode.SnippetTextEdit(range, edit.snippet) as T;
  }

  return new vscode.TextEdit(range, edit.newText) as T;
}

function isTextEdit<T extends vscode.TextEdit | vscode.SnippetTextEdit>(edit: T | undefined): edit is T {
  return edit !== undefined;
}

function translateRange(
  range: vscode.Range | undefined,
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument
): vscode.Range | undefined {
  if (!range) {
    return undefined;
  }

  return createDocumentMapping(originalDocument, generated)?.toOriginalRange(range, MappingBehavior.Inclusive);
}

function toGeneratedPosition(
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument,
  position: vscode.Position
): vscode.Position | undefined {
  return createDocumentMapping(originalDocument, generated)?.toGeneratedPosition(position, MappingBehavior.Strict);
}

function toGeneratedCompletionPosition(
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument,
  position: vscode.Position
): vscode.Position | undefined {
  const mapping = createDocumentMapping(originalDocument, generated);
  if (!mapping || !generated.textDocument) {
    return undefined;
  }

  const strict = mapping.toGeneratedPosition(position, MappingBehavior.Strict);
  if (strict) {
    return strict;
  }

  const offset = originalDocument.offsetAt(position);
  if (offset > 0) {
    const previous = mapping.toGeneratedPosition(
      originalDocument.positionAt(offset - 1),
      MappingBehavior.Strict
    );
    if (previous) {
      return generated.textDocument.positionAt(generated.textDocument.offsetAt(previous) + 1);
    }
  }

  return mapping.toGeneratedPosition(position, MappingBehavior.Inclusive);
}

function createDocumentMapping(
  originalDocument: vscode.TextDocument,
  generated: GeneratedCSharpDocument
): CsmxDocumentMapping | undefined {
  return generated.textDocument
    ? new CsmxDocumentMapping(originalDocument, generated.textDocument, generated.mappings)
    : undefined;
}

type DecodedSemanticToken = {
  line: number;
  character: number;
  length: number;
  typeIndex: number;
  modifierSet: number;
};

type GeneratedSemanticTokens = {
  tokens: vscode.SemanticTokens;
  legend: vscode.SemanticTokensLegend;
};

async function getGeneratedSemanticTokens(
  uri: vscode.Uri,
  onError?: (error: unknown) => void
): Promise<GeneratedSemanticTokens | undefined> {
  try {
    const [tokens, legend] = await Promise.all([
      vscode.commands.executeCommand<vscode.SemanticTokens>('vscode.provideDocumentSemanticTokens', uri),
      vscode.commands.executeCommand<vscode.SemanticTokensLegend>('vscode.provideDocumentSemanticTokensLegend', uri)
    ]);

    return tokens && legend ? { tokens, legend } : undefined;
  } catch (error) {
    onError?.(error);
    return undefined;
  }
}

function decodeSemanticTokens(tokens: vscode.SemanticTokens | undefined | null): DecodedSemanticToken[] {
  if (!tokens) {
    return [];
  }

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
      typeIndex: tokens.data[index + 3],
      modifierSet: tokens.data[index + 4]
    });
  }

  return decoded;
}

function translateGeneratedSemanticTokens(
  generated: GeneratedSemanticTokens,
  generatedDocument: vscode.TextDocument,
  mapping: CsmxDocumentMapping
): DecodedSemanticToken[] {
  const translated: DecodedSemanticToken[] = [];
  for (const token of decodeSemanticTokens(generated.tokens)) {
    const tokenType = generated.legend.tokenTypes[token.typeIndex];
    const typeIndex = semanticTokenTypes.indexOf(tokenType);
    if (typeIndex < 0) {
      continue;
    }

    const generatedStart = new vscode.Position(token.line, token.character);
    const generatedEnd = generatedDocument.positionAt(
      generatedDocument.offsetAt(generatedStart) + token.length
    );
    const originalStart = mapping.toOriginalPosition(generatedStart, MappingBehavior.Strict);
    const originalEnd = mapping.toOriginalPosition(generatedEnd, MappingBehavior.Inclusive);
    if (!originalStart || !originalEnd || originalStart.line !== originalEnd.line) {
      continue;
    }

    translated.push({
      line: originalStart.line,
      character: originalStart.character,
      length: originalEnd.character - originalStart.character,
      typeIndex,
      modifierSet: 0
    });
  }

  return translated;
}

function mergeSemanticTokens(
  baseTokens: DecodedSemanticToken[],
  delegatedTokens: DecodedSemanticToken[]
): vscode.SemanticTokens {
  const byRange = new Map<string, DecodedSemanticToken>();
  for (const token of baseTokens) {
    byRange.set(semanticTokenKey(token), token);
  }

  for (const token of delegatedTokens) {
    if (token.length > 0) {
      for (const [key, baseToken] of byRange) {
        if (semanticTokensOverlap(baseToken, token)) {
          byRange.delete(key);
        }
      }

      byRange.set(semanticTokenKey(token), token);
    }
  }

  const merged = [...byRange.values()].sort((left, right) =>
    left.line - right.line || left.character - right.character || left.length - right.length
  );

  const data: number[] = [];
  let previousLine = 0;
  let previousCharacter = 0;
  for (const token of merged) {
    const deltaLine = token.line - previousLine;
    const deltaStart = deltaLine === 0 ? token.character - previousCharacter : token.character;
    if (deltaLine < 0 || deltaStart < 0 || token.length <= 0) {
      continue;
    }

    data.push(deltaLine, deltaStart, token.length, token.typeIndex, token.modifierSet);
    previousLine = token.line;
    previousCharacter = token.character;
  }

  return new vscode.SemanticTokens(new Uint32Array(data));
}

function semanticTokenKey(token: DecodedSemanticToken): string {
  return `${token.line}:${token.character}:${token.length}`;
}

function semanticTokensOverlap(left: DecodedSemanticToken, right: DecodedSemanticToken): boolean {
  if (left.line !== right.line) {
    return false;
  }

  const leftEnd = left.character + left.length;
  const rightEnd = right.character + right.length;
  return left.character < rightEnd && right.character < leftEnd;
}

enum MappingBehavior {
  Strict,
  Inclusive,
  Inferred
}

class CsmxDocumentMapping {
  constructor(
    private readonly originalDocument: vscode.TextDocument,
    private readonly generatedDocument: vscode.TextDocument,
    mappings: SourceMapEntryResponse[]
  ) {
    this.mappings = mappings.filter(mapping => csharpDelegationMappingKinds.has(mapping.kind));
  }

  private readonly mappings: SourceMapEntryResponse[];

  toGeneratedPosition(position: vscode.Position, behavior: MappingBehavior): vscode.Position | undefined {
    const offset = this.mapOriginalOffsetToGeneratedOffset(this.originalDocument.offsetAt(position), behavior);
    return offset === undefined ? undefined : this.generatedDocument.positionAt(offset);
  }

  toOriginalPosition(position: vscode.Position, behavior: MappingBehavior): vscode.Position | undefined {
    const offset = this.mapGeneratedOffsetToOriginalOffset(this.generatedDocument.offsetAt(position), behavior);
    return offset === undefined ? undefined : this.originalDocument.positionAt(offset);
  }

  toOriginalRange(range: vscode.Range, behavior: MappingBehavior): vscode.Range | undefined {
    const start = this.toOriginalPosition(range.start, behavior);
    const end = this.toOriginalPosition(range.end, behavior);
    return start && end ? new vscode.Range(start, end) : undefined;
  }

  private mapOriginalOffsetToGeneratedOffset(offset: number, behavior: MappingBehavior): number | undefined {
    const mapping = this.findMapping(offset, behavior, mapping => mapping.originalStart, mapping => mapping.originalLength);
    if (!mapping) {
      return undefined;
    }

    return mapOffset(
      offset,
      behavior,
      mapping.originalStart,
      mapping.originalLength,
      mapping.generatedStart,
      mapping.generatedLength
    );
  }

  private mapGeneratedOffsetToOriginalOffset(offset: number, behavior: MappingBehavior): number | undefined {
    const mapping = this.findMapping(offset, behavior, mapping => mapping.generatedStart, mapping => mapping.generatedLength);
    if (!mapping) {
      return undefined;
    }

    return mapOffset(
      offset,
      behavior,
      mapping.generatedStart,
      mapping.generatedLength,
      mapping.originalStart,
      mapping.originalLength
    );
  }

  private findMapping(
    offset: number,
    behavior: MappingBehavior,
    getStart: (mapping: SourceMapEntryResponse) => number,
    getLength: (mapping: SourceMapEntryResponse) => number
  ): SourceMapEntryResponse | undefined {
    const containing = this.mappings
      .filter(mapping => {
        const start = getStart(mapping);
        const length = Math.max(1, getLength(mapping));
        return offset >= start && offset < start + length;
      })
      .sort((left, right) => getLength(left) - getLength(right))[0];

    if (containing || behavior === MappingBehavior.Strict) {
      return containing;
    }

    if (behavior === MappingBehavior.Inclusive) {
      return this.mappings
        .filter(mapping => offset === getStart(mapping) + Math.max(1, getLength(mapping)))
        .sort((left, right) => getLength(left) - getLength(right))[0];
    }

    return this.mappings
      .filter(mapping => getStart(mapping) <= offset)
      .sort((left, right) => getStart(right) - getStart(left))[0];
  }
}

function mapOffset(
  offset: number,
  behavior: MappingBehavior,
  sourceStart: number,
  sourceLength: number,
  targetStart: number,
  targetLength: number
): number {
  const boundedSourceLength = Math.max(1, sourceLength);
  const boundedTargetLength = Math.max(1, targetLength);
  const distance = offset - sourceStart;
  if (behavior !== MappingBehavior.Strict && distance === boundedSourceLength) {
    return targetStart + boundedTargetLength;
  }

  return targetStart + Math.min(Math.max(0, distance), boundedTargetLength - 1);
}

function hasCompletionItems(
  result: vscode.CompletionItem[] | vscode.CompletionList | null | undefined
): boolean {
  if (!result) {
    return false;
  }

  return Array.isArray(result) ? result.length > 0 : result.items.length > 0;
}

async function fileExists(filePath: string): Promise<boolean> {
  try {
    await vscode.workspace.fs.stat(vscode.Uri.file(filePath));
    return true;
  } catch {
    return false;
  }
}

async function hasContainingProject(uri: vscode.Uri): Promise<boolean> {
  if (uri.scheme !== 'file') {
    return false;
  }

  let directory = path.dirname(uri.fsPath);
  while (directory.length > 0) {
    try {
      const entries = await fs.readdir(directory);
      if (entries.some(entry => entry.toLowerCase().endsWith('.csproj'))) {
        return true;
      }
    } catch {
      return false;
    }

    const parent = path.dirname(directory);
    if (parent === directory) {
      return false;
    }

    directory = parent;
  }

  return false;
}

function logScratchDelegationIssue(message: string): void {
  scratchDelegationOutput ??= vscode.window.createOutputChannel('CSMX Scratch C# Delegation');
  scratchDelegationOutput.appendLine(`[${new Date().toISOString()}] ${message}`);
}

function formatError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function inspectTokenAtCursor(context: vscode.ExtensionContext): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'csmx') {
    void vscode.window.showWarningMessage('Open a .csmx document first.');
    return;
  }

  const document = editor.document;
  const position = editor.selection.active;
  const offset = document.offsetAt(position);
  const character = getCharacterAt(document, position);
  const textMateToken = await inspectTextMateToken(context, document, position);
  const semanticToken = await inspectSemanticToken(document, position);
  const bracketInfo = await inspectBracketConfig(context, character);
  const generated = await inspectGeneratedMapping(document, offset);

  tokenInspectionOutput ??= vscode.window.createOutputChannel('CSMX Token Inspector');
  tokenInspectionOutput.clear();
  tokenInspectionOutput.appendLine(`Document: ${document.uri.toString()}`);
  tokenInspectionOutput.appendLine(`Language: ${document.languageId}`);
  tokenInspectionOutput.appendLine(`Position: ${position.line}:${position.character}`);
  tokenInspectionOutput.appendLine(`Character: ${JSON.stringify(character)}`);
  tokenInspectionOutput.appendLine('');
  tokenInspectionOutput.appendLine('TextMate');
  tokenInspectionOutput.appendLine(
    textMateToken
      ? `  Range: ${textMateToken.start}-${textMateToken.end}`
      : '  Range: <none>'
  );
  tokenInspectionOutput.appendLine(
    textMateToken
      ? `  Text: ${JSON.stringify(textMateToken.text)}`
      : '  Text: <none>'
  );
  tokenInspectionOutput.appendLine(
    textMateToken
      ? `  Scopes: ${textMateToken.scopes.join(' ')}`
      : '  Scopes: <none>'
  );
  tokenInspectionOutput.appendLine('');
  tokenInspectionOutput.appendLine('Semantic Token');
  tokenInspectionOutput.appendLine(
    semanticToken
      ? `  Range: ${semanticToken.line}:${semanticToken.character}+${semanticToken.length}`
      : '  Range: <none>'
  );
  tokenInspectionOutput.appendLine(
    semanticToken
      ? `  Type: ${semanticToken.type}`
      : '  Type: <none>'
  );
  tokenInspectionOutput.appendLine('');
  tokenInspectionOutput.appendLine('Language Configuration');
  tokenInspectionOutput.appendLine(`  Bracket pair for character: ${bracketInfo}`);
  tokenInspectionOutput.appendLine('');
  tokenInspectionOutput.appendLine('Generated Mapping');
  tokenInspectionOutput.appendLine(generated);
  tokenInspectionOutput.show(true);

  void vscode.window.showInformationMessage('CSMX token details written to the CSMX Token Inspector output.');
}

function getCharacterAt(document: vscode.TextDocument, position: vscode.Position): string {
  const line = document.lineAt(position.line).text;
  if (position.character < line.length) {
    return line[position.character];
  }

  return '';
}

type TextMateTokenInspection = {
  start: number;
  end: number;
  text: string;
  scopes: string[];
};

async function inspectTextMateToken(
  context: vscode.ExtensionContext,
  document: vscode.TextDocument,
  position: vscode.Position
): Promise<TextMateTokenInspection | undefined> {
  const grammar = await getTextMateGrammar(context);
  if (!grammar) {
    return undefined;
  }

  let ruleStack = textmate.INITIAL;
  let tokens: textmate.IToken[] = [];
  for (let line = 0; line <= position.line; line++) {
    const result = grammar.tokenizeLine(document.lineAt(line).text, ruleStack);
    ruleStack = result.ruleStack;
    if (line === position.line) {
      tokens = result.tokens;
    }
  }

  const lineText = document.lineAt(position.line).text;
  const character = Math.min(position.character, Math.max(0, lineText.length - 1));
  const token = tokens.find(item => character >= item.startIndex && character < item.endIndex)
    ?? findLastTokenBefore(tokens, character);
  if (!token) {
    return undefined;
  }

  return {
    start: token.startIndex,
    end: token.endIndex,
    text: lineText.slice(token.startIndex, token.endIndex),
    scopes: token.scopes
  };
}

function findLastTokenBefore(tokens: textmate.IToken[], character: number): textmate.IToken | undefined {
  for (let i = tokens.length - 1; i >= 0; i--) {
    if (tokens[i].startIndex <= character) {
      return tokens[i];
    }
  }

  return undefined;
}

async function getTextMateGrammar(context: vscode.ExtensionContext): Promise<textmate.IGrammar | null> {
  textMateGrammar ??= loadTextMateGrammar(context);
  return await textMateGrammar;
}

async function loadTextMateGrammar(context: vscode.ExtensionContext): Promise<textmate.IGrammar | null> {
  const wasmPath = path.join(context.extensionPath, 'node_modules', 'vscode-oniguruma', 'release', 'onig.wasm');
  const wasm = await fs.readFile(wasmPath);
  await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));

  const grammarPath = path.join(context.extensionPath, 'syntaxes', 'csmx.tmLanguage.json');
  const registry = new textmate.Registry({
    onigLib: Promise.resolve({
      createOnigScanner(patterns: string[]) {
        return new oniguruma.OnigScanner(patterns);
      },
      createOnigString(value: string) {
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

  return await registry.loadGrammar('source.csmx');
}

type SemanticTokenInspection = {
  line: number;
  character: number;
  length: number;
  type: string;
};

async function inspectSemanticToken(
  document: vscode.TextDocument,
  position: vscode.Position
): Promise<SemanticTokenInspection | undefined> {
  const tokens = await vscode.commands.executeCommand<vscode.SemanticTokens | undefined>(
    'vscode.provideDocumentSemanticTokens',
    document.uri
  );
  if (!tokens) {
    return undefined;
  }

  for (const token of decodeSemanticTokens(tokens)) {
    if (
      token.line === position.line
      && position.character >= token.character
      && position.character < token.character + token.length
    ) {
      return {
        line: token.line,
        character: token.character,
        length: token.length,
        type: semanticTokenTypes[token.typeIndex] ?? `#${token.typeIndex}`
      };
    }
  }

  return undefined;
}

async function inspectBracketConfig(
  context: vscode.ExtensionContext,
  character: string
): Promise<string> {
  const configPath = path.join(context.extensionPath, 'language-configuration.json');
  const config = JSON.parse(await fs.readFile(configPath, 'utf8')) as {
    brackets?: [string, string][];
  };
  const pair = config.brackets?.find(([open, close]) => character === open || character === close);
  return pair ? `${pair[0]} ${pair[1]}` : '<none>';
}

async function inspectGeneratedMapping(
  document: vscode.TextDocument,
  offset: number
): Promise<string> {
  const generated = await getGeneratedCSharp(document);
  const mapping = generated.mappings
    ?.filter(item => offset >= item.originalStart && offset < item.originalStart + Math.max(1, item.originalLength))
    .sort((left, right) => left.originalLength - right.originalLength)[0];
  if (!mapping) {
    return '  <none>';
  }

  return [
    `  Kind: ${mapping.kind}`,
    `  Original: ${mapping.originalStart}+${mapping.originalLength}`,
    `  Generated: ${mapping.generatedStart}+${mapping.generatedLength}`
  ].join('\n');
}

function isInsideOpeningTag(text: string, index: number): boolean {
  const searchStart = Math.max(0, Math.min(index - 1, text.length - 1));
  const lastOpen = text.lastIndexOf('<', searchStart);
  const lastClose = text.lastIndexOf('>', searchStart);

  if (
    lastOpen < 0
    || lastOpen <= lastClose
    || lastOpen + 1 >= text.length
    || text[lastOpen + 1] === '/'
  ) {
    return false;
  }

  let braceDepth = 0;
  let quote = '';
  for (let cursor = lastOpen + 1; cursor < index && cursor < text.length; cursor++) {
    const c = text[cursor];
    if (quote.length > 0) {
      if (c === '\\') {
        cursor++;
        continue;
      }

      if (c === quote) {
        quote = '';
      }

      continue;
    }

    if (c === '"' || c === '\'') {
      quote = c;
      continue;
    }

    if (c === '{') {
      braceDepth++;
      continue;
    }

    if (c === '}' && braceDepth > 0) {
      braceDepth--;
    }
  }

  return braceDepth === 0 && quote.length === 0;
}
