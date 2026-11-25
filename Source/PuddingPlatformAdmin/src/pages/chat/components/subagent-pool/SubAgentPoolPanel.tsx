// ── 子代理池面板（任务 B：前端骨架，不含动效）─────────────────────
// 结构：顶部搜索 + 计数，左栏条目列表（固定 320px），右栏详情（未选中时空态）。
// 状态纪律（三态必须互斥，不得互相冒充）：
//   loading            → 只渲染加载态
//   error !== null     → 只渲染错误态 + 重试；**绝不显示「暂无子代理」**
//   成功 && 空数组      → 只渲染空态；**绝不显示错误，也不显示加载中**
import { ReloadOutlined, RobotOutlined, SearchOutlined } from '@ant-design/icons';
import { Button, Input, Modal, Spin, message } from 'antd';
import dayjs from 'dayjs';
import React, { useMemo, useState } from 'react';
import { deriveAvatar } from './avatar';
import {
  destroySubAgentPoolEntry,
  sleepSubAgentPoolEntry,
  updateSubAgentPoolEntry,
  type SubAgentPoolEntry,
} from './api';
import { useSubAgentPoolStyles } from './SubAgentPoolPanel.styles';
import { describeSubAgentPoolActionError } from './errors';
import { describeRunSummary, describeSpend, describeSpendBrief } from './runs';
import { useSubAgentPool } from './useSubAgentPool';
import { useSubAgentRuns } from './useSubAgentRuns';

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
 * 模型路由：池表里的 model_id 是【声明的偏好】，不是执行时的硬绑定。
 *
 * 执行时若未显式传 model，会继承条目声明的路由；条目也没声明，就用派发方当轮解析的路由。
 * 因此空值不是一个“坏掉的字段”，而是「跟随派发方」——写「—」会被读成「未知 / 缺失」，
 * 与实际行为相反。语义已由 e2e 验证：不传 model 且条目声明 deepseek/deepseek-flash 时，
 * 派发输出 advisory 并确实走该路由。
 */
const describeModel = (value: string | null | undefined): string => {
  const trimmed = (value ?? '').trim();
  return trimmed.length > 0 ? trimmed : '跟随派发方';
};

/**
 * 归属会话：创建该条目的父会话，空值就写「未记录」，不拿名字或模板顶替。
 *
 * 截短是因为完整 id 是 32 位十六进制，在右栏会把行撑破；完整值放在 title 里。
 * 只截前 8 位仍能区分同屏的几个条目（同一会话建的多条会显示同一个值，这正是要表达的语义）。
 */
export const describeOwnerSession = (
  value: string | null | undefined,
): string => {
  const trimmed = (value ?? '').trim();
  return trimmed.length > 0 ? `${trimmed.slice(0, 8)}…` : '未记录';
};

/**
 * 模型路由是否合法：恰好一个斜杠，且两侧都非空（`providerId/modelId`）。
 *
 * 用 split 而不是正则：要同时拒掉 `/x`、`a/`、`a/b/c` 三种形态；
 * 分段后逐段判空比写一条带 `+?` 与转义的正则更难写错。
 */
const isValidModelRoute = (value: string): boolean => {
  const parts = value.split('/');
  return (
    parts.length === 2 &&
    parts[0].trim().length > 0 &&
    parts[1].trim().length > 0
  );
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

/**
 * 条目是否真的被派过活。
 *
 * 判据是任务数与执行态，**不是** `lastUsedAt` 是否有值：建条目时后端把
 * `LastUsedAt` 初始化成 `CreatedAt`（`SubAgentPool.cs:85`），所以「时间非空」
 * 对从未使用的条目也成立。只看时间会把创建时刻误读成使用记录。
 */
export const hasBeenUsed = (
  entry: Pick<SubAgentPoolEntry, 'taskCount' | 'status'>,
): boolean => (entry.taskCount ?? 0) > 0 || entry.status === 'busy';

/**
 * 「最近使用」的展示值。
 *
 * 从未使用时如实说「从未使用」，而不是把创建时间当成使用时间显示 ——
 * 那种显示与同屏的「任务数 0 / 最近成功 从未执行」自相矛盾。
 */
export const describeLastUsed = (
  entry: Pick<SubAgentPoolEntry, 'lastUsedAt' | 'taskCount' | 'status'>,
): string => (hasBeenUsed(entry) ? formatTimestamp(entry.lastUsedAt) : '从未使用');

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

/**
 * 列表右侧纵列：状态徽标在下挂一行花费。
 *
 * 为什么单独抽一个组件而不是在 `map` 里算：`visibleEntries.map` 保持简洁箭头体，
 * 不必为一个右栏把 20 多行 JSX 改成块体（块体会迫使整段重缩进，
 * 而大段落重缩进的 diff 没人能审）。
 *
 * 无账本（`spend` 为 null）时**只渲染徽标**，不渲染 `$0` ———
 * 「没记过账」与「花了 $0」不是同一件事。
 */
const EntryRail: React.FC<{ entry: SubAgentPoolEntry }> = ({ entry }) => {
  const { styles } = useSubAgentPoolStyles();
  const brief = describeSpendBrief(entry.spend);

  if (brief === null) return <StatusBadge status={entry.status} />;

  const summary = describeSpend(entry.spend);
  // 窄栏只放得下一个金额；完整口径（token 总量、调用次数、未计价告警）放悬停提示。
  const title = [summary?.amount, summary?.detail, summary?.warning]
    .filter((part): part is string => Boolean(part))
    .join(' · ');

  return (
    <span className={styles.itemRail}>
      <StatusBadge status={entry.status} />
      <span
        className={styles.itemSpend}
        data-testid="subagent-pool-item-spend"
        title={title}
      >
        {brief}
      </span>
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

const DetailRow: React.FC<{ label: string; value: string; title?: string }> = ({
  label,
  value,
  title,
}) => {
  const { styles } = useSubAgentPoolStyles();
  return (
    <div className={styles.detailRow}>
      <span className={styles.detailLabel}>{label}</span>
      <span className={styles.detailValue} title={title}>
        {value}
      </span>
    </div>
  );
};

/**
 * 管理面动作：休眠与销毁。
 *
 * 产品边界（用户 2026-11-24 裁定）：主代理驱动子代理，本界面只做**管理与查看**。
 * 所以这里没有「创建」与「派任务」—— 那两条属于 Agent 侧（spawn_sub_agent）；
 * 从管理界面建出来的条目拿不到恰当的 owner，恰好会落进池的「无主」降级分支。
 */
const EntryActions: React.FC<{
  entry: SubAgentPoolEntry;
  onChanged: () => void;
}> = ({ entry, onChanged }) => {
  const [busy, setBusy] = useState<null | 'sleep' | 'destroy' | 'patch' | 'model' | 'role'>(
    null,
  );
  // NULL = 未编辑；字符串 = 正在编辑该条目的备注草稿。
  const [draft, setDraft] = useState<string | null>(null);
  // 模型路由单独一份草稿：它与备注是两个独立弹窗，共用一个 state 会互相覆盖。
  const [modelDraft, setModelDraft] = useState<string | null>(null);
  // 角色同样单独一份：三个弹窗各自独立，谁也不能把别人的草稿清掉。
  const [roleDraft, setRoleDraft] = useState<string | null>(null);

  const run = async (
    kind: 'sleep' | 'destroy' | 'patch' | 'model' | 'role',
    action: () => Promise<void>,
    successText: string,
  ) => {
    setBusy(kind);
    try {
      await action();
      message.success(successText);
      onChanged();
    } catch (caught: unknown) {
      // 按动作选兜底文案：销毁被拒时不能提示「加载失败」，那会让人以为只是列表没刷新。
      message.error(describeSubAgentPoolActionError(kind, caught));
    } finally {
      setBusy(null);
    }
  };

  const confirmDestroy = () => {
    Modal.confirm({
      title: `销毁「${entry.name}」？`,
      content:
        '销毁后条目退出池，名字可被新条目占用；若它正在执行，执行会被取消。' +
        '与「休眠」不同，销毁不可恢复。',
      okText: '销毁',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () =>
        run('destroy', () => destroySubAgentPoolEntry(entry.name), '已销毁'),
    });
  };

  return (
    <>
      <div
        data-testid="subagent-pool-actions"
        style={{ display: 'flex', gap: 8, marginTop: 12 }}
      >
        <Button
          size="small"
          disabled={busy !== null || entry.status === 'dead'}
          onClick={() => setDraft(entry.responsibility ?? '')}
          data-testid="subagent-action-edit"
        >
          改备注
        </Button>
        <Button
          size="small"
          disabled={busy !== null || entry.status === 'dead'}
          onClick={() => setRoleDraft(entry.role ?? '')}
          data-testid="subagent-action-role"
        >
          改角色
        </Button>
        <Button
          size="small"
          disabled={busy !== null || entry.status === 'dead'}
          onClick={() => setModelDraft(entry.modelId ?? '')}
          data-testid="subagent-action-model"
        >
          改模型
        </Button>
        <Button
          size="small"
          loading={busy === 'sleep'}
          disabled={
            busy !== null || entry.status === 'dead' || entry.status === 'sleeping'
          }
          onClick={() =>
            run('sleep', () => sleepSubAgentPoolEntry(entry.name), '已休眠')
          }
          data-testid="subagent-action-sleep"
        >
          休眠
        </Button>
        <Button
          size="small"
          danger
          loading={busy === 'destroy'}
          disabled={busy !== null || entry.status === 'dead'}
          onClick={confirmDestroy}
          data-testid="subagent-action-destroy"
        >
          销毁
        </Button>
      </div>

      <Modal
        open={draft !== null}
        title={`编辑「${entry.name}」的备注`}
        okText="保存"
        cancelText="取消"
        confirmLoading={busy === 'patch'}
        onCancel={() => {
          if (busy === null) setDraft(null);
        }}
        onOk={() => {
          const value = draft ?? '';
          setDraft(null);
          run(
            'patch',
            () => updateSubAgentPoolEntry(entry.name, { responsibility: value }),
            '备注已更新',
          );
        }}
      >
        <Input.TextArea
          rows={3}
          maxLength={200}
          showCount
          value={draft ?? ''}
          onChange={(event) => setDraft(event.target.value)}
          placeholder="一句话说明它负责什么（留空则清空备注）"
          data-testid="subagent-edit-responsibility"
        />
      </Modal>

      <Modal
        open={roleDraft !== null}
        title={`设置「${entry.name}」的角色`}
        okText="保存"
        cancelText="取消"
        confirmLoading={busy === 'role'}
        onCancel={() => {
          if (busy === null) setRoleDraft(null);
        }}
        onOk={() => {
          const value = (roleDraft ?? '').trim();
          setRoleDraft(null);
          run(
            'role',
            () => updateSubAgentPoolEntry(entry.name, { role: value }),
            value === '' ? '已清空角色' : '角色已更新',
          );
        }}
      >
        <Input
          maxLength={64}
          value={roleDraft ?? ''}
          onChange={(event) => setRoleDraft(event.target.value)}
          placeholder="能力角色短标签，如 verifier / scout（留空 = 清空）"
          data-testid="subagent-edit-role"
        />
        <div style={{ marginTop: 8, color: 'var(--pudding-text-secondary, #888)', fontSize: 12 }}>
          角色仅用于展示与分类，不参与派发时的模型选择。
        </div>
      </Modal>

      <Modal
        open={modelDraft !== null}
        title={`设置「${entry.name}」的模型路由`}
        okText="保存"
        cancelText="取消"
        confirmLoading={busy === 'model'}
        onCancel={() => {
          if (busy === null) setModelDraft(null);
        }}
        onOk={() => {
          const value = (modelDraft ?? '').trim();
          // 在前端先拦形状：裸 modelId 在多 provider 注册同名模型时会解析失败，
          // 存进去也是等派发时才炸，不如当场告诉用户格式。
          if (value !== '' && !isValidModelRoute(value)) {
            message.error('模型路由要写成 providerId/modelId，例如 deepseek/deepseek-flash');
            return; // 不关弹窗，让用户接着改
          }

          setModelDraft(null);
          run(
            'model',
            () => updateSubAgentPoolEntry(entry.name, { modelId: value }),
            value === '' ? '已改为跟随派发方' : '模型路由已更新',
          );
        }}
      >
        <Input
          maxLength={128}
          value={modelDraft ?? ''}
          onChange={(event) => setModelDraft(event.target.value)}
          placeholder="providerId/modelId，如 deepseek/deepseek-flash（留空 = 跟随派发方）"
          data-testid="subagent-edit-model"
        />
        <div style={{ marginTop: 8, color: 'var(--pudding-text-secondary, #888)', fontSize: 12 }}>
          注意：本列目前是团队约定与审计信息，派发时仍以调用方当轮解析出的模型为准。
        </div>
      </Modal>
    </>
  );
};

/**
 * 运行历史（履历）。
 *
 * 展示「这个人跑过什么」：状态 / 轮次 / 工具数 / 耗时 / 失败原因。
 * 三态纪律与花名册同源：空数组是「从未执行」（成功态），不是错误也不是加载中；
 * 失败时也不许显示成「从未执行」——前者要刷新，后者要建任务，下一步完全不同。
 */
const RunHistory: React.FC<{ name: string }> = ({ name }) => {
  const { runs, total, truncated, loading, error, reload, spend } = useSubAgentRuns(name);
  const muted = { fontSize: 12, color: 'var(--pudding-text-secondary, #888)' } as const;
  // 花费与列表同源（同一个响应），但不从属于某一行：它描述的是这个成员的累计。
  const spendSummary = describeSpend(spend);

  return (
    <section
      data-testid="subagent-run-history"
      aria-label={`${name} 运行历史`}
      style={{
        marginTop: 16,
        paddingTop: 12,
        borderTop: '1px solid var(--pudding-chat-border, #eee)',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 4, marginBottom: 6 }}>
        <span style={muted}>运行历史</span>
        <Button
          size="small"
          type="text"
          icon={<ReloadOutlined />}
          onClick={reload}
          data-testid="subagent-run-reload"
        >
          刷新
        </Button>
      </div>

      {loading ? (
        <div data-testid="subagent-run-loading" style={muted}>
          <Spin size="small" /> <span style={{ marginLeft: 8 }}>正在加载运行历史…</span>
        </div>
      ) : error !== null ? (
        <div data-testid="subagent-run-error" style={{ ...muted, color: '#c0392b' }}>
          {error}
        </div>
      ) : (runs?.length ?? 0) === 0 ? (
        <div data-testid="subagent-run-empty" style={muted}>
          从未执行过任务
        </div>
      ) : (
        <ul
          data-testid="subagent-run-list"
          style={{ margin: 0, padding: 0, listStyle: 'none' }}
        >
          {(runs ?? []).map((run) => (
            <li
              key={run.runId}
              data-testid="subagent-run-row"
              data-run-status={run.status}
              style={{ padding: '4px 0', fontSize: 12 }}
            >
              <span>{describeRunSummary(run)}</span>
              {run.errorMessage ? (
                <div
                  data-testid="subagent-run-error-text"
                  style={{ color: '#c0392b', marginTop: 2 }}
                >
                  {run.errorMessage}
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}

      {/* 累计花费：与 load / error / 空态同一套三态纪律 —— 没拿到结果时不渲染。
          不把它塞进某一行：一行是一条执行，这笔钱是整个子会话（含池复用后的所有派发）。 */}
      {!loading && error === null && spendSummary !== null ? (
        <div
          data-testid="subagent-spend"
          style={{
            marginTop: 8,
            paddingTop: 8,
            borderTop: '1px dashed var(--pudding-chat-border, #eee)',
          }}
        >
          <div style={{ fontSize: 13 }}>
            <span style={muted}>累计花费 </span>
            <strong data-testid="subagent-spend-amount">{spendSummary.amount}</strong>
          </div>
          <div data-testid="subagent-spend-detail" style={muted}>
            {spendSummary.detail}
          </div>
          {spendSummary.warning !== null ? (
            <div
              data-testid="subagent-spend-warning"
              style={{ ...muted, color: '#b9770e' }}
            >
              {spendSummary.warning}
            </div>
          ) : null}
        </div>
      ) : null}

      {/* 截断提示放在列表外：它描述的是「你看到的不是全部」，
          而不是某一条记录的性质。放进列表就会凭空多出一个假行。 */}
      {!loading && error === null && truncated ? (
        <div data-testid="subagent-run-truncated" style={{ ...muted, marginTop: 6 }}>
          仅显示最近 {runs?.length ?? 0} 条，共 {total} 条
        </div>
      ) : null}
    </section>
  );
};

const EntryDetail: React.FC<{
  entry: SubAgentPoolEntry;
  onChanged: () => void;
}> = ({ entry, onChanged }) => {
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
      <DetailRow label="模型" value={describeModel(entry.modelId)} />
      <DetailRow label="模板" value={textOrDash(entry.templateId)} />
      <DetailRow label="任务数" value={String(entry.taskCount ?? 0)} />
      <DetailRow label="最近使用" value={describeLastUsed(entry)} />
      <DetailRow label="最近成功" value={describeLastSuccess(entry)} />
      <DetailRow label="创建时间" value={formatTimestamp(entry.createdAt)} />
      {/* 归属放在时间列之后：它是解释「为何此处驱动不了」的因，不是主体信息。 */}
      <DetailRow
        label="归属会话"
        value={describeOwnerSession(entry.ownerSessionId)}
        title={
          entry.ownerSessionId
            ? `${entry.ownerSessionId}（仅该会话可驱动此成员）`
            : undefined
        }
      />

      <EntryActions entry={entry} onChanged={onChanged} />

      {/* 履历放在动作之后：条目履历会变长，按钮位置必须稳定在上方。 */}
      <RunHistory name={entry.name} />
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
                      任务 {entry.taskCount ?? 0} ·{' '}
                      {hasBeenUsed(entry)
                        ? `最近使用 ${formatTimestamp(entry.lastUsedAt)}`
                        : '从未使用'}
                    </span>
                  </span>
                  <EntryRail entry={entry} />
                </button>
              </li>
            ))
          )}
        </ul>

        {selectedEntry ? (
          <EntryDetail entry={selectedEntry} onChanged={reload} />
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
