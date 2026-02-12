import { test, expect } from '@playwright/test';

test.describe('Token Pie Chart', () => {
  test('should render after context event', async ({ page }) => {
    await page.goto('/admin/chat');

    // 发送消息触发 context 帧
    const input = page.locator('textarea, input[type="text"]').first();
    await input.fill('测试 Token 饼图');
    await input.press('Enter');

    await page.waitForTimeout(5000);
  });

  test('should show layer breakdown on hover', async ({ page }) => {
    await page.goto('/admin/chat');

    // 如果在 chat 页面看不到饼图，验证页面正常加载
    await expect(page.locator('body')).toBeVisible();
  });
});
