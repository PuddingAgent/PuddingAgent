import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './specs',
  timeout: 60000,
  retries: 0,
  use: {
    baseURL: process.env.PUDDING_E2E_BASE_URL || 'http://localhost:5000',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
  },
  outputDir: './artifacts',
});
