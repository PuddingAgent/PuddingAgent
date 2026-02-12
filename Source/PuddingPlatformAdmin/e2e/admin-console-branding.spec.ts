/**
 * admin-console-branding.spec.ts
 *
 * 验收：Admin Console 不显示 Ant Design Pro 模板痕迹。
 * 阻断 Ant Pro footer、水印、模板链接和品牌文案回归。
 */
import { expect, test } from '@playwright/test';

test.describe('Admin Console Branding — No Ant Design Pro', () => {
  test('workspace page does not expose Ant Design Pro branding', async ({ page }) => {
    await page.goto('/admin/pudding/workspaces');

    await expect(page.getByText('Ant Design Pro')).toHaveCount(0);
    await expect(page.getByText('Powered by Ant Design')).toHaveCount(0);
    await expect(page.getByText('Powered by Ant Desgin')).toHaveCount(0);
    await expect(page.locator('a[href*="pro.ant.design"]')).toHaveCount(0);
    await expect(page.locator('a[href*="github.com/ant-design/ant-design-pro"]')).toHaveCount(0);
  });

  test('workspace page heading is visible', async ({ page }) => {
    await page.goto('/admin/pudding/workspaces');

    await expect(page.getByRole('heading', { name: '场景' })).toBeVisible();
    await expect(page.getByRole('button', { name: /新建场景/ })).toBeVisible();
  });

  test('workspace page uses Segmented view toggle, not Radio.Button', async ({ page }) => {
    await page.goto('/admin/pudding/workspaces');

    await expect(page.getByRole('button', { name: '表格' })).toBeVisible();
    await expect(page.getByRole('button', { name: '卡片' })).toBeVisible();
  });
});
