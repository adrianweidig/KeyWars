const { defineConfig, devices } = require("@playwright/test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const port = Number(process.env.KEYWARS_BROWSER_PORT || 5187);
const baseURL = process.env.KEYWARS_BROWSER_BASE_URL || `http://127.0.0.1:${port}`;
const dataDirectory = process.env.KEYWARS_BROWSER_DATA_DIRECTORY ||
  fs.mkdtempSync(path.join(os.tmpdir(), "keywars-browser-"));
const browserChannel = process.env.KEYWARS_BROWSER_CHANNEL || (process.env.CI ? undefined : "chrome");

module.exports = defineConfig({
  testDir: "./tests/browser",
  timeout: 60_000,
  expect: {
    timeout: 10_000
  },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [
    ["list"],
    ["html", { outputFolder: "output/playwright/html-report", open: "never" }]
  ],
  outputDir: "output/playwright/test-results",
  webServer: {
    command: `node tests/browser/start-keywars.mjs ${port}`,
    url: `${baseURL}/health/ready`,
    timeout: 120_000,
    reuseExistingServer: !process.env.CI,
    stdout: "pipe",
    stderr: "pipe",
    env: {
      ASPNETCORE_ENVIRONMENT: "Development",
      KEYWARS__AUTH__DEVELOPMENT_LOGIN: "true",
      KEYWARS__DATA__DIRECTORY: dataDirectory,
      KEYWARS__LIVE__COUNTDOWN_SECONDS: "1",
      Logging__LogLevel__Default: "Warning",
      Logging__LogLevel__Microsoft: "Warning"
    }
  },
  use: {
    baseURL,
    colorScheme: "dark",
    reducedMotion: "reduce",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off"
  },
  projects: [
    {
      name: "chromium-1366",
      use: {
        ...devices["Desktop Chrome"],
        browserName: "chromium",
        channel: browserChannel,
        viewport: { width: 1366, height: 768 }
      }
    }
  ]
});
