import { render } from '@testing-library/react';
import * as React from 'react';
import { useAgentStyles } from './agent.styles';

/**
 * IMG09 回归守卫（设计规格 §13.2）。
 * Agent 行状态徽标必须主题感知：原实现把浅色主题的实块色写死
 * （#fff4d6 / #dcfce7 等），深色主题下就成了比主操作还亮的近白块。
 * 断言改为「硬编码浅色块不得回来」+「必须走语义状态 token」。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

const Probe: React.FC = () => {
  const { styles } = useAgentStyles();
  return (
    <span
      className={`${styles.agentStatusTag} ${styles.agentStatusTag_working} ${styles.agentStatusTag_idle} ${styles.agentStatusTag_disabled}`}
      data-testid="probe"
    />
  );
};

describe('IMG09 Agent 状态徽标不变量', () => {
  it('使用语义状态色 + 轻底，而不是硬编码浅色实块', () => {
    render(<Probe />);
    const css = injectedCssText();

    expect(css).toContain('var(--pudding-status-waiting)');
    expect(css).toContain('var(--pudding-status-success)');
    // 半透明轻底（color-mix + transparent），深色主题下不会变成亮块
    expect(css).toContain('transparent');

    for (const legacy of [
      '#fff4d6', // 旧「工作中」浅黄实底
      '#f2cf7a',
      '#8a4b00',
      '#dcfce7', // 旧「在线」浅绿实底
      '#9be5b7',
      '#216e48',
    ]) {
      expect(css).not.toContain(legacy);
    }
  });

  it('禁用态已经是主题 token（不回归硬编码）', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('var(--pudding-chat-surface-muted)');
    expect(css).toContain('var(--pudding-chat-border)');
  });
});
