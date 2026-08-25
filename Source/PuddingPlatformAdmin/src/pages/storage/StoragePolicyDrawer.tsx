import React, { useEffect, useState } from 'react';
import { Alert, Button, Drawer, InputNumber, Popconfirm, Switch, Table, Tag, Typography, message } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { StorageRetentionPolicy, StorageRetentionPolicyTarget } from './types';
import { formatUtc, updateRetentionPolicy } from './api';

// ── 语义目标策略编辑 Drawer（ADR-076 §7.4：保存前显示每类最小/最大范围；CAS 冲突提示刷新）──

interface DraftTarget {
  targetId: string;
  enabled: boolean;
  retentionDays: number;
}

interface PolicyRow extends StorageRetentionPolicyTarget {
  draftIndex: number;
}

export const StoragePolicyDrawer: React.FC<{
  open: boolean;
  policy: StorageRetentionPolicy | null;
  onClose: () => void;
  onSaved: () => void;
}> = ({ open, policy, onClose, onSaved }) => {
  const [draft, setDraft] = useState<DraftTarget[]>([]);
  const [automaticEnabled, setAutomaticEnabled] = useState(true);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (policy) {
      setDraft(
        policy.targets.map((target) => ({
          targetId: target.targetId,
          enabled: target.enabled,
          retentionDays: target.retentionDays ?? target.defaultRetentionDays ?? 14,
        })),
      );
      setAutomaticEnabled(policy.automaticCleanupEnabled);
    }
  }, [policy]);

  const save = async () => {
    if (!policy) return;
    setSaving(true);
    try {
      const invalid = draft.find((item) => {
        const target = policy.targets.find((entry) => entry.targetId === item.targetId);
        if (!target?.automaticCleanupAllowed || !item.enabled) return false;
        return item.retentionDays === 0;
      });
      if (invalid) {
        message.error('保留期不能为 0（0 不代表立即删除，禁用请关闭开关）');
        return;
      }

      await updateRetentionPolicy({
        expectedRevision: policy.policyRevision,
        automaticCleanupEnabled: automaticEnabled,
        targets: draft.map((item) => ({
          targetId: item.targetId,
          enabled: item.enabled,
          retentionDays: item.retentionDays,
        })),
      });
      message.success('保留策略已保存');
      onSaved();
      onClose();
    } catch (error) {
      const detail = (error as { data?: { errorCode?: string; detail?: string }; message?: string }) ?? {};
      if (detail.data?.errorCode === 'storage_policy_conflict') {
        message.error('策略已被其他会话修改，请刷新后重试');
      } else {
        message.error(detail.data?.detail ?? detail.message ?? '保存失败');
      }
    } finally {
      setSaving(false);
    }
  };

  const columns: ColumnsType<PolicyRow> = [
    {
      title: '数据类型',
      dataIndex: 'displayName',
      width: 150,
      render: (name: string, target) => (
        <>
          <div>{name}</div>
          {!target.automaticCleanupAllowed ? (
            <Tag style={{ marginTop: 2 }}>不支持自动</Tag>
          ) : null}
        </>
      ),
    },
    {
      title: '自动清理',
      dataIndex: 'enabled',
      width: 90,
      render: (_: unknown, target) => (
        <Switch
          size="small"
          disabled={!target.automaticCleanupAllowed}
          checked={draft[target.draftIndex]?.enabled ?? false}
          onChange={(checked) => {
            setDraft((previous) =>
              previous.map((item, index) =>
                index === target.draftIndex ? { ...item, enabled: checked } : item,
              ),
            );
          }}
        />
      ),
    },
    {
      title: '保留期（天）',
      dataIndex: 'retentionDays',
      width: 160,
      render: (_: unknown, target) => (
        <InputNumber
          size="small"
          min={target.minRetentionDays ?? 1}
          max={target.maxRetentionDays ?? 365}
          value={draft[target.draftIndex]?.retentionDays}
          disabled={!target.automaticCleanupAllowed}
          onChange={(value) => {
            setDraft((previous) =>
              previous.map((item, index) =>
                index === target.draftIndex ? { ...item, retentionDays: value ?? 1 } : item,
              ),
            );
          }}
        />
      ),
    },
    {
      title: '允许范围',
      dataIndex: 'range',
      render: (_: unknown, target) => (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {target.minRetentionDays ?? 1}–{target.maxRetentionDays ?? 365} 天（0 不合法）
        </Typography.Text>
      ),
    },
  ];

  return (
    <Drawer
      title="自动清理策略"
      open={open}
      width={680}
      onClose={onClose}
      footer={
        <div style={{ display: 'flex', gap: 12, justifyContent: 'flex-end' }}>
          <Button onClick={onClose}>取消</Button>
          <Popconfirm title="确认保存自动清理策略？" onConfirm={save} disabled={saving}>
            <Button type="primary" loading={saving} disabled={saving}>
              保存（CAS）
            </Button>
          </Popconfirm>
        </div>
      }
    >
      {policy?.warnings?.length ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message="策略校验告警（fail closed：对应目标自动清理已暂停）"
          description={
            <ul style={{ margin: 0, paddingLeft: 18 }}>
              {policy.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          }
        />
      ) : null}

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
        <Switch checked={automaticEnabled} onChange={setAutomaticEnabled} />
        <Typography.Text>
          自动清理总开关（每 {policy?.runIntervalHours ?? 24} 小时检查一次；上次完成{' '}
          {formatUtc(policy?.lastCompletedAtUtc)}）
        </Typography.Text>
      </div>

      <Table<PolicyRow>
        rowKey="targetId"
        size="small"
        pagination={false}
        dataSource={(policy?.targets ?? []).map((target, index) => ({ ...target, draftIndex: index }))}
        columns={columns}
      />
    </Drawer>
  );
};
