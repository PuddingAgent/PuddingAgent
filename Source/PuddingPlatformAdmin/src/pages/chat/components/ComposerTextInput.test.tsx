// ── ComposerTextInput 叶子组件测试（输入框性能修复契约）──────────────────
// 锁定行为：
//  - IME 组合期间逐键不 lift（onInputChange 不触发），compositionEnd 一次性 lift；
//  - 非组合输入逐键 lift（发送读父级 state，无竞态）；
//  - hasText 仅空↔非空翻转上报（不逐键）；
//  - 「/」命令面板：Enter 选中命令替换草稿并 lift；
//  - 外部改写（inputValue prop / ref.setValue）被采纳；
//  - Enter+待发图片由 onEnterWithImages 消费，其余按键透传父级 onKeyDown。
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import ComposerTextInput, {
  type ComposerTextInputHandle,
} from './ComposerTextInput';

// 本套件显式 cleanup（该 jest 环境未启用 RTL 自动清理，跨用例 DOM 会累积）
afterEach(cleanup);

const baseHandlers = () => {
  const calls: { change: string[]; hasText: boolean[]; keys: string[] } = {
    change: [],
    hasText: [],
    keys: [],
  };
  return {
    calls,
    onInputChange: (v: string) => calls.change.push(v),
    onHasTextChange: (hasText: boolean) => calls.hasText.push(hasText),
    onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) =>
      calls.keys.push(e.key),
    onEnterWithImages: () => calls.keys.push('enter-with-images'),
  };
};

interface HarnessProps {
  inputValue?: string;
  hasPendingImages?: boolean;
  handlers: ReturnType<typeof baseHandlers>;
  handleRef: React.RefObject<ComposerTextInputHandle | null>;
  textareaRef: React.MutableRefObject<HTMLTextAreaElement | null>;
}

const Harness: React.FC<HarnessProps> = ({
  inputValue = '',
  hasPendingImages = false,
  handlers,
  handleRef,
  textareaRef,
}) => (
  <ComposerTextInput
    ref={handleRef}
    inputValue={inputValue}
    onInputChange={handlers.onInputChange}
    onKeyDown={handlers.onKeyDown}
    onHasTextChange={handlers.onHasTextChange}
    onEnterWithImages={handlers.onEnterWithImages}
    hasPendingImages={hasPendingImages}
    placeholder="输入"
    textareaRef={textareaRef}
  />
);

const mount = (props: Partial<HarnessProps> = {}) => {
  const handlers = baseHandlers();
  const handleRef = React.createRef<ComposerTextInputHandle>();
  const textareaRef = { current: null as HTMLTextAreaElement | null };
  const utils = render(
    <Harness
      handlers={handlers}
      handleRef={handleRef}
      textareaRef={textareaRef}
      {...props}
    />,
  );
  return {
    ...utils,
    handlers,
    handleRef,
    textareaRef,
    input: screen.getByTestId('chat-input') as HTMLTextAreaElement,
    rerender: (next: Partial<HarnessProps>) =>
      utils.rerender(
        <Harness
          handlers={handlers}
          handleRef={handleRef}
          textareaRef={textareaRef}
          inputValue=""
          hasPendingImages={false}
          {...props}
          {...next}
        />,
      ),
  };
};

describe('ComposerTextInput（IME / lift 契约）', () => {
  it('组合期间逐键不 lift；compositionEnd 一次性 lift 最终值', () => {
    const { handlers, input } = mount();
    fireEvent.compositionStart(input);
    fireEvent.change(input, { target: { value: 'ni' } });
    fireEvent.change(input, { target: { value: 'nihao' } });
    expect(handlers.calls.change).toEqual([]);
    fireEvent.compositionEnd(input);
    expect(handlers.calls.change).toEqual(['nihao']);
  });

  it('非组合输入逐键 lift（发送读父级 state，无竞态）', () => {
    const { handlers, input } = mount();
    fireEvent.change(input, { target: { value: 'a' } });
    fireEvent.change(input, { target: { value: 'ab' } });
    expect(handlers.calls.change).toEqual(['a', 'ab']);
  });

  it('hasText 仅空↔非空翻转上报（不逐键）', () => {
    const { handlers, input } = mount();
    fireEvent.change(input, { target: { value: 'a' } });
    fireEvent.change(input, { target: { value: 'ab' } });
    fireEvent.change(input, { target: { value: 'abc' } });
    expect(handlers.calls.hasText).toEqual([true]);
    fireEvent.change(input, { target: { value: '' } });
    expect(handlers.calls.hasText).toEqual([true, false]);
  });

  it('「/」命令面板：Enter 选中命令替换草稿并 lift', () => {
    const { handlers, input } = mount();
    fireEvent.change(input, { target: { value: '/' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(handlers.calls.change.length).toBeGreaterThan(0);
    expect(input.value).not.toBe('/');
  });

  it('外部改写：inputValue prop 变化被采纳（引用插入场景）', () => {
    const { input, rerender } = mount();
    expect(input.value).toBe('');
    rerender({ inputValue: '引用文本' });
    expect(input.value).toBe('引用文本');
  });

  it('Enter+待发图片：onEnterWithImages 消费，不透传父级', () => {
    const { handlers, input } = mount({ hasPendingImages: true });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(handlers.calls.keys).toEqual(['enter-with-images']);
  });

  it('普通 Enter 透传父级 onKeyDown（发送快捷键归 ChatPage）', () => {
    const { handlers, input } = mount();
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(handlers.calls.keys).toEqual(['Enter']);
  });

  it('ref.setValue：外部改写草稿并同步 lift', () => {
    const { handlers, handleRef } = mount();
    handleRef.current?.setValue('语音转写文本');
    expect(handlers.calls.change).toEqual(['语音转写文本']);
  });
});
