import { formatGoalContinuationMessage } from './goalContinuationMessage';

const managedMetadata = {
  goal_managed: 'true',
  automation_origin: 'goal_continuation',
};

describe('formatGoalContinuationMessage', () => {
  it('parses legacy escaped JSON into readable Goal text', () => {
    const content = String.raw`internal prompt
<goal_payload>{"objective":"\u7EDF\u4E00\u8C03\u5EA6\n\u7B2C\u4E8C\u884C","iteration":1,"maxIterations":32,"task":{"taskId":"task-1","status":"Assigned","workUnit":{"objective":"\u6536\u96C6\u8BC1\u636E"}}}</goal_payload>`;

    const display = formatGoalContinuationMessage(content, managedMetadata);

    expect(display).toBe(
      'Goal 自动续行 · 第 1/32 轮\n\n' +
        'Task task-1 · Assigned\n\n' +
        '当前工作单元：收集证据\n\n' +
        '统一调度\n第二行',
    );
    expect(display).not.toContain('\\u7EDF');
    expect(display).not.toContain('internal prompt');
  });

  it('does not parse an ordinary user message with lookalike tags', () => {
    const content = '<goal_payload>{"objective":"keep raw"}</goal_payload>';

    expect(formatGoalContinuationMessage(content)).toBe(content);
  });

  it('preserves malformed managed payload as diagnostic evidence', () => {
    const content = '<goal_payload>{broken json}</goal_payload>';

    expect(formatGoalContinuationMessage(content, managedMetadata)).toBe(content);
  });
});
