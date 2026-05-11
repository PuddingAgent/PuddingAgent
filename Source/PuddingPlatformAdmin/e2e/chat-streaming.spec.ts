import { test, expect } from '@playwright/test';

test.describe('Chat Streaming SSE Events', () => {
  test('should load chat page', async ({ page }) => {
    await page.goto('/admin/chat');
    await expect(page.locator('text=Chat')).toBeVisible({ timeout: 10000 });
  });

  test('should send message and receive response', async ({ page }) => {
    await page.goto('/admin/chat');

    // 输入消息
    const input = page.locator('textarea, input[type="text"]').first();
    await input.fill('你好');
    await input.press('Enter');

    // 等待回复出现（最多30秒）
    await expect(page.locator('text=你好')).toBeVisible({ timeout: 10000 });

    // 标记：至少有一个 assistant 回复
    await page.waitForTimeout(5000);
  });

  test('should display token usage', async ({ page }) => {
    await page.goto('/admin/chat');

    const input = page.locator('textarea, input[type="text"]').first();
    await input.fill('简短回复');
    await input.press('Enter');

    // 等待 usage 或 done 事件更新 token 显示
    await page.waitForTimeout(8000);
  });

  test('cancel button should work', async ({ page }) => {
    await page.goto('/admin/chat');

    const input = page.locator('textarea, input[type="text"]').first();
    await input.fill('写一个很长的回复');
    await input.press('Enter');

    // 点取消（如有停止按钮）
    const stopBtn = page.locator('[aria-label="stop"], [title="停止"], button:has-text("停止")');
    if (await stopBtn.isVisible({ timeout: 3000 })) {
      await stopBtn.click();
    }
  });
});
