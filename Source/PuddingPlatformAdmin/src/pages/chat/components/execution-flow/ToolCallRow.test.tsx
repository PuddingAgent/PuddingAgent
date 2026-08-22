// ── CU-07: ToolCallRow（基于 ToolNode）测试 ────────────────────────────────
// 验收（CU-07 任务书验收 1/4）：
//  - 基于 ToolNode，复用 ExecutionDisclosureRow chrome（role=button / aria-expanded /
//    Enter+Space 键盘 / chevron 占位稳定）
//  - state 四态映射：running→running / completed→done / failed→error（data-status）
//  - summary 映射：running=参数摘要；completed 单行输出=输出首行、多行=参数摘要；
//    failed=错误首行（summarizeError）
//  - 展开体 IN（arguments）/ OUT（output/error/exitCode/durationMs）
//  - 单 aria-live 不透传冗余：ToolCallRow 不设置 ariaLive → 行无 aria-live 属性
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { ToolNode } from '../../projections/executionFlowProjector';
import { ToolCallRow, mapToolStateToRowStatus } from './ToolCallRow';

const makeNode = (extra: Partial<ToolNode> = {}): ToolNode => ({
  kind: 'tool',
  key: 'tool:call-1',
  firstEventId: 'evt-1',
  sequence: 1,
  sourceEventIds: ['evt-1'],
  toolCallId: 'call-1',
  state: 'completed',
  placeholder: false,
  name: 'shell',
  arguments: '{"command":"git status"}',
  output: 'On branch master',
  children: [],
  ...extra,
});

/** 收集 antd-style（emotion/cssinjs）注入到 <style> 的 CSS 文本 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

describe('ToolCallRow (ToolNode)', () => {
  it('四态映射：running→running / completed→done / failed→error', () => {
    expect(mapToolStateToRowStatus('running')).toBe('running');
    expect(mapToolStateToRowStatus('completed')).toBe('done');
    expect(mapToolStateToRowStatus('failed')).toBe('error');
  });

  it('复用 ExecutionDisclosureRow chrome：整行可点、键盘展开、aria-expanded（验收 1）', () => {
    render(<ToolCallRow node={makeNode()} />);
    const row = screen.getByTestId('toolcall-row');
    expect(row.getAttribute('role')).toBe('button');
    expect(row.getAttribute('tabindex')).toBe('0');
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(row.getAttribute('aria-label')).toBe('shell 工具调用');
    expect(row.getAttribute('data-status')).toBe('done');
    expect(row.getAttribute('data-toolname')).toBe('shell');

    fireEvent.click(row);
    expect(row.getAttribute('aria-expanded')).toBe('true');
    fireEvent.keyDown(row, { key: 'Enter' });
    expect(row.getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(row, { key: ' ' });
    expect(row.getAttribute('aria-expanded')).toBe('true');
  });

  it('running：折叠行显示参数摘要（JSON 结构化，字段名不进默认面板），状态点 ongoing', () => {
    render(
      <ToolCallRow
        node={makeNode({
          state: 'running',
          name: 'search',
          arguments: '{"query":"retention policy","limit":20}',
          output: undefined,
        })}
      />,
    );
    const row = screen.getByTestId('toolcall-row');
    expect(row.getAttribute('data-status')).toBe('running');
    expect(screen.getByTestId('toolcall-summary').textContent).toContain(
      'retention policy',
    );
    expect(screen.getByTestId('toolcall-summary').textContent).not.toContain(
      '"query"',
    );
    expect(screen.getByTestId('state-dot').getAttribute('data-state')).toBe(
      'ongoing',
    );
    // running sweep 动画注入（旧契约保留）
    expect(injectedCssText()).toContain('toolCallSweep');
  });

  it('completed 单行输出：摘要=输出首行；展开后 IN/OUT 卡', () => {
    render(<ToolCallRow node={makeNode()} />);
    expect(screen.getByTestId('toolcall-title').textContent).toBe('shell');
    expect(screen.getByTestId('toolcall-summary').textContent).toBe(
      'On branch master',
    );
    expect(screen.getByTestId('state-dot').getAttribute('data-state')).toBe(
      'done',
    );
    expect(screen.queryByTestId('toolcall-in')).toBeNull();

    fireEvent.click(screen.getByTestId('toolcall-row'));
    expect(screen.getByTestId('toolcall-in-label').textContent).toBe('IN');
    expect(screen.getByTestId('toolcall-out-label').textContent).toBe('OUT');
    expect(screen.getByTestId('toolcall-in').textContent).toContain('git status');
    expect(screen.getByTestId('toolcall-out').textContent).toContain(
      'On branch master',
    );
  });

  it('completed 多行输出：摘要回退参数摘要，首行不进默认面板；展开后 OUT 卡暴露完整输出', () => {
    render(
      <ToolCallRow
        node={makeNode({
          output: ['line 1', 'line 2', 'line 3'].join('\n'),
        })}
      />,
    );
    const summary = screen.getByTestId('toolcall-summary');
    expect(summary.textContent).toBe('git status');
    expect(summary.textContent).not.toContain('line 1');
    expect(document.body.textContent).not.toContain('line 1');

    fireEvent.click(screen.getByTestId('toolcall-row'));
    const outCard = screen.getByTestId('toolcall-out');
    expect(outCard.textContent).toContain('line 1');
    expect(outCard.textContent).toContain('line 3');
  });

  it('failed：摘要=错误首行（红色 token），OUT 卡 error-line 高亮 + exitCode + duration（验收 1）', () => {
    render(
      <ToolCallRow
        node={makeNode({
          state: 'failed',
          name: 'file_patch',
          arguments: '{"path":"a.ts"}',
          output: 'apply_patch verification failed\npatch rejected at line 12',
          error: 'apply_patch verification failed\npatch rejected at line 12',
          exitCode: 2,
          durationMs: 350,
        })}
      />,
    );
    const row = screen.getByTestId('toolcall-row');
    expect(row.getAttribute('data-status')).toBe('error');
    expect(screen.getByTestId('toolcall-summary').textContent).toBe(
      'apply_patch verification failed',
    );
    expect(screen.getByTestId('state-dot').getAttribute('data-state')).toBe(
      'error',
    );
    expect(injectedCssText()).toContain('--pudding-status-error');

    fireEvent.click(row);
    const errorLine = screen.getByTestId('toolcall-out-error-line');
    expect(errorLine.textContent).toBe('apply_patch verification failed');
    expect(errorLine.className.length).toBeGreaterThan(0);
    const outCard = screen.getByTestId('toolcall-out');
    expect(outCard.textContent).toContain('patch rejected at line 12');
    expect(outCard.textContent).toContain('exit code: 2');
    expect(outCard.textContent).toContain('duration: 350ms');
  });

  it('占位节点：无任何输出/错误/exitCode 时仍渲染行（占位过滤由 ToolCallTree 层负责）', () => {
    render(
      <ToolCallRow
        node={makeNode({
          placeholder: true,
          state: 'running',
          arguments: undefined,
          output: undefined,
          error: undefined,
          exitCode: undefined,
        })}
      />,
    );
    // 无 arguments/output → 不可展开（expandedContent=undefined → 非 button）
    const row = screen.getByTestId('toolcall-row');
    expect(row.getAttribute('role')).toBeNull();
    expect(row.getAttribute('aria-expanded')).toBeNull();
  });

    it('单 aria-live 不透传冗余：ToolCallRow 不设置 ariaLive → 行无 aria-live 属性', () => {
    render(<ToolCallRow node={makeNode()} />);
    expect(screen.getByTestId('toolcall-row').getAttribute('aria-live')).toBe(
      null,
    );
  });

  it('completed 超长输出（>阈值）：默认 OUT 卡仅 preview + 查看完整输出按钮，禁全量挂 DOM（验收 4/5）', () => {
    const longOutput = [
      '-'.repeat(120),
      'start',
      'middle'.repeat(600),
      'end',
      '-'.repeat(120),
    ].join('\n');
    render(<ToolCallRow node={makeNode({ output: longOutput })} />);
    fireEvent.click(screen.getByTestId('toolcall-row'));
    const outCard = screen.getByTestId('toolcall-out');
    // 默认仅 preview：保留可读头尾、含折叠标记、不挂完整中间
    expect(outCard.textContent).toContain('start');
    expect(outCard.textContent).toContain('end');
    expect(outCard.textContent).toContain('（输出过长，已折叠）');
    expect(outCard.textContent).not.toContain('middle'.repeat(600));
    expect(screen.getByTestId('toolcall-out-expand')).toBeTruthy();
    // 点击展开 → 显示完整输出，按钮消失
    fireEvent.click(screen.getByTestId('toolcall-out-expand'));
    expect(outCard.textContent).toContain('middle'.repeat(600));
    expect(screen.queryByTestId('toolcall-out-expand')).toBeNull();
  });

  it('completed 阈值内输出：OUT 卡直接渲染 full，无展开按钮（验收 4）', () => {
    render(<ToolCallRow node={makeNode({ output: 'short output' })} />);
    fireEvent.click(screen.getByTestId('toolcall-row'));
    const outCard = screen.getByTestId('toolcall-out');
    expect(outCard.textContent).toContain('short output');
    expect(screen.queryByTestId('toolcall-out-expand')).toBeNull();
  });

  // CU-10：卡片挂载必须走 resolveRenderer 分派路径（按 presentation.kind；
  // 未注册七类暂回落 Generic renderer —— 分派必须活，防 Registry 死代码）。
  it('CU-10：presentation 存在时按 kind 走 resolveRenderer 分派（未注册回落 Generic renderer）', () => {
    render(
      <ToolCallRow
        node={makeNode({
          presentation: { kind: 'terminal', meta: { command: 'git status' } },
        })}
      />,
    );
    // 折叠态：presentation 存在即可展开（即使无 IN/OUT 卡）。
    const row = screen.getByTestId('toolcall-row');
    expect(row.getAttribute('role')).toBe('button');
    fireEvent.click(row);
    // 分派活：toolcall-presentation-card 挂载 Generic renderer（terminal 未注册回落）。
    const card = screen.getByTestId('toolcall-presentation-card');
    expect(card).toBeTruthy();
    expect(screen.getByTestId('presentation-generic')).toBeTruthy();
  });
});
