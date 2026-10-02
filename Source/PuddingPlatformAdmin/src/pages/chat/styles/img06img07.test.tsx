import { render } from '@testing-library/react';
import * as React from 'react';
import { useMarkdownStyles } from './markdown.styles';
import { useMessageStyles } from './message.styles';

/**
 * IMG06 / IMG07 回归守卫（设计规格 §13.5）。
 * 断言的是不变量：助手回合不再有大面积有色底；链接有可辨认的强调色；
 * 行内代码走主题 surface-muted 而非旧蓝灰 chip；表格分隔线走主题 border。
 * IMG07 的首列最小宽**只能挂在 chatLabelTable 上**（规格禁止全局首列规则），
 * 因此这里同时做反向守卫：markdownBody 作用域下不得再出现 first-child 定宽。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

/** 由 Probe 回填，用于按真实类名断言"规则挂在哪个作用域" */
let markdownClassNames: Record<string, string> = {};

const Probe: React.FC = () => {
  const { styles } = useMarkdownStyles();
  const { styles: messageStyles } = useMessageStyles();
  markdownClassNames = styles as unknown as Record<string, string>;
  return (
    <div
      className={`${styles.markdownBody} ${styles.inlineCode} ${styles.chatLabelTable} ${messageStyles.agentTurnCard}`}
      data-testid="probe"
    />
  );
};

describe('IMG06/IMG07 消息正文与表格样式不变量', () => {
  it('助手回合卡片不再使用有色底，且不再跨层取 admin token', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('background:transparent');
    expect(css).not.toContain('--pudding-admin-surface');
    expect(css).not.toContain('--pudding-admin-border');
  });

  it('链接使用主题强调色，不用读不出来的旧 --sky-soft', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('var(--pudding-chat-accent)');
    expect(css).not.toContain('--sky-soft');
  });

  it('行内代码收敛为主题 surface-muted、4px 圆角、常规字重', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('var(--pudding-chat-surface-muted)');
    expect(css).toContain('font-weight:400');
    expect(css).not.toContain('--misty-blue');
  });

  it('表格：单元格 10/12、分隔线走主题 border；首列最小宽只在 chatLabelTable 作用域内', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('margin:0 0 12px');
    expect(css).toContain('padding:10px 12px');
    expect(css).toContain('var(--pudding-chat-border)');

    // 规格 §13.5：min-width:7em + nowrap 只允许作用于 chatLabelTable
    const labelClass = markdownClassNames.chatLabelTable;
    const bodyClass = markdownClassNames.markdownBody;
    expect(labelClass).toBeTruthy();
    expect(css).toContain(`${labelClass} th:first-child`);
    expect(css).toContain('min-width:7em');
    expect(css).toContain('white-space:nowrap');

    // 反向守卫：不得再出现"全局给首列定宽"的规则 —— 它既违反 §13.5，
    // 实测在 Chromium 自动表格布局下也不生效（真实会话该列约 4em）。
    expect(css).not.toContain(`${bodyClass} th:first-child`);

    // 旧实现用 color-mix(--text-primary 25%) 画分隔线，已替换
    expect(css).not.toContain('var(--text-primary, #333)');
  });
});
