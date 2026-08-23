// ── TurnStatusOrb：TurnStatus 阶段墨球（thinking-orbs，行为链动效升级 2026-08-23）──
// 参考 https://github.com/Jakubantalik/thinking-orbs（MIT）：纯 2D canvas 单色墨点球，
// 九种手调状态动画；内置 prefers-reduced-motion 静帧降级、离屏/隐藏页自动暂停、共享时钟。
//
// 「不喧宾夺主」约束（设计文档 §3.2 动效纪律）：
//  - 全局仅 TurnStatus 运行监视行 leading 槽渲染一颗 20px inline 档墨球；
//    Reasoning/Tool/Delegation 行保持静态 StateDot + 行扫光，同屏不叠加第二颗动画；
//  - 单色墨球（随主题深浅墨切换）不引入新颜色，speed 0.9 略降速；
//  - 库的 theme auto 检测 data-theme/class（Tailwind 约定），与本应用
//    data-pudding-theme 不匹配 —— 显式解析并经 MutationObserver 实时跟随。
import React, { useEffect, useState } from 'react';
import { ThinkingOrb } from 'thinking-orbs';
import type { OrbState, OrbTheme } from 'thinking-orbs';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import type { TurnPhase } from './TurnStatus';

/** 阶段 → orb 状态：pending=待命呼吸；五阶段各取语义形态。 */
const PHASE_TO_ORB: Record<'pending' | TurnPhase, OrbState> = {
  pending: 'breathing',
  connecting: 'connecting',
  reasoning: 'working',
  executing: 'solving',
  delegating: 'weaving',
  answering: 'composing',
};

/** 解析 data-pudding-theme → orb 墨色主题（dark=浅墨 / light=深墨），实时跟随切换。 */
const usePuddingOrbTheme = (): OrbTheme => {
  const read = (): OrbTheme =>
    typeof document !== 'undefined' &&
    document.documentElement.getAttribute('data-pudding-theme') === 'dark'
      ? 'dark'
      : 'light';
  const [theme, setTheme] = useState<OrbTheme>(read);
  useEffect(() => {
    if (typeof MutationObserver === 'undefined') return undefined;
    const observer = new MutationObserver(() => setTheme(read()));
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-pudding-theme'],
    });
    return () => observer.disconnect();
    // read 仅依赖 documentElement 属性，无需进依赖数组
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return theme;
};

export interface TurnStatusOrbProps {
  /** 当前阶段（TurnStatus.phase）；无可见事件时传 pending=true。 */
  phase?: TurnPhase;
  /** turn 无可见事件（「{agentName} 正在运行」待命态）。 */
  pending?: boolean;
  /** 无障碍标签（覆盖库默认英文标签，传阶段中文文案）。 */
  ariaLabel?: string;
}

export const TurnStatusOrb: React.FC<TurnStatusOrbProps> = ({
  phase,
  pending = false,
  ariaLabel,
}) => {
  const { styles } = useExecutionFlowStyles();
  const theme = usePuddingOrbTheme();
  const orbState = PHASE_TO_ORB[pending ? 'pending' : (phase ?? 'connecting')];

  return (
    <span className={styles.orbHost} data-testid="turn-status-orb">
      <ThinkingOrb
        state={orbState}
        size={20}
        theme={theme}
        speed={0.9}
        aria-label={ariaLabel ?? orbState}
      />
    </span>
  );
};

export default React.memo(TurnStatusOrb);
