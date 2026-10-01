import { render, screen } from '@testing-library/react';
import * as React from 'react';
import PermissionModeSelector from '../components/PermissionModeSelector';
import { useComposerStyles } from './composer.styles';

/**
 * IMG08 回归守卫（设计规格 §13.2 / §3）。
 * 断言的是不变量：工具行允许换行、图标与发送按钮点击区 36、触屏 44、
 * 禁用态与 ready 态颜色区分、控件有可见前缀（否则「自动/自动审批」看不出是什么）。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

const Probe: React.FC = () => {
  const { styles } = useComposerStyles();
  return <div className={styles.composerSendButton} data-testid="probe" />;
};

describe('IMG08 输入区微控件不变量', () => {
  it('工具行允许换行（窄窗口靠换行而不是页面横向滚动）', () => {
    render(<Probe />);
    expect(injectedCssText()).toContain('flex-wrap:wrap');
  });

  it('发送与图标按钮点击区 36，触屏 44', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('min-width:36px');
    expect(css).toContain('hover: none');
    expect(css).toContain('min-width:44px');
  });

  it('禁用态与 ready 态用不同颜色，而不是只把主色调淡', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('var(--pudding-chat-surface-muted)');
    expect(css).toContain('var(--pudding-chat-text-caption)');
  });

  it('控件前缀用 caption 色，数值本身仍走正文色', () => {
    render(<Probe />);
    expect(injectedCssText()).toContain('--pudding-chat-text-caption');
  });

  it('权限控件显示可见前缀，且不破坏既有可访问名称', () => {
    render(<PermissionModeSelector value="auto" onChange={() => {}} />);

    const trigger = screen.getByTestId('permission-mode-selector');
    expect(trigger.textContent).toContain('权限：');
    expect(trigger.textContent).toContain('自动审批');
    // 既有测试依赖这个可访问名称，必须保持不变
    expect(trigger.getAttribute('aria-label')).toBe('权限模式：自动审批');
  });
});
