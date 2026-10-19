import React from 'react';
import { DocumentPreview } from './DocumentPreview';
import { SideArtifactPanel } from './SideArtifactPanel';
import { useSideArtifact } from '../sideArtifact/SideArtifactContext';

/**
 * 把「当前右侧文档」接到 `SideArtifactPanel`。
 *
 * 这是 chat 页里唯一一处消费 `useSideArtifact()` 的渲染点：
 * 状态由 Provider 持有，长消息卡负责 `openDocument`，这里只负责显示。
 *
 * 没有文档时返回 null——不占布局空间，也不渲染空壳。
 */
export const SideDocumentPanel: React.FC = () => {
  const side = useSideArtifact();
  if (!side || !side.document) return null;

  return (
    <SideArtifactPanel
      open={side.open}
      onClose={side.close}
      tabs={[
        {
          key: 'document',
          label: '文档',
          content: (
            <DocumentPreview
              name={side.document.name}
              markdown={side.document.markdown}
              meta={`约 ${side.document.markdown.length} 字`}
              onCopyPath={() => {
                // 复制路径只对"来自文件"的文档有意义；长消息没有路径。
                void navigator.clipboard?.writeText(side.document?.sourceId ?? side.document?.name ?? '');
              }}
            />
          ),
        },
      ]}
    />
  );
};

export default SideDocumentPanel;
