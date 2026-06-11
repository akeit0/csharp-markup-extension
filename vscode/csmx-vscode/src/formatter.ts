import * as path from 'path';
import * as vscode from 'vscode';

type FormattingOptions = {
  indent: string;
};

export function provideCsjsxFormattingEdits(
  document: vscode.TextDocument,
  options: vscode.FormattingOptions
): Promise<vscode.TextEdit[]> {
  return provideCsjsxFormattingEditsAsync(document, options);
}

async function provideCsjsxFormattingEditsAsync(
  document: vscode.TextDocument,
  options: vscode.FormattingOptions
): Promise<vscode.TextEdit[]> {
  const formatted = formatCsjsxText(document.getText(), {
    indent: await resolveIndent(document, options)
  });
  if (formatted === document.getText()) {
    return [];
  }

  const fullRange = new vscode.Range(
    document.positionAt(0),
    document.positionAt(document.getText().length)
  );
  return [vscode.TextEdit.replace(fullRange, formatted)];
}

export function formatCsjsxText(text: string, options: FormattingOptions): string {
  const newline = text.includes('\r\n') ? '\r\n' : '\n';
  const hasTrailingNewline = /\r?\n$/.test(text);
  const normalized = text.replace(/\r\n/g, '\n');
  const lines = normalized.split('\n');
  if (lines.length > 0 && lines[lines.length - 1] === '') {
    lines.pop();
  }

  const formatted: string[] = [];
  let depth = 0;
  let continuationDepth = 0;
  let inBlockComment = false;
  let inRawString = false;
  let pendingTag: PendingTag | undefined;

  for (const sourceLine of lines) {
    const trimmed = sourceLine.trim();
    if (trimmed.length === 0) {
      formatted.push('');
      continue;
    }

    if (pendingTag) {
      const tagEnd = readTagContinuationEnd(trimmed);
      const lineDepth = tagEnd?.startsAtFirstToken ? pendingTag.baseDepth : pendingTag.baseDepth + 1;
      formatted.push(options.indent.repeat(lineDepth) + trimmed);

      if (tagEnd) {
        depth = pendingTag.baseDepth + (tagEnd.kind === 'open' ? 1 : 0);
        pendingTag = undefined;
      }
      continue;
    }

    const lineInfo = analyzeLine(trimmed, inBlockComment, inRawString);
    inBlockComment = lineInfo.inBlockComment;
    inRawString = lineInfo.inRawString;

    const leadingOutdent = lineInfo.leadingCloseCount;
    const structuralDepth = Math.max(0, depth - leadingOutdent);
    const lineDepth = structuralDepth + (isFluentChainContinuation(trimmed) ? 1 : 0);
    formatted.push(options.indent.repeat(lineDepth) + trimmed);

    const unclosedTag = findUnclosedOpeningTag(trimmed);
    if (unclosedTag) {
      pendingTag = { baseDepth: lineDepth };
      depth = lineDepth;
      continue;
    }

    const updateDepthBase =
      isFluentChainContinuation(trimmed) && !trimmed.endsWith(';') ? lineDepth : structuralDepth;
    const restoredLeadingOutdent = shouldRestoreLeadingOutdent(trimmed) ? leadingOutdent : 0;
    depth = Math.max(0, updateDepthBase + lineInfo.netOpenCount + restoredLeadingOutdent);
    if (lineInfo.opensContinuation && !isLikelyAttributeLambda(trimmed)) {
      depth++;
      continuationDepth++;
    }

    if (lineInfo.closesContinuation && continuationDepth > 0) {
      depth = Math.max(0, depth - 1);
      continuationDepth--;
    }
  }

  return formatted.join(newline) + (hasTrailingNewline ? newline : '');
}

type PendingTag = {
  baseDepth: number;
};

async function resolveIndent(
  document: vscode.TextDocument,
  options: vscode.FormattingOptions
): Promise<string> {
  const editorConfig = await readEditorConfigIndent(document.uri);
  if (editorConfig?.indentStyle === 'tab') {
    return '\t';
  }

  if (editorConfig?.indentStyle === 'space') {
    const size = editorConfig.indentSize ?? editorConfig.tabWidth ?? fallbackIndentSize(options);
    return ' '.repeat(clampIndentSize(size));
  }

  if (!options.insertSpaces) {
    return '\t';
  }

  return ' '.repeat(clampIndentSize(fallbackIndentSize(options)));
}

function fallbackIndentSize(options: vscode.FormattingOptions): number {
  const configured = vscode.workspace
    .getConfiguration('csmx')
    .get<number>('format.indentSize');
  return typeof options.tabSize === 'number' && options.tabSize > 0
    ? options.tabSize
    : configured ?? 4;
}

function clampIndentSize(size: number): number {
  return Math.max(1, Math.min(8, Math.floor(size)));
}

type EditorConfigIndent = {
  indentStyle?: 'space' | 'tab';
  indentSize?: number;
  tabWidth?: number;
};

async function readEditorConfigIndent(uri: vscode.Uri): Promise<EditorConfigIndent | undefined> {
  if (uri.scheme !== 'file') {
    return undefined;
  }

  const configs = await collectEditorConfigFiles(uri.fsPath);
  if (configs.length === 0) {
    return undefined;
  }

  const merged: EditorConfigIndent = {};
  for (const configPath of configs.reverse()) {
    const content = await readTextFile(configPath);
    if (!content) {
      continue;
    }

    const relativePath = normalizePath(path.relative(path.dirname(configPath), uri.fsPath));
    for (const section of parseEditorConfig(content)) {
      if (!matchesEditorConfigSection(section.pattern, relativePath)) {
        continue;
      }

      if (section.properties.indent_style === 'space' || section.properties.indent_style === 'tab') {
        merged.indentStyle = section.properties.indent_style;
      }

      const tabWidth = parseEditorConfigSize(section.properties.tab_width);
      if (typeof tabWidth === 'number') {
        merged.tabWidth = tabWidth;
      }

      const indentSize = parseEditorConfigSize(section.properties.indent_size);
      if (typeof indentSize === 'number') {
        merged.indentSize = indentSize;
      } else if (indentSize === 'tab' && merged.tabWidth !== undefined) {
        merged.indentSize = merged.tabWidth;
      }
    }
  }

  return merged.indentStyle || merged.indentSize || merged.tabWidth
    ? merged
    : undefined;
}

async function collectEditorConfigFiles(filePath: string): Promise<string[]> {
  const configs: string[] = [];
  let directory = path.dirname(filePath);
  while (true) {
    const configPath = path.join(directory, '.editorconfig');
    const content = await readTextFile(configPath);
    if (content !== undefined) {
      configs.push(configPath);
      if (/^\s*root\s*=\s*true\s*$/im.test(content)) {
        break;
      }
    }

    const parent = path.dirname(directory);
    if (parent === directory) {
      break;
    }

    directory = parent;
  }

  return configs;
}

async function readTextFile(filePath: string): Promise<string | undefined> {
  try {
    const bytes = await vscode.workspace.fs.readFile(vscode.Uri.file(filePath));
    return Buffer.from(bytes).toString('utf8');
  } catch {
    return undefined;
  }
}

type EditorConfigSection = {
  pattern: string;
  properties: Record<string, string>;
};

function parseEditorConfig(content: string): EditorConfigSection[] {
  const sections: EditorConfigSection[] = [{ pattern: '*', properties: {} }];
  let current = sections[0];
  for (const rawLine of content.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line.length === 0 || line.startsWith('#') || line.startsWith(';')) {
      continue;
    }

    if (line.startsWith('[') && line.endsWith(']')) {
      current = { pattern: line.slice(1, -1).trim(), properties: {} };
      sections.push(current);
      continue;
    }

    const separator = line.indexOf('=');
    if (separator < 0) {
      continue;
    }

    current.properties[line.slice(0, separator).trim().toLowerCase()] = line
      .slice(separator + 1)
      .trim()
      .toLowerCase();
  }

  return sections;
}

function parseEditorConfigSize(value: string | undefined): number | 'tab' | undefined {
  if (!value || value === 'unset') {
    return undefined;
  }

  if (value === 'tab') {
    return 'tab';
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
}

function matchesEditorConfigSection(pattern: string, relativePath: string): boolean {
  const normalized = normalizePath(relativePath);
  const fileName = path.posix.basename(normalized);
  if (pattern === '*') {
    return true;
  }

  for (const expanded of expandEditorConfigPattern(pattern)) {
    if (matchesSimplePattern(expanded, fileName) || matchesSimplePattern(expanded, normalized)) {
      return true;
    }
  }

  return false;
}

function expandEditorConfigPattern(pattern: string): string[] {
  const match = /\{([^{}]+)\}/.exec(pattern);
  if (!match) {
    return [normalizePath(pattern)];
  }

  return match[1]
    .split(',')
    .flatMap(part =>
      expandEditorConfigPattern(
        pattern.slice(0, match.index) + part.trim() + pattern.slice(match.index + match[0].length)
      )
    );
}

function matchesSimplePattern(pattern: string, value: string): boolean {
  const regex = '^' + pattern
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*\*/g, '.*')
    .replace(/\*/g, '[^/]*')
    .replace(/\?/g, '[^/]') + '$';
  return new RegExp(regex).test(value);
}

function normalizePath(value: string): string {
  return value.replace(/\\/g, '/');
}

type LineAnalysis = {
  leadingCloseCount: number;
  netOpenCount: number;
  opensContinuation: boolean;
  closesContinuation: boolean;
  inBlockComment: boolean;
  inRawString: boolean;
};

function analyzeLine(
  line: string,
  initialBlockComment: boolean,
  initialRawString: boolean
): LineAnalysis {
  let inBlockComment = initialBlockComment;
  let inRawString = initialRawString;
  let inString: '"' | '\'' | null = null;
  let verbatimString = false;
  let leadingCloseCount = 0;
  let netOpenCount = 0;
  let sawCode = false;

  for (let index = 0; index < line.length; index++) {
    const current = line[index];
    const next = line[index + 1] ?? '';

    if (inBlockComment) {
      if (current === '*' && next === '/') {
        inBlockComment = false;
        index++;
      }
      continue;
    }

    if (inRawString) {
      if (line.startsWith('"""', index)) {
        inRawString = false;
        index += 2;
      }
      continue;
    }

    if (inString) {
      if (verbatimString && current === '"' && next === '"') {
        index++;
        continue;
      }

      if (!verbatimString && current === '\\') {
        index++;
        continue;
      }

      if (current === inString) {
        inString = null;
        verbatimString = false;
      }
      continue;
    }

    if (current === '/' && next === '/') {
      break;
    }

    if (current === '/' && next === '*') {
      inBlockComment = true;
      index++;
      continue;
    }

    if (line.startsWith('"""', index)) {
      inRawString = true;
      index += 2;
      continue;
    }

    if (current === '"' || current === '\'') {
      inString = current;
      verbatimString = current === '"' && index > 0 && line[index - 1] === '@';
      continue;
    }

    if (/\s/.test(current)) {
      continue;
    }

    if (current === '<' && isTagStart(line, index)) {
      const tag = readTag(line, index);
      if (tag) {
        if (tag.kind === 'close') {
          if (!sawCode) {
            leadingCloseCount++;
          }
          netOpenCount--;
        } else if (tag.kind === 'open') {
          netOpenCount++;
        }

        sawCode = true;
        index = tag.end - 1;
        continue;
      }
    }

    if (current === '}' || current === ')' || current === ']') {
      if (!sawCode) {
        leadingCloseCount++;
      }
      netOpenCount--;
      sawCode = true;
      continue;
    }

    if (current === '{' || current === '(' || current === '[') {
      netOpenCount++;
      sawCode = true;
      continue;
    }

    sawCode = true;
  }

  return {
    leadingCloseCount,
    netOpenCount,
    opensContinuation: line.endsWith('=>'),
    closesContinuation: line.endsWith(';'),
    inBlockComment,
    inRawString
  };
}

type TagInfo = {
  kind: 'open' | 'close' | 'self';
  end: number;
};

function readTag(line: string, start: number): TagInfo | undefined {
  let index = start + 1;
  let close = false;
  if (line[index] === '/') {
    close = true;
    index++;
  }

  if (!/[A-Za-z_]/.test(line[index] ?? '')) {
    return undefined;
  }

  let quote: '"' | '\'' | null = null;
  let expressionDepth = 0;
  for (; index < line.length; index++) {
    const current = line[index];
    if (quote) {
      if (current === '\\') {
        index++;
      } else if (current === quote) {
        quote = null;
      }
      continue;
    }

    if (current === '"' || current === '\'') {
      quote = current;
      continue;
    }

    if (current === '{') {
      expressionDepth++;
      continue;
    }

    if (current === '}') {
      expressionDepth = Math.max(0, expressionDepth - 1);
      continue;
    }

    if (current === '>' && expressionDepth === 0) {
      return {
        kind: close ? 'close' : line[index - 1] === '/' ? 'self' : 'open',
        end: index + 1
      };
    }
  }

  return undefined;
}

function isTagStart(line: string, index: number): boolean {
  const next = line[index + 1] ?? '';
  if (next === '/') {
    return /[A-Za-z_]/.test(line[index + 2] ?? '');
  }

  if (!/[A-Za-z_]/.test(next)) {
    return false;
  }

  const previous = previousNonWhitespace(line, index - 1);
  if (previous < 0 || !/[A-Za-z0-9_\])]/.test(line[previous])) {
    return true;
  }

  const previousWord = readPreviousWord(line, previous);
  return previousWord === 'return'
    || previousWord === 'throw'
    || previousWord === 'case'
    || previousWord === 'yield';
}

function previousNonWhitespace(line: string, start: number): number {
  for (let index = start; index >= 0; index--) {
    if (!/\s/.test(line[index])) {
      return index;
    }
  }

  return -1;
}

function readPreviousWord(line: string, endIndex: number): string {
  let start = endIndex;
  while (start >= 0 && /[A-Za-z0-9_]/.test(line[start])) {
    start--;
  }

  return line.slice(start + 1, endIndex + 1);
}

function findUnclosedOpeningTag(line: string): TagStartInfo | undefined {
  let index = 0;
  while (index < line.length) {
    const open = line.indexOf('<', index);
    if (open < 0) {
      return undefined;
    }

    if (isTagStart(line, open)) {
      const tag = readTag(line, open);
      if (!tag) {
        return { start: open };
      }
    }

    index = open + 1;
  }

  return undefined;
}

type TagStartInfo = {
  start: number;
};

type TagContinuationEnd = {
  kind: 'open' | 'self';
  startsAtFirstToken: boolean;
};

function readTagContinuationEnd(line: string): TagContinuationEnd | undefined {
  let quote: '"' | '\'' | null = null;
  let expressionDepth = 0;
  let firstToken = -1;

  for (let index = 0; index < line.length; index++) {
    const current = line[index];
    const next = line[index + 1] ?? '';

    if (firstToken < 0 && !/\s/.test(current)) {
      firstToken = index;
    }

    if (quote) {
      if (current === '\\') {
        index++;
      } else if (current === quote) {
        quote = null;
      }
      continue;
    }

    if (current === '"' || current === '\'') {
      quote = current;
      continue;
    }

    if (current === '{') {
      expressionDepth++;
      continue;
    }

    if (current === '}') {
      expressionDepth = Math.max(0, expressionDepth - 1);
      continue;
    }

    if (current === '>' && expressionDepth === 0) {
      return {
        kind: line[index - 1] === '/' || line.slice(index + 1).includes('</') ? 'self' : 'open',
        startsAtFirstToken: firstToken === index
      };
    }

    if (current === '/' && next === '>') {
      return {
        kind: 'self',
        startsAtFirstToken: firstToken === index
      };
    }
  }

  return undefined;
}

function isFluentChainContinuation(line: string): boolean {
  return line.startsWith('.') || line.startsWith('?.');
}

function isLikelyAttributeLambda(line: string): boolean {
  return line.includes('={') && line.endsWith('=>');
}

function shouldRestoreLeadingOutdent(line: string): boolean {
  return !line.startsWith(')') && !line.startsWith(']');
}
