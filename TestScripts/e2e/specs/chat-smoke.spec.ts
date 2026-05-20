import { test, expect } from '@playwright/test';

test.describe('Chat Smoke', () => {
  test('should login, send message, and verify evidence chain', async ({ page }) => {
    // 1. Open the app with debug mode
    await page.goto('/?debug=1');
    
    // 2. Wait for redirect to login
    await page.waitForURL(/\/admin\/user\/login/);
    
    // 3. Fill in bootstrap password
    const passwordInput = page.locator('input[type="password"]');
    if (await passwordInput.isVisible()) {
      await passwordInput.fill('admin123');
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
    
    // 9. ADR-027: Verify debug API returns non-null session/trace (strong assert)
    const sessionId = await page.evaluate(() => {
      const debug = (window as any).__PUDDING_DEBUG__;
      return debug?.getLastSessionId() || null;
    });
    expect(sessionId).toBeTruthy();
    console.log('Session ID:', sessionId);

    const traceId = await page.evaluate(() => {
      const debug = (window as any).__PUDDING_DEBUG__;
      return debug?.getLastTraceId() || null;
    });
    expect(traceId).toBeTruthy();
    console.log('Trace ID:', traceId);

    // 10. ADR-027: Verify evidence API (correct route + component assertions)
    const evidenceResp = await page.request.get(
      `/api/diagnostics/e2e/evidence/${encodeURIComponent(traceId!)}`
    );
    expect(evidenceResp.ok()).toBeTruthy();
    const evidence = await evidenceResp.json();
    expect(Array.isArray(evidence.timeline)).toBeTruthy();
    expect(evidence.timeline.length).toBeGreaterThan(0);

    const components: string[] = evidence.timeline.map((x: any) => x.component).filter(Boolean);
    expect(components).toContain('agent_execution');
    const hasLlmComponent = components.some((c: string) =>
      c === 'llm_gateway' || c === 'llm_invocation' || c === 'direct_llm'
    );
    expect(hasLlmComponent).toBeTruthy();
    console.log('Evidence timeline items:', evidence.timeline.length, 'components:', components);
  });
});
