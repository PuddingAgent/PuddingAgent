import * as React from 'react';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { VoiceBlock } from './VoiceBlock';
import { VoiceAutoplayScope } from './VoiceAutoplayContext';
import { defaultBrowserVoiceOutputAdapter } from '../../hooks/browserVoiceOutput';
import * as httpVoiceOutput from '../../hooks/httpVoiceOutput';

jest.mock('../../hooks/httpVoiceOutput', () => {
  const actual = jest.requireActual('../../hooks/httpVoiceOutput');
  return {
    ...actual,
    createHttpVoiceOutputAdapter: jest.fn(actual.createHttpVoiceOutputAdapter),
  };
});

function createAdapter(options: { supported?: boolean } = {}) {
  const stop = jest.fn();
  const speak = jest.fn(
    (
      _text: string,
      handlers?: import('../../hooks/browserVoiceOutput').BrowserVoiceOutputHandlers,
    ) => {
      handlers?.onStart?.();
      return { stop };
    },
  );
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
  beforeEach(() => localStorage.clear());

  it('文本增量变化只自动朗读一次，手动按钮仍可重播最终文本', async () => {
    const { adapter, speak } = createAdapter();
    const view = render(<VoiceBlock code="今天" adapter={adapter} />);
    await act(async () => {});
    view.rerender(<VoiceBlock code="今天先睡" adapter={adapter} />);
    await act(async () => {});
    view.rerender(<VoiceBlock code="今天先睡吧" adapter={adapter} />);
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(2);
    expect(speak.mock.calls[1][0]).toBe('今天先睡吧');
  });
  it('默认自动朗读围栏文本', async () => {
    const { adapter, speak } = createAdapter();
    render(<VoiceBlock code="今天先睡吧" adapter={adapter} />);
    await act(async () => {});
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

  it('播放中再点一次会停止', async () => {
    const { adapter, stop } = createAdapter();
    render(<VoiceBlock code="停一下" adapter={adapter} />);
    await act(async () => {});
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('不支持朗读时显示原因而不是静默', async () => {
    const { adapter } = createAdapter({ supported: false });
    render(<VoiceBlock code="念不出来" adapter={adapter} />);
    await act(async () => {});
    expect(screen.getByText('当前浏览器不支持语音朗读')).toBeTruthy();
  });

  it('卸载时停止朗读', async () => {
    const { adapter, stop } = createAdapter();
    const view = render(<VoiceBlock code="别念了" adapter={adapter} />);
    await act(async () => {});
    view.unmount();
    expect(stop).toHaveBeenCalled();
  });

  it('空白围栏不渲染任何东西', () => {
    const { adapter } = createAdapter();
    const view = render(<VoiceBlock code="   " adapter={adapter} />);
    expect(view.container.firstChild).toBeNull();
  });

  it('显示音色下拉，默认读 localStorage 里的选择', async () => {
    localStorage.setItem('pudding_voice_name', 'Vera');
    const { adapter } = createAdapter();
    render(<VoiceBlock code="换个声音" adapter={adapter} />);
    await act(async () => {});
    const select = screen.getByTestId('voice-block-voice') as HTMLSelectElement;
    expect(select.value).toBe('Vera');
    // 三个音色都要可选，否则用户只能听一个。
    expect(select.options.length).toBe(3);
  });

  it('切换音色会记住选择且不自动重播', async () => {
    const { adapter, speak, stop } = createAdapter();
    render(<VoiceBlock code="记住我选的" adapter={adapter} />);
    await act(async () => {});
    const select = screen.getByTestId('voice-block-voice') as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'Stella' } });
    await act(async () => {});
    expect(select.value).toBe('Stella');
    expect(localStorage.getItem('pudding_voice_name')).toBe('Stella');
    expect(speak).toHaveBeenCalledTimes(1);
    expect(stop).not.toHaveBeenCalled();
  });

  it('showVoicePicker=false 时只保留播放按钮', async () => {
    const { adapter } = createAdapter();
    render(
      <VoiceBlock code="只要按钮" adapter={adapter} showVoicePicker={false} />,
    );
    await act(async () => {});
    expect(screen.queryByTestId('voice-block-voice')).toBeNull();
  });

  it('文本、语言或适配器变化不打断播放，手动重播使用最新配置', async () => {
    const first = createAdapter();
    const next = createAdapter();
    const view = render(<VoiceBlock code="今天" adapter={first.adapter} />);
    await act(async () => {});
    view.rerender(
      <VoiceBlock code="今天先睡吧" adapter={next.adapter} lang="en-US" />,
    );
    await act(async () => {});
    expect(first.speak).toHaveBeenCalledTimes(1);
    expect(first.stop).not.toHaveBeenCalled();
    expect(next.speak).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(first.stop).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(next.speak).toHaveBeenCalledWith(
      '今天先睡吧',
      expect.objectContaining({ lang: 'en-US' }),
    );
    view.unmount();
    expect(next.stop).toHaveBeenCalledTimes(1);
  });

  it('默认 HTTP 适配器切换音色不重播，下一次手动播放使用新音色', async () => {
    const initial = createAdapter();
    const next = createAdapter();
    const factory = jest.mocked(httpVoiceOutput.createHttpVoiceOutputAdapter);
    factory.mockImplementation((options) =>
      options?.voice === 'Stella' ? next.adapter : initial.adapter,
    );
    try {
      render(<VoiceBlock code="切换音色" />);
      await act(async () => {});
      fireEvent.change(screen.getByTestId('voice-block-voice'), {
        target: { value: 'Stella' },
      });
      await act(async () => {});
      expect(initial.speak).toHaveBeenCalledTimes(1);
      expect(initial.stop).not.toHaveBeenCalled();
      expect(next.speak).not.toHaveBeenCalled();
      fireEvent.click(screen.getByTestId('voice-block-toggle'));
      fireEvent.click(screen.getByTestId('voice-block-toggle'));
      expect(next.speak).toHaveBeenCalledTimes(1);
    } finally {
      factory.mockImplementation(
        jest.requireActual('../../hooks/httpVoiceOutput')
          .createHttpVoiceOutputAdapter,
      );
    }
  });

  it('自然播放结束后仍允许手动重播', async () => {
    const { adapter, speak } = createAdapter();
    render(<VoiceBlock code="可以重播" adapter={adapter} />);
    await act(async () => {});
    act(() => speak.mock.calls[0][1]?.onComplete?.());
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(2);
  });

  it('自动启动前手动点击只播放一次，结束后文本更新仍需手动重播', async () => {
    const { adapter, speak } = createAdapter();
    const view = render(<VoiceBlock code="今天" adapter={adapter} />);
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    act(() => speak.mock.calls[0][1]?.onComplete?.());
    view.rerender(<VoiceBlock code="今天先睡吧" adapter={adapter} />);
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(2);
    expect(speak.mock.calls[1][0]).toBe('今天先睡吧');
  });

  it('StrictMode effect 重放只启动一次且保留卸载即停', async () => {
    const { adapter, speak, stop } = createAdapter();
    const view = render(
      <React.StrictMode>
        <VoiceBlock code="只念一次" adapter={adapter} />
      </React.StrictMode>,
    );
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    expect(stop).not.toHaveBeenCalled();
    view.unmount();
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('启动前卸载会取消排队的自动播放', async () => {
    const { adapter, speak } = createAdapter();
    const view = render(<VoiceBlock code="不要念" adapter={adapter} />);
    view.unmount();
    await act(async () => {});
    expect(speak).not.toHaveBeenCalled();
  });

  it('空白变为非空文本才消耗一次自动播放机会', async () => {
    const { adapter, speak } = createAdapter();
    const view = render(<VoiceBlock code=" " adapter={adapter} />);
    await act(async () => {});
    view.rerender(<VoiceBlock code="有文字了" adapter={adapter} />);
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
  });

  it('合成失败显式报错，不因文本更新自动重试，允许手动重试', async () => {
    const { adapter, speak, stop } = createAdapter();
    const fallback = jest.spyOn(defaultBrowserVoiceOutputAdapter, 'speak');
    speak.mockImplementationOnce(() => {
      throw new Error('Qwen 合成失败');
    });
    const view = render(<VoiceBlock code="今天" adapter={adapter} />);
    await act(async () => {});
    expect(screen.getByText('Qwen 合成失败')).toBeTruthy();
    view.rerender(<VoiceBlock code="今天先睡吧" adapter={adapter} />);
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    expect(fallback).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(2);
    view.unmount();
    expect(stop).toHaveBeenCalledTimes(1);
    fallback.mockRestore();
  });

  it('历史消息不自动播放，但手动仍可朗读', async () => {
    const { adapter, speak } = createAdapter();
    render(
      <VoiceAutoplayScope generating={false} completed>
        <VoiceBlock code="历史回复" adapter={adapter} />
      </VoiceAutoplayScope>,
    );
    await act(async () => {});
    expect(speak).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(1);
  });

  it('新消息流式生成时不播放，成功完成后只朗读完整文本，子组件重挂载不重播', async () => {
    const { adapter, speak } = createAdapter();
    const content = (generating: boolean, code: string, blockKey: string) => (
      <VoiceAutoplayScope generating={generating} completed={!generating}>
        <VoiceBlock key={blockKey} code={code} adapter={adapter} />
      </VoiceAutoplayScope>
    );
    const view = render(content(true, '今天', 'a'));
    await act(async () => {});
    view.rerender(content(true, '今天先睡', 'b'));
    await act(async () => {});
    expect(speak).not.toHaveBeenCalled();
    view.rerender(content(false, '今天先睡吧', 'c'));
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    expect(speak.mock.calls[0][0]).toBe('今天先睡吧');
    view.rerender(content(false, '今天先睡吧', 'd'));
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    expect(speak).toHaveBeenCalledTimes(2);
  });

  it('手动播放并停止后，消息收尾和重挂载不再自动启动', async () => {
    const { adapter, speak } = createAdapter();
    const content = (generating: boolean) => (
      <VoiceAutoplayScope generating={generating} completed={!generating}>
        <VoiceBlock
          key={String(generating)}
          code={generating ? '今天' : '今天先睡吧'}
          adapter={adapter}
        />
      </VoiceAutoplayScope>
    );
    const view = render(content(true));
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    fireEvent.click(screen.getByTestId('voice-block-toggle'));
    view.rerender(content(false));
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(1);
  });

  it('不同新消息包含相同文本时各自允许播放', async () => {
    const { adapter, speak } = createAdapter();
    const content = (generating: boolean) => (
      <>
        {['a', 'b'].map((id) => (
          <VoiceAutoplayScope
            key={id}
            generating={generating}
            completed={!generating}
          >
            <VoiceBlock code="相同文本" adapter={adapter} />
          </VoiceAutoplayScope>
        ))}
      </>
    );
    const view = render(content(true));
    view.rerender(content(false));
    await act(async () => {});
    expect(speak).toHaveBeenCalledTimes(2);
  });

  it('未成功完成的消息不自动播放', async () => {
    const { adapter, speak } = createAdapter();
    const view = render(
      <VoiceAutoplayScope generating>
        <VoiceBlock code="中断的回复" adapter={adapter} />
      </VoiceAutoplayScope>,
    );
    view.rerender(
      <VoiceAutoplayScope generating={false}>
        <VoiceBlock code="中断的回复" adapter={adapter} />
      </VoiceAutoplayScope>,
    );
    await act(async () => {});
    expect(speak).not.toHaveBeenCalled();
  });
});
