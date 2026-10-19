import React, { useEffect, useState } from 'react';

/**
 * Mermaid 围栏渲染器（` ```mermaid `）。
 *
 * ## 三条硬约束（来自 2026-10-19 调研报告）
 *
 * 1. **必须懒加载**：mermaid 主入口 173.5 KB gzip，绝不能进 chat 首屏。
 *    这里用 `await import('mermaid')`，由 `React.lazy` 在上层兜住。
 * 2. **必须串行**：`mermaid.render` 会操作内部全局状态与一个隐藏的临时容器，
 *    并发渲染会互相踩（图表串味、id 冲突）。用模块级 Promise 链排队。
 * 3. **必须再消毒**：即便 `securityLevel: 'strict'` 已让 mermaid 内部走 DOMPurify，
 *    我们仍把返回的 SVG 再洗一遍后再插入 DOM——它是字符串，插入就是
 *    `dangerouslySetInnerHTML` 级别的 sink，不能只信任上游一次。
 *
 * ## 失败必须降级
 *
 * 语法错误、版本不兼容、库加载失败都不能留白：回退成代码块 + 一行原因，
 * 用户至少还能复制原文去别处渲染。
 */

/** 全站唯一的渲染队列：同一时刻只允许一个 mermaid.render 在跑。 */
let renderQueue: Promise<unknown> = Promise.resolve();
let renderSeq = 0;

/** 动态加载得到的最小接口面（只写我们真用到的方法，避免绑定具体实现）。 */
export interface MermaidLib {
  initialize: (config: Record<string, unknown>) => void;
  render: (id: string, code: string) => Promise<{ svg: string }>;
}

export interface PurifyLib {
  sanitize: (html: string, options?: Record<string, unknown>) => string;
}

/**
 * 动态加载器。默认走真 `import()`，测试可替换。
 *
 * 为什么要把这一步抽出来：jest 的 CommonJS 运行时无法执行 `import()`
 * （会报 ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING_FLAG），若不注入，
 * 成功路径根本无法测——只能测到降级分支。
 */
let loadLibs = async (): Promise<[MermaidLib, PurifyLib]> => {
  const [{ default: mermaid }, { default: DOMPurify }] = await Promise.all([
    import('mermaid'),
    import('dompurify'),
  ]);
  return [mermaid as unknown as MermaidLib, DOMPurify as unknown as PurifyLib];
};

/** 仅测试使用：替换动态加载器。返回还原函数。 */
export function __setRichBlockLoadersForTest(
  loader: () => Promise<[MermaidLib, PurifyLib]>,
): () => void {
  const previous = loadLibs;
  loadLibs = loader;
  return () => {
    loadLibs = previous;
  };
}

const enqueue = <T,>(task: () => Promise<T>): Promise<T> => {
  const run = renderQueue.then(task, task);
  // 队列自身永不 reject，否则后续任务会被前一个失败阻断。
  renderQueue = run.then(
    () => undefined,
    () => undefined,
  );
  return run;
};

const mermaidConfig = {
  startOnLoad: false,
  securityLevel: 'strict',
  suppressErrorRendering: true,
  // 图表里的超长文本会拖垮布局；与官方默认 50000 保持一致，显式写出便于审计。
  maxTextSize: 50000,
} as const;

type Status = 'rendering' | 'ready' | 'failed';

export interface MermaidBlockProps {
  code: string;
  /** 用于测试与排障：渲染成功/失败都会带上。 */
  testId?: string;
}

export const MermaidBlock: React.FC<MermaidBlockProps> = ({
  code,
  testId = 'mermaid-block',
}) => {
  const [status, setStatus] = useState<Status>('rendering');
  const [svg, setSvg] = useState('');
  const [reason, setReason] = useState('');

  useEffect(() => {
    let cancelled = false;
    setStatus('rendering');
    setSvg('');
    setReason('');

    enqueue(async () => {
      const [mermaid, DOMPurify] = await loadLibs();
      mermaid.initialize(mermaidConfig);
      const id = `pb-mermaid-${++renderSeq}`;
      const result = await mermaid.render(id, code);
      return DOMPurify.sanitize(result.svg, {
        USE_PROFILES: { svg: true, svgFilters: true },
      });
    })
      .then((clean) => {
        if (cancelled) return;
        setSvg(clean);
        setStatus('ready');
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        const message =
          error instanceof Error ? error.message : String(error ?? 'unknown error');
        setReason(message);
        setStatus('failed');
      });

    return () => {
      cancelled = true;
    };
  }, [code]);

  if (status === 'failed') {
    return (
      <div data-testid={`${testId}-fallback`} role="note">
        <p style={{ margin: '0 0 6px', fontSize: 12, opacity: 0.7 }}>
          Mermaid 渲染失败，已回退为原文：{reason}
        </p>
        <pre style={{ margin: 0, overflow: 'auto' }}>
          <code>{code}</code>
        </pre>
      </div>
    );
  }

  if (status === 'rendering') {
    return (
      <div data-testid={`${testId}-pending`} style={{ fontSize: 12, opacity: 0.6 }}>
        正在渲染图表…
      </div>
    );
  }

  return (
    <div
      data-testid={testId}
      // 内容来自 mermaid.render 的返回值，已过 DOMPurify（USE_PROFILES.svg）；
      // 这是本仓库唯一允许 dangerouslySetInnerHTML 的位置。
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
};

export default MermaidBlock;
