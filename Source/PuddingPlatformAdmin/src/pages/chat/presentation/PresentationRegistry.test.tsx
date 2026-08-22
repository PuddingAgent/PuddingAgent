// ── PresentationRegistry 单测（CU-08 / TR-04b）──
// 覆盖：八类 intent 键注册、未知/未注册/undefined 回落 Generic、getPresentationKind 规范化、
// GenericRenderer 基础渲染。
import React from 'react';
import { render, screen } from '@testing-library/react';
import type { ToolPresentationKind } from '@/services/platform/api';
import {
  PRESENTATION_KINDS,
  getPresentationKind,
  resolveRenderer,
} from './PresentationRegistry';
import { GenericRenderer } from './renderers/generic';

/** 权威八类词表（与 services/platform/api.ts ToolPresentationKind 对齐）。 */
const ALL_KINDS: readonly ToolPresentationKind[] = [
  'generic',
  'terminal',
  'diff',
  'search',
  'read',
  'web',
  'delegation',
  'job',
];

describe('PresentationRegistry', () => {
  it('PRESENTATION_KINDS 含全部八类 intent 键且无重复', () => {
    expect(PRESENTATION_KINDS).toHaveLength(8);
    for (const kind of ALL_KINDS) {
      expect(PRESENTATION_KINDS).toContain(kind);
    }
    expect(new Set(PRESENTATION_KINDS).size).toBe(8);
  });

  it('generic 注册为 Generic renderer；其余七类可解析（通用占位）', () => {
    expect(resolveRenderer('generic')).toBe(GenericRenderer);
    for (const kind of ALL_KINDS) {
      expect(typeof resolveRenderer(kind)).toBe('function');
    }
  });

  it('未知 / 未注册 kind 回落 Generic renderer', () => {
    expect(resolveRenderer('bogus' as ToolPresentationKind)).toBe(GenericRenderer);
  });

  it('undefined / null 回落 Generic renderer', () => {
    expect(resolveRenderer(undefined)).toBe(GenericRenderer);
    expect(resolveRenderer(null)).toBe(GenericRenderer);
  });

  it('getPresentationKind：显式 kind 原样返回', () => {
    expect(getPresentationKind({ kind: 'terminal' })).toBe('terminal');
  });

  it('getPresentationKind：缺失 / 未定义 kind 回落 generic', () => {
    expect(getPresentationKind({})).toBe('generic');
    expect(getPresentationKind({ kind: undefined })).toBe('generic');
    expect(getPresentationKind({ kind: null })).toBe('generic');
    expect(getPresentationKind(undefined)).toBe('generic');
    expect(getPresentationKind(null)).toBe('generic');
  });

  it('GenericRenderer 渲染 meta 与 payload（文本 + JSON）', () => {
    render(
      <GenericRenderer
        meta={{ label: 'demo' }}
        payload={{ tool: 'demo', note: 'hello' }}
      />,
    );
    const node = screen.getByTestId('presentation-generic');
    expect(node).toBeTruthy();
    expect(node.textContent).toContain('hello');
    expect(node.textContent).toContain('demo');
  });

  it('GenericRenderer 空入参不抛错并给出占位文案', () => {
    render(<GenericRenderer />);
    expect(screen.getByTestId('presentation-generic').textContent).toContain(
      '无附加信息',
    );
  });
});
