import { Button, Card, Col, Drawer, message, Row, Space, Spin, Table, Tag, Tree, Typography } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import type { TableRowSelection } from 'antd/es/table/interface';
import type { EvoMapDto, EvoMapNodeDto, HubSkillSummaryDto } from '@/services/platform/api';
import { getHubLineage, getHubSkillVersion, listHubSkills } from '@/services/platform/api';
import {
  ALL_EVO_ACTIONS,
  buildEvoTreeData,
  EVO_ACTION_COLOR,
  evoActionTag,
  errText,
  formatBytes,
  formatDateTime,
  HubEmpty,
  renderSkillStatusTag,
} from './skillHubShared';

const { Text, Paragraph } = Typography;

/**
 * EVO MAP Tab：左侧多选技能 → 右侧进化树（节点 = {SkillId}@{Version}，动作→颜色）。
 * 点节点弹出侧栏：发布者 / 时间 / 字节数 / 安装数 / SKILL.md 全文（经 getHubSkillVersion 拉取）。
 */
const EvoMapTab: React.FC = () => {
  const [skills, setSkills] = useState<HubSkillSummaryDto[]>([]);
  const [skillsLoading, setSkillsLoading] = useState(true);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);

  const [map, setMap] = useState<EvoMapDto | null>(null);
  const [mapLoading, setMapLoading] = useState(false);
  const [mapError, setMapError] = useState(false);

  // 节点侧栏
  const [nodeOpen, setNodeOpen] = useState(false);
  const [node, setNode] = useState<EvoMapNodeDto | null>(null);
  const [nodeMarkdown, setNodeMarkdown] = useState<string | null>(null);
  const [nodeLoading, setNodeLoading] = useState(false);

  const loadSkills = useCallback(async () => {
    setSkillsLoading(true);
    try {
      const data = await listHubSkills({ page: 1, pageSize: 500 });
      setSkills(data ?? []);
    } catch (e) {
      setSkills([]);
      message.error(`加载技能列表失败：${errText(e)}`);
    } finally {
      setSkillsLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSkills();
  }, [loadSkills]);

  const loadMap = useCallback(async (ids: string[]) => {
    if (ids.length === 0) {
      setMap(null);
      return;
    }
    setMapLoading(true);
    setMapError(false);
    try {
      setMap(await getHubLineage(ids));
    } catch (e) {
      setMap(null);
      setMapError(true);
      message.error(`加载进化图谱失败：${errText(e)}`);
    } finally {
      setMapLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadMap(selectedIds);
  }, [selectedIds, loadMap]);

  const openNode = useCallback(async (nodeDto: EvoMapNodeDto) => {
    setNodeOpen(true);
    setNode(nodeDto);
    setNodeMarkdown(null);
    setNodeLoading(true);
    try {
      const full = await getHubSkillVersion(nodeDto.skillId, nodeDto.version);
      setNodeMarkdown(full?.skillMarkdown ?? '');
    } catch (e) {
      setNodeMarkdown(null);
      message.error(`加载 SKILL.md 全文失败：${errText(e)}`);
    } finally {
      setNodeLoading(false);
    }
  }, []);

  const tree = useMemo(() => buildEvoTreeData(map), [map]);

  const rowSelection: TableRowSelection<HubSkillSummaryDto> = {
    type: 'checkbox',
    selectedRowKeys: selectedIds,
    onChange: (keys) => setSelectedIds(keys.map(String)),
  };

  const skillColumns = [
    {
      title: '技能',
      dataIndex: 'name',
      render: (_: unknown, r: HubSkillSummaryDto) => (
        <span>
          {r.name}
          <br />
          <code style={{ fontSize: 11 }}>{r.skillId}</code>
        </span>
      ),
    },
    {
      title: '最新',
      dataIndex: 'latestVersion',
      width: 70,
      render: (v: string) => <Tag color="blue">v{v}</Tag>,
    },
  ];

  const onSelectNode = (keys: React.Key[]): void => {
    const key = String(keys[0] ?? '');
    const hit = tree.nodeByKey.get(key);
    if (hit) void openNode(hit);
  };

  return (
    <div>
      <Card size="small" style={{ marginBottom: 16 }}>
        <Space size={8} wrap>
          <Text type="secondary" style={{ fontSize: 12 }}>动作图例：</Text>
          {ALL_EVO_ACTIONS.map((a) => (
            <Tag key={a} color={EVO_ACTION_COLOR[a] ?? 'default'} style={{ fontSize: 11 }}>
              {a}
            </Tag>
          ))}
          <Button
            size="small"
            icon={<ReloadOutlined />}
            onClick={() => {
              void loadSkills();
              void loadMap(selectedIds);
            }}
            loading={skillsLoading || mapLoading}
          >
            刷新
          </Button>
        </Space>
      </Card>

      <Row gutter={16}>
        <Col flex="340px">
          <Card size="small" title={`选择技能（已选 ${selectedIds.length}）`}>
            {skills.length === 0 && !skillsLoading ? (
              <HubEmpty description="Hub 中还没有任何技能：待 Agent 发布技能后，可在此勾选并查看进化图谱" />
            ) : (
              <Table<HubSkillSummaryDto>
                rowKey="skillId"
                size="small"
                loading={skillsLoading}
                rowSelection={rowSelection}
                columns={skillColumns}
                dataSource={skills}
                pagination={{ pageSize: 8, showSizeChanger: false, size: 'small' }}
                scroll={{ y: 420 }}
              />
            )}
          </Card>
        </Col>
        <Col flex="auto">
          <Card size="small" title="进化图谱">
            {skillsLoading || mapLoading ? (
              <div style={{ textAlign: 'center', padding: 40 }}>
                <Spin />
              </div>
            ) : selectedIds.length === 0 ? (
              <HubEmpty description="请在左侧勾选技能（可多选）以查看技能进化图谱" />
            ) : mapError ? (
              <HubEmpty description="图谱加载失败：请点击「刷新」重试" />
            ) : tree.treeData.length === 0 ? (
              <HubEmpty description="所选技能暂无进化图谱（尚无版本血缘记录）" />
            ) : (
              <div style={{ overflowX: 'auto', paddingBottom: 8 }}>
                <Tree
                  blockNode
                  defaultExpandAll
                  showLine={{ showLeafIcon: false }}
                  treeData={tree.treeData}
                  onSelect={(keys) => onSelectNode(keys)}
                />
              </div>
            )}
          </Card>
        </Col>
      </Row>

      {/* 节点侧栏 */}
      <Drawer
        title={node ? node.nodeId : '节点详情'}
        open={nodeOpen}
        width={640}
        onClose={() => setNodeOpen(false)}
      >
        {node ? (
          <div>
            <Space size={8} wrap style={{ marginBottom: 12 }}>
              {evoActionTag(node.evolutionAction)}
              {renderSkillStatusTag(node.status)}
              <Tag color="blue">v{node.version}</Tag>
              {node.parentNodeId ? (
                <Tag>继承自 {node.parentNodeId}</Tag>
              ) : (
                <Tag>根节点</Tag>
              )}
            </Space>
            <Paragraph>
              <Text type="secondary">名称：</Text>
              <Text strong>{node.name}</Text>
              <br />
              <Text type="secondary">SKILL ID：</Text>
              <Text code>{node.skillId}</Text>
            </Paragraph>
            <Row gutter={16} style={{ margin: '12px 0' }}>
              <Col span={6}>
                <Text strong>{formatBytes(node.contentBytes)}</Text>
                <br />
                <Text type="secondary" style={{ fontSize: 12 }}>内容字节数</Text>
              </Col>
              <Col span={6}>
                <Text strong>{node.installCount}</Text>
                <br />
                <Text type="secondary" style={{ fontSize: 12 }}>装机数</Text>
              </Col>
            </Row>
            <Paragraph type="secondary" style={{ fontSize: 12 }}>
              发布者：{node.publishedByAgentId || '-'} ｜ 发布时间：{formatDateTime(node.createdAt)}
            </Paragraph>

            <Text strong>SKILL.md 全文</Text>
            {nodeLoading ? (
              <div style={{ padding: 24, textAlign: 'center' }}>
                <Spin />
              </div>
            ) : nodeMarkdown != null ? (
              <pre
                style={{
                  marginTop: 8,
                  maxHeight: 460,
                  overflow: 'auto',
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-word',
                  fontSize: 12,
                  background: 'rgba(0,0,0,0.02)',
                  padding: 12,
                  borderRadius: 8,
                }}
              >
                {nodeMarkdown || '（空内容）'}
              </pre>
            ) : (
              <HubEmpty description="全文加载失败：请关闭侧栏后重试" />
            )}
          </div>
        ) : (
          <HubEmpty description="未选择节点" />
        )}
      </Drawer>
    </div>
  );
};

export default EvoMapTab;
