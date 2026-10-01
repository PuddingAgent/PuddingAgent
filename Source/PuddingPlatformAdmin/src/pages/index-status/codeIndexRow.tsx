import React from 'react';
import {
  formatRelativeTime,
  formatTriStateBoolean,
  formatTriStateText,
  formatUtcTime,
} from './api';
import {
  LEVEL_TONE,
  deriveCalibrationFreshnessRatio,
  deriveCodeIndexProjectMarks,
  describeMaintenanceReason,
  describeMaintenanceWord,
} from './health';
import {
  PathBrokenMark,
  RingGauge,
  RippleMark,
  StaleMark,
  StatusOrb,
  ToggleGlyph,
  UnregisteredMark,
  UnknownGlyph,
  cx,
} from './visuals';
import type { CodeIndexProjectStatus } from './types';

// ── Slice P3 · B 卡（符号索引）逐项目行 ─────────────────────────────────
// **为什么单独一个文件**：它是「事实 → 视觉」的组合层（吃 `health.ts` 的纯函数结果，喂
// `visuals.tsx` 的组件），与页面（L0/L1/L2 编排 + antd 表格）的依赖面完全不同。
// 拆出来之后切片测试可以**只**渲染这一行做静态断言，而不必把 antd / pro-components 拉进
// 测试进程（可测性优先；也避免给 jest 留下不该有的句柄）。
//
// 三条纪律：
// 1) **D3 红线**：注册态（`registrationHint`，真源 = 索引注册表）与维护态（`maintenanceHint`，
//    真源 = 维护驱动）**分区呈现**（两个 `data-zone`），绝不合成一个「状态」。
// 2) 三重编码：每个标记 = 形状（`vs-mark--*`）+ 动效（或静态）+ 短词；颜色只是冗余编码。
// 3) 噪声预算（§9.7 ⑥）：行内球体 `animate={false}` —— 唯一的「呼吸」名额留给 L0。

export interface CodeIndexProjectRowProps {
  entry: CodeIndexProjectStatus;
  /** 注入的「现在」（不读真实时钟 ⇒ 静态渲染断言不随时间漂移）。 */
  nowMs: number;
}

export const CodeIndexProjectRow: React.FC<CodeIndexProjectRowProps> = ({ entry, nowMs }) => {
  const marks = deriveCodeIndexProjectMarks(entry);
  const maintenanceWord = describeMaintenanceWord(entry);
  const calibrationAt = entry.maintenance?.lastCalibrationAtUtc ?? null;
  const calibrationRatio = deriveCalibrationFreshnessRatio(calibrationAt, nowMs);
  const calibrationLabel = formatRelativeTime(calibrationAt, nowMs);
  const entryTitle = [
    entry.displayName ?? entry.projectId,
    marks.registrationHint,
    marks.maintenanceHint,
  ].join('\n');

  return (
    <div className="vs-ci-row" data-project={entry.projectId}>
      <span className="vs-ci-row__id">
        {/* `animate={false}`：§9.7 ⑥ —— 行内球体不呼吸，唯一的呼吸名额留给 L0；
            行内的动效语义由陈旧急闪 / 涟漪标记承载。 */}
        <StatusOrb
          level={marks.level}
          word={marks.word}
          size="card"
          title={entryTitle}
          animate={false}
        />
        <span className="vs-ci-row__name">{entry.displayName ?? entry.projectId}</span>
      </span>

      {/* 注册态（真源：索引注册表） */}
      <span className="vs-ci-row__zone" title={marks.registrationHint} data-zone="registry">
        <span className="vs-caption">注册</span>
        {marks.unregistered ? (
          <UnregisteredMark
            title={`registered = false ⇒ 未在注册表登记（D1：已注销 ≠ 已清除）\n${marks.registrationHint}`}
          />
        ) : (
          <span className={cx('vs-shortword', 'vs-tone-ok')}>
            {formatTriStateText(entry.registrationState)}
          </span>
        )}
        <span className="vs-caption">{`原始 ${formatTriStateText(entry.registrationStatus)}`}</span>
        <span className="vs-caption">{`来源 ${formatTriStateText(entry.registrationSource)}`}</span>
      </span>

      {/* 维护态（真源：维护驱动进程内状态） */}
      <span className="vs-ci-row__zone" title={marks.maintenanceHint} data-zone="driver">
        <span className="vs-caption">维护</span>
        {marks.maintenanceMissing ? (
          <UnknownGlyph
            label="维护态缺席"
            title={`maintenance = null ⇒ 欠一次挂接（**不等于陈旧**）\n${describeMaintenanceReason(
              entry.maintenanceReason,
            )}`}
          />
        ) : (
          <>
            {marks.indexInFlight ? (
              <RippleMark
                title={`indexInFlight = true ⇒ 正在写索引（不定长：后端无 processed/total）\n${marks.maintenanceHint}`}
              />
            ) : (
              <span className="vs-caption">{formatTriStateText(maintenanceWord)}</span>
            )}
            <ToggleGlyph
              value={marks.watcherAttached}
              label="watcher"
              title={`watcherAttached = ${formatTriStateBoolean(marks.watcherAttached)}\n${marks.maintenanceHint}`}
            />
            <RingGauge
              tone={LEVEL_TONE[marks.level]}
              ratio={calibrationRatio}
              label={calibrationLabel}
              caption="校准"
              size={48}
              strokeWidth={7}
              title={`lastCalibrationAtUtc = ${formatTriStateText(calibrationAt)}（绝对 ${formatUtcTime(
                calibrationAt,
              )}）\n新鲜度窗口 = 24h（表达层约定，后端无校准周期字段）`}
            />
          </>
        )}
      </span>

      {/* 异常标记：陈旧 / 路径失效 / 被拒校准 */}
      <span className="vs-ci-row__marks">
        {marks.pathBroken ? (
          <PathBrokenMark title="rootPathExists = false ⇒ 根路径不存在（D2：死路径不得当作已索引）" />
        ) : null}
        {marks.stale ? (
          <StaleMark
            title={`stale = true ⇒ 未登记 或 根路径不存在（后端 fail-closed 判定）\n根路径：${entry.rootPath}`}
          />
        ) : null}
        {marks.calibrationRejected ? (
          <span
            className={cx('vs-shortword', 'vs-tone-warn')}
            title={`rejectedCalibrationRunCount = ${entry.maintenance?.rejectedCalibrationRunCount} ⇒ 校准因根路径缺失/不可读被拒，索引可能留下陈旧条目`}
          >{`被拒校准 ${entry.maintenance?.rejectedCalibrationRunCount}`}</span>
        ) : null}
      </span>
    </div>
  );
};
