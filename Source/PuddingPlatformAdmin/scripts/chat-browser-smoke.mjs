import { chromium } from '@playwright/test';
import fs from 'node:fs';

const base = process.env.SMOKE_BASE ?? 'http://127.0.0.1:5199';
const token = fs.readFileSync(process.env.SMOKE_TOKEN_FILE, 'utf8').trim();

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await context.addInitScript((value) => {
  localStorage.setItem('pudding_token', value);
}, token);
const page = await context.newPage();
const consoleErrors = [];
const failedResponses = [];
const chatRequests = [];
page.on('pageerror', (error) => consoleErrors.push(String(error)));
page.on('console', (message) => {
  if (message.type() === 'error') consoleErrors.push(message.text());
});
page.on('response', (response) => {
  if (response.status() >= 400) {
    failedResponses.push({ status: response.status(), url: response.url() });
  }
});
page.on('request', (request) => {
  const url = request.url();
  if (
    url.includes('/conversation') ||
    url.includes('/history') ||
    url.includes('/process') ||
    url.includes('/agents/status')
  ) {
    chatRequests.push(url);
  }
});

const startedAt = Date.now();
await page.goto(`${base}/admin/chat`, { waitUntil: 'domcontentloaded', timeout: 60000 });
const domContentLoadedMs = Date.now() - startedAt;
await page.waitForSelector('[data-testid="chat-message-list"]', { timeout: 60000 });
const messageListMs = Date.now() - startedAt;
let firstContentMs = null;
try {
  await page.waitForFunction(
    () => document.body.innerText.includes('加载更多历史消息'),
    { timeout: 30000 },
  );
  firstContentMs = Date.now() - startedAt;
} catch {
  firstContentMs = null;
}
await page.waitForTimeout(1500);

let expandResult = null;
const expandLink = page.locator('a[href^="#pudding-expand-"]').first();
if ((await expandLink.count()) > 0) {
  const beforeRequests = chatRequests.filter((url) => url.includes('/content?')).length;
  const startedExpandAt = Date.now();
  await expandLink.click();
  await page.waitForTimeout(1500);
  expandResult = {
    latencyMs: Date.now() - startedExpandAt,
    contentRequests:
      chatRequests.filter((url) => url.includes('/content?')).length - beforeRequests,
    remainingLinks: await page.locator('a[href^="#pudding-expand-"]').count(),
  };
}

const metrics = await page.evaluate(() => {
  const paints = performance.getEntriesByType('paint').map((entry) => ({
    name: entry.name,
    startTime: Math.round(entry.startTime),
  }));
  const nav = performance.getEntriesByType('navigation')[0];
  const resources = performance.getEntriesByType('resource');
  return {
    paints,
    domContentLoadedMs: nav ? Math.round(nav.domContentLoadedEventEnd) : null,
    loadEventMs: nav ? Math.round(nav.loadEventEnd) : null,
    resourceCount: resources.length,
    transferBytes: resources.reduce((sum, entry) => sum + (entry.transferSize ?? 0), 0),
    messageRows: document.querySelectorAll('[data-testid="chat-message-list"] [role]').length,
    agentRows: document.querySelectorAll('[data-testid="chat-message-list"] [role="agent"]').length,
    userRows: document.querySelectorAll('[data-testid="chat-message-list"] [role="user"]').length,
    documentAgentRows: document.querySelectorAll('[role="agent"]').length,
    documentUserRows: document.querySelectorAll('[role="user"]').length,
    badge: document.querySelector('[data-testid="frontend-version-badge"]')?.textContent?.trim() ?? null,
    bodyTextPreview: document.body.innerText.slice(0, 300),
  };
});

await page.screenshot({ path: 'temp/test-out/chat-smoke.png', fullPage: false });
const report = {
  base,
  domContentLoadedMs,
  messageListMs,
  firstContentMs,
  expandResult,
  metrics,
  failedResponses: failedResponses.slice(0, 30),
  chatRequests: chatRequests.slice(0, 30),
  consoleErrors: consoleErrors.slice(0, 20),
  capturedAt: new Date().toISOString(),
};
fs.writeFileSync('temp/test-out/chat-smoke.json', JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
await browser.close();
