import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  timeout: 60000,
  retries: 1,
  use: {
    baseURL: 'http://localhost:5000',
    headless: true,
    viewport: { width: 1280, height: 720 },
    video: 'off',
  },
  webServer: {
    command: 'echo "Server already running via Docker"',
    url: 'http://localhost:5000',
    reuseExistingServer: true,
  },
});
