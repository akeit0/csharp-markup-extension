import { runTests } from '@vscode/test-electron';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionDevelopmentPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const extensionTestsPath = path.join(extensionDevelopmentPath, 'out-test', 'suite', 'index.js');
const workspacePath = await fs.mkdtemp(path.join(os.tmpdir(), 'csmx-vscode-smoke-'));
const pathEnvironmentKey = Object.keys(process.env).find(key => key.toLowerCase() === 'path') ?? 'PATH';
const windowsAppsPath = path.join(os.homedir(), 'AppData', 'Local', 'Microsoft', 'WindowsApps').toLowerCase();
const sanitizedPath = (process.env[pathEnvironmentKey] ?? '')
  .split(path.delimiter)
  .filter(entry => path.resolve(entry).toLowerCase() !== windowsAppsPath)
  .join(path.delimiter);

try {
  await runTests({
    extensionDevelopmentPath,
    extensionTestsPath,
    version: '1.124.0',
    extensionTestsEnv: {
      [pathEnvironmentKey]: sanitizedPath,
      PATH: sanitizedPath,
      Path: sanitizedPath
    },
    launchArgs: [
      workspacePath,
      '--disable-extensions'
    ]
  });
} finally {
  await fs.rm(workspacePath, { recursive: true, force: true });
}
