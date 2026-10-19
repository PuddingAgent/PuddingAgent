import React from 'react';
import { displayName, type DocumentRef } from '../markdown/documentRefs';

/**
 * 文档引用卡片：把消息里的 `Docs/xx.md` 变成一个可点击的卡片。
 *
 * 用户原来的痛点是"还得复制路径去硬盘打开"。卡片把这一步压成一次点击，
 * 并把"这是什么文件、多大、什么类型"直接摆在眼前。
 *
 * 注意：这里只负责**展示与触发**，真正读文件是宿主的事（受工作区边界约束）。
 * 组件不碰文件系统，所以可以在任何一端复用。
 */
export interface DocumentRefCardProps {
  ref: DocumentRef;
  onOpen?: (ref: DocumentRef) => void;
  /** 文件大小等附加信息（宿主读到后传入；没有就不显示）。 */
  meta?: string;
}

const ICONS: Record<string, string> = {
  md: '📄',
  markdown: '📄',
  pdf: '📕',
  html: '🌐',
  htm: '🌐',
};

export const DocumentRefCard: React.FC<DocumentRefCardProps> = ({ ref, onOpen, meta }) => {
  const name = ref.kind === 'artifact' ? ref.target : displayName(ref);
  const icon = ref.kind === 'artifact' ? '🖼' : ICONS[ref.ext] ?? '📄';
  const clickable = typeof onOpen === 'function';

  return (
    <button
      type="button"
      data-testid={`doc-ref-${ref.kind}`}
      disabled={!clickable}
      onClick={() => onOpen?.(ref)}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        width: '100%',
        textAlign: 'left',
        padding: '10px 12px',
        border: '1px solid #e5e7eb',
        borderRadius: 10,
        background: '#fff',
        cursor: clickable ? 'pointer' : 'default',
        margin: '6px 0',
        font: 'inherit',
      }}
    >
      <span style={{ fontSize: 18 }}>{icon}</span>
      <span style={{ minWidth: 0, flex: 1 }}>
        <span
          style={{
            display: 'block',
            fontWeight: 600,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {name}
        </span>
        <span style={{ display: 'block', fontSize: 12, opacity: 0.65 }}>
          {ref.kind === 'artifact' ? '产物引用' : ref.target}
          {meta ? ` · ${meta}` : ''}
        </span>
      </span>
      <span
        style={{
          fontSize: 11,
          padding: '1px 7px',
          borderRadius: 999,
          background: '#f2f3f5',
          color: '#5b6169',
          textTransform: 'uppercase',
        }}
      >
        {ref.kind === 'artifact' ? 'artifact' : ref.ext || 'file'}
      </span>
    </button>
  );
};

export default DocumentRefCard;
