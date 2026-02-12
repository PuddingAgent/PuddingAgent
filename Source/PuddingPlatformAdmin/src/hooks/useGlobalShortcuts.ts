import { useEffect } from 'react';

/**
 * 全局键盘快捷键
 * - Ctrl+Enter: 在 chat 页面触发发送消息（派发 pudding:chat:send 自定义事件）
 * - Esc: 关闭当前打开的 Drawer/Modal（antd Modal 默认已处理 Esc；此处补全 Drawer 场景）
 * - Ctrl+K: 在管理页面聚焦搜索框（优先 .ant-input-search input，其次带有 placeholder 含"搜索"的 input）
 *
 * 冲突检测：
 * - Ctrl+K：仅在非 chat 页面触发，避免覆盖浏览器默认行为
 * - 当焦点在 input/textarea/select 等表单控件内时，Esc 不触发（允许用户正常取消输入）
 */

const INPUT_ELEMENTS = new Set(['INPUT', 'TEXTAREA', 'SELECT']);

function isEditableFocused(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  if (INPUT_ELEMENTS.has(el.tagName)) return true;
  if ((el as HTMLElement).isContentEditable) return true;
  return false;
}

function isChatPage(): boolean {
  const path = window.location.pathname;
  // /chat 独立聊天页，或 /workspace/:id?tab=chat
  return path === '/chat' || (path.startsWith('/workspace/') && window.location.search.includes('tab=chat'));
}

function closeTopDrawer(): void {
  // 查找当前打开的 Drawer wrapper（antd 5.x: .ant-drawer-open）
  const openDrawers = document.querySelectorAll('.ant-drawer-open');
  if (openDrawers.length > 0) {
    // 点击最后一个（最顶层）Drawer 的关闭按钮
    const topDrawer = openDrawers[openDrawers.length - 1];
    const closeBtn = topDrawer.querySelector('.ant-drawer-close') as HTMLElement | null;
    if (closeBtn) {
      closeBtn.click();
      return;
    }
    // fallback: 点击 mask
    const mask = topDrawer.querySelector('.ant-drawer-mask') as HTMLElement | null;
    if (mask) mask.click();
  }
}

function focusSearchInput(): void {
  // 优先找 antd 搜索框
  const searchInput = document.querySelector('.ant-input-search input') as HTMLInputElement | null;
  if (searchInput) {
    searchInput.focus();
    return;
  }
  // 其次找带有搜索 placeholder 的 input
  const inputs = document.querySelectorAll('input[placeholder]');
  for (const input of inputs) {
    const ph = (input as HTMLInputElement).placeholder.toLowerCase();
    if (ph.includes('search') || ph.includes('搜索') || ph.includes('查找')) {
      (input as HTMLInputElement).focus();
      return;
    }
  }
}

export function useGlobalShortcuts(): void {
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      // Ctrl+Enter: 触发发送消息（chat 页面监听此自定义事件执行发送）
      if (e.ctrlKey && e.key === 'Enter') {
        if (isChatPage()) {
          e.preventDefault();
          window.dispatchEvent(new CustomEvent('pudding:chat:send'));
          return;
        }
      }

      // Esc: 关闭 Drawer（Modal 由 antd 内置 keyboard 处理）
      if (e.key === 'Escape') {
        // 如果焦点在输入框内，不拦截（允许用户取消输入建议等）
        if (!isEditableFocused()) {
          closeTopDrawer();
        }
        return;
      }

      // Ctrl+K: 聚焦搜索框（仅在管理页面，非 chat 页面）
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        if (!isChatPage()) {
          e.preventDefault();
          focusSearchInput();
        }
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);
}
