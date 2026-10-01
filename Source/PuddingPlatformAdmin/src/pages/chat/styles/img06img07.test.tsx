import { render } from '@testing-library/react';
import * as React from 'react';
import { useMarkdownStyles } from './markdown.styles';
import { useMessageStyles } from './message.styles';

/**
 * IMG06 / IMG07 回归守卫（设计规格 §13.5）。
 * 断言的是不变量：助手回合不再有大面积有色底；链接有可辨认的强调色；
 * 行内代码走主题 surface-muted 而非旧蓝灰 chip；表格分隔线走主题 border、
 * 首列有最小宽。这些值一旦被改回旧写法就会失败。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

const Probe: React.FC = () => {
  const { styles } = useMarkdownStyles();
  const { styles: messageStyles } = useMessageStyles();
  return (
    <div
      className={`${styles.markdownBody} ${styles.inlineCode} ${messageStyles.agentTurnCard}`}
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

  it('表格：单元格 10/12、分隔线走主题 border、首列 7em 最小宽、正文段落 12', () => {
    render(<Probe />);
    const css = injectedCssText();
    expect(css).toContain('margin:0 0 12px');
    expect(css).toContain('padding:10px 12px');
    expect(css).toContain('min-width:7em');
    expect(css).toContain('var(--pudding-chat-border)');
    // 旧实现用 color-mix(--text-primary 25%) 画分隔线，已替换
    expect(css).not.toContain('var(--text-primary, #333)');
  });
});
