import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const extensionRoot = resolve(scriptDirectory, "..");
const repoRoot = resolve(extensionRoot, "../..");
const serverProject = join(repoRoot, "src/Csmx.LanguageServer/Csmx.LanguageServer.csproj");
const nugetConfig = join(repoRoot, "NuGet.Config");
const serverOutput = join(extensionRoot, "server");

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

run("dotnet", ["restore", serverProject, "--configfile", nugetConfig]);
run("dotnet", ["publish", serverProject, "-c", "Release", "-o", serverOutput, "--no-restore"]);

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    env,
    stdio: "inherit",
  });

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}
