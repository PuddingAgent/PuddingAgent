// ── splitSkillHint：技能提示的展示层剥离 ──────────────────────────
// 技能不是协议参数：发送时由 useChatState 的 outgoingDecoration 当作文本附加到消息
// 末尾，Agent 据此读取；展示层再把提示剥出来渲染成徽标，避免系统提示混进用户正文。
// 这两个位置的**格式必须一致**（apply 生成 / splitSkillHint 解析）。
import { splitSkillHint } from './types';

describe('splitSkillHint', () => {
  it('strips the trailing hint and returns skill ids', () => {
    const result = splitSkillHint('做个 PPT\n（本轮请使用技能：ppt-master、foo-bar）');

    expect(result.text).toBe('做个 PPT');
    expect(result.skillIds).toEqual(['ppt-master', 'foo-bar']);
  });

  it('normalizes the legacy `id（展示名）` form down to id', () => {
    // 回归防线：旧版本把展示名一起拼进了提示，这里必须归一为纯 skillId。
    // 嵌套全角括号是正则的坑点（用 [^）]* 会提前截断在展示名的 ） 处）。
    const result = splitSkillHint(
      '看一下\n（本轮请使用技能：safe-multi-line-git-commit-via-message-file（Safe multi-line git commit via message file））',
    );

    expect(result.text).toBe('看一下');
    expect(result.skillIds).toEqual([
      'safe-multi-line-git-commit-via-message-file',
    ]);
  });

  it('keeps plain text untouched', () => {
    expect(splitSkillHint('普通消息，未选技能')).toEqual({
      text: '普通消息，未选技能',
      skillIds: [],
    });
  });

  it('supports a hint-only message with no body text', () => {
    const result = splitSkillHint('（本轮请使用技能：ppt-master）');

    expect(result.text).toBe('');
    expect(result.skillIds).toEqual(['ppt-master']);
  });

  it('does not treat a mid-message mention as the trailing hint', () => {
    // 提示必须**位于末尾**（发送时就是这么拼的）；正文里出现同样字样不该被吞掉。
    const result = splitSkillHint(
      '（本轮请使用技能：ppt-master）这句话在正文里\n后面还有正常内容',
    );

    expect(result.skillIds).toEqual([]);
    expect(result.text).toContain('后面还有正常内容');
  });
});
