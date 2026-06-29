var fs = require("fs");
var path = require("path");
var spawnSync = require("child_process").spawnSync;

var minimumMajor = 18;
var scriptArgs = process.argv.slice(2);

if (scriptArgs.length === 0) {
  console.error("Usage: node scripts/run-modern-node.js <script> [args...]");
  process.exit(1);
}

var selected = findModernNode();
if (!selected) {
  console.error("KeyWars asset and browser scripts require Node.js " + minimumMajor + " or newer.");
  console.error("The current Node.js is " + process.version + " at " + process.execPath + ".");
  console.error("Install a current Node.js release or place it earlier on PATH.");
  process.exit(1);
}

var result = spawnSync(selected.path, scriptArgs, {
  cwd: process.cwd(),
  env: process.env,
  stdio: "inherit"
});

if (result.error) {
  console.error(result.error.message);
  process.exit(1);
}

if (result.signal) {
  process.kill(process.pid, result.signal);
}

process.exit(typeof result.status === "number" ? result.status : 1);

function findModernNode() {
  var candidates = uniquePaths(collectNodeCandidates());
  var best = null;

  for (var index = 0; index < candidates.length; index += 1) {
    var candidate = candidates[index];
    var version = readNodeMajor(candidate);
    if (version >= minimumMajor && (!best || version > best.major)) {
      best = { path: candidate, major: version };
    }
  }

  return best;
}

function collectNodeCandidates() {
  var candidates = [process.execPath];
  var executableName = process.platform === "win32" ? "node.exe" : "node";
  var pathValue = process.env.PATH || process.env.Path || "";
  var pathEntries = pathValue.split(path.delimiter);

  for (var index = 0; index < pathEntries.length; index += 1) {
    if (pathEntries[index]) {
      candidates.push(path.join(pathEntries[index], executableName));
    }
  }

  if (process.platform === "win32") {
    addIfPresent(candidates, path.join(process.env.ProgramFiles || "", "nodejs", "node.exe"));
    addIfPresent(candidates, path.join(process.env["ProgramFiles(x86)"] || "", "nodejs", "node.exe"));
    addIfPresent(candidates, path.join(process.env.LOCALAPPDATA || "", "Programs", "nodejs", "node.exe"));
    addLocalNodeInstalls(candidates, executableName);
  }

  return candidates;
}

function addLocalNodeInstalls(candidates, executableName) {
  var userProfile = process.env.USERPROFILE || "";
  var root = path.join(userProfile, ".local", "nodejs");
  if (!fs.existsSync(root)) {
    return;
  }

  var entries;
  try {
    entries = fs.readdirSync(root);
  } catch (error) {
    return;
  }

  for (var index = 0; index < entries.length; index += 1) {
    addIfPresent(candidates, path.join(root, entries[index], executableName));
  }
}

function addIfPresent(candidates, candidate) {
  if (candidate && fs.existsSync(candidate)) {
    candidates.push(candidate);
  }
}

function uniquePaths(paths) {
  var seen = {};
  var result = [];

  for (var index = 0; index < paths.length; index += 1) {
    var candidate = paths[index];
    if (!candidate) {
      continue;
    }

    var key = process.platform === "win32" ? candidate.toLowerCase() : candidate;
    if (!seen[key] && fs.existsSync(candidate)) {
      seen[key] = true;
      result.push(candidate);
    }
  }

  return result;
}

function readNodeMajor(candidate) {
  var result = spawnSync(candidate, ["--version"], { encoding: "utf8" });
  if (result.error || result.status !== 0) {
    return 0;
  }

  var match = String(result.stdout || "").trim().match(/^v(\d+)\./);
  return match ? Number(match[1]) : 0;
}
