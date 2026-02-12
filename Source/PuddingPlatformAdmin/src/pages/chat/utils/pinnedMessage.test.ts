import { buildPinnedMessageQuote, summarizePinnedMessage } from './pinnedMessage';

describe('pinnedMessage utilities', () => {
  it('summarizes the first three non-empty lines', () => {
    expect(
      summarizePinnedMessage('第一行\n\n第二行\n第三行\n第四行'),
    ).toBe('第一行\n第二行\n第三行');
  });

  it('builds the pinned quote format used by the composer', () => {
    expect(
      buildPinnedMessageQuote({
        turnId: 'turn-1633',
        preview:
          '## 当前状态总览\n### 记忆系统结构化项目\n| Phase | 状态 | 内容 | Commit |',
        fullText: '完整消息',
        pinnedAt: 1,
      }),
    ).toBe(
      [
        '> 消息ID：请通过Query Session Log查询 turnId=turn-1633',
        '> 请通过Query Session Log工具获取原始信息',
        '> 摘要：## 当前状态总览',
        '> ### 记忆系统结构化项目',
        '> | Phase | 状态 | 内容 | Commit |',
        '',
      ].join('\n'),
    );
  });
});
