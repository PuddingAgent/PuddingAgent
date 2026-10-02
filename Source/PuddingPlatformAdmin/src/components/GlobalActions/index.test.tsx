import { render, screen } from '@testing-library/react';
import * as React from 'react';
import { PUDDING_HELP_URL } from '@/components/RightContent';
import {
  buildGlobalHelpMenuItem,
  PuddingGlobalActions,
} from './index';

// 只把"重"的兄弟组件换成桩，帮助图标本身要真实渲染 —— 这一批断言的就是它出现在哪。
// 注意：jest.mock 工厂会被提升到 import 之前，因此工厂里只能用内联类型，不能引用导入的类型。
jest.mock('@umijs/max', () => ({
  SelectLang: () => <span data-testid="select-lang" />,
}));
jest.mock('@/components/ThemeMode', () => ({
  ThemeToggleAction: () => <span data-testid="theme-toggle" />,
}));
jest.mock('@/components/RightContent/AvatarDropdown', () => ({
  AvatarDropdown: ({ children }: { children: any }) => <>{children}</>,
}));

const helpHref = () => {
  const link = document.querySelector<HTMLAnchorElement>(
    `a[href="${PUDDING_HELP_URL}"]`,
  );
  return link?.getAttribute('href') ?? null;
};

describe('IMG03 帮助入口归组', () => {
  it('chat 变体默认仍内联帮助（未被 Chat 接管的消费方保持原样）', () => {
    render(<PuddingGlobalActions variant="chat" />);

    expect(helpHref()).toBe(PUDDING_HELP_URL);
  });

  it('chat 变体 hideHelp 后不再内联帮助，且不吃掉高频全局操作', () => {
    render(<PuddingGlobalActions variant="chat" hideHelp />);

    expect(helpHref()).toBeNull();
    // 主题、语言、用户是高频全局操作，必须留在顶栏
    expect(screen.getByTestId('theme-toggle')).toBeTruthy();
    expect(screen.getByTestId('select-lang')).toBeTruthy();
    expect(screen.getByLabelText('用户菜单')).toBeTruthy();
  });

  it('Console（pro-layout）变体不受 hideHelp 影响：那里没有 Chat 的“更多”', () => {
    render(<PuddingGlobalActions variant="pro-layout" hideHelp />);

    expect(helpHref()).toBe(PUDDING_HELP_URL);
  });

  it('“更多”里的帮助项与内联图标指向同一 URL，并保持新窗口打开', () => {
    // 注意：本仓库的测试转换器不允许在类型标注里引用导入的绑定，故此处用 any
    const item = buildGlobalHelpMenuItem() as any;

    expect(item?.key).toBe('global-help');

    render(item?.label);

    const link = document.querySelector<HTMLAnchorElement>(
      `a[href="${PUDDING_HELP_URL}"]`,
    );
    expect(link).toBeTruthy();
    expect(link?.getAttribute('target')).toBe('_blank');
    // rel=noreferrer 是既有行为，不因搬进菜单而丢
    expect(link?.getAttribute('rel')).toBe('noreferrer');
  });
});
