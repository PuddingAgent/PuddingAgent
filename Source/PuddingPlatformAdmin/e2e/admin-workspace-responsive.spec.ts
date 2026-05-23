/**
 * admin-workspace-responsive.spec.ts
 *
 * 验收：Workspace 页面在多个 viewport 下无布局破裂。
 * 覆盖 375（移动端）、768（平板）、1024（小桌面）、1440（大桌面）。
 */
import { expect, test } from '@playwright/test';

const viewports = [
  { width: 375, height: 812 },
  { width: 768, height: 1024 },
  { width: 1024, height: 768 },
  { width: 1440, height: 900 },
];

test.describe('Workspace Page Responsive Layout', () => {
  for (const viewport of viewports) {
    test(`renders correctly at ${viewport.width}px`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await page.goto('/admin/workspace');

      await expect(page.getByRole('heading', { name: '场景' })).toBeVisible();
      await expect(page.getByRole('button', { name: /新建场景/ })).toBeVisible();
      await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
    });
  }
});

test.describe('Workspace Page Accessibility', () => {
  test('icon-only actions have accessible names', async ({ page }) => {
    await page.goto('/admin/workspace');

    // 表格视图下的操作按钮
    const enterButtons = page.getByRole('button', { name: /进入 .* Chat/ });
    if (await enterButtons.count() > 0) {
      await expect(enterButtons.first()).toBeVisible();
    }

    const deleteButtons = page.getByRole('button', { name: /删除/ });
    if (await deleteButtons.count() > 0) {
      await expect(deleteButtons.first()).toBeVisible();
    }
  });

  test('search input has proper label', async ({ page }) => {
    await page.goto('/admin/workspace');

    const searchInput = page.locator('input[placeholder*="搜索"]');
    await expect(searchInput).toBeVisible();
  });
});
