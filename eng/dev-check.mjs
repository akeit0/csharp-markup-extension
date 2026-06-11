import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const skipExtensionIntegration = process.argv.includes("--skip-extension-integration");

const localState = {
  APPDATA: join(repoRoot, ".appdata"),
  DOTNET_CLI_HOME: join(repoRoot, ".dotnet"),
  NUGET_PACKAGES: join(repoRoot, ".nuget-packages"),
  NUGET_HTTP_CACHE_PATH: join(repoRoot, ".nuget-http-cache"),
  NUGET_SCRATCH: join(repoRoot, ".nuget-scratch"),
};

for (const directory of Object.values(localState)) {
  mkdirSync(directory, { recursive: true });
}

const env = {
  ...process.env,
  ...localState,
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
  DOTNET_CLI_TELEMETRY_OPTOUT: "1",
};

const testProjects = [
  "tests/Csmx.Compiler.Tests/Csmx.Compiler.Tests.csproj",
  "tests/Csmx.LanguageServer.Tests/Csmx.LanguageServer.Tests.csproj",
  "tests/Csmx.EnagaSignals.Tests/Csmx.EnagaSignals.Tests.csproj",
];

run("dotnet", [
  "restore",
  join(repoRoot, "Csmx.slnx"),
  "--configfile",
  join(repoRoot, "NuGet.Config"),
]);

for (const project of testProjects) {
  run("dotnet", [
    "test",
    join(repoRoot, project),
    "-v:minimal",
    "--no-restore",
  ]);
}

const extensionRoot = join(repoRoot, "vscode/csmx-vscode");
runNpm(["run", "test:unit"], extensionRoot);

if (!skipExtensionIntegration) {
  runNpm(["run", "test:integration"], extensionRoot);
}

function runNpm(args, cwd) {
  if (process.platform === "win32") {
    run(process.env.ComSpec ?? "cmd.exe", ["/d", "/s", "/c", "npm", ...args], cwd);
    return;
  }

  run("npm", args, cwd);
}

function run(command, args, cwd = repoRoot) {
  const result = spawnSync(command, args, {
    cwd,
    env,
    stdio: "inherit",
  });

  if (result.error) {
    console.error(result.error.message);
    process.exit(1);
  }

  if (result.status !== 0) {
    console.error(`'${command} ${args.join(" ")}' failed with exit code ${result.status}.`);
    process.exit(result.status ?? 1);
  }
}
