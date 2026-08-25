// ── useDisclosureRegistry：TurnContentStream 受控折叠注册表（2026-08-25）──
//
// 折叠规则（AgentTurnCard 重构）：
//  - 默认值：尾部 ActivityGroup 始终展开、历史组折叠；组内节点全部保持
//    单行折叠，运行态由对应行的状态点/扫光表达；
//  - 用户手动展开/折叠优先（key 级 override 粘性）：新事件不得反复抢夺用户
//    的展开状态——尾部组因新正文到达自动转为历史组折叠时，未被用户触碰的
//    组随默认值变化，被触碰过的组保持用户选择。
//
// key 粒度：ActivityGroup 用 block.key，组内节点用 node.key；同一 registry
// 服务整张 Turn 卡片。
import { useCallback, useMemo, useState } from 'react';

export interface DisclosureRegistry {
  /** override 优先；未被用户触碰的 key 返回 defaultExpanded。 */
  isExpanded: (key: string, defaultExpanded: boolean) => boolean;
  /** 翻转 key 的 override（currentEffective 由消费方按当前渲染值传入）。 */
  toggle: (key: string, currentEffective: boolean) => void;
}

export function useDisclosureRegistry(): DisclosureRegistry {
  const [overrides, setOverrides] = useState<Record<string, boolean>>({});

  const isExpanded = useCallback(
    (key: string, defaultExpanded: boolean) =>
      Object.prototype.hasOwnProperty.call(overrides, key)
        ? overrides[key]
        : defaultExpanded,
    [overrides],
  );

  const toggle = useCallback((key: string, currentEffective: boolean) => {
    setOverrides((prev) => ({ ...prev, [key]: !currentEffective }));
  }, []);

  // SSE 每个 delta 都会重渲染内容流；保持 registry 引用稳定，避免所有历史
  // ActivityGroup 仅因包装对象变更而失去 memo 收益。
  return useMemo(() => ({ isExpanded, toggle }), [isExpanded, toggle]);
}
