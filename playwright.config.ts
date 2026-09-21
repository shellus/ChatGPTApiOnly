import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./ui/tests",
  use: {
    baseURL: "http://127.0.0.1:1420",
    headless: true,
    channel: process.env.PLAYWRIGHT_CHANNEL,
  },
  webServer: {
    command: "npm run dev",
    url: "http://127.0.0.1:1420",
    reuseExistingServer: !process.env.CI,
  },
});
