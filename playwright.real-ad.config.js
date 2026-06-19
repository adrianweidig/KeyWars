const { defineConfig, devices } = require("@playwright/test");

const baseURL = process.env.KEYWARS_REAL_AD_BASE_URL || "http://127.0.0.1:18080";
const browserChannel = process.env.KEYWARS_BROWSER_CHANNEL || (process.env.CI ? undefined : "chrome");
const forwardedProto = process.env.KEYWARS_REAL_AD_FORWARDED_PROTO || "https";
const forwardedFor = process.env.KEYWARS_REAL_AD_FORWARDED_FOR || "127.0.0.1";

module.exports = defineConfig({
  testDir: "./tests/real-ad",
  timeout: 90_000,
  expect: {
    timeout: 12_000
  },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [
    ["list"],
    ["html", { outputFolder: "output/playwright/real-ad-html-report", open: "never" }]
  ],
  outputDir: "output/playwright/real-ad-results",
  use: {
    baseURL,
    extraHTTPHeaders: {
      "X-Forwarded-For": forwardedFor,
      "X-Forwarded-Proto": forwardedProto
    },
    colorScheme: "dark",
    reducedMotion: "reduce",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off"
  },
  projects: [
    {
      name: "real-ad-chromium-1366",
      use: {
        ...devices["Desktop Chrome"],
        browserName: "chromium",
        channel: browserChannel,
        viewport: { width: 1366, height: 768 }
      }
    }
  ]
});
