import React from 'react';
import type { IndexHealthLevel, StatusTone } from './health';

// ── Slice P2 · 索引与检索面板「视觉语言」组件（规格 §9 · 视觉优先）────────
// 规格真源：`Docs/12_features/Index-Status-Panel-UI-Prototype-2026-10-01.md` **§9**（视觉语言与动效）
// 线框真源：`Docs/12_features/index-status-prototype/visual-language.svg`（图 3：L0 四视觉单元 +
//           五态编码矩阵（形状×动效×容器）+ L1 四卡视觉）
//           `Docs/12_features/index-status-prototype/motion-tokens.svg`（图 4：动效三档 + 语义色板
//           token + 尺度层次 + 红线）
//
// 三条纪律（逐条对应 §9.1 / §9.3 / §9.7）：
// 1) **只吃 props**：本文件不 import 任何推导函数（`./health` 只用于取类型），不读时钟、
//    不发请求、不推导业务真值 —— `level` / 值 / 标签一律由 `index.tsx` 从 health.ts 的纯函数
//    结果传入。「视觉」与「事实」互不污染 ⇒ 视觉层可独立单测（见 `visuals.test.tsx`）。
// 2) **三重编码**（§9.1 原则 2「颜色永不单独承载语义」）：每个状态**同时**给出
//    形状类（`vs-shape-*`）+ 动效类（`vs-anim-*`，或 `ANIM_NONE` 表示静止）+ 短词；
//    灰度打印与色盲下仍可辨（§4「双编码」的升级版）。
// 3) **静默即健康**（§9.1 原则 1）：只有 `ok` 呼吸；`unknown` / `off` **无动效**、低饱和
//    （「不确定就该没有质感」）。动效一律由 `index.css` 的 keyframes 提供，且受
//    `prefers-reduced-motion: reduce` 全量降级（§9.7 ①：无障碍，不是可选）。

/** 无动效哨兵值（I10：`unknown` / `off` 的动效类必须**恰好**是它）。 */
export const ANIM_NONE = 'none';

/** 唯一允许「呼吸」的动效类（I10 / §9.4 `ambient` 档）。 */
export const AMBIENT_MOTION = 'vs-anim-ambient';

export interface LevelVisual {
  /** 编码一 · 形状类名（`vs-shape-*`，各 level 两两不同 ⇒ 不靠颜色区分）。 */
  shape: string;
  /** 编码二 · 动效类名（`vs-anim-*`；`ANIM_NONE` = 静止）。 */
  motion: string;
  /** 编码三 · 容器处理（外框：色条 / 虚线 / 灰化）。 */
  container: string;
  /** 编码三 · 短词（文字降级为副信息，§9.6）。 */
  word: string;
}

/**
 * 五态视觉编码矩阵（§9.3，图 3 · B）：形状 × 动效 × 容器**三处同时变化**。
 * `warn` 与 `error` 共用急闪（同为「需处理」节奏），但**形状类不同**（光晕球 vs 牛眼环）。
 */
export const LEVEL_VISUALS = {
  ok: { shape: 'vs-shape-solid', motion: AMBIENT_MOTION, container: 'vs-frame--plain', word: '就绪' },
  warn: { shape: 'vs-shape-halo', motion: 'vs-anim-alert', container: 'vs-frame--alert', word: '需处理' },
  error: { shape: 'vs-shape-ringed', motion: 'vs-anim-alert', container: 'vs-frame--alert', word: '故障' },
  unknown: { shape: 'vs-shape-dashed', motion: ANIM_NONE, container: 'vs-frame--dashed', word: '未知' },
  off: { shape: 'vs-shape-dot', motion: ANIM_NONE, container: 'vs-frame--asleep', word: '已关闭' },
  busy: { shape: 'vs-shape-ripple', motion: 'vs-anim-ripple', container: 'vs-frame--busy', word: '重建中' },
} as const satisfies Record<IndexHealthLevel, LevelVisual>;

/**
 * 允许呼吸的 level 集合（I10 静态断言点）。
 * **由矩阵派生**（不手写常量）⇒ 任何人给 `unknown` / `off` 加上呼吸，这里立刻变红。
 */
export const BREATHING_LEVELS: readonly IndexHealthLevel[] = (
  Object.keys(LEVEL_VISUALS) as IndexHealthLevel[]
).filter((level) => LEVEL_VISUALS[level].motion === AMBIENT_MOTION);

/** 视觉族 → 容器处理（L1 卡片角标只有 tone 语义，故按 tone 取容器类）。 */
export const TONE_FRAMES: Record<StatusTone, string> = {
  ok: 'vs-frame--plain',
  warn: 'vs-frame--alert',
  error: 'vs-frame--alert',
  neutral: 'vs-frame--dashed',
  busy: 'vs-frame--busy',
};

/** 类名拼接（不引第三方 className 库，保持本目录零新增依赖）。 */
export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter((part): part is string => typeof part === 'string' && part.length > 0).join(' ');
}

/** 卡片/容器的统一类名入口（色族 + 容器处理 + fast 过渡档）。 */
export function frameClass(tone: StatusTone): string {
  return cx(`vs-tone-${tone}`, TONE_FRAMES[tone]);
}

// ── 1) 脉冲球（§9.2 隐喻：心跳 —— 还活着；停跳即可见）────────────────────

export interface StatusOrbProps {
  level: IndexHealthLevel;
  /** 短词（§9.6：正常 / 未知 / 关闭态只给短词）。 */
  word?: string;
  /** tooltip：绝对时刻 / 路径 / 原始字节等「要点得到但不占首屏」的事实。 */
  title?: string;
  size?: 'l0' | 'card';
  className?: string;
  /**
   * 是否允许带动效（默认 `true`，保持 P2 既有行为不变）。
   *
   * §9.7 ⑥「同屏最多一个会呼吸的元素」：**列表内的球体必须传 `false`** ——
   * 把唯一的「呼吸」名额留给 L0；行内的动效语义由 `StaleMark`（急闪）/
   * `RippleMark`（涟漪）这些**标记**承载（每个状态仍然有「形状 + 动效 + 短词」三重编码）。
   */
  animate?: boolean;
}

export const StatusOrb: React.FC<StatusOrbProps> = ({
  level,
  word,
  title,
  size = 'l0',
  className,
  animate = true,
}) => {
  const visual = LEVEL_VISUALS[level];
  const anim = visual.motion === ANIM_NONE || !animate ? null : visual.motion;
  const label = word ?? visual.word;
  return (
    // 注意：动效类**只挂在被动画的子元素上**（光晕 / 核心 / 涟漪），根节点不重复挂 ——
    // 否则「同屏呼吸元素计数」（I13）会被同一个球体数成 2 次，视觉噪声预算失去意义。
    <span
      className={cx('vs-orb', `vs-orb--${size}`, `vs-level-${level}`, visual.shape, className)}
      title={title}
      role="img"
      aria-label={label}
      data-level={level}
    >
      {level === 'ok' ? (
        <>
          <span className={cx('vs-orb__halo', anim)} />
          <span className="vs-orb__ring" />
          <span className="vs-orb__core" />
        </>
      ) : null}
      {level === 'warn' ? (
        <>
          <span className={cx('vs-orb__halo', anim)} />
          <span className="vs-orb__core" />
        </>
      ) : null}
      {level === 'error' ? (
        <>
          <span className="vs-orb__ring" />
          <span className={cx('vs-orb__core', 'vs-orb__core--sm', anim)} />
        </>
      ) : null}
      {level === 'unknown' ? <span className="vs-orb__hole" /> : null}
      {level === 'off' ? <span className="vs-orb__dot" /> : null}
      {level === 'busy' ? (
        <>
          <span className={cx('vs-orb__ripple', anim)} />
          <span className="vs-orb__core" />
        </>
      ) : null}
    </span>
  );
};

// ── 2) 环形仪表（§9.2 隐喻：连续量 → 环；stroke-dasharray 承载占比）──────

export interface RingGaugeProps {
  /** 0~1 的占比；`null` = **未知**（画虚线空环，不假装成 0 填充）。 */
  ratio: number | null;
  /** 环心短标（`12 分钟前` / `10%` / `未知`）。 */
  label: string;
  /** 环下短词（`新鲜度` / `体积占用`）。 */
  caption?: string;
  /** tooltip：绝对时刻 / 原始字节。 */
  title?: string;
  tone: StatusTone;
  size?: number;
  strokeWidth?: number;
}

export const RingGauge: React.FC<RingGaugeProps> = ({
  ratio,
  label,
  caption,
  title,
  tone,
  size = 72,
  strokeWidth = 9,
}) => {
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const known = ratio !== null && Number.isFinite(ratio);
  const filled = known ? Math.max(0, Math.min(1, ratio)) * circumference : 0;
  return (
    <span className={cx('vs-ring', `vs-tone-${tone}`, known ? null : 'vs-ring--unknown')} title={title}>
      <span className="vs-ring__svgwrap">
        <svg
          width={size}
          height={size}
          viewBox={`0 0 ${size} ${size}`}
          role="img"
          aria-label={cx(caption, label)}
        >
          <circle
            className="vs-ring__track"
            cx={size / 2}
            cy={size / 2}
            r={radius}
            fill="none"
            strokeWidth={strokeWidth}
          />
          {known ? (
            <circle
              className="vs-ring__value vs-t-fast"
              cx={size / 2}
              cy={size / 2}
              r={radius}
              fill="none"
              strokeWidth={strokeWidth}
              strokeDasharray={`${filled} ${Math.max(0, circumference - filled)}`}
              strokeLinecap="round"
              transform={`rotate(-90 ${size / 2} ${size / 2})`}
            />
          ) : (
            <circle
              className="vs-ring__value vs-ring__value--dashed"
              cx={size / 2}
              cy={size / 2}
              r={radius}
              fill="none"
              strokeWidth={strokeWidth}
              strokeDasharray="4 6"
              transform={`rotate(-90 ${size / 2} ${size / 2})`}
            />
          )}
        </svg>
        <span className="vs-ring__label">{label}</span>
      </span>
      {caption !== undefined ? <span className="vs-caption">{caption}</span> : null}
    </span>
  );
};

// ── 3) 点阵（§9.2 隐喻：离散集合 → 一眼数清）─────────────────────────────

export interface ScopeDotsProps {
  /** 受理数；`null` = 未知（虚线空心点 + 短词，不假装成 0）。 */
  accepted: number | null;
  /** 被拒数；`null` = 未知。 */
  rejected: number | null;
  caption?: string;
  title?: string;
  /** 单次最多画几个点（长尾折叠成 `+n`，避免点阵刷屏）。 */
  max?: number;
}

export const ScopeDots: React.FC<ScopeDotsProps> = ({
  accepted,
  rejected,
  caption,
  title,
  max = 8,
}) => {
  if (accepted === null || rejected === null) {
    return (
      <span className={cx('vs-dots', 'vs-tone-neutral')} title={title} role="img" aria-label="scope 受理：未知">
        <span className="vs-dot vs-dot--unknown" />
        <span className="vs-caption">{cx(caption, '未知')}</span>
      </span>
    );
  }
  const shownAccepted = Math.min(accepted, max);
  const shownRejected = Math.min(rejected, max - shownAccepted);
  const hidden = accepted + rejected - shownAccepted - shownRejected;
  return (
    <span
      className="vs-dots"
      title={title}
      role="img"
      aria-label={cx(caption, `${accepted} 受理 / ${rejected} 拒绝`)}
    >
      {Array.from({ length: shownAccepted }, (_, index) => (
        <span key={`ok-${index + 1}`} className="vs-dot vs-level-ok" />
      ))}
      {Array.from({ length: shownRejected }, (_, index) => (
        <span key={`rejected-${index + 1}`} className="vs-dot vs-dot--rejected vs-level-error" />
      ))}
      {hidden > 0 ? <span className="vs-caption">+{hidden}</span> : null}
      {caption !== undefined ? <span className="vs-caption">{caption}</span> : null}
    </span>
  );
};

// ── 4) 火花线（§9.2 隐喻：序列 → 长度=耗时，颜色=结果）──────────────────

export interface SparkBar {
  key: string;
  /** 相对最长耗时的 0~1；`null` = **耗时未知**（虚线细杠，不假装成 0）。 */
  ratio: number | null;
  tone: StatusTone;
  /** tooltip：jobId / state / phase / 清点 / 用时。 */
  title: string;
  /** 进行中 ⇒ 用**不定长流光**（I12：不模拟百分比进度）。 */
  active?: boolean;
}

export interface SparkBarsProps {
  bars: readonly SparkBar[];
  caption?: string;
  /** 台账为空时的如实说法（§3 三种 `jobsReason` 之一）⇒ 画虚线占位，**不画空条**。 */
  emptyText?: string;
}

export const SparkBars: React.FC<SparkBarsProps> = ({ bars, caption, emptyText }) => {
  if (bars.length === 0) {
    return (
      <span className="vs-bars vs-bars--empty" title={emptyText}>
        <span className="vs-bars__empty" />
        {emptyText !== undefined ? <span className="vs-caption">{emptyText}</span> : null}
      </span>
    );
  }
  return (
    <span className="vs-bars">
      {bars.map((bar) => (
        <span key={bar.key} className="vs-bars__row" title={bar.title}>
          <span className={cx('vs-bar', `vs-tone-${bar.tone}`, bar.active === true ? 'vs-bar--active' : null)}>
            {bar.active === true ? (
              <span className="vs-bar__flow vs-anim-flow" />
            ) : bar.ratio === null ? (
              <span className="vs-bar__unknown" />
            ) : (
              <span
                className="vs-bar__fill vs-t-fast"
                style={{ width: `${Math.max(4, Math.round(bar.ratio * 100))}%` }}
              />
            )}
          </span>
        </span>
      ))}
      {caption !== undefined ? <span className="vs-caption">{caption}</span> : null}
    </span>
  );
};

// ── 5) 拨动开关（§9.2 隐喻：配置开/关 → 图形，不必读 `enabled = true` 这行字）──

export interface ToggleGlyphProps {
  /** `true` = 开 · `false` = 关 · `null` = **生效值不可知**（虚线轨道；≠ 关）。 */
  value: boolean | null;
  label: string;
  title?: string;
  /** 给出即为可交互（真开关）；缺省则只作图形展示。 */
  onChange?: (next: boolean) => void;
}

export const ToggleGlyph: React.FC<ToggleGlyphProps> = ({ value, label, title, onChange }) => {
  const classes = cx(
    'vs-toggle',
    value === true ? 'vs-toggle--on' : value === false ? 'vs-toggle--off' : 'vs-toggle--unknown',
  );
  const word = value === true ? '开' : value === false ? '关' : '未知';
  const glyph = (
    <>
      <span className="vs-toggle__track">
        <span className="vs-toggle__knob vs-t-fast" />
      </span>
      <span className="vs-caption">
        {label} · {word}
      </span>
    </>
  );
  if (onChange === undefined) {
    return (
      <span className={classes} title={title} role="img" aria-label={`${label}：${word}`}>
        {glyph}
      </span>
    );
  }
  return (
    <button
      type="button"
      className={cx(classes, 'vs-toggle--button')}
      title={title}
      role="switch"
      aria-checked={value === true}
      aria-label={label}
      onClick={() => onChange(value !== true)}
    >
      {glyph}
    </button>
  );
};

// ── 6) 未知图形（§9.2 隐喻：不确定 → 「没有质感」的虚线空心圆）──────────

export interface UnknownGlyphProps {
  label?: string;
  title?: string;
  size?: 'l0' | 'card';
}

export const UnknownGlyph: React.FC<UnknownGlyphProps> = ({ label = '未知', title, size = 'card' }) => (
  <span className={cx('vs-unknown', `vs-unknown--${size}`)} title={title} role="img" aria-label={label}>
    <span className="vs-unknown__hole" />
    <span className="vs-caption">{label}</span>
  </span>
);

// ── 7) 不定长流光（§9.2：只在「无终点信息」时使用 —— 表达「还在走」而非「走了多少」）──

export interface FlowBandProps {
  /** 条带上方的一行说明（**不得含百分比**，I12）。 */
  label?: string;
  title?: string;
}

export const FlowBand: React.FC<FlowBandProps> = ({ label, title }) => (
  <span className="vs-flow" title={title}>
    <span className="vs-flow__track">
      <span className="vs-flow__band vs-anim-flow" />
    </span>
    {label !== undefined ? <span className="vs-caption">{label}</span> : null}
  </span>
);

// ── 8) L0 状态条（图 3 · A：四个视觉单元 + 一个开关；**无一句话结论**）──
// 文字只在 tooltip 与异常时出现（§9.6）。I13：L0 内最多 1 个呼吸元素。

export interface L0UnitHint {
  tone: StatusTone;
  ratio: number | null;
  label: string;
  title?: string;
}

export interface L0StripProps {
  level: IndexHealthLevel;
  /** 短词（正常 / 未知 / 关闭态：只给短词）。 */
  shortWord: string;
  /** 异常态**唯一允许**的长句（一句话结论）；非异常态传空串。 */
  headline?: string;
  /** 异常态的修复指引（与 headline 同屏）。 */
  guidance?: string;
  /** 结论 tooltip（异常逐条原因 / 绝对时刻）。 */
  title?: string;
  freshness: L0UnitHint;
  volume: L0UnitHint;
  scopes: { accepted: number | null; rejected: number | null; title?: string };
  /** busy 专用：不定长流光上的一行说明（**不得含百分比**）。 */
  busyLabel?: string;
  showRawFields: boolean;
  onToggleRawFields: (next: boolean) => void;
}

export const L0Strip: React.FC<L0StripProps> = ({
  level,
  shortWord,
  headline,
  guidance,
  title,
  freshness,
  volume,
  scopes,
  busyLabel,
  showRawFields,
  onToggleRawFields,
}) => {
  const visual = LEVEL_VISUALS[level];
  const hasSentence = headline !== undefined && headline !== '';
  return (
    <section className={cx('vs-l0', `vs-level-${level}`, visual.container)} data-level={level}>
      <span className="vs-l0__unit">
        <StatusOrb level={level} word={shortWord} title={title} />
        <span className="vs-l0__word">{shortWord}</span>
      </span>
      <RingGauge
        tone={freshness.tone}
        ratio={freshness.ratio}
        label={freshness.label}
        caption="新鲜度"
        title={freshness.title}
      />
      <RingGauge
        tone={volume.tone}
        ratio={volume.ratio}
        label={volume.label}
        caption="体积占用"
        title={volume.title}
      />
      <ScopeDots
        accepted={scopes.accepted}
        rejected={scopes.rejected}
        caption="scope 受理"
        title={scopes.title}
      />
      {level === 'busy' && busyLabel !== undefined ? (
        <FlowBand
          label={busyLabel}
          title="后端未提供 processed/total ⇒ 只给「已清点 + 已耗时」，不给百分比（§6 缺口 ①）"
        />
      ) : null}
      <span className="vs-l0__spacer" />
      <ToggleGlyph
        value={showRawFields}
        label="原始字段"
        title="展开 L2 原始字段（证据层：字段一个不丢，主动下钻才出现）"
        onChange={onToggleRawFields}
      />
      {hasSentence ? (
        <span className="vs-l0__sentence">
          <strong>{headline}</strong>
          {guidance !== undefined && guidance !== '' ? (
            <span className="vs-l0__guidance">{guidance}</span>
          ) : null}
        </span>
      ) : null}
    </section>
  );
};

// ── 9) 项目标记（B 卡 · 符号索引）：陈旧 / 未登记 / 路径失效 / 进行中涟漪 ──
// 规格真源：§9.1 原则 2「颜色永不单独承载语义」—— 每个标记**同时**给出
//   形状类（`vs-mark--*`）+ 动效（`vs-anim-*`，或静态） + 短词；颜色只是第四重冗余编码。
// 动效**只复用 §9.4 已有的三档**（陈旧 = `alert` 1.2s 急闪；进行中 = `alert` 1.6s 涟漪），
// **不自创第四档**；因此本组不新增任何 `@keyframes` 或 `animation/transition` 声明，
// `prefers-reduced-motion` 的既有降级块对它们自动生效（I11 无需扩写）。

/** 标记短词（页面不得另写一份）。 */
export const MARK_WORDS = {
  stale: '陈旧',
  unregistered: '未登记',
  pathBroken: '路径失效',
  indexInFlight: '索引中',
} as const;

export interface MarkProps {
  /** 短词（默认取本标记的规范短词）。 */
  word?: string;
  /** tooltip：原始字段 / 判定来源 / 修复方向。 */
  title?: string;
}

/** 陈旧标记：形状 = 斜纹块（`vs-mark--stale`）· 动效 = 急闪（§9.4 alert 档）· 短词「陈旧」。 */
export const StaleMark: React.FC<MarkProps> = ({ word = MARK_WORDS.stale, title }) => (
  <span className={cx('vs-mark', 'vs-mark--stale')} title={title} role="img" aria-label={word} data-mark="stale">
    <span className={cx('vs-mark__glyph', 'vs-anim-alert')} />
    <span className="vs-caption">{word}</span>
  </span>
);

/** 未登记标记（D1）：形状 = 虚线方框 + 斜杠 · 静态 · 短词「未登记」。 */
export const UnregisteredMark: React.FC<MarkProps> = ({ word = MARK_WORDS.unregistered, title }) => (
  <span
    className={cx('vs-mark', 'vs-mark--unregistered')}
    title={title}
    role="img"
    aria-label={word}
    data-mark="unregistered"
  >
    <span className="vs-mark__glyph" />
    <span className="vs-caption">{word}</span>
  </span>
);

/** 路径失效标记（D2）：形状 = 断开的线段 · 静态 · 短词「路径失效」。 */
export const PathBrokenMark: React.FC<MarkProps> = ({ word = MARK_WORDS.pathBroken, title }) => (
  <span
    className={cx('vs-mark', 'vs-mark--pathbroken')}
    title={title}
    role="img"
    aria-label={word}
    data-mark="pathbroken"
  >
    <span className="vs-mark__glyph" />
    <span className="vs-caption">{word}</span>
  </span>
);

/** 进行中涟漪标记：形状 = 圆 + 外扩环 · 动效 = 涟漪（§9.4 alert 1.6s）· 短词「索引中」。 */
export const RippleMark: React.FC<MarkProps> = ({ word = MARK_WORDS.indexInFlight, title }) => (
  <span
    className={cx('vs-mark', 'vs-mark--inflight')}
    title={title}
    role="img"
    aria-label={word}
    data-mark="inflight"
  >
    <span className="vs-mark__glyph">
      <span className={cx('vs-mark__ripple', 'vs-anim-ripple')} />
    </span>
    <span className="vs-caption">{word}</span>
  </span>
);
