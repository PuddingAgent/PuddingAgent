// ── HistorySearchModal：历史消息搜索模态窗（参照微信搜索四维分类）────────
import {
  CalendarOutlined,
  ClockCircleOutlined,
  MessageOutlined,
  RightOutlined,
  SearchOutlined,
  TagOutlined,
} from '@ant-design/icons';
import { DatePicker, Input, Modal, Segmented, Spin, Typography } from 'antd';
import dayjs from 'dayjs';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  type MessageSearchMatch,
  type TopicSearchMatch,
  searchMessages,
  searchTopicsOnly,
} from '@/services/platform/api';

const { Text } = Typography;

/** 搜索维度：全部 / 话题 / 消息 / 日期 */
type SearchMode = 'all' | 'topic' | 'message' | 'date';

interface HistorySearchModalProps {
  open: boolean;
  workspaceId: string;
  onClose: () => void;
  onQuote: (quoteText: string) => void;
}

interface SearchResultItem {
  id: string;
  kind: 'message' | 'topic';
  sessionId: string;
  day: string;
  title: string;
  subtitle: string;
  messageId?: number;
  raw: MessageSearchMatch | TopicSearchMatch;
}

const HistorySearchModal: React.FC<HistorySearchModalProps> = ({
  open,
  workspaceId,
  onClose,
  onQuote,
}) => {
  const [mode, setMode] = useState<SearchMode>('all');
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(false);
  const [results, setResults] = useState<SearchResultItem[]>([]);
  const [searched, setSearched] = useState(false);
  const [dateFilter, setDateFilter] = useState<string | null>(null);
  const [selectedItem, setSelectedItem] = useState<SearchResultItem | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailContent, setDetailContent] = useState<string | null>(null);
  const inputRef = useRef<any>(null);

  useEffect(() => {
    if (open && inputRef.current) {
      setTimeout(() => inputRef.current?.focus(), 100);
    }
  }, [open]);

  useEffect(() => {
    if (!open) {
      setQuery('');
      setResults([]);
      setSearched(false);
      setMode('all');
      setDateFilter(null);
      setSelectedItem(null);
      setDetailContent(null);
    }
  }, [open]);

  const handleSearch = useCallback(
    async (q: string, searchMode?: SearchMode) => {
      const effectiveMode = searchMode ?? mode;
      setQuery(q);
      if (!q.trim() && effectiveMode !== 'date') {
        setResults([]);
        setSearched(false);
        return;
      }

      // 日期模式无关键词也可搜索
      if (effectiveMode === 'date' && !q.trim() && !dateFilter) {
        setResults([]);
        setSearched(false);
        return;
      }

      setLoading(true);
      setSearched(true);
      try {
        const items: SearchResultItem[] = [];
        const searchQuery = q.trim();

        // 话题搜索：all / topic 模式
        if (effectiveMode === 'all' || effectiveMode === 'topic') {
          try {
            const topicRes = await searchTopicsOnly(workspaceId, searchQuery || q, 20);
            for (const t of topicRes.topics) {
              items.push({
                id: `topic-${t.messageId}`,
                kind: 'topic',
                sessionId: t.sessionId,
                day: t.createdAt?.substring(0, 10) ?? '',
                title: `# ${t.topicTitle}`,
                subtitle: `${t.createdAt?.substring(0, 10) ?? ''}  ·  话题`,
                messageId: t.messageId,
                raw: t,
              });
            }
          } catch { /* ignore */ }
        }

        // 全文检索：all / message / date 模式
        if (effectiveMode === 'all' || effectiveMode === 'message' || effectiveMode === 'date') {
          try {
            const msgRes = await searchMessages(workspaceId, searchQuery || q, {
              limit: 50,
              fromDay: effectiveMode === 'date' && dateFilter ? dateFilter : undefined,
              toDay: effectiveMode === 'date' && dateFilter ? dateFilter : undefined,
            });
            for (const m of msgRes.matches) {
              const preview = m.snippet.length > 60 ? m.snippet.substring(0, 60) + '…' : m.snippet;
              items.push({
                id: `msg-${m.sessionId}-${m.sequenceNum}`,
                kind: 'message',
                sessionId: m.sessionId,
                day: m.day,
                title: preview || '(无内容)',
                subtitle: `${m.day}  ·  消息`,
                raw: m,
              });
            }
          } catch { /* ignore */ }
        }

        // 日期模式下按日期分组排序
        if (effectiveMode === 'date') {
          items.sort((a, b) => b.day.localeCompare(a.day));
        }

        setResults(items);
      } catch {
        setResults([]);
      } finally {
        setLoading(false);
      }
    },
    [workspaceId, mode, dateFilter],
  );

  const handleModeChange = useCallback(
    (val: string | number) => {
      const newMode = val as SearchMode;
      setMode(newMode);
      if (newMode === 'date' && dateFilter) {
        handleSearch(query, newMode);
      } else if (query.trim()) {
        handleSearch(query, newMode);
      }
    },
    [query, dateFilter, handleSearch],
  );

  const handleDateChange = useCallback(
    (date: dayjs.Dayjs | null) => {
      const dayStr = date ? date.format('YYYY-MM-DD') : null;
      setDateFilter(dayStr);
      if (dayStr) {
        handleSearch(query, 'date');
      }
    },
    [query, handleSearch],
  );

  const handleQuote = useCallback(
    (item: SearchResultItem) => {
      if (item.kind === 'topic' && item.messageId) {
        const t = item.raw as TopicSearchMatch;
        const lines = [
          `> 消息ID：${item.messageId}`,
          `> 请通过Query Session Log工具获取原始信息`,
          `> 话题：#${t.topicTitle}`,
        ];
        onQuote(lines.join('\n') + '\n');
      } else {
        const preview = item.title.length > 80 ? item.title.substring(0, 80) + '…' : item.title;
        const lines = [
          `> 消息ID：请通过Query Session Log查询 session=${item.sessionId}`,
          `> 请通过Query Session Log工具获取原始信息`,
          `> 摘要：${preview || '(无内容)'}`,
        ];
        onQuote(lines.join('\n') + '\n');
      }
      onClose();
    },
    [onQuote, onClose],
  );

  /** 选中条目 → 右侧显示完整消息 */
  const handleSelectItem = useCallback(
    async (item: SearchResultItem) => {
      setSelectedItem(item);
      setDetailLoading(true);
      setDetailContent(null);
      try {
        if (item.kind === 'topic' && item.messageId) {
          const t = item.raw as TopicSearchMatch;
          setDetailContent(
            `# ${t.topicTitle}\n\n---\n\n> 消息ID：${item.messageId}\n> 会话：${item.sessionId}\n> 日期：${item.day}\n>\n> 请通过 **Query Session Log** 工具获取完整原文。`,
          );
        } else {
          const m = item.raw as MessageSearchMatch;
          // 优先使用完整内容
          const fullContent = m.fullContent || m.snippet;
          setDetailContent(
            `**消息全文**  ·  ${item.day}\n\n---\n\n${fullContent || '(无内容)'}\n\n---\n\n> 会话：${item.sessionId}\n> 日期：${item.day}\n> 请通过 **Query Session Log** 工具获取完整原文。`,
          );
        }
      } catch {
        setDetailContent('加载失败');
      } finally {
        setDetailLoading(false);
      }
    },
    [],
  );

  const handleBackToList = useCallback(() => {
    setSelectedItem(null);
    setDetailContent(null);
  }, []);

  return (
    <Modal
      title={null}
      open={open}
      onCancel={onClose}
      footer={null}
      width={selectedItem ? 1100 : 640}
      closable={false}
      styles={{ body: { padding: 0 } }}
    >
      {/* 搜索栏 */}
      <div style={{ padding: '12px 16px 0' }}>
        <Input
          ref={inputRef}
          placeholder={mode === 'date' ? '输入关键词（可选）+ 选择日期筛选' : '搜索'}
          prefix={<SearchOutlined style={{ color: '#8e8e93' }} />}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onPressEnter={() => handleSearch(query)}
          variant="borderless"
          style={{ fontSize: 16, padding: '4px 0' }}
          allowClear
        />
      </div>

      {/* Segmented 四维切换（参照微信） */}
      <div style={{ padding: '8px 16px 12px', borderBottom: '1px solid #f0f0f0' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <Segmented
            size="small"
            value={mode}
            onChange={handleModeChange}
            options={[
              { label: '全部', value: 'all' },
              { label: '话题', value: 'topic' },
              { label: '消息', value: 'message' },
              { label: '日期', value: 'date' },
            ]}
            block
            style={{ flex: 1 }}
          />
        </div>
        {/* 日期模式下显示日期选择器 */}
        {mode === 'date' && (
          <div style={{ marginTop: 8 }}>
            <DatePicker
              size="small"
              value={dateFilter ? dayjs(dateFilter) : null}
              onChange={handleDateChange}
              placeholder="选择日期筛选"
              style={{ width: '100%' }}
              allowClear
            />
          </div>
        )}
      </div>

      {/* 结果区域 — 选中后左右分栏 */}
      <div style={{ display: 'flex', maxHeight: '52vh', minHeight: selectedItem ? 0 : 'auto' }}>
        {/* 左侧：搜索列表 */}
        <div style={{ flex: selectedItem ? '0 0 320px' : 1, overflow: 'auto', borderRight: selectedItem ? '1px solid #f0f0f0' : 'none' }}>
        {loading && (
          <div style={{ textAlign: 'center', padding: 32 }}>
            <Spin />
          </div>
        )}

        {!loading && !searched && (
          <div style={{ textAlign: 'center', padding: 48, color: '#8e8e93' }}>
            <SearchOutlined style={{ fontSize: 32, marginBottom: 12, display: 'block' }} />
            <Text type="secondary">
              {mode === 'date'
                ? '选择日期或输入关键词搜索'
                : mode === 'topic'
                  ? '输入话题标题关键词搜索'
                  : '输入关键词搜索历史消息或话题'}
            </Text>
          </div>
        )}

        {!loading && searched && results.length === 0 && (
          <div style={{ textAlign: 'center', padding: 48, color: '#8e8e93' }}>
            <Text type="secondary">未找到匹配的结果</Text>
          </div>
        )}

        {!loading &&
          results.map((item) => {
            // 高亮搜索词
            const highlightTitle = (text: string, keyword: string) => {
              if (!keyword) return text;
              const idx = text.toLowerCase().indexOf(keyword.toLowerCase());
              if (idx < 0) return text;
              return (
                <>
                  {text.substring(0, idx)}
                  <span style={{ color: '#576b95' }}>{text.substring(idx, idx + keyword.length)}</span>
                  {text.substring(idx + keyword.length)}
                </>
              );
            };

            const searchKeyword = query.trim();

            const isSelected = selectedItem?.id === item.id;

            return (
              <div
                key={item.id}
                onClick={() => handleSelectItem(item)}
                style={{
                  display: 'flex',
                  alignItems: 'flex-start',
                  padding: '13px 16px',
                  cursor: 'pointer',
                  transition: 'background 0.1s',
                  borderBottom: '1px solid #f0f0f0',
                  background: isSelected ? '#e8eeff' : 'transparent',
                }}
                onMouseEnter={(e) => {
                  if (!isSelected) e.currentTarget.style.background = '#f0f0f0';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = 'transparent';
                }}
              >
                {/* 左图标 */}
                <div
                  style={{
                    width: 40,
                    height: 40,
                    borderRadius: 6,
                    background: item.kind === 'topic' ? '#e8eeff' : '#f0f0f0',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    marginRight: 12,
                    marginTop: 2,
                    flexShrink: 0,
                  }}
                >
                  {item.kind === 'topic' ? (
                    <TagOutlined style={{ color: '#4b7bec', fontSize: 18 }} />
                  ) : (
                    <MessageOutlined style={{ color: '#8e8e93', fontSize: 18 }} />
                  )}
                </div>

                {/* 中间文本区 */}
                <div style={{ flex: 1, minWidth: 0 }}>
                  {/* 主标题：最多两行 */}
                  <div
                    style={{
                      fontSize: 15,
                      color: '#191919',
                      fontWeight: item.kind === 'topic' ? 600 : 400,
                      lineHeight: '22px',
                      display: '-webkit-box',
                      WebkitLineClamp: item.kind === 'topic' ? 1 : 2,
                      WebkitBoxOrient: 'vertical',
                      overflow: 'hidden',
                      wordBreak: 'break-all',
                    }}
                  >
                    {highlightTitle(item.title, searchKeyword)}
                  </div>

                  {/* 副标题：日期 + 类型 */}
                  <div
                    style={{
                      fontSize: 13,
                      color: '#b0b0b0',
                      marginTop: 4,
                      display: 'flex',
                      alignItems: 'center',
                      gap: 4,
                      lineHeight: '18px',
                    }}
                  >
                    {item.day || item.subtitle.split('·')[0]?.trim()}
                    {(item.day || item.kind === 'topic') && (
                      <span style={{ color: '#d0d0d0' }}>·</span>
                    )}
                    {item.kind === 'topic' ? '话题' : '消息'}
                  </div>
                </div>

                {/* 右侧箭头 */}
                <RightOutlined
                  style={{
                    color: '#c0c0c0',
                    fontSize: 12,
                    flexShrink: 0,
                    marginLeft: 8,
                    marginTop: 6,
                  }}
                />
              </div>
            );
          })}
        </div>

        {/* 右侧：详情面板（fixed 按钮） */}
        {selectedItem && (
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            {/* 固定头部：返回 + 标题 + 元信息 */}
            <div style={{ flexShrink: 0, padding: '16px 16px 0' }}>
              <div
                onClick={handleBackToList}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 6,
                  marginBottom: 12,
                  cursor: 'pointer',
                  color: '#576b95',
                  fontSize: 14,
                  userSelect: 'none',
                }}
              >
                <span style={{ fontSize: 12 }}>‹</span> 返回列表
              </div>

              <div style={{ fontSize: 15, fontWeight: 600, color: '#191919', marginBottom: 6, lineHeight: '22px' }}>
                {selectedItem.kind === 'topic'
                  ? `# ${(selectedItem.raw as TopicSearchMatch).topicTitle}`
                  : '消息详情'}
              </div>
              <div style={{ fontSize: 12, color: '#b0b0b0', marginBottom: 8 }}>
                {selectedItem.day}  ·  {selectedItem.kind === 'topic' ? '话题' : '消息'}
              </div>
              <div style={{ borderTop: '1px solid #f0f0f0' }} />
            </div>

            {/* 可滚动内容区 */}
            <div style={{ flex: 1, overflow: 'auto', padding: '12px 16px' }}>
              {detailLoading ? (
                <div style={{ textAlign: 'center', padding: 32 }}><Spin /></div>
              ) : (
                <div style={{ fontSize: 14, color: '#333', lineHeight: '22px', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                  {detailContent || '无内容'}
                </div>
              )}
            </div>

            {/* 固定底部：引用按钮 */}
            <div style={{ flexShrink: 0, padding: '12px 16px', borderTop: '1px solid #f0f0f0' }}>
              <button
                onClick={() => handleQuote(selectedItem)}
                style={{ background: '#576b95', color: '#fff', border: 'none', borderRadius: 6, padding: '8px 20px', fontSize: 14, cursor: 'pointer' }}
              >
                引用此消息
              </button>
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
};

export default HistorySearchModal;
