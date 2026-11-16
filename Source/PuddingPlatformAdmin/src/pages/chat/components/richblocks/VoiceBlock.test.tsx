import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { VoiceBlock } from './VoiceBlock';

function createAdapter(options: { supported?: boolean } = {}) {
  const stop = jest.fn();
  const speak = jest.fn((_text: string, handlers?: { onStart?: () => void }) => {
    handlers?.onStart?.();
    return { stop };
  });
  return {
    stop,
    speak,
    adapter: {
      isSupported: () => options.supported !== false,
      speak,
    },
  };
}

describe('VoiceBlock', () => {
  it('默认自动朗读围栏文本', () => {
    const { adapter, speak } = createAdapter();
    render(<VoiceBlock code="今天先睡吧" adapter={adapter} />);
    expect(speak).toHaveBeenCalledTimes(1);
    expect(speak.mock.calls[0][0]).toBe('今天先睡吧');
    expect(screen.getByTestId('voice-block')).toBeTruthy();
  });

  it('autoPlay=false 时不自动朗读，点击后才朗读', () => {
    const { adapter, speak } = createAdapter();
    render(<VoiceBlock code="点我再念" adapter={adapter} autoPlay={false} />);
    expect(speak).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(1);
  });

  it('播放中再点一次会停止', () => {
    const { adapter, stop } = createAdapter();
    render(<VoiceBlock code="停一下" adapter={adapter} />);
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('不支持朗读时显示原因而不是静默', () => {
    const { adapter } = createAdapter({ supported: false });
    render(<VoiceBlock code="念不出来" adapter={adapter} />);
    expect(screen.getByText('当前浏览器不支持语音朗读')).toBeTruthy();
  });

  it('卸载时停止朗读', () => {
    const { adapter, stop } = createAdapter();
    const view = render(<VoiceBlock code="别念了" adapter={adapter} />);
    view.unmount();
    expect(stop).toHaveBeenCalled();
  });

  it('空白围栏不渲染任何东西', () => {
    const { adapter } = createAdapter();
    const view = render(<VoiceBlock code="   " adapter={adapter} />);
    expect(view.container.firstChild).toBeNull();
  });
});
