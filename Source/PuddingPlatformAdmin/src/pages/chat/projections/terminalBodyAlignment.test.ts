// ── terminalBodyAlignment 单元回归（2026-09-23）─────────────────────────────
// 覆盖：终态才介入、无投影/无正文/权威为空不介入、已完整不介入、
//       前缀缺尾补到最后一个正文段、非前缀一律不猜（fail-closed）。
// fixture 说明：本模块只读 `kind` 与 `text`（以及节点顺序），因此用最小节点
// 构造即可；不引用投影器的全部字段，避免无关字段漂移导致测试脆裂。
import type { ExecutionFlowNode } from './executionFlowProjector';
import {
  alignTerminalBodyText,
  type ProjectionLike,
} from './terminalBodyAlignment';

const msg = (text: string): ExecutionFlowNode =>
  ({ kind: 'message', text }) as unknown as ExecutionFlowNode;

const tool = (): ExecutionFlowNode =>
  ({ kind: 'tool', state: 'completed' }) as unknown as ExecutionFlowNode;

const projectionOf = (nodes: ExecutionFlowNode[]): ProjectionLike => ({ nodes });

const textOf = (nodes: readonly ExecutionFlowNode[], index: number): string =>
  (nodes[index] as { text: string }).text;

describe('alignTerminalBodyText', () => {
  it('非终态（运行中）：一律不介入', () => {
    const projection = projectionOf([msg('前 800')]);
    const result = alignTerminalBodyText(projection, '前 800 后 1555', {
      terminal: false,
    });
    expect(result.outcome).toBe('not-terminal');
    expect(result.appendedChars).toBe(0);
    expect(result.projection).toBe(projection);
  });

  it('无投影 / 无正文节点：不介入（正文走兜底气泡路径）', () => {
    expect(
      alignTerminalBodyText(undefined, '全文', { terminal: true }).outcome,
    ).toBe('no-projection');
    expect(
      alignTerminalBodyText(projectionOf([tool()]), '全文', { terminal: true })
        .outcome,
    ).toBe('no-text-node');
  });

  it('权威全文为空或全空白：不介入（不伪造）', () => {
    const projection = projectionOf([msg('投影正文')]);
    expect(
      alignTerminalBodyText(projection, '', { terminal: true }).outcome,
    ).toBe('no-authoritative-text');
    expect(
      alignTerminalBodyText(projection, '   \n  ', { terminal: true }).outcome,
    ).toBe('no-authoritative-text');
  });

  it('投影拼接已等于权威全文：不介入', () => {
    const projection = projectionOf([msg('完整正文')]);
    const result = alignTerminalBodyText(projection, '完整正文', {
      terminal: true,
    });
    expect(result.outcome).toBe('already-complete');
    expect(result.projection).toBe(projection);
  });

  it('投影是权威全文的前缀且更短：只把缺的尾段补到最后一个正文段', () => {
    const head = 'A'.repeat(800);
    const tail = 'B'.repeat(1555);
    const projection = projectionOf([tool(), msg(head)]);
    const result = alignTerminalBodyText(projection, head + tail, {
      terminal: true,
    });

    expect(result.outcome).toBe('appended-tail');
    expect(result.appendedChars).toBe(1555);
    expect(textOf(result.projection!.nodes, 1)).toBe(head + tail);
    // 只补正文段，行为节点原样保留（含对象标识）——不重排、不新建。
    expect(result.projection!.nodes[0]).toBe(projection.nodes[0]);
    expect(result.projection!.nodes).toHaveLength(2);
  });

  it('多正文段（夹行为组）：尾段补到最后一个正文段，前面的正文段不动', () => {
    const first = '第一段。';
    const second = '第二段。';
    const tail = '缺失的尾段。';
    const projection = projectionOf([msg(first), tool(), msg(second)]);
    const result = alignTerminalBodyText(projection, first + second + tail, {
      terminal: true,
    });

    expect(result.outcome).toBe('appended-tail');
    expect(textOf(result.projection!.nodes, 0)).toBe(first);
    expect(textOf(result.projection!.nodes, 2)).toBe(second + tail);
  });

  it('投影拼接不是权威全文的前缀：一律不动（fail-closed，不猜）', () => {
    const projection = projectionOf([msg('另一份文本')]);
    const result = alignTerminalBodyText(projection, '权威全文', {
      terminal: true,
    });
    expect(result.outcome).toBe('diverged');
    expect(result.appendedChars).toBe(0);
    expect(result.projection).toBe(projection);
  });

  it('空白差异也按 fail-closed 处理：不一致即不动', () => {
    const projection = projectionOf([msg('正文\n')]);
    // 权威文本中间多一个空格 ⇒ 非前缀 ⇒ 不动（宁可保留投影，也不猜合并）
    const result = alignTerminalBodyText(projection, '正文 结尾', {
      terminal: true,
    });
    expect(result.outcome).toBe('diverged');
    expect(result.projection).toBe(projection);
  });

  it('不改写入参：返回的投影是浅拷贝，原投影对象未被修改', () => {
    const head = 'x'.repeat(10);
    const projection = projectionOf([msg(head)]);
    const result = alignTerminalBodyText(projection, head + 'tail', {
      terminal: true,
    });
    expect(result.outcome).toBe('appended-tail');
    expect(textOf(projection.nodes, 0)).toBe(head);
    expect(result.projection).not.toBe(projection);
  });
});
