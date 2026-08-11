import {
  AppstoreOutlined,
  ArrowRightOutlined,
  BugOutlined,
  CheckCircleOutlined,
  CommentOutlined,
  HomeOutlined,
  ReloadOutlined,
  TeamOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Helmet, history, useModel } from '@umijs/max';
import { Alert, Button, Skeleton, Tag } from 'antd';
import React from 'react';
import {
  listWorkspaces,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import {
  buildChatPath,
  buildWorkspacePath,
  readRecentWorkspaceVisit,
  resolveDefaultWorkspace,
  resolveWorkspaceEntryPath,
} from '@/utils/workspaceNavigation';
import Settings from '../../../config/defaultSettings';
import { useHomeStyles } from './styles';
import { createHomeWorkspaceSummary, getHomeGreeting } from './viewModel';

const Home: React.FC = () => {
  const { styles } = useHomeStyles();
  const { initialState } = useModel('@@initialState');
  const [workspaces, setWorkspaces] = React.useState<WorkspaceWithPermDto[]>(
    [],
  );
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string>();
  const [reloadKey, setReloadKey] = React.useState(0);

  React.useEffect(() => {
    let active = true;
    setLoading(true);
    setError(undefined);

    listWorkspaces()
      .then((result) => {
        if (active) setWorkspaces(result);
      })
      .catch((reason: unknown) => {
        if (active)
          setError(
            reason instanceof Error ? reason.message : '工作空间加载失败',
          );
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    return () => {
      active = false;
    };
  }, [reloadKey]);

  const summary = React.useMemo(
    () => createHomeWorkspaceSummary(workspaces),
    [workspaces],
  );
  const availableWorkspaces = React.useMemo(
    () =>
      workspaces.filter(
        (workspace) => workspace.isEnabled && !workspace.isFrozen,
      ),
    [workspaces],
  );
  const displayName =
    initialState?.currentUser?.name ||
    initialState?.currentUser?.userid ||
    'Pudding 用户';
  const greeting = getHomeGreeting(new Date().getHours());
  const defaultWorkspaceId = resolveDefaultWorkspace(workspaces);
  const chatPath = buildChatPath({ workspaceId: defaultWorkspaceId });

  const enterWorkbench = () => {
    if (workspaces.length === 0) {
      history.push(buildWorkspacePath());
      return;
    }

    history.push(
      resolveWorkspaceEntryPath(workspaces, readRecentWorkspaceVisit()),
    );
  };

  const stats = [
    {
      label: '工作空间',
      value: loading ? '—' : summary.total,
      icon: <AppstoreOutlined />,
    },
    {
      label: '可用空间',
      value: loading ? '—' : summary.available,
      icon: <CheckCircleOutlined />,
    },
    {
      label: '协作团队',
      value: loading ? '—' : summary.teamCount,
      icon: <TeamOutlined />,
    },
    { label: 'Core 状态', value: '就绪', icon: <ThunderboltOutlined /> },
  ];

  const quickEntries = [
    {
      title: '开始对话',
      description: '与默认工作空间中的 Agent 协作。',
      icon: <CommentOutlined />,
      path: chatPath,
    },
    {
      title: '工作空间',
      description: '查看 Agent、团队和工作场景。',
      icon: <AppstoreOutlined />,
      path: buildWorkspacePath(),
    },
    {
      title: '模型服务',
      description: '管理 DeepSeek 与其他模型资源。',
      icon: <ThunderboltOutlined />,
      path: '/llm-resource-pool',
    },
    {
      title: '系统诊断',
      description: '检查 Core、运行时与任务状态。',
      icon: <BugOutlined />,
      path: '/diagnostics/overview',
    },
  ];

  return (
    <main className={styles.page} data-testid="pudding-home">
      <Helmet>
        <title>首页 - {Settings.title}</title>
      </Helmet>

      <section className={styles.hero} aria-labelledby="home-title">
        <div>
          <div className={styles.eyebrow}>
            <HomeOutlined />
            Pudding Workbench
          </div>
          <h1 id="home-title" className={styles.heroTitle}>
            {greeting}，{displayName}
          </h1>
          <p className={styles.heroDescription}>
            从这里继续最近的工作，或者让 Pudding
            帮你开启一次新的对话、任务与自动化流程。
          </p>
          <div className={styles.heroActions}>
            <Button
              type="primary"
              size="large"
              icon={<ArrowRightOutlined />}
              onClick={enterWorkbench}
            >
              进入工作台
            </Button>
            <Button
              size="large"
              icon={<CommentOutlined />}
              onClick={() => history.push(chatPath)}
            >
              开始新对话
            </Button>
          </div>
        </div>

        <section className={styles.statusPanel} aria-label="Pudding 运行状态">
          <div>
            <div className={styles.statusLine}>
              <span className={styles.statusDot} />
              Pudding Core 已连接
            </div>
            <div className={styles.statusCaption}>
              Workbench 已通过 Desktop 的本地 Loopback 服务安全连接。
            </div>
          </div>
          <div className={styles.statusMetric}>
            <div>
              <div className={styles.statusMetricValue}>
                {loading ? '—' : summary.available}
              </div>
              <div className={styles.statusMetricLabel}>个空间可以立即使用</div>
            </div>
            <CheckCircleOutlined
              style={{ color: 'var(--pudding-chat-success)', fontSize: 22 }}
            />
          </div>
        </section>
      </section>

      <section className={styles.statsGrid} aria-label="工作台概览">
        {stats.map((item) => (
          <div className={styles.statCard} key={item.label}>
            <span className={styles.statIcon}>{item.icon}</span>
            <div>
              <div className={styles.statValue}>{item.value}</div>
              <div className={styles.statLabel}>{item.label}</div>
            </div>
          </div>
        ))}
      </section>

      {error && (
        <Alert
          style={{ marginTop: 14 }}
          type="warning"
          showIcon
          message="部分首页信息暂时不可用"
          description={error}
          action={
            <Button
              size="small"
              icon={<ReloadOutlined />}
              onClick={() => setReloadKey((value) => value + 1)}
            >
              重试
            </Button>
          }
        />
      )}

      <div className={styles.contentGrid}>
        <section
          className={styles.section}
          aria-labelledby="recent-workspaces-title"
        >
          <div className={styles.sectionHeader}>
            <div>
              <h2 id="recent-workspaces-title" className={styles.sectionTitle}>
                继续工作
              </h2>
              <p className={styles.sectionDescription}>
                进入最近可用的工作空间，恢复 Agent 和任务上下文。
              </p>
            </div>
            <Button
              type="text"
              onClick={() => history.push(buildWorkspacePath())}
            >
              查看全部
            </Button>
          </div>

          {loading ? (
            <Skeleton active paragraph={{ rows: 4 }} title={false} />
          ) : availableWorkspaces.length > 0 ? (
            <div className={styles.workspaceList}>
              {availableWorkspaces.slice(0, 4).map((workspace) => (
                <button
                  type="button"
                  className={styles.workspaceRow}
                  key={workspace.workspaceId}
                  onClick={() =>
                    history.push(buildChatPath({ workspaceId: workspace.workspaceId }))
                  }
                >
                  <span>
                    <span className={styles.workspaceTitleLine}>
                      <span className={styles.workspaceTitle}>
                        {workspace.name}
                      </span>
                      {workspace.workspaceId === 'default' && (
                        <Tag color="purple">默认</Tag>
                      )}
                    </span>
                    <span className={styles.workspaceMeta}>
                      {workspace.teamName} · {workspace.memberCount} 位成员
                      {workspace.description
                        ? ` · ${workspace.description}`
                        : ''}
                    </span>
                  </span>
                  <ArrowRightOutlined className={styles.workspaceArrow} />
                </button>
              ))}
            </div>
          ) : (
            <div className={styles.emptyState}>
              暂无可用工作空间。请先创建或启用一个工作空间。
            </div>
          )}
        </section>

        <section className={styles.section} aria-labelledby="quick-start-title">
          <div className={styles.sectionHeader}>
            <div>
              <h2 id="quick-start-title" className={styles.sectionTitle}>
                快速开始
              </h2>
              <p className={styles.sectionDescription}>常用能力集中在这里。</p>
            </div>
          </div>
          <div className={styles.quickGrid}>
            {quickEntries.map((entry) => (
              <button
                type="button"
                className={styles.quickEntry}
                key={entry.title}
                onClick={() => history.push(entry.path)}
              >
                <span className={styles.quickIcon}>{entry.icon}</span>
                <span>
                  <span className={styles.quickTitle}>{entry.title}</span>
                  <span className={styles.quickDescription}>
                    {entry.description}
                  </span>
                </span>
              </button>
            ))}
          </div>
        </section>
      </div>
    </main>
  );
};

export default Home;
