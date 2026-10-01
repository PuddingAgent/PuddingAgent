import fs from 'node:fs';
import path from 'node:path';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { IndexHealthLevel } from './health';
import {
  AMBIENT_MOTION,
  ANIM_NONE,
  BREATHING_LEVELS,
  FlowBand,
  L0Strip,
  LEVEL_VISUALS,
  RingGauge,
  ScopeDots,
  SparkBars,
  StatusOrb,
  ToggleGlyph,
  UnknownGlyph,
} from './visuals';

// ── Slice P2 定向测试：**表达层**不变量（I9~I13）────────────────────────
// 事实层（9 行状态矩阵）已由 health.test.ts 逐行覆盖；本文件只管「视觉优先」的可失败断言：
//   I9  五态视觉三重编码（形状 × 动效 × 短词，两两不同 ⇒ 不靠颜色单独承载语义）
//   I10 静默即健康（只有 ok 允许呼吸；unknown / off 必须无动效）
//   I11 prefers-reduced-motion 降级存在且覆盖**全部**动画/过渡使用点
//   I12 busy 不得假装进度（只有不定长流光，没有 % / 定长进度条）
//   I13 首屏同屏最多 1 个呼吸元素（L0 内 ambient 出现次数 ≤ 1）
// 断言失败 ⇒ 说明改动越过了 §9 的红线，而不是「测试过时」。

const ALL_LEVELS: IndexHealthLevel[] = ['ok', 'warn', 'error', 'unknown', 'off', 'busy'];

/** 规格 §9.3「五态视觉编码」矩阵覆盖的五个语义位（warn/error 同为「需处理」）。 */
const FIVE_STATE_SLOTS: IndexHealthLevel[] = ['ok', 'warn', 'error', 'unknown', 'off', 'busy'];

const CSS_PATH = path.join(__dirname, 'index.css');
// ⚠️ 仪器纪律：**不能**用 indexOf('@media (prefers-reduced-motion: reduce)') 定位降级块 ——
// 文件头注释里也引用了这段媒体查询，indexOf 会命中注释（首次运行即被这个假位置骗过一次：
// head 被截成 305 字符 ⇒ 扫描器零命中 ⇒ 三条断言假红）。故必须匹配「媒体查询 + 规则块开头」。
const REDUCE_RE = /@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{/;

/**
 * 读取样式表并**剥掉注释**再扫描。
 * 仪器纪律：注释里会出现 `2.4s` / `.vs-anim-*` / 媒体查询等字样，若不剥离，正则会把
 * `2.4s` 误当成「类名 `.4s`」收进集合 ⇒ 覆盖断言被噪声污染（假红/假绿都可能）。
 */
function readCss(): string {
  return fs.readFileSync(CSS_PATH, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
}

function reduceIndex(css: string): number {
  const match = REDUCE_RE.exec(css);
  return match === null ? -1 : match.index;
}

/** CSS 文本按「降级块」一切两半：head = 常态规则，tail = reduced-motion 块。 */
function splitAtReduce(css: string): { head: string; tail: string } {
  const index = reduceIndex(css);
  if (index < 0) return { head: css, tail: '' };
  return { head: css.slice(0, index), tail: css.slice(index) };
}

/** 逐 `选择器 { 声明 }` 扫描 head，收集「声明了 animation/transition 且未归零」的类名。 */
function classesDeclaring(declaration: 'animation' | 'transition'): string[] {
  const { head } = splitAtReduce(readCss());
  const found = new Set<string>();
  const blockRe = /([^{}]+)\{([^{}]*)\}/g;
  let match = blockRe.exec(head);
  while (match !== null) {
    const selector = match[1];
    const body = match[2];
    const declares = new RegExp(`${declaration}\\s*:`).test(body);
    const resets = new RegExp(`${declaration}\\s*:\\s*none`).test(body);
    if (declares && !resets) {
      for (const cls of selector.match(/\.[A-Za-z0-9_-]+/g) ?? []) found.add(cls.slice(1));
    }
    match = blockRe.exec(head);
  }
  return [...found].sort();
}

/** 取所有「选择器里提到该类名」的声明块（含后代选择器 ⇒ 加在哪儿都能被抓住）。 */
function declarationBodiesFor(className: string): string[] {
  const { head } = splitAtReduce(readCss());
  const bodies: string[] = [];
  const blockRe = /([^{}]+)\{([^{}]*)\}/g;
  let match = blockRe.exec(head);
  while (match !== null) {
    const selector = match[1];
    if (selector.match(/\.[A-Za-z0-9_-]+/g)?.includes(`.${className}`) === true) {
      bodies.push(match[2]);
    }
    match = blockRe.exec(head);
  }
  return bodies;
}

function countOccurrences(haystack: string, needle: string): number {
  return haystack.split(needle).length - 1;
}

function l0Props(level: IndexHealthLevel) {
  return {
    level,
    shortWord: LEVEL_VISUALS[level].word,
    freshness: { tone: 'ok' as const, ratio: 0.8, label: '12 分钟前' },
    volume: { tone: 'ok' as const, ratio: 0.1, label: '10%' },
    scopes: { accepted: 1, rejected: 0 },
    showRawFields: false,
    onToggleRawFields: () => undefined,
  };
}

// ══ I9 三重编码（形状 × 动效 × 短词）═══════════════════════════════════

describe('I9 五态视觉三重编码（形状 × 动效 × 短词，颜色永不单独承载语义）', () => {
  it('I9：六个 level 都有视觉参数，覆盖 §9.3 的全部状态位', () => {
    for (const level of FIVE_STATE_SLOTS) {
      expect(LEVEL_VISUALS[level]).toBeDefined();
      expect(LEVEL_VISUALS[level].shape).toMatch(/^vs-shape-/);
      expect(LEVEL_VISUALS[level].word.length).toBeGreaterThan(0);
    }
    expect(Object.keys(LEVEL_VISUALS).sort()).toEqual([...ALL_LEVELS].sort());
  });

  it('I9：形状类名两两不相同（灰度打印 / 色盲下仍可辨）', () => {
    const shapes = ALL_LEVELS.map((level) => LEVEL_VISUALS[level].shape);
    expect(new Set(shapes).size).toBe(ALL_LEVELS.length);
  });

  it('I9：形状 + 动效的**组合**同样两两不相同', () => {
    const combos = ALL_LEVELS.map(
      (level) => `${LEVEL_VISUALS[level].shape}|${LEVEL_VISUALS[level].motion}`,
    );
    expect(new Set(combos).size).toBe(ALL_LEVELS.length);
  });

  it('I9：短词两两不相同（unknown 与 off 不得同文案）', () => {
    const words = ALL_LEVELS.map((level) => LEVEL_VISUALS[level].word);
    expect(new Set(words).size).toBe(ALL_LEVELS.length);
    expect(LEVEL_VISUALS.unknown.word).not.toBe(LEVEL_VISUALS.off.word);
  });

  it('I9：渲染出的球体确实带上各自的形状类（表与渲染不脱节）', () => {
    for (const level of ALL_LEVELS) {
      const html = renderToStaticMarkup(<StatusOrb level={level} />);
      expect(html).toContain(LEVEL_VISUALS[level].shape);
      expect(html).toContain(`vs-level-${level}`);
    }
  });
});

// ══ I10 静默即健康 ═════════════════════════════════════════════════════

describe('I10 只有 ok 允许呼吸；unknown / off 必须无动效', () => {
  it('I10：允许呼吸的 level 集合恰好是 [ok]（由矩阵派生，加错即红）', () => {
    expect([...BREATHING_LEVELS]).toEqual(['ok']);
  });

  it('I10：unknown / off 的动效类是 ANIM_NONE（静止）', () => {
    expect(LEVEL_VISUALS.unknown.motion).toBe(ANIM_NONE);
    expect(LEVEL_VISUALS.off.motion).toBe(ANIM_NONE);
  });

  it('I10：渲染出的 unknown / off 球体不含任何动画类', () => {
    for (const level of ['unknown', 'off'] as IndexHealthLevel[]) {
      const html = renderToStaticMarkup(<StatusOrb level={level} />);
      expect(html).not.toContain('vs-anim-');
    }
  });

  it('I10：unknown / off 的形状类在 CSS 层也不得声明 animation（不给「质感」）', () => {
    for (const className of ['vs-shape-dashed', 'vs-shape-dot']) {
      const bodies = declarationBodiesFor(className);
      expect(bodies.length).toBeGreaterThan(0);
      for (const body of bodies) expect(body).not.toMatch(/animation/);
    }
  });
});

// ══ I11 无障碍降级 ═════════════════════════════════════════════════════

describe('I11 prefers-reduced-motion 降级（覆盖全部 keyframes 使用点）', () => {
  it('I11：index.css 存在 @media (prefers-reduced-motion: reduce) 块', () => {
    const css = readCss();
    expect(reduceIndex(css)).toBeGreaterThanOrEqual(0);
    expect(css.indexOf('@keyframes vs-breathe')).toBeGreaterThanOrEqual(0);
    // 降级块必须在文件**末尾**（head 必须是常态规则的全部 ⇒ 覆盖断言才有意义）
    expect(css.indexOf('}', reduceIndex(css))).toBeGreaterThan(reduceIndex(css));
  });

  it('I11：每一个声明了 animation 的类都被降级块覆盖', () => {
    const animClasses = classesDeclaring('animation');
    // 非空性检查：先确认真的抓到了动效类（否则断言会退化成「空集恒真」）
    expect(animClasses).toEqual(
      expect.arrayContaining(['vs-anim-alert', 'vs-anim-ambient', 'vs-anim-flow', 'vs-anim-ripple']),
    );
    const { tail } = splitAtReduce(readCss());
    for (const className of animClasses) expect(tail).toContain(`.${className}`);
  });

  it('I11：每一个声明了 transition 的类都被降级块覆盖', () => {
    const transitionClasses = classesDeclaring('transition');
    expect(transitionClasses.length).toBeGreaterThan(0);
    const { tail } = splitAtReduce(readCss());
    for (const className of transitionClasses) expect(tail).toContain(`.${className}`);
  });

  it('I11：降级块把 animation 与 transition 一并归零', () => {
    const { tail } = splitAtReduce(readCss());
    expect(tail).toMatch(/animation:\s*none/);
    expect(tail).toMatch(/transition:\s*none/);
  });
});

// ══ I12 不假装进度 ═════════════════════════════════════════════════════

describe('I12 busy 只给不定长流光（不模拟百分比进度）', () => {
  it('I12：流光带是不定长的（有 vs-anim-flow），且不含百分比 / progress 语义', () => {
    const html = renderToStaticMarkup(
      <FlowBand label="正在重建 · 已清点 4,560 文件 · 82.8 MB · 已用 46 秒" />,
    );
    expect(html).toContain('vs-anim-flow');
    expect(html).not.toMatch(/%/);
    expect(html).not.toMatch(/progress/i);
    expect(html).not.toMatch(/width:\s*\d/);
  });

  it('I12：busy 球体同样不含百分比 / 定长进度条', () => {
    const html = renderToStaticMarkup(<StatusOrb level="busy" />);
    expect(html).toContain('vs-shape-ripple');
    expect(html).not.toMatch(/%/);
    expect(html).not.toMatch(/progress/i);
  });

  it('I12：L0 在 busy 态下不给定长进度条（只有流光带）', () => {
    const html = renderToStaticMarkup(
      <L0Strip {...l0Props('busy')} busyLabel="正在重建 · 已清点 4,560 文件 · 已用 46 秒" />,
    );
    expect(html).toContain('vs-anim-flow');
    expect(html).not.toMatch(/<progress/i);
  });
});

// ══ I13 同屏噪声上限 ═══════════════════════════════════════════════════

describe('I13 首屏最多 1 个呼吸元素', () => {
  it('I13：L0 状态条的 ambient（呼吸）元素出现次数 ≤ 1（ok 时恰为 1）', () => {
    const html = renderToStaticMarkup(<L0Strip {...l0Props('ok')} />);
    expect(countOccurrences(html, AMBIENT_MOTION)).toBe(1);
  });

  it('I13：非 ok 态的 L0 不出现任何呼吸元素', () => {
    for (const level of ['warn', 'error', 'unknown', 'off', 'busy'] as IndexHealthLevel[]) {
      const html = renderToStaticMarkup(<L0Strip {...l0Props(level)} />);
      expect(countOccurrences(html, AMBIENT_MOTION)).toBe(0);
    }
  });
});

// ══ 视觉层边界与三态（组件只吃 props，不折叠未知）══════════════════════

describe('视觉层边界（只吃 props）与组件三态', () => {
  it('边界：visuals.tsx 只从 ./health 取**类型**，不 import 任何推导函数', () => {
    const source = fs.readFileSync(path.join(__dirname, 'visuals.tsx'), 'utf8');
    expect(source).toMatch(/import type \{[^}]*\} from '\.\/health';/);
    expect(source).not.toMatch(/derive/i);
    expect(source).not.toMatch(/buildBusyTitle/);
    expect(source).not.toMatch(/Date\.now/);
  });

  it('RingGauge：ratio=null ⇒ 虚线空环（未知 ≠ 0 填充）', () => {
    const unknownHtml = renderToStaticMarkup(
      <RingGauge tone="neutral" ratio={null} label="未知" caption="体积占用" />,
    );
    const zeroHtml = renderToStaticMarkup(
      <RingGauge tone="ok" ratio={0} label="0%" caption="体积占用" />,
    );
    expect(unknownHtml).toContain('vs-ring--unknown');
    expect(unknownHtml).toContain('vs-ring__value--dashed');
    expect(zeroHtml).not.toContain('vs-ring--unknown');
    expect(zeroHtml).toContain('stroke-dasharray');
  });

  it('ScopeDots：null ≠ 0（未知画虚线空心点，0 画不出点）', () => {
    const unknownHtml = renderToStaticMarkup(<ScopeDots accepted={null} rejected={null} />);
    const zeroHtml = renderToStaticMarkup(<ScopeDots accepted={0} rejected={0} />);
    expect(unknownHtml).toContain('vs-dot--unknown');
    expect(zeroHtml).not.toContain('vs-dot--unknown');
    expect(zeroHtml).not.toContain('vs-dot vs-level-ok');
  });

  it('SparkBars：台账为空 ⇒ 虚线占位 + 如实说法，不画空条', () => {
    const html = renderToStaticMarkup(<SparkBars bars={[]} emptyText="供给组件尚未启动（无人触发过预建）" />);
    expect(html).toContain('vs-bars__empty');
    expect(html).toContain('无人触发过预建');
    expect(html).not.toContain('vs-bar__fill');
  });

  it('SparkBars：进行中的 job 用不定长流光（vs-anim-flow），不画定长条', () => {
    const html = renderToStaticMarkup(
      <SparkBars bars={[{ key: 'j1', ratio: null, tone: 'busy', title: 'j1', active: true }]} />,
    );
    expect(html).toContain('vs-anim-flow');
    expect(html).not.toContain('vs-bar__fill');
  });

  it('ToggleGlyph：true/false/null 三种说法互不相同', () => {
    expect(renderToStaticMarkup(<ToggleGlyph value={true} label="全文索引" />)).toContain('开');
    expect(renderToStaticMarkup(<ToggleGlyph value={false} label="全文索引" />)).toContain('关');
    const unknownHtml = renderToStaticMarkup(<ToggleGlyph value={null} label="维护循环" />);
    expect(unknownHtml).toContain('未知');
    expect(unknownHtml).toContain('vs-toggle--unknown');
  });

  it('UnknownGlyph：不确定 ⇒ 虚线空心圆（低饱和、无动效）', () => {
    const html = renderToStaticMarkup(<UnknownGlyph label="未接入" />);
    expect(html).toContain('vs-unknown__hole');
    expect(html).toContain('未接入');
    expect(html).not.toContain('vs-anim-');
  });
});
