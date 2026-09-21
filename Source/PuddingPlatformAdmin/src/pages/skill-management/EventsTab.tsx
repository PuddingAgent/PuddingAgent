import { Button, Input, message, Select, Space, Table, Tag, Typography } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import type { ColumnsType } from 'antd/es/table';
import type { HubSkillEventDto } from '@/services/platform/api';
import { listHubEvents } from '@/services/platform/api';
import { errText, formatDateTime, HubEmpty, renderActor } from './skillHubShared';

const { Text } = Typography;

const LIMIT_OPTIONS = [50, 100, 200, 500].map((n) => ({ value: n, label: `${n} 条` }));

/** 事件类型 → 展示色（未识别类型用 default，保持克制） */
function eventTypeColor(eventType: string): string {
  const t = (eventType || '').toLowerCase();
  if (t.includes('publish') || t.includes('create')) return 'green';
  if (t.includes('update') || t.includes('patch') || t.includes('evolve') || t.includes('version'))
    return 'blue';
  if (t.includes('install')) return 'cyan';
  if (t.includes('retire') || t.includes('delete') || t.includes('unpublish')) return 'default';
  if (t.includes('meta') || t.includes('status')) return 'orange';
  return 'default';
}

/** 事件审计 Tab：事件类型 / 技能 / 版本 / 操作者 / 工作区 / payload / 时间 */
const EventsTab: React.FC = () => {
  const [events, setEvents] = useState<HubSkillEventDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [skillIdFilter, setSkillIdFilter] = useState('');
  const [appliedSkillId, setAppliedSkillId] = useState('');
  const [limit, setLimit] = useState<number>(100);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const data = await listHubEvents({
        skillId: appliedSkillId.trim() || undefined,
        limit,
      });
      setEvents(data ?? []);
    } catch (e) {
      setEvents([]);
      message.error(`加载审计事件失败：${errText(e)}`);
    } finally {
      setLoading(false);
    }
  }, [appliedSkillId, limit]);

  useEffect(() => {
    void load();
  }, [load]);

  const columns: ColumnsType<HubSkillEventDto> = [
    {
      title: '事件类型',
      dataIndex: 'eventType',
      width: 160,
      render: (v: string) => (
        <Tag color={eventTypeColor(v)} style={{ fontSize: 11 }}>
          {v}
        </Tag>
      ),
    },
    {
      title: '技能',
      dataIndex: 'skillId',
      render: (v: string) => (
        <Text code style={{ fontSize: 11 }}>
          {v}
        </Text>
      ),
    },
    {
      title: '版本',
      dataIndex: 'version',
      width: 90,
      render: (v: string | null) => (v ? <Tag color="blue">v{v}</Tag> : <Text type="secondary">-</Text>),
    },
    {
      title: '操作者',
      key: 'actor',
      width: 180,
      render: (_: unknown, r: HubSkillEventDto) => renderActor(r.actorKind, r.actorId),
    },
    {
      title: '工作区',
      dataIndex: 'workspaceId',
      width: 140,
      ellipsis: true,
      render: (v: string | null) => v || <Text type="secondary">-</Text>,
    },
    {
      title: 'payload',
      dataIndex: 'payloadJson',
      ellipsis: true,
      render: (v: string | null) =>
        v ? (
          <Text code style={{ fontSize: 11 }}>
            {v}
          </Text>
        ) : (
          <Text type="secondary">-</Text>
        ),
    },
    {
      title: '时间',
      dataIndex: 'createdAt',
      width: 170,
      render: (v: string) => <Text style={{ fontSize: 12 }}>{formatDateTime(v)}</Text>,
    },
  ];

  return (
    <div>
      <Space size={8} wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          placeholder="按技能 ID 过滤事件"
          style={{ width: 260 }}
          value={skillIdFilter}
          onChange={(e) => setSkillIdFilter(e.target.value)}
          onSearch={(v) => setAppliedSkillId(v)}
        />
        <Select value={limit} options={LIMIT_OPTIONS} onChange={(v) => setLimit(v)} style={{ width: 110 }} />
        <Button icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
          刷新
        </Button>
      </Space>

      <Table<HubSkillEventDto>
        rowKey="id"
        size="small"
        loading={loading}
        columns={columns}
        dataSource={events}
        pagination={{ pageSize: 20, showSizeChanger: false, showTotal: (t) => `共 ${t} 条` }}
        scroll={{ x: 1000 }}
        locale={{
          emptyText: (
            <HubEmpty description="暂无审计事件：技能发布 / 进化 / 停用 / 安装等操作发生时会在此留痕" />
          ),
        }}
        expandable={{
          rowExpandable: (r) => Boolean(r.payloadJson),
          expandedRowRender: (r) => (
            <pre
              style={{
                margin: 0,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                fontSize: 12,
              }}
            >
              {(() => {
                try {
                  return JSON.stringify(JSON.parse(r.payloadJson ?? ''), null, 2);
                } catch {
                  return r.payloadJson ?? '';
                }
              })()}
            </pre>
          ),
        }}
      />
    </div>
  );
};

export default EventsTab;
