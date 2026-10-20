// ── ShellBridgeHost：把外壳桥挂到文档面板生命周期上 ────────────────
//
// 纯展示无关组件（渲染 null），只做两件事：
//   1. 文档面板**由关变开**时，通知原生收起工具面板；
//   2. 收到原生「工具面板展开」时，收起文档面板。
//
// 为什么单独一个组件而不是塞进 SideArtifactProvider：
// Provider 是状态容器，被单测和 demo 直接使用；让它依赖 window.chrome.webview
// 会把"有没有外壳"变成 Provider 的前置条件。这里放在应用层接入，
// 没挂外壳（浏览器开发）时它整体是 no-op。
//
// 通知只在**打开跃迁**时发一次：面板一直开着时重渲染不该反复打扰原生。
// ─────────────────────────────────────────────────────────────

import React, { useEffect, useRef } from 'react';
import { useSideArtifactActions, useSideArtifactState } from '../sideArtifact/SideArtifactContext';
import { attachShellBridge, type ShellBridge, type ShellTransport } from './shellBridge';

export interface ShellBridgeHostProps {
  /** 仅测试注入；生产走自动探测。 */
  transport?: ShellTransport | null;
}

export const ShellBridgeHost: React.FC<ShellBridgeHostProps> = ({ transport }) => {
  const state = useSideArtifactState();
  const actions = useSideArtifactActions();
  const bridgeRef = useRef<ShellBridge | null>(null);

  useEffect(() => {
    const bridge = attachShellBridge(
      { onToolPanelOpened: () => actions?.close() },
      transport === undefined ? undefined : transport,
    );
    bridgeRef.current = bridge;
    return () => {
      bridge.dispose();
      bridgeRef.current = null;
    };
  }, [actions, transport]);

  const wasOpen = useRef(false);
  const isOpen = Boolean(state?.open);

  useEffect(() => {
    if (isOpen && !wasOpen.current) bridgeRef.current?.notifyDocPanelOpened();
    wasOpen.current = isOpen;
  }, [isOpen]);

  return null;
};

export default ShellBridgeHost;
