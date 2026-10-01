import React from 'react';
import { FileTextOutlined } from '@ant-design/icons';
import { useWorkspaceAgentStyles } from './workbenchStyles';
import {
  groupPromptDocuments,
  PROMPT_DOCUMENTS,
  type PromptDocumentMeta,
} from './promptDocuments';

export interface AgentPromptCatalogProps {
  /** 当前选中的文档字段。 */
  activeField: string;
  /** 存在未保存修改的文档字段。 */
  dirtyFields: Set<string>;
  /** 存在校验错误的文档字段。 */
  errorFields: Set<string>;
  onSelect: (field: string) => void;
  documents?: PromptDocumentMeta[];
}

/**
 * 「角色与 Prompt」的文档目录。
 *
 * 目录列出全部文档，选中一份进行编辑；中文职责是主标题，文件名是次级标签。
 */
const AgentPromptCatalog: React.FC<AgentPromptCatalogProps> = ({
  activeField,
  dirtyFields,
  errorFields,
  onSelect,
  documents = PROMPT_DOCUMENTS,
}) => {
  const { styles, cx } = useWorkspaceAgentStyles();
  const groups = React.useMemo(() => groupPromptDocuments(documents), [documents]);

  return (
    <nav className={styles.documentNav} aria-label="Prompt 文档目录">
      <div className={styles.documentNavHeading}>
        <div className={styles.documentNavTitle}>角色与 Prompt</div>
        <div className={styles.documentNavSubtitle}>
          {documents.length} 份内容 · 按需配置
        </div>
      </div>

      {groups.map((group) => (
        <div key={group.key}>
          <div className={styles.documentGroupLabel}>{group.label}</div>
          {group.documents.map((doc) => {
            const active = doc.field === activeField;
            const hasError = errorFields.has(doc.field);
            const dirty = dirtyFields.has(doc.field);
            const statusLabel = hasError ? '，校验错误' : dirty ? '，已修改' : '';
            return (
              <button
                key={doc.field}
                type="button"
                className={cx(styles.documentItem, active && styles.documentItemActive)}
                onClick={() => onSelect(doc.field)}
                aria-current={active ? 'true' : undefined}
                aria-label={`${doc.label}（${doc.tag}）${statusLabel}`}
                title={`${doc.label} · ${doc.tag}`}
              >
                <span className={styles.documentItemCopy}>
                  <span
                    className={cx(
                      styles.documentItemLabel,
                      active && styles.documentItemLabelActive,
                      hasError && styles.documentItemError,
                    )}
                  >
                    {doc.label}
                  </span>
                  <span
                    className={cx(
                      styles.documentItemTag,
                      dirty && !hasError && styles.documentItemTagDirty,
                    )}
                  >
                    {hasError ? '校验未通过' : dirty ? `${doc.tag} · 已修改` : doc.tag}
                  </span>
                </span>
                {hasError ? (
                  <span className={cx(styles.documentItemMark, 'mark-error')} aria-hidden />
                ) : dirty ? (
                  <span className={styles.documentItemMark} aria-hidden />
                ) : null}
              </button>
            );
          })}
        </div>
      ))}

      <div className={styles.documentNavSubtitle} style={{ padding: '12px 8px 0' }}>
        <FileTextOutlined /> 目录只是同一份 Agent 配置的编辑入口。
      </div>
    </nav>
  );
};

export default AgentPromptCatalog;
