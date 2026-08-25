import React, { useState } from 'react';
import { Alert, Button, Modal, Popconfirm, Space, Table, Tag, Typography, message } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { StorageCleanupPreview, StorageCleanupTargetPreview } from './types';
import { createCleanupJob, formatBytes, formatCount, formatUtc } from './api';

// ── 清理预览确认 Modal（ADR-076 §6.2/§7.4：估算约数、固定 cutoff、保护声明、不可逆提示）──

export const CleanupPreviewModal: React.FC<{
  open: boolean;
  preview: StorageCleanupPreview | null;
  submitting: boolean;
  onClose: () => void;
  onConfirmed: (jobId: string) => void;
}> = ({ open, preview, submitting, onClose, onConfirmed }) => {
  const [confirming, setConfirming] = useState(false);

  const confirm = async () => {
    if (!preview) return;
    setConfirming(true);
    try {
      const job = await createCleanupJob(preview.previewId);
      message.success(`清理作业已创建（${job.jobId.slice(0, 8)}…），可关闭页面稍后查看进度`);
      onConfirmed(job.jobId);
      onClose();
    } catch (error) {
      const detail = (error as { data?: { errorCode?: string; detail?: string }; message?: string }) ?? {};
      if (detail.data?.errorCode === 'storage_preview_expired') {
        message.error('预览已过期，请重新生成预览');
      } else {
        message.error(detail.data?.detail ?? detail.message ?? '创建清理作业失败');
      }
    } finally {
      setConfirming(false);
    }
  };

  const columns: ColumnsType<StorageCleanupTargetPreview> = [
    {
      title: '数据类型',
      dataIndex: 'displayName',
      width: 140,
    },
    {
      title: '动作',
      dataIndex: 'actionSummary',
    },
    {
      title: '约候选量',
      dataIndex: 'estimatedCandidateRows',
      width: 150,
      align: 'right',
      render: (rows: number, target) => (
        <Space size={4}>
          <span>≈{formatCount(rows)}</span>
          {target.candidatesTruncated ? <Tag color="orange">≥ 上限</Tag> : null}
        </Space>
      ),
    },
    {
      title: '约占用',
      dataIndex: 'estimatedBytes',
      width: 110,
      align: 'right',
      render: (bytes: number | null) => (bytes == null ? '—' : `≈${formatBytes(bytes)}`),
    },
  ];

  return (
    <Modal
      title="确认清理（估算口径）"
      open={open}
      onCancel={onClose}
      width={760}
      footer={
        <Space>
          <Button onClick={onClose}>取消</Button>
          <Popconfirm
            title="清理不可逆，确认创建作业？"
            description="作业按小批执行并可在批次边界取消；候选数量是估算，最终以作业结果为准。"
            onConfirm={confirm}
            disabled={!preview?.hasCandidates || confirming}
          >
            <Button
              type="primary"
              danger
              loading={confirming || submitting}
              disabled={!preview?.hasCandidates}
            >
              创建清理作业
            </Button>
          </Popconfirm>
        </Space>
      }
    >
      {preview ? (
        <>
          <Typography.Paragraph type="secondary" style={{ marginBottom: 8 }}>
            截止时间（固定）：{formatUtc(preview.cutoffUtc)} ｜ 预览有效期至{' '}
            {formatUtc(preview.expiresAtUtc)}
          </Typography.Paragraph>
          <Table<StorageCleanupTargetPreview>
            rowKey="targetId"
            size="small"
            pagination={false}
            dataSource={preview.targets}
            columns={columns}
          />
          <Alert
            style={{ marginTop: 12 }}
            type="info"
            showIcon
            message="空间口径说明"
            description="删除/清字段后空间先进入 SQLite 库内可复用页，数据库文件不会立即缩小；在线不执行全库 VACUUM。会话正文、计费账本、记忆、任务与配置受保护，永不进入本次清理。"
          />
          {preview.warnings.length ? (
            <Alert
              style={{ marginTop: 8 }}
              type="warning"
              showIcon
              message="提示"
              description={
                <ul style={{ margin: 0, paddingLeft: 18 }}>
                  {preview.warnings.map((warning) => (
                    <li key={warning}>{warning}</li>
                  ))}
                </ul>
              }
            />
          ) : null}
          {!preview.hasCandidates ? (
            <Alert
              style={{ marginTop: 8 }}
              type="success"
              showIcon
              message="所选类型在截止时间之前没有可清理数据"
            />
          ) : null}
        </>
      ) : null}
    </Modal>
  );
};
