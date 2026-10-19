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
  if (!side?.document) return null;

  // 只有真实文件才有可复制的路径。长消息（对话正文）没有 sourcePath，
  // 因此不渲染「复制路径」—— 避免复制出一个磁盘上不存在的地址。
  const sourcePath = side.document.sourcePath;

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
              meta={
                sourcePath
                  ? `约 ${side.document.markdown.length} 字`
                  : `约 ${side.document.markdown.length} 字 · 来自对话消息`
              }
              onCopyPath={
                sourcePath
                  ? () => {
                      void navigator.clipboard?.writeText(sourcePath);
                    }
                  : undefined
              }
            />
          ),
        },
      ]}
    />
  );
};

export default SideDocumentPanel;
