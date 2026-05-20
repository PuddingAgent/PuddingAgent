import { test, expect } from '@playwright/test';

test.describe('Chat Smoke', () => {
  test('should login and send a message', async ({ page }) => {
    // 1. Open the app
    await page.goto('/');
    
    // 2. Wait for redirect to login
    await page.waitForURL(/\/admin\/user\/login/);
    
    // 3. Fill in bootstrap password (bootstrap default: 空或test)
    // Note: 根据实际登录页调整
    const passwordInput = page.locator('input[type="password"]');
    if (await passwordInput.isVisible()) {
      await passwordInput.fill('admin123'); // 根据实际默认密码调整
      await page.locator('button[type="submit"]').click();
    }
    
    // 4. Wait for redirect to chat page
    await page.waitForURL(/\/admin\/chat/, { timeout: 15000 });
    
    // 5. Click "新对话" to create new session
    const newChatBtn = page.locator('[data-testid="chat-new-session"]');
    if (await newChatBtn.isVisible()) {
      await newChatBtn.click();
    }
    
    // 6. Type and send a message
    const input = page.locator('[data-testid="chat-input"]');
    await input.fill('你好，请简单介绍你自己');
    
    const sendBtn = page.locator('[data-testid="chat-send"]');
    await sendBtn.click();
    
    // 7. Wait for a message to appear in the list (up to 30s for LLM response)
    await page.locator('[data-testid="chat-message-list"]').waitFor({ timeout: 30000 });
    
    // 8. Verify at least one assistant message
    const messages = page.locator('[data-testid^="chat-message-"]');
    await expect(messages.first()).toBeVisible({ timeout: 10000 });
    
    // 9. Collect trace/session ID from debug API if available
    const traceId = await page.evaluate(() => {
      const debug = (window as any).__PUDDING_DEBUG__;
      return debug?.getLastTraceId() || null;
    });
    console.log('Trace ID:', traceId);
    console.log('Session ID:', await page.evaluate(() => {
      const debug = (window as any).__PUDDING_DEBUG__;
      return debug?.getLastSessionId() || null;
    }));
  });
});
