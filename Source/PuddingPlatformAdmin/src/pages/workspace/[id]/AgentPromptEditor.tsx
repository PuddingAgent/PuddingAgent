import React from 'react';
import { CloseOutlined, DownOutlined, UpOutlined } from '@ant-design/icons';
import { Button, Input, Segmented, Select, Space, Tag, Tooltip } from 'antd';
import type { InputRef } from 'antd';
import { useWorkspaceAgentStyles } from './workbenchStyles';
import MarkdownPreview from './MarkdownPreview';
import {
  PROMPT_DOCUMENTS,
  estimateScrollTop,
  extractTemplateVariables,
  findPromptDocument,
  findTextMatches,
  type PromptDocumentMeta,
} from './promptDocuments';

/**
 * 查找命中的滚动估算参数。
 *
 * 字号 / 行高 / 内边距必须与 workbenchStyles 的 textarea / highlightLayer 保持一致。
 */
const EDITOR_FONT_SIZE_PX = 14;
const EDITOR_LINE_HEIGHT_PX = Math.round(EDITOR_FONT_SIZE_PX * 1.7);
const EDITOR_PADDING_TOP_PX = 16;

export interface AgentPromptEditorProps {
  /** 当前文档的表单字段名。 */
  field: string;
  /** 当前草稿内容（受控，来自 antd Form）。 */
  value: string;
  /** 打开工作台时的基线内容，用于「本次是否修改」提示。 */
  baselineValue: string;
  /** 校验错误文案（未通过校验时显示）。 */
  errorText?: string;
  /** 保存中：禁止重复提交，但保留正在显示的内容。 */
  readOnly?: boolean;
  onChange: (next: string) => void;
  onSwitchDocument: (field: string) => void;
  /** 切换文档时显示未保存与错误标记。 */
  dirtyFields: Set<string>;
  errorFields: Set<string>;
  documents?: PromptDocumentMeta[];
}

/**
 * 「角色与 Prompt」的单文档编辑区。
 *
 * 一期实现（设计文档第 5 节）沿用 antd 受控输入框并把正文交给剩余高度，
 * 不引入大型编辑器；Markdown 文档提供编辑 / 预览切换，查找命中使用覆盖层标记，
 * 不修改原文、不自动插入默认段落。
 */
const AgentPromptEditor: React.FC<AgentPromptEditorProps> = ({
  field,
  value,
  baselineValue,
  errorText,
  readOnly,
  onChange,
  onSwitchDocument,
  dirtyFields,
  errorFields,
  documents = PROMPT_DOCUMENTS,
}) => {
  const { styles, cx } = useWorkspaceAgentStyles();
  const document = findPromptDocument(field) ?? documents[0];
  const text = value ?? '';
  const [mode, setMode] = React.useState<'edit' | 'preview'>('edit');
  const [findOpen, setFindOpen] = React.useState(false);
  const [keyword, setKeyword] = React.useState('');
  const [activeMatch, setActiveMatch] = React.useState(0);
  const textareaRef = React.useRef<HTMLTextAreaElement>(null);
  const highlightRef = React.useRef<HTMLPreElement>(null);
  const findInputRef = React.useRef<InputRef>(null);

  const matches = React.useMemo(() => findTextMatches(text, keyword), [text, keyword]);

  // 切换文档：回到编辑模式，保留草稿内容，不清空也不修剪。
  React.useEffect(() => {
    setMode('edit');
    setKeyword('');
    setActiveMatch(0);
    // 光标落在正文开头，避免新文档沿用上一份文档的滚动位置观感
    const node = textareaRef.current;
    if (node) node.scrollTop = 0;
  }, [field]);

  React.useEffect(() => {
    setActiveMatch(0);
  }, [keyword]);

  // 查找栏打开后聚焦查找输入框
  React.useEffect(() => {
    if (findOpen) findInputRef.current?.focus();
  }, [findOpen]);

  const clampMatch = (index: number) => {
    if (matches.length === 0) return 0;
    return ((index % matches.length) + matches.length) % matches.length;
  };

  // 命中高亮 + 滚动到当前命中（浏览器原生选中，不修改内容）
  React.useEffect(() => {
    if (!findOpen || matches.length === 0) return;
    const index = clampMatch(activeMatch);
    const start = matches[index];
    const end = start + keyword.length;
    if (mode !== 'edit') return;
    const node = textareaRef.current;
    if (!node) return;
    node.setSelectionRange(start, end);
    node.scrollTop = estimateScrollTop(text, start, {
      fontSize: EDITOR_FONT_SIZE_PX,
      lineHeight: EDITOR_LINE_HEIGHT_PX,
      paddingTop: EDITOR_PADDING_TOP_PX,
    });
    if (highlightRef.current) {
      highlightRef.current.scrollTop = node.scrollTop;
    }
  }, [activeMatch, findOpen, keyword, matches, mode, text]);

  const syncHighlightScroll = () => {
    const node = textareaRef.current;
    if (node && highlightRef.current) highlightRef.current.scrollTop = node.scrollTop;
  };

  const handleFind = () => {
    if (findOpen) {
      setFindOpen(false);
      setKeyword('');
      return;
    }
    setFindOpen(true);
  };

  const stepMatch = (delta: number) => {
    if (matches.length === 0) return;
    setActiveMatch((current) => clampMatch(current + delta));
  };

  const handleFindKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      stepMatch(event.shiftKey ? -1 : 1);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      setFindOpen(false);
      setKeyword('');
    }
  };

  const isDirty = text !== (baselineValue ?? '');
  const variables = document.field === 'userPromptTemplate' ? extractTemplateVariables(text) : [];
  const supportsPreview = document.kind === 'markdown';

  // 高亮覆盖层：所有命中标黄，当前命中加深；文本本身透明，仅用于定位底纹。
  const highlightNodes = React.useMemo(() => {
    if (!findOpen || matches.length === 0) return null;
    const current = clampMatch(activeMatch);
    const nodes: React.ReactNode[] = [];
    let cursor = 0;
    matches.forEach((start, index) => {
      if (start > cursor) nodes.push(text.slice(cursor, start));
      nodes.push(
        <mark key={`${start}-${index}`} data-current={index === current ? 'true' : undefined}>
          {text.slice(start, start + keyword.length)}
        </mark>,
      );
      cursor = start + keyword.length;
    });
    if (cursor < text.length) nodes.push(text.slice(cursor));
    return nodes;
  }, [activeMatch, findOpen, keyword, matches, text]);

  const findBar = findOpen ? (
    <div className={styles.findBar} role="search">
      <Input
        ref={findInputRef}
        size="small"
        allowClear
        style={{ maxWidth: 280 }}
        placeholder="在本文档内查找"
        value={keyword}
        onChange={(event) => setKeyword(event.target.value)}
        onKeyDown={handleFindKeyDown}
        aria-label="在本文档内查找"
      />
      <span className={styles.findCount} aria-live="polite">
        {keyword ? `${matches.length} 处` : '—'}
      </span>
      <Tooltip title="上一个（Shift+Enter）">
        <Button
          size="small"
          type="text"
          icon={<UpOutlined />}
          disabled={matches.length === 0}
          onClick={() => stepMatch(-1)}
          aria-label="上一个匹配"
        />
      </Tooltip>
      <Tooltip title="下一个（Enter）">
        <Button
          size="small"
          type="text"
          icon={<DownOutlined />}
          disabled={matches.length === 0}
          onClick={() => stepMatch(1)}
          aria-label="下一个匹配"
        />
      </Tooltip>
      <Button
        size="small"
        type="text"
        icon={<CloseOutlined />}
        onClick={handleFind}
        aria-label="关闭查找"
      />
    </div>
  ) : null;

  return (
    <section className={styles.editor} data-document-field={document.field}>
      {/* 窄屏（<1024px）用选择器取代文档目录 */}
      <div className={styles.documentPicker}>
        <Select
          size="small"
          style={{ flex: '1 1 auto', minWidth: 0, maxWidth: 360 }}
          value={document.field}
          onChange={(next) => onSwitchDocument(next)}
          aria-label="选择 Prompt 文档"
          popupMatchSelectWidth={280}
          options={documents.map((doc) => ({
            value: doc.field,
            label: `${doc.label} · ${doc.tag}${
              errorFields.has(doc.field)
                ? ' · 校验未通过'
                : dirtyFields.has(doc.field)
                  ? ' · 已修改'
                  : ''
            }`,
          }))}
        />
      </div>

      <header className={styles.editorHeader}>
        <div style={{ minWidth: 0 }}>
          <div className={styles.editorTitle}>
            <span>{document.label}</span>
            <Tag bordered={false} style={{ marginInlineEnd: 0, fontSize: 12 }}>
              {document.tag}
            </Tag>
            {document.optional && (
              <Tag bordered={false} color="default" style={{ marginInlineEnd: 0, fontSize: 12 }}>
                可选
              </Tag>
            )}
            {isDirty && (
              <Tag bordered={false} color="warning" style={{ marginInlineEnd: 0, fontSize: 12 }}>
                已修改
              </Tag>
            )}
          </div>
          <div className={styles.editorDescription}>{document.description}</div>
        </div>

        <Space size={8} wrap className={styles.editorToolbar}>
          <Segmented
            size="small"
            value={mode}
            onChange={(next) => setMode(next as 'edit' | 'preview')}
            options={[
              { label: '编辑', value: 'edit' },
              {
                label: '预览',
                value: 'preview',
                disabled: !supportsPreview,
                title: supportsPreview ? undefined : '该内容为纯文本，不提供 Markdown 预览',
              },
            ]}
          />
          <Button size="small" onClick={handleFind} aria-pressed={findOpen}>
            查找
          </Button>
        </Space>
      </header>

      {errorText && (
        <div style={{ marginTop: 8 }}>
          <Tag color="error">{errorText}</Tag>
        </div>
      )}

      {document.field === 'userPromptTemplate' && (
        <div className={styles.variableHint}>
          <span>格式说明：变量写作 {'{{变量名}}'}；本期只做文本辅助，不替换为猜测值。</span>
          <span>
            已出现变量：
            {variables.length > 0
              ? variables.map((name) => (
                  <Tag key={name} bordered={false} style={{ fontSize: 12 }}>
                    {`{{${name}}}`}
                  </Tag>
                ))
              : '无'}
          </span>
        </div>
      )}

      {findBar}

      <div className={styles.editorSurface}>
        {mode === 'edit' ? (
          <div className={styles.highlightWrap}>
            {highlightNodes && (
              <pre ref={highlightRef} className={styles.highlightLayer} aria-hidden="true">
                {highlightNodes}
              </pre>
            )}
            <textarea
              ref={textareaRef}
              className={styles.textarea}
              value={text}
              placeholder={document.placeholder}
              disabled={readOnly}
              spellCheck={false}
              onChange={(event) => onChange(event.target.value)}
              onScroll={syncHighlightScroll}
              aria-label={document.label}
            />
          </div>
        ) : (
          <MarkdownPreview
            markdown={text}
            emptyText={document.emptyHint}
            className={cx(styles.previewScroll, !text.trim() && styles.emptyPreview)}
          />
        )}
      </div>

      <footer className={styles.editorFooter}>
        <span className={styles.editorFooterMeta} aria-live="polite">
          {text.length.toLocaleString('zh-CN')} 字符
          {' · '}
          {isDirty ? (
            <span className={styles.metaDirty}>已修改，将与其他分区一起保存</span>
          ) : text.trim() ? (
            '与打开时一致'
          ) : (
            document.emptyHint
          )}
        </span>
        <span className={styles.editorFooterMeta}>不展示近似 Token 数：前端没有真实分词器。</span>
      </footer>
    </section>
  );
};

export default AgentPromptEditor;
