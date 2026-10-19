import React, { useMemo } from 'react';
import type { RichBlockProps } from './RichBlockRegistry';
import { MiniChart } from './MiniChart';
import { parseChartSpecJson } from './chartSpec';

/**
 * ```chart 围栏渲染器。
 *
 * 围栏内容是 **JSON spec**（字段声明 + 数据来源），不是可执行代码。
 * 这条约定让新增图表不会扩大 XSS 面：模型产出的只是数据描述。
 *
 * 数据从哪来：
 * - `inline` → spec 自带 rows；
 * - `artifact` → 由宿主通过 `resolveArtifactRows` 注入（本组件不读存储）。
 *
 * 宿主没提供解析器时**显式说明**，而不是画一张空图——空图会被误读成"数据全为 0"。
 */
export const ChartBlock: React.FC<RichBlockProps> = ({ code, resolveArtifactRows }) => {
  const parsed = useMemo(() => parseChartSpecJson(code), [code]);

  if (!parsed.ok || !parsed.spec) {
    return (
      <div
        data-testid="chart-block-error"
        style={{
          padding: 12,
          border: '1px dashed #f53f3f',
          borderRadius: 8,
          fontSize: 12,
          color: '#f53f3f',
          background: '#fff1f0',
          whiteSpace: 'pre-wrap',
        }}
      >
        图表 spec 无效：{parsed.error}
      </div>
    );
  }

  const spec = parsed.spec;

  if (spec.source.kind === 'artifact' && !resolveArtifactRows) {
    return (
      <div
        data-testid="chart-block-needs-data"
        style={{
          padding: 12,
          border: '1px dashed #ff7d00',
          borderRadius: 8,
          fontSize: 12,
          color: '#ad4e00',
          background: '#fff7e8',
        }}
      >
        该图表引用数据产物 `{spec.source.artifactId}`，需要宿主解析后提供。
      </div>
    );
  }

  const rows =
    spec.source.kind === 'inline'
      ? spec.source.rows
      : resolveArtifactRows?.(spec.source.artifactId) ?? null;

  return <MiniChart spec={spec} rows={rows} />;
};

export default ChartBlock;
