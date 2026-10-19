import type { SurveySpec } from './spec';
import { isSurveyReady, validateSurveyValues } from './surveyValues';

/**
 * 问卷取值校验：这层决定"什么能进对话"。
 * 重点不是合法值能过，而是**非法值必须被挡或归一化**。
 */
const spec: SurveySpec = {
  v: 1,
  id: 'p',
  kind: 'survey',
  fields: [
    {
      key: 'scope',
      label: '范围',
      type: 'radio',
      required: true,
      options: [
        { value: 'a', label: 'A' },
        { value: 'b', label: 'B' },
      ],
    },
    {
      key: 'rooms',
      label: '房间',
      type: 'checkbox',
      options: [
        { value: 'living', label: '客厅' },
        { value: 'bed', label: '主卧' },
      ],
    },
    { key: 'note', label: '备注', type: 'textarea', maxLength: 10 },
    { key: 'threshold', label: '阈值', type: 'number', min: 0, max: 100 },
  ],
};

describe('validateSurveyValues', () => {
  it('合法值通过并原样保留', () => {
    const result = validateSurveyValues(spec, {
      scope: 'a',
      rooms: ['living'],
      note: '好',
      threshold: 26,
    });
    expect(result.ok).toBe(true);
    expect(result.values).toEqual({
      scope: 'a',
      rooms: ['living'],
      note: '好',
      threshold: 26,
    });
  });

  it('必填缺失被拒', () => {
    const result = validateSurveyValues(spec, { note: 'x' });
    expect(result.ok).toBe(false);
    expect(result.errors.scope).toBeTruthy();
  });

  it('select/radio 只接受 options 里出现过的值（防伪造）', () => {
    const result = validateSurveyValues(spec, { scope: 'admin' });
    expect(result.ok).toBe(false);
    expect(result.errors.scope).toBe('选项无效');
  });

  it('checkbox 白名单过滤：含非法项整项被拒', () => {
    const result = validateSurveyValues(spec, { scope: 'a', rooms: ['living', 'garage'] });
    expect(result.ok).toBe(false);
    expect(result.errors.rooms).toBe('包含无效选项');
  });

  it('文本超长被拒', () => {
    const result = validateSurveyValues(spec, { scope: 'a', note: 'x'.repeat(11) });
    expect(result.ok).toBe(false);
    expect(result.errors.note).toContain('10');
  });

  it('数字越界被拒，数字字符串会被转换', () => {
    expect(validateSurveyValues(spec, { scope: 'a', threshold: 101 }).ok).toBe(false);

    const stringNumber = validateSurveyValues(spec, { scope: 'a', threshold: '26' });
    expect(stringNumber.ok).toBe(true);
    expect(stringNumber.values.threshold).toBe(26);
  });

  it('spec 未声明的 key 一律丢弃（不允许夹带）', () => {
    const result = validateSurveyValues(spec, {
      scope: 'a',
      extra: 'value',
    } as Record<string, unknown>);

    expect(result.ok).toBe(true);
    expect(Object.keys(result.values)).toEqual(['scope']);
  });

  it('空值不写入结果', () => {
    const result = validateSurveyValues(spec, { scope: 'a', note: '' });
    expect(result.ok).toBe(true);
    expect('note' in result.values).toBe(false);
  });
});

describe('isSurveyReady', () => {
  it('必填未填时为 false', () => {
    expect(isSurveyReady(spec, {})).toBe(false);
    expect(isSurveyReady(spec, undefined)).toBe(false);
  });

  it('必填填了即为 true（不替代提交时的完整校验）', () => {
    expect(isSurveyReady(spec, { scope: 'a' })).toBe(true);
  });
});
