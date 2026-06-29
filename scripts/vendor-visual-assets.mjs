import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import https from "node:https";
import path from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const originalsDir = path.join(root, "third_party", "visual-assets", "originals");
const licensesDir = path.join(root, "third_party", "visual-assets", "licenses");
const runtimeDir = path.join(root, "src", "KeyWars", "wwwroot", "vendor", "keywars-assets");
const runtimeIllustrationsDir = path.join(runtimeDir, "illustrations");
const imgDir = path.join(root, "src", "KeyWars", "wwwroot", "img");
const manifestPath = path.join(root, "third_party", "visual-assets", "asset-manifest.json");
const noticesPath = path.join(root, "THIRD-PARTY-NOTICES.md");

const sources = [
  {
    id: "bootstrap-icons",
    title: "Bootstrap Icons",
    version: "1.13.1",
    license: "MIT",
    packageName: "bootstrap-icons",
    url: "https://registry.npmjs.org/bootstrap-icons/-/bootstrap-icons-1.13.1.tgz",
    homepage: "https://icons.getbootstrap.com/",
    originalFile: "bootstrap-icons-1.13.1.tgz",
    categories: ["ui-icons", "navigation", "dashboard"]
  },
  {
    id: "heroicons",
    title: "Heroicons",
    version: "2.2.0",
    license: "MIT",
    packageName: "heroicons",
    url: "https://registry.npmjs.org/heroicons/-/heroicons-2.2.0.tgz",
    homepage: "https://heroicons.com/",
    originalFile: "heroicons-2.2.0.tgz",
    categories: ["ui-icons", "achievement-icons"]
  },
  {
    id: "tabler-icons",
    title: "Tabler Icons",
    version: "3.44.0",
    license: "MIT",
    packageName: "@tabler/icons",
    url: "https://registry.npmjs.org/@tabler/icons/-/icons-3.44.0.tgz",
    homepage: "https://tabler.io/icons",
    originalFile: "tabler-icons-3.44.0.tgz",
    categories: ["ui-icons", "leaderboard-icons", "motivation-icons"]
  },
  {
    id: "lucide-static",
    title: "Lucide Static",
    version: "1.22.0",
    license: "ISC",
    packageName: "lucide-static",
    url: "https://registry.npmjs.org/lucide-static/-/lucide-static-1.22.0.tgz",
    homepage: "https://lucide.dev/",
    originalFile: "lucide-static-1.22.0.tgz",
    categories: ["ui-icons", "status-icons"]
  }
];

const illustrationSources = [
  {
    id: "open-peeps",
    title: "Open Peeps",
    version: "official-site-2026-06-29",
    license: "CC0",
    homepage: "https://www.openpeeps.com/",
    pageFile: "open-peeps.html",
    categories: ["illustrations", "empty-states", "motivation"]
  },
  {
    id: "humaaans",
    title: "Humaaans",
    version: "official-site-2026-06-29",
    license: "CC0",
    homepage: "https://www.humaaans.com/",
    pageFile: "humaaans.html",
    categories: ["illustrations", "empty-states", "motivation"]
  }
];

const iconAliases = [
  ["home", ["house-door", "home"]],
  ["house-door", ["house-door", "home"]],
  ["play", ["keyboard", "device-desktop", "play"]],
  ["keyboard", ["keyboard"]],
  ["trophy", ["trophy", "award"]],
  ["arena", ["users", "people", "user-group"]],
  ["people", ["users", "people", "user-group"]],
  ["challenge", ["flag", "flag-2"]],
  ["flag", ["flag", "flag-2"]],
  ["texts", ["file-text", "file-earmark-text", "article"]],
  ["file-earmark-text", ["file-text", "file-earmark-text"]],
  ["profile", ["user", "person", "user-circle"]],
  ["person", ["user", "person"]],
  ["settings", ["settings", "gear", "adjustments-horizontal"]],
  ["gear", ["settings", "gear"]],
  ["menu", ["menu", "list"]],
  ["list", ["menu", "list"]],
  ["close", ["x", "x-lg"]],
  ["x-lg", ["x", "x-lg"]],
  ["spark", ["sparkles", "stars", "sparkle"]],
  ["stars", ["sparkles", "stars"]],
  ["logout", ["log-out", "box-arrow-right"]],
  ["box-arrow-right", ["log-out", "box-arrow-right"]],
  ["chevron", ["chevron-right"]],
  ["chevron-right", ["chevron-right"]],
  ["target", ["target", "bullseye", "crosshair"]],
  ["bullseye", ["target", "bullseye", "crosshair"]],
  ["fire", ["flame", "fire"]],
  ["bolt", ["zap", "bolt", "lightning-charge-fill"]],
  ["lightning-charge-fill", ["zap", "bolt", "lightning-charge-fill"]],
  ["type", ["letter-case", "type", "typography"]],
  ["words", ["book-open-text", "journal-text", "notes"]],
  ["journal-text", ["book-open-text", "journal-text", "notes"]],
  ["shield", ["shield-check", "shield"]],
  ["shield-check", ["shield-check", "shield"]],
  ["play-fill", ["play-filled", "player-play-filled", "play-fill", "play"]],
  ["stopwatch", ["stopwatch", "timer", "clock"]],
  ["award", ["award", "medal"]],
  ["clipboard", ["clipboard-check", "clipboard-list", "clipboard"]],
  ["clipboard-check", ["clipboard-check", "clipboard-list", "clipboard"]],
  ["graph", ["chart-line", "graph-up-arrow", "trending-up"]],
  ["graph-up-arrow", ["chart-line", "graph-up-arrow", "trending-up"]],
  ["magic", ["wand", "magic", "sparkles"]],
  ["sun", ["sun"]],
  ["bell", ["bell"]],
  ["hexagon", ["hexagon"]],
  ["quest-rounds", ["repeat", "rotate-clockwise", "arrow-repeat"]],
  ["quest-accuracy", ["crosshair", "target", "bullseye"]],
  ["quest-tempo", ["gauge", "activity", "bolt"]],
  ["quest-arena", ["users", "people", "swords"]],
  ["quest-week", ["calendar-week", "calendar"]],
  ["quest-texts", ["file-text", "book-open-text", "article"]],
  ["mission-daily", ["calendar-check", "clipboard-check"]],
  ["mission-weekly", ["calendar-star", "calendar-week", "calendar"]],
  ["achievement-training", ["keyboard", "dumbbell"]],
  ["achievement-precision", ["crosshair", "target"]],
  ["achievement-speed", ["gauge", "speedometer", "stopwatch"]],
  ["achievement-streak", ["flame", "fire"]],
  ["achievement-arena", ["swords", "trophy"]],
  ["achievement-text", ["book-open-text", "file-text"]],
  ["achievement-team", ["users", "user-group"]],
  ["achievement-mission", ["clipboard-check", "list-checks"]],
  ["level-up", ["chevrons-up", "circle-arrow-up", "trending-up"]],
  ["xp", ["badge-plus", "sparkles", "plus-circle"]],
  ["personal-best", ["medal", "trophy", "award"]],
  ["rank", ["hexagon", "badge"]],
  ["podium", ["podium", "trophy"]],
  ["empty-leaderboard", ["chart-no-axes-column", "bar-chart-3", "trophy"]],
  ["empty-achievements", ["medal", "award"]],
  ["empty-results", ["clipboard-list", "history"]],
  ["empty-arena", ["users-round", "users"]],
  ["empty-texts", ["file-plus", "file-text"]],
  ["lock", ["lock"]],
  ["unlock", ["unlock"]],
  ["medal", ["medal"]],
  ["badge", ["badge", "hexagon"]]
];

const fallbackSymbols = {
  hexagon: '<path d="M12 2.2 20.1 6.8v10.4L12 21.8l-8.1-4.6V6.8L12 2.2Z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/><path d="M8.3 12h7.4M12 8.3v7.4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>'
};

async function main() {
  const command = process.argv[2] || "verify";
  if (command === "fetch") {
    await fetchAssets();
    return;
  }

  if (command === "build") {
    await buildAssets();
    return;
  }

  if (command === "verify") {
    await verifyAssets();
    return;
  }

  throw new Error("Unknown command. Use fetch, build, or verify.");
}

async function fetchAssets() {
  ensureDirs();
  for (const source of sources) {
    const target = path.join(originalsDir, source.originalFile);
    if (!fs.existsSync(target)) {
      await downloadFile(source.url, target);
    }
  }

  for (const source of illustrationSources) {
    await fetchIllustrationSource(source);
  }

  writeManifest(readManifestData());
}

async function buildAssets() {
  ensureDirs();
  const packageFiles = {};
  for (const source of sources) {
    const archivePath = path.join(originalsDir, source.originalFile);
    if (!fs.existsSync(archivePath)) {
      throw new Error("Missing original archive. Run npm run assets:fetch first: " + source.originalFile);
    }

    packageFiles[source.id] = parseTgz(fs.readFileSync(archivePath));
    writeLicenseFile(source, packageFiles[source.id]);
  }

  writeCc0LicenseNotes();
  writeSprite(packageFiles);
  writeIllustrations();
  writeBrandAssets();
  writeManifest(readManifestData());
}

async function verifyAssets() {
  const manifest = readJson(manifestPath);
  const runtimeFiles = listFiles(runtimeDir)
    .concat(listFiles(imgDir).filter(function(file) {
      return /(?:favicon\.svg|apple-touch-icon\.png|site\.webmanifest|keywars-circuit\.svg)$/u.test(file);
    }));

  for (const source of manifest.sources) {
    if (source.originalFile) {
      const absolute = path.join(root, source.originalFile);
      if (!fs.existsSync(absolute)) {
        throw new Error("Manifest source missing original file: " + source.originalFile);
      }

      const actual = sha256File(absolute);
      if (actual !== source.sha256) {
        throw new Error("SHA256 mismatch for " + source.originalFile);
      }
    }
  }

  const notices = fs.existsSync(noticesPath) ? fs.readFileSync(noticesPath, "utf8") : "";
  for (const source of manifest.sources) {
    if (source.noticeRequired && notices.indexOf(source.title) === -1) {
      throw new Error("THIRD-PARTY-NOTICES.md is missing " + source.title);
    }
  }

  for (const file of runtimeFiles) {
    if (/\.(svg|css|json|webmanifest)$/iu.test(file)) {
      const content = fs.readFileSync(file, "utf8");
      const externalUrls = findRuntimeExternalUrls(content);
      if (externalUrls.length > 0) {
        throw new Error("Runtime asset contains external URL in " + path.relative(root, file) + ": " + externalUrls.join(", "));
      }
    }
  }

  const sprite = fs.readFileSync(path.join(runtimeDir, "keywars-icons.svg"), "utf8");
  const required = ["kw-home", "kw-achievement-speed", "kw-mission-weekly", "kw-empty-achievements", "kw-level-up"];
  for (const id of required) {
    if (sprite.indexOf('id="' + id + '"') === -1) {
      throw new Error("Runtime sprite is missing " + id);
    }
  }

  console.log("Visual asset verification OK");
}

function ensureDirs() {
  [
    originalsDir,
    licensesDir,
    runtimeDir,
    runtimeIllustrationsDir,
    imgDir
  ].forEach(function(dir) {
    fs.mkdirSync(dir, { recursive: true });
  });
}

async function fetchIllustrationSource(source) {
  const dir = path.join(originalsDir, source.id);
  const assetsDir = path.join(dir, "assets");
  fs.mkdirSync(assetsDir, { recursive: true });

  const htmlPath = path.join(dir, source.pageFile);
  let html = "";
  try {
    if (!fs.existsSync(htmlPath)) {
      html = normalizeTextSnapshot(await fetchText(source.homepage));
      fs.writeFileSync(htmlPath, html, "utf8");
    } else {
      html = normalizeTextSnapshot(fs.readFileSync(htmlPath, "utf8"));
      fs.writeFileSync(htmlPath, html, "utf8");
    }
  } catch (error) {
    fs.writeFileSync(path.join(dir, "FETCH-BLOCKED.txt"), String(error.message || error), "utf8");
    return;
  }

  const urls = extractAssetUrls(html).slice(0, 150);
  let downloaded = 0;
  for (const url of urls) {
    const filename = safeAssetName(url);
    const target = path.join(assetsDir, filename);
    if (!fs.existsSync(target)) {
      try {
        await downloadFile(url, target);
      } catch {
        continue;
      }
    }

    downloaded += 1;
  }

  if (downloaded === 0) {
    fs.writeFileSync(path.join(dir, "FETCH-BLOCKED.txt"), "No direct SVG or PNG assets were discoverable from the official page.", "utf8");
  }
}

function normalizeTextSnapshot(content) {
  return content.replace(/[ \t]+$/gmu, "");
}

function extractAssetUrls(html) {
  const decoded = html
    .replace(/&amp;/g, "&")
    .replace(/\\u0026/g, "&");
  const matches = decoded.match(/https?:\/\/[^"'\s<>)]*\.(?:svg|png)(?:\?[^"'\s<>)]*)?/giu) || [];
  const filtered = matches.filter(function(url) {
    return url.indexOf("openpeeps") !== -1 ||
      url.indexOf("humaaans") !== -1 ||
      url.indexOf("website-files.com") !== -1 ||
      url.indexOf("assets-global.website-files.com") !== -1;
  });
  return Array.from(new Set(filtered));
}

function safeAssetName(url) {
  const clean = url.split("?")[0].split("#")[0];
  const base = path.basename(clean).replace(/[^a-z0-9._-]+/giu, "-");
  const hash = crypto.createHash("sha1").update(url).digest("hex").slice(0, 10);
  return hash + "-" + base;
}

function parseTgz(buffer) {
  const tar = zlib.gunzipSync(buffer);
  const files = {};
  let offset = 0;
  while (offset + 512 <= tar.length) {
    const header = tar.slice(offset, offset + 512);
    if (header.every(function(value) { return value === 0; })) {
      break;
    }

    const name = readTarString(header, 0, 100);
    const prefix = readTarString(header, 345, 155);
    const fullName = prefix ? prefix + "/" + name : name;
    const size = parseInt(readTarString(header, 124, 12).trim() || "0", 8);
    const type = readTarString(header, 156, 1);
    offset += 512;
    const content = tar.slice(offset, offset + size);
    if ((type === "0" || type === "") && fullName) {
      files[fullName.replace(/\\/g, "/")] = content;
    }

    offset += Math.ceil(size / 512) * 512;
  }

  return files;
}

function readTarString(buffer, start, length) {
  return buffer
    .slice(start, start + length)
    .toString("utf8")
    .replace(/\0.*$/u, "");
}

function writeLicenseFile(source, files) {
  const licensePath = Object.keys(files).find(function(file) {
    return /(^|\/)(license|licence)(\.[a-z0-9]+)?$/iu.test(file);
  });
  const target = path.join(licensesDir, source.id + ".txt");
  const body = licensePath
    ? files[licensePath].toString("utf8")
    : source.title + " declares license " + source.license + " in package metadata.";
  fs.writeFileSync(target, body, "utf8");
}

function writeCc0LicenseNotes() {
  for (const source of illustrationSources) {
    const target = path.join(licensesDir, source.id + ".txt");
    const body = source.title + "\nLicense: " + source.license + "\nSource: " + source.homepage + "\n\nThe official project page describes the assets as free for personal and commercial use under CC0. This repository vendors only assets discovered from the official project page or records that the full library download was not automatable.\n";
    fs.writeFileSync(target, body, "utf8");
  }
}

function writeSprite(packageFiles) {
  const symbols = [];
  for (const alias of iconAliases) {
    const id = alias[0];
    const names = alias[1];
    const resolved = resolveIcon(packageFiles, names);
    if (resolved) {
      symbols.push(svgToSymbol("kw-" + id, resolved.content.toString("utf8")));
    } else {
      symbols.push('<symbol id="kw-' + id + '" viewBox="0 0 24 24">' + fallbackSymbols.hexagon + "</symbol>");
    }
  }

  const sprite = '<svg xmlns="http://www.w3.org/2000/svg">\n' +
    symbols.map(function(symbol) { return "  " + symbol; }).join("\n") +
    "\n</svg>\n";
  fs.writeFileSync(path.join(runtimeDir, "keywars-icons.svg"), sprite, "utf8");
}

function resolveIcon(packageFiles, names) {
  const preferredSources = ["tabler-icons", "lucide-static", "heroicons", "bootstrap-icons"];
  for (const name of names) {
    for (const sourceId of preferredSources) {
      const files = packageFiles[sourceId];
      if (!files) {
        continue;
      }

      const match = findSvgByBase(files, name, sourceId);
      if (match) {
        return match;
      }
    }
  }

  return null;
}

function findSvgByBase(files, base, sourceId) {
  const keys = Object.keys(files).filter(function(file) {
    return file.toLowerCase().endsWith("/" + base.toLowerCase() + ".svg");
  });
  if (keys.length === 0) {
    return null;
  }

  keys.sort(function(left, right) {
    return sourcePriority(left, sourceId) - sourcePriority(right, sourceId);
  });
  return { path: keys[0], content: files[keys[0]] };
}

function sourcePriority(file, sourceId) {
  if (sourceId === "heroicons") {
    if (file.indexOf("/24/outline/") !== -1) {
      return 1;
    }

    if (file.indexOf("/24/solid/") !== -1) {
      return 2;
    }
  }

  if (sourceId === "tabler-icons" && file.indexOf("/outline/") !== -1) {
    return 1;
  }

  if (sourceId === "lucide-static" && file.indexOf("/icons/") !== -1) {
    return 1;
  }

  if (sourceId === "bootstrap-icons" && file.indexOf("/icons/") !== -1) {
    return 1;
  }

  return 9;
}

function svgToSymbol(id, svg) {
  const open = svg.match(/<svg\b([^>]*)>/iu);
  const viewBox = open && open[1].match(/\bviewBox="([^"]+)"/u)
    ? open[1].match(/\bviewBox="([^"]+)"/u)[1]
    : "0 0 24 24";
  const inheritedAttributes = open ? extractInheritedSvgAttributes(open[1]) : "";
  const inner = svg
    .replace(/<\?xml[^>]*>/giu, "")
    .replace(/<!DOCTYPE[^>]*>/giu, "")
    .replace(/<svg\b[^>]*>/iu, "")
    .replace(/<\/svg>\s*$/iu, "")
    .replace(/<title>.*?<\/title>/giu, "")
    .replace(/<desc>.*?<\/desc>/giu, "")
    .trim();
  return '<symbol id="' + id + '" viewBox="' + escapeXml(viewBox) + '"' + inheritedAttributes + ">" + inner + "</symbol>";
}

function extractInheritedSvgAttributes(attributes) {
  const allowed = ["fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit"];
  const result = [];
  for (const name of allowed) {
    const match = attributes.match(new RegExp("\\b" + name + "=\"([^\"]+)\"", "u"));
    if (match) {
      result.push(name + '="' + escapeXml(match[1]) + '"');
    }
  }

  return result.length > 0 ? " " + result.join(" ") : "";
}

function writeIllustrations() {
  const selected = selectDownloadedIllustrations();
  const slots = [
    ["motivator-achievements", "Achievement badges waiting on a neon shelf", "award"],
    ["motivator-leaderboard", "Leaderboard podium with bright progress rails", "podium"],
    ["motivator-arena", "Team arena start line with shared typing lanes", "arena"],
    ["motivator-results", "Typing result board with XP burst", "results"],
    ["motivator-texts", "Stacked training texts and a cursor path", "texts"]
  ];

  for (let index = 0; index < slots.length; index += 1) {
    const slot = slots[index];
    const downloaded = selected[index];
    const target = path.join(runtimeIllustrationsDir, slot[0] + ".svg");
    if (downloaded && downloaded.endsWith(".svg")) {
      const content = fs.readFileSync(downloaded, "utf8");
      if (findRuntimeExternalUrls(content).length === 0) {
        fs.writeFileSync(target, content, "utf8");
        continue;
      }
    }

    fs.writeFileSync(target, fallbackIllustration(slot[1], slot[2]), "utf8");
  }

  fs.writeFileSync(path.join(runtimeIllustrationsDir, "reward-burst.svg"), rewardBurstSvg(), "utf8");
}

function selectDownloadedIllustrations() {
  const candidates = [];
  for (const source of illustrationSources) {
    const assetsDir = path.join(originalsDir, source.id, "assets");
    if (!fs.existsSync(assetsDir)) {
      continue;
    }

    for (const file of listFiles(assetsDir)) {
      if (/\.svg$/iu.test(file)) {
        candidates.push(file);
      }
    }
  }

  candidates.sort();
  return candidates;
}

function fallbackIllustration(title, kind) {
  const icon = kind === "podium"
    ? '<path d="M276 252h62v88h-62zM188 284h62v56h-62zM364 304h62v36h-62z" fill="#f2b84b" opacity=".95"/><path d="M303 193 318 224 352 229 327 253 333 287 303 271 273 287 279 253 254 229 288 224Z" fill="#f6d778"/>'
    : kind === "arena"
      ? '<circle cx="224" cy="226" r="32" fill="#55d7e6"/><circle cx="336" cy="226" r="32" fill="#f2b84b"/><path d="M158 334c17-49 112-49 132 0M270 334c17-49 112-49 132 0" fill="none" stroke="#d9f8ff" stroke-width="18" stroke-linecap="round"/>'
      : kind === "texts"
        ? '<rect x="180" y="166" width="210" height="138" rx="18" fill="#102637" stroke="#55d7e6" stroke-width="5"/><path d="M214 206h138M214 242h112M214 278h154" stroke="#d9f8ff" stroke-width="12" stroke-linecap="round"/>'
        : kind === "results"
          ? '<rect x="168" y="154" width="244" height="158" rx="20" fill="#102637" stroke="#55d7e6" stroke-width="5"/><path d="M206 260h52M206 216h84M318 262l48-74" stroke="#d9f8ff" stroke-width="14" stroke-linecap="round"/><circle cx="366" cy="188" r="24" fill="#8fe36d"/>'
          : '<path d="M287 129 327 209 416 222 352 284 367 372 287 330 208 372 223 284 159 222 247 209Z" fill="#f2b84b"/><path d="M234 250h107M260 286h55" stroke="#102637" stroke-width="16" stroke-linecap="round"/>';

  return '<svg xmlns="http://www.w3.org/2000/svg" width="560" height="420" viewBox="0 0 560 420" role="img" aria-label="' + escapeXml(title) + '"><rect width="560" height="420" rx="32" fill="#07131c"/><path d="M72 320C141 176 275 125 486 148" fill="none" stroke="#143247" stroke-width="24" stroke-linecap="round" opacity=".7"/><path d="M82 332C183 252 291 226 478 253" fill="none" stroke="#55d7e6" stroke-width="8" stroke-linecap="round" opacity=".35"/>' + icon + '<circle cx="100" cy="102" r="12" fill="#55d7e6"/><circle cx="448" cy="96" r="16" fill="#8fe36d"/><circle cx="462" cy="330" r="10" fill="#f2b84b"/></svg>\n';
}

function rewardBurstSvg() {
  return '<svg xmlns="http://www.w3.org/2000/svg" width="360" height="240" viewBox="0 0 360 240" role="img" aria-label="XP reward burst"><path d="M180 34v42M180 164v42M75 120h42M243 120h42M106 46l30 30M224 164l30 30M254 46l-30 30M136 164l-30 30" stroke="#55d7e6" stroke-width="10" stroke-linecap="round"/><circle cx="180" cy="120" r="54" fill="#f2b84b"/><path d="M156 103h49M156 122h38M156 141h49" stroke="#102637" stroke-width="12" stroke-linecap="round"/></svg>\n';
}

function writeBrandAssets() {
  const badgeSvg = '<svg xmlns="http://www.w3.org/2000/svg" width="192" height="192" viewBox="0 0 192 192" role="img" aria-label="KeyWars"><rect width="192" height="192" rx="42" fill="#06111a"/><path d="M96 18 162 56v80l-66 38-66-38V56Z" fill="#0d2232" stroke="#55d7e6" stroke-width="8"/><path d="M68 66v60M68 96h44M112 66 84 96l30 30" fill="none" stroke="#f2b84b" stroke-width="14" stroke-linecap="round" stroke-linejoin="round"/><circle cx="138" cy="72" r="10" fill="#8fe36d"/></svg>\n';
  fs.writeFileSync(path.join(runtimeIllustrationsDir, "keywars-badge.svg"), badgeSvg, "utf8");
  fs.writeFileSync(path.join(imgDir, "favicon.svg"), badgeSvg, "utf8");
  fs.writeFileSync(path.join(imgDir, "site.webmanifest"), JSON.stringify({
    name: "KeyWars",
    short_name: "KeyWars",
    icons: [
      { src: "/img/favicon.svg", sizes: "any", type: "image/svg+xml" },
      { src: "/img/apple-touch-icon.png", sizes: "180x180", type: "image/png" }
    ],
    theme_color: "#06111a",
    background_color: "#06111a",
    display: "standalone"
  }, null, 2) + "\n", "utf8");
  writePngIcon(path.join(imgDir, "apple-touch-icon.png"));
}

function writePngIcon(target) {
  const size = 180;
  const data = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y += 1) {
    for (let x = 0; x < size; x += 1) {
      const index = (y * size + x) * 4;
      const dx = x - size / 2;
      const dy = y - size / 2;
      const dist = Math.sqrt(dx * dx + dy * dy);
      const insideHex = Math.abs(dx) * 0.866 + Math.abs(dy) * 0.5 < 70;
      data[index] = insideHex ? 13 : 6;
      data[index + 1] = insideHex ? 34 : 17;
      data[index + 2] = insideHex ? 50 : 26;
      data[index + 3] = 255;
      if (insideHex && (Math.abs(dist - 56) < 4 || Math.abs(x - y) < 3 && x > 58 && x < 122)) {
        data[index] = 85;
        data[index + 1] = 215;
        data[index + 2] = 230;
      }

      if ((Math.abs(x - 68) < 7 && y > 54 && y < 126) ||
        (Math.abs(y - 90) < 7 && x > 68 && x < 122) ||
        (Math.abs((x - 106) - (y - 90)) < 7 && x > 92 && x < 138 && y > 62 && y < 118) ||
        (Math.abs((x - 106) + (y - 90)) < 7 && x > 92 && x < 138 && y > 62 && y < 118)) {
        data[index] = 242;
        data[index + 1] = 184;
        data[index + 2] = 75;
      }
    }
  }

  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y += 1) {
    raw[y * (size * 4 + 1)] = 0;
    data.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }

  const png = Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk("IHDR", Buffer.concat([u32(size), u32(size), Buffer.from([8, 6, 0, 0, 0])])),
    pngChunk("IDAT", zlib.deflateSync(raw)),
    pngChunk("IEND", Buffer.alloc(0))
  ]);
  fs.writeFileSync(target, png);
}

function pngChunk(type, data) {
  const typeBuffer = Buffer.from(type, "ascii");
  const crcInput = Buffer.concat([typeBuffer, data]);
  return Buffer.concat([u32(data.length), typeBuffer, data, u32(crc32(crcInput))]);
}

function u32(value) {
  const buffer = Buffer.alloc(4);
  buffer.writeUInt32BE(value >>> 0, 0);
  return buffer;
}

function crc32(buffer) {
  let crc = 0xffffffff;
  for (let index = 0; index < buffer.length; index += 1) {
    crc ^= buffer[index];
    for (let bit = 0; bit < 8; bit += 1) {
      crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
    }
  }

  return (crc ^ 0xffffffff) >>> 0;
}

function readManifestData() {
  const existingGeneratedAt = readExistingGeneratedAt();
  const manifestSources = sources.map(function(source) {
    const originalRelative = path.join("third_party", "visual-assets", "originals", source.originalFile).replace(/\\/g, "/");
    const absolute = path.join(root, originalRelative);
    return {
      id: source.id,
      title: source.title,
      version: source.version,
      license: source.license,
      homepage: source.homepage,
      packageName: source.packageName,
      sourceUrl: source.url,
      originalFile: originalRelative,
      sha256: fs.existsSync(absolute) ? sha256File(absolute) : "",
      licenseFile: path.join("third_party", "visual-assets", "licenses", source.id + ".txt").replace(/\\/g, "/"),
      runtimeFiles: [
        "src/KeyWars/wwwroot/vendor/keywars-assets/keywars-icons.svg"
      ],
      categories: source.categories,
      noticeRequired: true
    };
  });

  for (const source of illustrationSources) {
    const dirRelative = path.join("third_party", "visual-assets", "originals", source.id).replace(/\\/g, "/");
    const pageRelative = path.join(dirRelative, source.pageFile).replace(/\\/g, "/");
    const pageAbsolute = path.join(root, pageRelative);
    const assetsAbsolute = path.join(root, dirRelative, "assets");
    const files = fs.existsSync(assetsAbsolute)
      ? listFiles(assetsAbsolute).map(function(file) { return path.relative(root, file).replace(/\\/g, "/"); })
      : [];
    manifestSources.push({
      id: source.id,
      title: source.title,
      version: source.version,
      license: source.license,
      homepage: source.homepage,
      sourceUrl: source.homepage,
      originalFile: fs.existsSync(pageAbsolute) ? pageRelative : "",
      sha256: fs.existsSync(pageAbsolute) ? sha256File(pageAbsolute) : "",
      downloadedAssetCount: files.length,
      downloadedAssets: files,
      licenseFile: path.join("third_party", "visual-assets", "licenses", source.id + ".txt").replace(/\\/g, "/"),
      runtimeFiles: [
        "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/motivator-achievements.svg",
        "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/motivator-leaderboard.svg",
        "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/motivator-arena.svg",
        "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/motivator-results.svg",
        "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/motivator-texts.svg"
      ],
      categories: source.categories,
      noticeRequired: true,
      automationNote: files.length > 0
        ? "Direct official-page SVG/PNG assets were downloaded. Full library downloads that require interactive checkout are not automated."
        : "No direct SVG/PNG assets were discoverable or downloadable from the official page. Runtime illustrations use project-owned fallbacks."
    });
  }

  manifestSources.push({
    id: "keywars-owned-visuals",
    title: "KeyWars project-owned visual assets",
    version: "repository",
    license: "Repository license",
    homepage: "",
    sourceUrl: "",
    originalFile: "",
    sha256: "",
    licenseFile: "",
    runtimeFiles: [
      "src/KeyWars/wwwroot/img/keywars-circuit.svg",
      "src/KeyWars/wwwroot/img/favicon.svg",
      "src/KeyWars/wwwroot/img/apple-touch-icon.png",
      "src/KeyWars/wwwroot/img/site.webmanifest",
      "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/keywars-badge.svg",
      "src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/reward-burst.svg"
    ],
    categories: ["branding", "backgrounds", "fallback-illustrations"],
    noticeRequired: false
  });

  return {
    generatedAt: existingGeneratedAt || new Date().toISOString(),
    offlineRuntimePolicy: "Runtime pages must load only same-origin assets from wwwroot. Source downloads are build-time only.",
    sources: manifestSources
  };
}

function readExistingGeneratedAt() {
  if (!fs.existsSync(manifestPath)) {
    return "";
  }

  try {
    const manifest = readJson(manifestPath);
    return typeof manifest.generatedAt === "string" ? manifest.generatedAt : "";
  } catch {
    return "";
  }
}

function writeManifest(manifest) {
  fs.mkdirSync(path.dirname(manifestPath), { recursive: true });
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
}

function readJson(file) {
  if (!fs.existsSync(file)) {
    throw new Error("Missing JSON file: " + path.relative(root, file));
  }

  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function listFiles(dir) {
  if (!fs.existsSync(dir)) {
    return [];
  }

  const result = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const absolute = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      result.push.apply(result, listFiles(absolute));
    } else {
      result.push(absolute);
    }
  }

  return result;
}

function findRuntimeExternalUrls(content) {
  const matches = content.match(/https?:\/\/[^"'\s)<>]+/giu) || [];
  return matches.filter(function(url) {
    return url.indexOf("http://www.w3.org/2000/svg") !== 0 &&
      url.indexOf("http://www.w3.org/1999/xlink") !== 0;
  });
}

function sha256File(file) {
  return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

function escapeXml(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function downloadFile(url, target) {
  return new Promise(function(resolve, reject) {
    fs.mkdirSync(path.dirname(target), { recursive: true });
    const file = fs.createWriteStream(target);
    requestUrl(url, function(response) {
      if (response.statusCode < 200 || response.statusCode >= 300) {
        file.close();
        fs.unlinkSync(target);
        reject(new Error("Download failed " + response.statusCode + " for " + url));
        return;
      }

      response.pipe(file);
      file.on("finish", function() {
        file.close(resolve);
      });
    }, reject);
  });
}

function fetchText(url) {
  return new Promise(function(resolve, reject) {
    requestUrl(url, function(response) {
      if (response.statusCode < 200 || response.statusCode >= 300) {
        reject(new Error("Fetch failed " + response.statusCode + " for " + url));
        return;
      }

      const chunks = [];
      response.on("data", function(chunk) { chunks.push(chunk); });
      response.on("end", function() { resolve(Buffer.concat(chunks).toString("utf8")); });
    }, reject);
  });
}

function requestUrl(url, onResponse, onError, redirects) {
  const count = redirects || 0;
  const parsed = new URL(url);
  const client = parsed.protocol === "http:" ? http : https;
  const request = client.get(parsed, {
    headers: {
      "user-agent": "KeyWars visual asset vendor/1.0"
    }
  }, function(response) {
    if ([301, 302, 303, 307, 308].indexOf(response.statusCode) !== -1 && response.headers.location && count < 5) {
      response.resume();
      requestUrl(new URL(response.headers.location, parsed).toString(), onResponse, onError, count + 1);
      return;
    }

    onResponse(response);
  });
  request.on("error", onError);
}

main().catch(function(error) {
  console.error(error.stack || error.message || error);
  process.exitCode = 1;
});
