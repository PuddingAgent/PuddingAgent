import { Button, Segmented } from 'antd';
import React, { useState } from 'react';

/**
 * 右侧产物区外壳。
 *
 * 承载「交互面板 / 文档 / 制成品」三类内容，本身**不认识**任何一种：
 * 只负责 tab 切换、宽度、关闭与滚动。这样面板与文档可以各自演进，
 * 不用改这一层。
 *
 * ## 为什么单独抽出来
 *
 * chat 页（`pages/chat/index.tsx`，900+ 行）目前没有右侧栏机制。
 * 直接改它风险高；先把外壳做成可独立测试的组件，接入时只需加一个
 * state + 一行渲染。
 *
 * ## 受控与非受控
 *
 * 传 `activeKey` 时受控（外部可程序化切换，例如"在右侧打开全文"直接跳到文档 tab）；
 * 不传时内部自管。
 */
export interface SideArtifactTab {
  key: string;
  label: string;
  content: React.ReactNode;
}

export interface SideArtifactPanelProps {
  open: boolean;
  tabs: SideArtifactTab[];
  /** 受控激活的 tab key；不传则内部自管。 */
  activeKey?: string;
  defaultActiveKey?: string;
  onTabChange?: (key: string) => void;
  onClose?: () => void;
  width?: number;
}

export const SideArtifactPanel: React.FC<SideArtifactPanelProps> = ({
  open,
  tabs,
  activeKey,
  defaultActiveKey,
  onTabChange,
  onClose,
  width = 460,
}) => {
  const [innerKey, setInnerKey] = useState(
    defaultActiveKey ?? tabs[0]?.key ?? '',
  );
  const current = activeKey ?? innerKey;
  const activeTab = tabs.find((tab) => tab.key === current) ?? tabs[0];

  if (!open) return null;

  const handleChange = (key: string) => {
    if (activeKey === undefined) setInnerKey(key);
    onTabChange?.(key);
  };

  return (
    <aside
      data-testid="side-artifact-panel"
      aria-label="右侧产物区"
      style={{
        width,
        flex: `0 0 ${width}px`,
        display: 'flex',
        flexDirection: 'column',
        background: '#fff',
        border: '1px solid #e5e7eb',
        borderRadius: 14,
        overflow: 'hidden',
        minHeight: 0,
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '10px 12px',
          borderBottom: '1px solid #e5e7eb',
        }}
      >
        <Segmented
          size="small"
          value={activeTab?.key}
          options={tabs.map((tab) => ({ value: tab.key, label: tab.label }))}
          onChange={(value) => handleChange(String(value))}
        />
        {onClose ? (
          <Button
            size="small"
            type="text"
            data-testid="side-artifact-close"
            onClick={onClose}
            style={{ marginLeft: 'auto' }}
          >
            关闭
          </Button>
        ) : null}
      </div>

      <div
        data-testid="side-artifact-body"
        style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: 12 }}
      >
        {tabs.length === 0 ? (
          <div style={{ fontSize: 12, opacity: 0.5 }}>还没有内容</div>
        ) : (
          activeTab?.content
        )}
      </div>
    </aside>
  );
};

export default SideArtifactPanel;
