// ── 子代理池面板（任务 B：前端骨架，不含动效）─────────────────────
// 结构：顶部搜索 + 计数，左栏条目列表（固定 320px），右栏详情（未选中时空态）。
// 状态纪律（三态必须互斥，不得互相冒充）：
//   loading            → 只渲染加载态
//   error !== null     → 只渲染错误态 + 重试；**绝不显示「暂无子代理」**
//   成功 && 空数组      → 只渲染空态；**绝不显示错误，也不显示加载中**
import { ReloadOutlined, RobotOutlined, SearchOutlined } from '@ant-design/icons';
import { Button, Input, Modal, Spin } from 'antd';
import dayjs from 'dayjs';
import React, { useMemo, useState } from 'react';
import { deriveAvatar } from './avatar';
import type { SubAgentPoolEntry } from './api';
import { useSubAgentPoolStyles } from './SubAgentPoolPanel.styles';
import { useSubAgentPool } from './useSubAgentPool';

type StatusTone = 'idle' | 'busy' | 'sleeping' | 'dead';

/** 四态各自独立文案与色彩档；不合成模糊的「健康/异常」。 */
const STATUS_META: Record<string, { label: string; tone: StatusTone }> = {
  idle: { label: '空闲', tone: 'idle' },
  busy: { label: '运行中', tone: 'busy' },
  sleeping: { label: '休眠', tone: 'sleeping' },
  dead: { label: '已销毁', tone: 'dead' },
};

/** 后端将来加新状态时按「未知」渲染，而不是崩溃或误报成空闲。 */
const UNKNOWN_STATUS: { label: string; tone: StatusTone } = {
  label: '未知状态',
  tone: 'idle',
};

const statusOf = (status: string) => STATUS_META[status] ?? UNKNOWN_STATUS;

const DASH = '—';

const formatTimestamp = (value: string | null | undefined): string => {
  if (!value) return DASH;
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : value;
};

const textOrDash = (value: string | null | undefined): string => {
  const trimmed = (value ?? '').trim();
  return trimmed.length > 0 ? trimmed : DASH;
};

/**
 * 「最近成功」有两个可能的来源：任务书约定的 `lastSuccessAt` 时间戳，
 * 与后端实际 DTO 的三态 `lastSuccess`（库里没有时刻列）。两者都支持，
 * 绝不用一个恒空的时间字段把人唬成「从未成功」。
 */
export const describeLastSuccess = (
  entry: Pick<SubAgentPoolEntry, 'lastSuccess' | 'lastSuccessAt'>,
): string => {
  if (entry.lastSuccess !== undefined) {
    if (entry.lastSuccess === null) return '从未执行';
    return entry.lastSuccess ? '上次成功' : '上次失败';
  }
  if (entry.lastSuccessAt !== undefined) {
    return formatTimestamp(entry.lastSuccessAt);
  }
  return DASH;
};

/** 匹配 name / role（大小写不敏感，忽略首尾空白）。 */
export const filterSubAgentEntries = (
  entries: SubAgentPoolEntry[],
  keyword: string,
): SubAgentPoolEntry[] => {
  const needle = keyword.trim().toLowerCase();
  if (!needle) return entries;
  return entries.filter((entry) => {
    const name = entry.name.toLowerCase();
    const role = (entry.role ?? '').toLowerCase();
    return name.includes(needle) || role.includes(needle);
  });
};

const StatusBadge: React.FC<{ status: string }> = ({ status }) => {
  const { styles, cx } = useSubAgentPoolStyles();
  const meta = statusOf(status);
  const toneClass =
    meta.tone === 'busy'
      ? styles.badgeBusy
      : meta.tone === 'sleeping'
        ? styles.badgeSleeping
        : meta.tone === 'dead'
          ? styles.badgeDead
          : styles.badgeIdle;

  return (
    <span
      className={cx(styles.badge, toneClass)}
      data-testid={`subagent-status-${status}`}
      data-tone={meta.tone}
    >
      <span className={styles.dot} aria-hidden="true" />
      {meta.label}
    </span>
  );
};

const EntryAvatar: React.FC<{ entry: SubAgentPoolEntry }> = ({ entry }) => {
  const { styles } = useSubAgentPoolStyles();
  const avatarKey = (entry.avatarKey ?? '').trim();

  // avatarKey 只在「像短标识」时按徽标显示；它可能是任意后端标识，
  // 拼成 URL 猜测资源路径会产生 404 噪声，故不做。
  if (avatarKey && avatarKey.length <= 12) {
    return (
      <span className={styles.avatarText} data-testid="subagent-avatar-key">
        {avatarKey}
      </span>
    );
  }

  const { initial, hue } = deriveAvatar(entry.name, entry.poolId);
  return (
    <span
      className={styles.avatar}
      data-testid="subagent-avatar-fallback"
      style={{ '--sa-avatar-hue': hue } as React.CSSProperties}
      aria-hidden="true"
    >
      {initial}
    </span>
  );
};

const DetailRow: React.FC<{ label: string; value: string }> = ({
  label,
  value,
}) => {
  const { styles } = useSubAgentPoolStyles();
  return (
    <div className={styles.detailRow}>
      <span className={styles.detailLabel}>{label}</span>
      <span className={styles.detailValue}>{value}</span>
    </div>
  );
};

const EntryDetail: React.FC<{ entry: SubAgentPoolEntry }> = ({ entry }) => {
  const { styles } = useSubAgentPoolStyles();
  return (
    <section
      className={styles.detail}
      data-testid="subagent-pool-detail"
      aria-label={`${entry.name} 详情`}
    >
      <header className={styles.detailHeader}>
        <EntryAvatar entry={entry} />
        <div style={{ minWidth: 0 }}>
          <div className={styles.detailName} data-testid="subagent-detail-name">
            {entry.name}
          </div>
          <div className={styles.detailResponsibility} data-testid="subagent-detail-responsibility">
            {textOrDash(entry.responsibility ?? entry.role)}
          </div>
        </div>
        <div style={{ marginLeft: 'auto' }}>
          <StatusBadge status={entry.status} />
        </div>
      </header>

      <DetailRow label="角色" value={textOrDash(entry.role)} />
      <DetailRow label="模型" value={textOrDash(entry.modelId)} />
      <DetailRow label="模板" value={textOrDash(entry.templateId)} />
      <DetailRow label="任务数" value={String(entry.taskCount ?? 0)} />
      <DetailRow label="最近使用" value={formatTimestamp(entry.lastUsedAt)} />
      <DetailRow label="最近成功" value={describeLastSuccess(entry)} />
      <DetailRow label="创建时间" value={formatTimestamp(entry.createdAt)} />
    </section>
  );
};

export const SubAgentPoolPanel: React.FC = () => {
  const { styles, cx } = useSubAgentPoolStyles();
  const { data, loading, error, reload } = useSubAgentPool();
  const [keyword, setKeyword] = useState('');
  const [selectedPoolId, setSelectedPoolId] = useState<string | null>(null);

  const entries = data ?? [];
  const visibleEntries = useMemo(
    () => filterSubAgentEntries(entries, keyword),
    [entries, keyword],
  );
  // 选中的条目从**全量**里找：过滤只是收窄视图，不应把已选详情清空。
  const selectedEntry =
    entries.find((entry) => entry.poolId === selectedPoolId) ?? null;

  if (loading) {
    return (
      <div className={styles.host} data-testid="subagent-pool">
        <div className={styles.stateBox} data-testid="subagent-pool-loading">
          <Spin />
          <span className={styles.stateText}>正在加载子代理池…</span>
        </div>
      </div>
    );
  }

  if (error !== null) {
    return (
      <div className={styles.host} data-testid="subagent-pool">
        <div className={styles.stateBox} data-testid="subagent-pool-error">
          <span className={cx(styles.stateTitle, styles.stateError)}>
            加载子代理池失败
          </span>
          <span className={styles.stateText}>{error}</span>
          <Button
            size="small"
            icon={<ReloadOutlined />}
            onClick={reload}
            data-testid="subagent-pool-retry"
          >
            重试
          </Button>
        </div>
      </div>
    );
  }

  if (entries.length === 0) {
    return (
      <div className={styles.host} data-testid="subagent-pool">
        <div className={styles.stateBox} data-testid="subagent-pool-empty">
          <RobotOutlined style={{ fontSize: 22, color: 'var(--pudding-chat-text-subtle)' }} />
          <span className={styles.stateTitle}>暂无子代理</span>
          <span className={styles.stateText}>
            子代理被创建后会出现在这里；当前池为空。
          </span>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.host} data-testid="subagent-pool">
      <div className={styles.toolbar}>
        <Input
          className={styles.search}
          allowClear
          size="small"
          prefix={<SearchOutlined />}
          placeholder="按名称或角色过滤"
          aria-label="过滤子代理"
          value={keyword}
          onChange={(event) => setKeyword(event.target.value)}
          data-testid="subagent-pool-search"
        />
        <span className={styles.count} data-testid="subagent-pool-count">
          共 {entries.length} 个
          {visibleEntries.length !== entries.length
            ? `（匹配 ${visibleEntries.length} 个）`
            : ''}
        </span>
      </div>

      <div className={styles.body}>
        <ul
          className={styles.tree}
          data-testid="subagent-pool-list"
          aria-label="子代理列表"
        >
          {visibleEntries.length === 0 ? (
            <li
              className={styles.treeEmpty}
              data-testid="subagent-pool-list-empty"
            >
              没有匹配的子代理
            </li>
          ) : (
            visibleEntries.map((entry) => (
              <li key={entry.poolId} className={styles.treeItem}>
                <button
                  type="button"
                  className={cx(
                    styles.item,
                    entry.poolId === selectedPoolId && styles.itemActive,
                  )}
                  data-testid={`subagent-pool-item-${entry.poolId}`}
                  aria-label={entry.name}
                  aria-current={entry.poolId === selectedPoolId}
                  onClick={() => setSelectedPoolId(entry.poolId)}
                >
                  <EntryAvatar entry={entry} />
                  <span className={styles.itemMain}>
                    <span className={styles.itemName}>{entry.name}</span>
                    <span className={styles.itemRole}>
                      {textOrDash(entry.role)}
                    </span>
                    <span className={styles.itemMeta}>
                      任务 {entry.taskCount ?? 0} · 最近使用{' '}
                      {formatTimestamp(entry.lastUsedAt)}
                    </span>
                  </span>
                  <StatusBadge status={entry.status} />
                </button>
              </li>
            ))
          )}
        </ul>

        {selectedEntry ? (
          <EntryDetail entry={selectedEntry} />
        ) : (
          <section
            className={styles.detail}
            data-testid="subagent-pool-detail-empty"
          >
            <span className={styles.detailEmpty}>从左侧选择一个子代理查看详情</span>
          </section>
        )}
      </div>
    </div>
  );
};

/** 模态壳：与任务看板一致——约占窗口 80% 宽高，关闭即卸载（下次打开重新取数）。 */
export const SubAgentPoolModal: React.FC<{
  open: boolean;
  onClose: () => void;
}> = ({ open, onClose }) => (
  <Modal
    title="子代理"
    open={open}
    onCancel={onClose}
    footer={null}
    width="80vw"
    destroyOnHidden
    styles={{
      body: {
        height: 'calc(80vh - 88px)',
        overflow: 'hidden',
        display: 'flex',
        flexDirection: 'column',
      },
    }}
  >
    <SubAgentPoolPanel />
  </Modal>
);

export default SubAgentPoolModal;
