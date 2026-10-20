import {
  COMPOSER_INSERT_EVENT,
  publishComposerInsert,
  subscribeComposerInsert,
} from './composerInsert';

/**
 * 原生 → 输入框信道。
 *
 * 断言的都不是"事件能发出去"，而是三条会**静默出错**的路径：
 * 空文本不该插空行、退订后不能再回调、别的事件名不能被误认。
 * 这三条任何一条破了，用户看到的表现分别是空白草稿、重复插入、莫名多一行。
 */
describe('composerInsert', () => {
  it('把文本交给订阅者', () => {
    const received: string[] = [];
    const off = subscribeComposerInsert((text) => received.push(text));

    publishComposerInsert('【画布：部署验证 rev 2】');
    off();

    expect(received).toEqual(['【画布：部署验证 rev 2】']);
  });

  it('空文本直接丢弃，不插空行', () => {
    const received: string[] = [];
    const off = subscribeComposerInsert((text) => received.push(text));

    publishComposerInsert('');
    off();

    expect(received).toEqual([]);
  });

  it('退订之后不再回调（防切页累积监听）', () => {
    const received: string[] = [];
    const off = subscribeComposerInsert((text) => received.push(text));

    off();
    publishComposerInsert('x');

    expect(received).toEqual([]);
  });

  it('只认自己的频道名，别的同形事件不触发', () => {
    const received: string[] = [];
    const off = subscribeComposerInsert((text) => received.push(text));

    window.dispatchEvent(new CustomEvent('some:other-event', { detail: { text: 'nope' } }));
    off();

    expect(received).toEqual([]);
  });

  it('频道名带命名空间前缀，避免与页面内其它事件撞名', () => {
    expect(COMPOSER_INSERT_EVENT.startsWith('pudding:')).toBe(true);
  });
});
