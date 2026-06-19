import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const browserDir = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(browserDir, "..", "..");
const port = Number(process.argv[2] || process.env.KEYWARS_BROWSER_PORT || 5187);
const url = `http://127.0.0.1:${port}`;
const appDll = path.join(repositoryRoot, "src", "KeyWars", "bin", "Release", "net10.0", "KeyWars.dll");

const dotnetCandidates = process.platform === "win32"
  ? [path.join(repositoryRoot, ".dotnet", "dotnet.exe"), "dotnet.exe", "dotnet"]
  : [path.join(repositoryRoot, ".dotnet", "dotnet"), "dotnet"];
const dotnet = dotnetCandidates.find((candidate) => candidate.includes(path.sep)
  ? fs.existsSync(candidate)
  : true);

if (!fs.existsSync(appDll)) {
  console.error(`KeyWars.dll fehlt unter ${appDll}. Fuehre zuerst dotnet build -c Release aus.`);
  process.exit(1);
}

const child = spawn(dotnet, [appDll], {
  cwd: repositoryRoot,
  env: {
    ...process.env,
    ASPNETCORE_URLS: url
  },
  stdio: "inherit"
});

const stop = () => {
  if (!child.killed) {
    child.kill();
  }
};

process.on("SIGINT", stop);
process.on("SIGTERM", stop);
child.on("exit", (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }

  process.exit(code ?? 0);
});
