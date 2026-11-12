import * as React from 'react';
import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Segmented, Tag } from 'antd';
import { DownOutlined, UpOutlined, ReloadOutlined, ToolOutlined } from '@ant-design/icons';
import { getConversationProcess } from '../client/conversationOpenApi';
import type { ConversationProcessItem, ConversationProcessPage } from '../client/conversationOpenTypes';
import { useChatMessageStyles } from '../styles/messageStyleContext';

/** Read a selected channel turn, never the agent's unrelated main conversation. */
export default function ConversationProcessDetails({ conversationId, turnId, running = false }: {
  conversationId: string; turnId: string; running?: boolean;
}) {
  const { styles: sharedStyles, cx } = useChatMessageStyles();
  const styles = sharedStyles as Record<string, string>;
  const [open, setOpen] = useState(false);
  const [view, setView] = useState('overview');
  const [items, setItems] = useState<ConversationProcessItem[]>([]);
  const [page, setPage] = useState<ConversationProcessPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const request = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const through = useRef<number | undefined>(undefined);

  const load = async (before?: number) => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    const current = ++generation.current;
    const timeout = setTimeout(() => controller.abort(), 15_000);
    setBusy(true); setError(false);
    try {
      const result = await getConversationProcess(conversationId, {
        turnId, beforeSequence: before, throughSequence: before ? through.current : undefined,
        limit: 100, signal: controller.signal,
      });
      if (current !== generation.current || controller.signal.aborted) return;
      if (result.hasMoreBefore && (result.nextBeforeSequence == null
        || (before != null && result.nextBeforeSequence >= before))) throw new Error('non-advancing cursor');
      if (!before) through.current = result.items.length ? Math.max(...result.items.map(item => item.sequence)) : undefined;
      setItems(previous => [...new Map([...(before ? previous : []), ...result.items]
        .map(item => [item.eventId, item])).values()].sort((a, b) => a.sequence - b.sequence));
      setPage(result);
    } catch {
      if (current === generation.current) setError(true);
    } finally {
      clearTimeout(timeout);
      if (current === generation.current) { request.current = null; setBusy(false); }
    }
  };

  useEffect(() => {
    setItems([]); setPage(null); setError(false);
    if (open) void load();
    return () => { generation.current++; request.current?.abort(); request.current = null; };
  }, [conversationId, turnId, open]);
  useEffect(() => {
    if (!open || !running) return;
    const timer = setInterval(() => {
      if (!request.current && document.visibilityState !== 'hidden') void load();
    }, 5000);
    return () => clearInterval(timer);
  }, [conversationId, turnId, open, running]);

  const calls = new Map<string, { request?: ConversationProcessItem; result?: ConversationProcessItem }>();
  for (const item of items.filter(item => item.type.startsWith('tool.'))) {
    const key = item.toolCallId || item.eventId;
    const call = calls.get(key) ?? {};
    if (item.type === 'tool.call.requested') call.request = item; else call.result = item;
    calls.set(key, call);
  }
  const thinking = items.filter(item => item.type === 'message.thinking_summary.appended').map(item => item.preview).join('');
  return <div style={{ padding: '8px 12px 8px 40px', minWidth: 0, maxWidth: '100%', overflowWrap: 'anywhere' }}>
    <div className={styles.processSummaryRow}>
      <Button type="text" size="small" aria-label="执行过程" aria-expanded={open}
        icon={open ? <UpOutlined /> : <DownOutlined />} onClick={() => setOpen(value => !value)}>
        执行过程{page ? ` · 已加载 ${calls.size} 次工具调用` : ''}
      </Button>
      {running && <Tag color="processing">进行中</Tag>}
    </div>
    {open && <section aria-label="本回合执行过程">
      <div className={styles.processSummaryHeader}>
        <Segmented size="small" aria-label="过程展示方式" value={view} onChange={value => setView(String(value))}
          options={[{ label: '工具与结果', value: 'overview' }, { label: '原始记录', value: 'raw' }]} />
        <Button size="small" type="text" aria-label="刷新过程" icon={<ReloadOutlined />} loading={busy} onClick={() => void load()}>刷新过程</Button>
      </div>
      {busy && <div role="status" className={styles.processThinkingLabel}>正在读取过程…</div>}
      {error && <Alert type="error" showIcon message="读取执行过程失败，请重试；这不表示没有工具调用。" />}
      {!busy && !error && page && items.length === 0 && !page.hasMoreBefore && <p>本回合没有可展示的过程记录。</p>}
      {view === 'overview' ? <div className={styles.processExpandedArea}>
        {[...calls.entries()].map(([id, call]) => {
          const result = call.result;
          const failed = result && (result.type === 'tool.call.failed' || result.error || (result.exitCode != null && result.exitCode !== 0));
          const name = call.request?.name || result?.name || '工具调用';
          return <section key={id} className={styles.processRound}>
            <div className={cx(styles.processItem, styles.processToolItem)}>
              <div className={styles.processItemHeader}>
                <span className={styles.processItemName}><ToolOutlined /> {name}</span>
                <Tag color={failed ? 'error' : result ? 'success' : 'processing'}>{failed ? '失败' : result ? '完成' : '等待结果'}</Tag>
              </div>
              <details>
                <summary className={styles.processSummaryLink}>参数与返回结果</summary>
                {call.request?.arguments != null && <pre style={{ whiteSpace: 'pre-wrap' }}>参数：{call.request.arguments}</pre>}
                {!call.request && <p>本页未包含调用参数，可加载更早记录。</p>}
                {result?.output != null && <pre style={{ whiteSpace: 'pre-wrap' }}>输出：{result.output}</pre>}
                {result?.exitCode != null && <p>退出码：{result.exitCode}</p>}
                {result?.error && <p role="alert">{result.error}</p>}
                {(call.request?.previewTruncated || result?.previewTruncated) && <p>部分记录为截断预览，原始记录中有标记。</p>}
              </details>
            </div>
          </section>;
        })}
        {thinking && <details className={styles.processRound}><summary className={styles.processSummaryLink}>思考摘要</summary>
          <div style={{ whiteSpace: 'pre-wrap' }}>{thinking}</div></details>}
        {page && !calls.size && items.length > 0 && <p>已加载记录中暂无工具调用，可查看原始记录。</p>}
      </div> : <ol>{items.map(item => <li key={item.eventId}>
        <details>
          <summary>#{item.sequence} · {item.name || item.type}{item.status ? ` · ${item.status}` : ''}</summary>
          {item.arguments != null && <pre style={{ whiteSpace: 'pre-wrap' }}>参数：{item.arguments}</pre>}
          {item.output != null && <pre style={{ whiteSpace: 'pre-wrap' }}>输出：{item.output}</pre>}
          {item.exitCode != null && <p>退出码：{item.exitCode}</p>}
          {item.error && <p role="alert">{item.error}</p>}
          {item.arguments == null && item.output == null && <pre style={{ whiteSpace: 'pre-wrap' }}>{item.preview}</pre>}
          {item.previewTruncated && <p>此条为截断预览。</p>}
        </details>
      </li>)}</ol>}
      {page?.hasMoreBefore && <p className={styles.processThinkingLabel}>更早过程尚未加载。<Button type="link" size="small" disabled={busy}
        onClick={() => void load(page.nextBeforeSequence!)}>加载更早过程</Button></p>}
    </section>}
  </div>;
}
