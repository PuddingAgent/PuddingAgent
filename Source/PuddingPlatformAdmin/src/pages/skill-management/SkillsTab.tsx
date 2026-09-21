import { Button, Card, Col, Drawer, Input, List, message, Modal, Radio, Row, Select, Space, Spin, Table, Tag, Tree, Typography } from 'antd';
import { AppstoreOutlined, EyeOutlined, ForkOutlined, PlusOutlined, TableOutlined, ApartmentOutlined, NodeIndexOutlined, ReloadOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import type { ColumnsType } from 'antd/es/table';
import type {
  EvoMapDto,
  HubSkillDetailDto,
  HubSkillSummaryDto,
  HubSkillVersionDto,
  HubSkillVersionFullDto,
} from '@/services/platform/api';
import {
  getHubSkill,
  getHubSkillLineage,
  getHubSkillVersion,
  listHubSkills,
  retireHubSkill,
  updateHubSkillMeta,
} from '@/services/platform/api';
import {
  buildEvoTreeData,
  compareVersions,
  evoActionTag,
  errText,
  formatBytes,
  formatDateTime,
  HubEmpty,
  renderSkillStatusTag,
} from './skillHubShared';
import { PublishHubSkillModal, PublishHubSkillVersionModal } from './SkillWriteModals';

const { Text, Paragraph } = Typography;

const STATUS_OPTIONS = [
  { value: 'active', label: '活跃' },
  { value: 'deprecated', label: '已停用' },
  { value: 'retired', label: '已退役' },
];

/** 技能库 Tab：搜索 + 标签/状态过滤 + 卡片/表格双视图 + 详情/版本/血缘/停用/退役 */
const SkillsTab: React.FC = () => {
  const [items, setItems] = useState<HubSkillSummaryDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [viewMode, setViewMode] = useState<'card' | 'table'>('card');

  // 过滤条件
  const [query, setQuery] = useState('');
  const [tagFilter, setTagFilter] = useState<string | undefined>(undefined);
  const [statusFilter, setStatusFilter] = useState<string | undefined>(undefined);

  // 详情抽屉
  const [detailOpen, setDetailOpen] = useState(false);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detail, setDetail] = useState<HubSkillDetailDto | null>(null);

  // 版本抽屉
  const [versionsOpen, setVersionsOpen] = useState(false);
  const [versionsLoading, setVersionsLoading] = useState(false);
  const [versions, setVersions] = useState<HubSkillVersionDto[]>([]);
  const [versionTitle, setVersionTitle] = useState('');
  const [versionFull, setVersionFull] = useState<HubSkillVersionFullDto | null>(null);
  const [versionFullLoading, setVersionFullLoading] = useState(false);

  // 血缘抽屉
  const [lineageOpen, setLineageOpen] = useState(false);
  const [lineageLoading, setLineageLoading] = useState(false);
  const [lineage, setLineage] = useState<EvoMapDto | null>(null);
  const [lineageTitle, setLineageTitle] = useState('');

  // 写路径入口（2026-09-21）：发布新技能 / 为指定技能发布新版本
  const [publishOpen, setPublishOpen] = useState(false);
  const [versionTarget, setVersionTarget] = useState<{
    skillId: string;
    name: string;
    latestVersion: string;
  } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const data = await listHubSkills({
        query: query.trim() || undefined,
        tag: tagFilter,
        status: statusFilter,
        page: 1,
        pageSize: 200,
      });
      setItems(data ?? []);
    } catch (e) {
      setItems([]);
      message.error(`加载技能库失败：${errText(e)}`);
    } finally {
      setLoading(false);
    }
  }, [query, tagFilter, statusFilter]);

  useEffect(() => {
    void load();
  }, [load]);

  /** 全部标签（由当前列表聚合，供标签过滤下拉） */
  const tagOptions = useMemo(() => {
    const set = new Set<string>();
    for (const it of items) for (const t of it.tags ?? []) set.add(t);
    return Array.from(set)
      .sort()
      .map((t) => ({ value: t, label: t }));
  }, [items]);

  // ── 行操作 ────────────────────────────────────────────────

  const openDetail = useCallback(async (item: HubSkillSummaryDto) => {
    setDetailOpen(true);
    setDetailLoading(true);
    setDetail(null);
    try {
      setDetail(await getHubSkill(item.skillId));
    } catch (e) {
      message.error(`加载技能详情失败：${errText(e)}`);
    } finally {
      setDetailLoading(false);
    }
  }, []);

  const openVersions = useCallback(async (item: HubSkillSummaryDto) => {
    setVersionsOpen(true);
    setVersionsLoading(true);
    setVersions([]);
    setVersionFull(null);
    setVersionTitle(`${item.name}（${item.skillId}）版本历史`);
    try {
      const d = await getHubSkill(item.skillId);
      setVersions(d?.versions ?? []);
    } catch (e) {
      message.error(`加载版本列表失败：${errText(e)}`);
    } finally {
      setVersionsLoading(false);
    }
  }, []);

  const openVersionContent = useCallback(async (skillId: string, version: string) => {
    setVersionFullLoading(true);
    setVersionFull(null);
    try {
      setVersionFull(await getHubSkillVersion(skillId, version));
    } catch (e) {
      message.error(`加载版本全文失败：${errText(e)}`);
    } finally {
      setVersionFullLoading(false);
    }
  }, []);

  const openLineage = useCallback(async (item: HubSkillSummaryDto) => {
    setLineageOpen(true);
    setLineageLoading(true);
    setLineage(null);
    setLineageTitle(`${item.name}（${item.skillId}）进化血缘`);
    try {
      setLineage(await getHubSkillLineage(item.skillId));
    } catch (e) {
      message.error(`加载血缘失败：${errText(e)}`);
    } finally {
      setLineageLoading(false);
    }
  }, []);

  const toggleDeprecated = useCallback(
    async (item: HubSkillSummaryDto) => {
      const next = item.status === 'deprecated' ? 'active' : 'deprecated';
      try {
        await updateHubSkillMeta(item.skillId, { status: next });
        message.success(next === 'deprecated' ? '技能已停用' : '技能已恢复为活跃');
        void load();
      } catch (e) {
        message.error(`更新状态失败：${errText(e)}`);
      }
    },
    [load],
  );

  const retire = useCallback(
    (item: HubSkillSummaryDto) => {
      Modal.confirm({
        title: `确认退役「${item.name}」？`,
        content: '退役为软删除：技能与全部版本内容保留，状态置为 retired，并写入审计事件。该操作会影响后续安装与更新。',
        okText: '确认退役',
        okType: 'danger',
        cancelText: '取消',
        onOk: async () => {
          try {
            await retireHubSkill(item.skillId);
            message.success('技能已退役');
            void load();
          } catch (e) {
            message.error(`退役失败：${errText(e)}`);
          }
        },
      });
    },
    [load],
  );

  // ── 渲染 ──────────────────────────────────────────────────

  const actionButtons = (item: HubSkillSummaryDto): React.ReactNode => (
    <Space size={4} wrap>
      <Button size="small" type="text" icon={<EyeOutlined />} onClick={() => void openDetail(item)}>
        详情
      </Button>
      <Button
        size="small"
        type="text"
        icon={<NodeIndexOutlined />}
        onClick={() => void openVersions(item)}
      >
        版本
      </Button>
      <Button
        size="small"
        type="text"
        icon={<ApartmentOutlined />}
        onClick={() => void openLineage(item)}
      >
        血缘
      </Button>
      {item.status !== 'retired' && (
        <>
          <Button size="small" type="text" onClick={() => void toggleDeprecated(item)}>
            {item.status === 'deprecated' ? '恢复' : '停用'}
          </Button>
          <Button size="small" type="text" danger onClick={() => retire(item)}>
            退役/删除
          </Button>
        </>
      )}
    </Space>
  );

  const toolbar = (
    <Space size={8} wrap style={{ marginBottom: 16 }}>
      <Input.Search
        allowClear
        placeholder="搜索名称 / 摘要 / 关键词"
        style={{ width: 260 }}
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        onSearch={() => void load()}
      />
      <Select
        allowClear
        placeholder="按标签过滤"
        style={{ width: 160 }}
        options={tagOptions}
        value={tagFilter}
        onChange={(v) => setTagFilter(v)}
      />
      <Select
        allowClear
        placeholder="按状态过滤"
        style={{ width: 140 }}
        options={STATUS_OPTIONS}
        value={statusFilter}
        onChange={(v) => setStatusFilter(v)}
      />
      <Radio.Group
        value={viewMode}
        onChange={(e) => setViewMode(e.target.value)}
        optionType="button"
        buttonStyle="solid"
        size="small"
      >
        <Radio.Button value="card">
          <AppstoreOutlined /> 卡片
        </Radio.Button>
        <Radio.Button value="table">
          <TableOutlined /> 表格
        </Radio.Button>
      </Radio.Group>
      <Button size="small" icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
        刷新
      </Button>
      <Button
        size="small"
        type="primary"
        icon={<PlusOutlined />}
        onClick={() => setPublishOpen(true)}
      >
        发布技能
      </Button>
    </Space>
  );

  const columns: ColumnsType<HubSkillSummaryDto> = [
    {
      title: '技能',
      dataIndex: 'name',
      render: (_: unknown, record: HubSkillSummaryDto) => (
        <Space size={6}>
          <Text strong>{record.name}</Text>
          <Text code style={{ fontSize: 11 }}>
            {record.skillId}
          </Text>
        </Space>
      ),
    },
    {
      title: '标签',
      dataIndex: 'tags',
      render: (tags: string[]) =>
        (tags ?? []).length > 0 ? (
          <>
            {(tags ?? []).slice(0, 4).map((t) => (
              <Tag key={t} style={{ fontSize: 11 }}>
                {t}
              </Tag>
            ))}
            {(tags ?? []).length > 4 ? <Text type="secondary">+{tags.length - 4}</Text> : null}
          </>
        ) : (
          <Text type="secondary">-</Text>
        ),
    },
    {
      title: '最新版本',
      dataIndex: 'latestVersion',
      width: 100,
      render: (v: string) => <Tag color="blue">v{v}</Tag>,
    },
    { title: '状态', dataIndex: 'status', width: 90, render: (s: string) => renderSkillStatusTag(s) },
    { title: '版本数', dataIndex: 'versionCount', width: 80 },
    { title: '装机数', dataIndex: 'installCount', width: 80 },
    {
      title: '更新时间',
      dataIndex: 'updatedAt',
      width: 160,
      render: (v: string) => <Text style={{ fontSize: 12 }}>{formatDateTime(v)}</Text>,
    },
    { title: '操作', key: 'actions', width: 320, render: (_: unknown, r: HubSkillSummaryDto) => actionButtons(r) },
  ];

  const empty = (
    <HubEmpty description="暂无 Hub 技能：等待 Agent 通过 skill_hub 工具发布，或调整搜索/过滤条件后重试" />
  );

  const lineageTree = useMemo(() => buildEvoTreeData(lineage), [lineage]);

  return (
    <div>
      {toolbar}

      <Spin spinning={loading}>
        {items.length === 0 && !loading ? (
          empty
        ) : viewMode === 'card' ? (
          <Row gutter={[16, 16]}>
            {items.map((item) => (
              <Col xs={24} sm={12} lg={8} xxl={6} key={item.skillId}>
                <Card
                  size="small"
                  hoverable
                  style={{ borderRadius: 12, borderLeft: '4px solid #9254de' }}
                  styles={{ body: { padding: 14 } }}
                  title={
                    <Space size={6}>
                      <Text strong>{item.name}</Text>
                      <Text code style={{ fontSize: 11 }}>
                        {item.skillId}
                      </Text>
                    </Space>
                  }
                  extra={renderSkillStatusTag(item.status)}
                >
                  <Paragraph
                    type="secondary"
                    style={{ fontSize: 12, marginBottom: 8, minHeight: 32 }}
                    ellipsis={{ rows: 2 }}
                  >
                    {item.summary || '（无摘要）'}
                  </Paragraph>
                  <Space size={4} wrap style={{ marginBottom: 8 }}>
                    <Tag color="blue">v{item.latestVersion}</Tag>
                    <Tag>{item.versionCount} 个版本</Tag>
                    <Tag color="cyan">{item.installCount} 装机</Tag>
                    {(item.tags ?? []).slice(0, 2).map((t) => (
                      <Tag key={t} style={{ fontSize: 11 }}>
                        {t}
                      </Tag>
                    ))}
                  </Space>
                  <div style={{ borderTop: '1px solid #f0f0f0', paddingTop: 8 }}>{actionButtons(item)}</div>
                </Card>
              </Col>
            ))}
          </Row>
        ) : (
          <Table<HubSkillSummaryDto>
            rowKey="skillId"
            size="small"
            columns={columns}
            dataSource={items}
            pagination={{ pageSize: 10, showSizeChanger: false, showTotal: (t) => `共 ${t} 条` }}
            scroll={{ x: 960 }}
          />
        )}
      </Spin>

      {/* 详情抽屉 */}
      <Drawer
        title={detail ? `技能详情 — ${detail.skill.name}` : '技能详情'}
        open={detailOpen}
        width={560}
        onClose={() => setDetailOpen(false)}
        extra={
          detail ? (
            <Button
              size="small"
              type="primary"
              icon={<ForkOutlined />}
              onClick={() =>
                setVersionTarget({
                  skillId: detail.skill.skillId,
                  name: detail.skill.name,
                  latestVersion: detail.skill.latestVersion,
                })
              }
            >
              发布新版本
            </Button>
          ) : null
        }
      >
        {detailLoading ? (
          <Spin />
        ) : detail ? (
          <div>
            <Space size={8} wrap style={{ marginBottom: 12 }}>
              {renderSkillStatusTag(detail.skill.status)}
              <Tag color="blue">v{detail.skill.latestVersion}</Tag>
              <Tag>{detail.skill.originKind}</Tag>
              <Tag>{detail.skill.visibility}</Tag>
            </Space>
            <Paragraph>
              <Text type="secondary">SKILL ID：</Text>
              <Text code>{detail.skill.skillId}</Text>
            </Paragraph>
            {detail.skill.summary && (
              <Paragraph>
                <Text type="secondary">摘要：</Text>
                {detail.skill.summary}
              </Paragraph>
            )}
            {detail.skill.description && (
              <Paragraph>
                <Text type="secondary">描述：</Text>
                {detail.skill.description}
              </Paragraph>
            )}
            <Paragraph>
              <Text type="secondary">标签：</Text>
              {(detail.skill.tags ?? []).length > 0 ? (
                (detail.skill.tags ?? []).map((t) => <Tag key={t}>{t}</Tag>)
              ) : (
                <Text type="secondary">（无）</Text>
              )}
            </Paragraph>
            <Paragraph>
              <Text type="secondary">关键词：</Text>
              {(detail.skill.keywords ?? []).join('、') || '（无）'}
            </Paragraph>
            <Row gutter={16} style={{ margin: '12px 0' }}>
              <Col span={6}>
                <Text strong>{detail.skill.versionCount}</Text>
                <br />
                <Text type="secondary" style={{ fontSize: 12 }}>版本数</Text>
              </Col>
              <Col span={6}>
                <Text strong>{detail.skill.installCount}</Text>
                <br />
                <Text type="secondary" style={{ fontSize: 12 }}>装机 Agent 数</Text>
              </Col>
              <Col span={6}>
                <Text strong>{detail.skill.publishCount}</Text>
                <br />
                <Text type="secondary" style={{ fontSize: 12 }}>发布次数</Text>
              </Col>
            </Row>
            <Paragraph type="secondary" style={{ fontSize: 12 }}>
              来源 Agent：{detail.skill.sourceAgentId || '-'} ｜ 工作区：{detail.skill.ownerWorkspaceId || '-'}
              <br />
              创建：{formatDateTime(detail.skill.createdAt)} ｜ 更新：{formatDateTime(detail.skill.updatedAt)}
            </Paragraph>

            {((detail.recentInstalls ?? []).length > 0) && (
              <>
                <Text strong>最近安装</Text>
                <List
                  size="small"
                  style={{ marginTop: 8 }}
                  dataSource={detail.recentInstalls}
                  renderItem={(ins) => (
                    <List.Item>
                      <Space size={8} wrap>
                        <Text code style={{ fontSize: 11 }}>{ins.agentInstanceId}</Text>
                        <Tag color="blue">v{ins.installedVersion}</Tag>
                        <Text type="secondary" style={{ fontSize: 12 }}>
                          {ins.workspaceId || '-'} · {formatDateTime(ins.installedAt)}
                        </Text>
                      </Space>
                    </List.Item>
                  )}
                />
              </>
            )}
          </div>
        ) : (
          <HubEmpty description="详情加载失败或暂无数据" />
        )}
      </Drawer>

      {/* 版本抽屉 */}
      <Drawer
        title={versionTitle}
        open={versionsOpen}
        width={680}
        onClose={() => setVersionsOpen(false)}
      >
        {versionsLoading ? (
          <Spin />
        ) : versions.length > 0 ? (
          <List
            size="small"
            dataSource={versions}
            renderItem={(v) => (
              <List.Item
                actions={[
                  <Button
                    key="view"
                    size="small"
                    type="link"
                    onClick={() => void openVersionContent(v.skillId, v.version)}
                  >
                    查看全文
                  </Button>,
                ]}
              >
                <List.Item.Meta
                  title={
                    <Space size={6} wrap>
                      {evoActionTag(v.evolutionAction)}
                      <Tag color="blue">v{v.version}</Tag>
                      {v.parentVersion ? (
                        <Text type="secondary" style={{ fontSize: 12 }}>
                          继承自 v{v.parentVersion}
                        </Text>
                      ) : (
                        <Text type="secondary" style={{ fontSize: 12 }}>根版本</Text>
                      )}
                    </Space>
                  }
                  description={
                    <span style={{ fontSize: 12 }}>
                      {formatBytes(v.contentBytes)} · {formatDateTime(v.createdAt)}
                      {v.publishedByAgentId ? ` · 发布者 ${v.publishedByAgentId}` : ''}
                      {v.publishNote ? ` · ${v.publishNote}` : ''}
                    </span>
                  }
                />
              </List.Item>
            )}
          />
        ) : (
          <HubEmpty description="暂无版本记录" />
        )}

        {versionFullLoading && <Spin style={{ marginTop: 12 }} />}
        {versionFull && !versionFullLoading && (
          <Card
            size="small"
            title={`SKILL.md — v${versionFull.version}`}
            style={{ marginTop: 16 }}
          >
            <pre
              style={{
                maxHeight: 420,
                overflow: 'auto',
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                fontSize: 12,
                margin: 0,
              }}
            >
              {versionFull.skillMarkdown || '（空内容）'}
            </pre>
          </Card>
        )}
      </Drawer>

      {/* 血缘抽屉 */}
      <Drawer title={lineageTitle} open={lineageOpen} width={720} onClose={() => setLineageOpen(false)}>
        {lineageLoading ? (
          <Spin />
        ) : lineageTree.treeData.length > 0 ? (
          <Tree
            blockNode
            defaultExpandAll
            showLine={{ showLeafIcon: false }}
            treeData={lineageTree.treeData}
          />
        ) : (
          <HubEmpty description="暂无血缘：该技能为根技能或尚无版本进化记录" />
        )}
      </Drawer>

      {/* 写路径：发布新技能 / 发布新版本（不新增依赖，仅 antd 组件） */}
      <PublishHubSkillModal
        open={publishOpen}
        onClose={() => setPublishOpen(false)}
        onPublished={() => void load()}
      />
      <PublishHubSkillVersionModal
        open={versionTarget !== null}
        skillId={versionTarget?.skillId ?? ''}
        skillName={versionTarget?.name ?? ''}
        latestVersion={versionTarget?.latestVersion ?? ''}
        onClose={() => setVersionTarget(null)}
        onPublished={() => void load()}
      />
    </div>
  );
};

export default SkillsTab;
