import { history } from '@umijs/max';
import { Alert, App, Button, Spin } from 'antd';
import React from 'react';
import { listWorkspaces } from '@/services/platform/api';
import {
  buildWorkspacePath,
  readRecentWorkspaceVisit,
  resolveWorkspaceEntryPath,
} from '@/utils/workspaceNavigation';

const pageStyles: Record<string, React.CSSProperties> = {
  shell: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'var(--warm-beige)',
    color: 'var(--text-primary)',
    padding: 24,
  },
  panel: {
    width: 'min(420px, 100%)',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: 16,
    textAlign: 'center',
  },
  title: {
    margin: 0,
    fontSize: 18,
    fontWeight: 650,
  },
  description: {
    margin: 0,
    fontSize: 13,
    lineHeight: '20px',
    color: 'var(--text-secondary)',
  },
};

const WorkspaceEntry: React.FC = () => {
  const [error, setError] = React.useState<string | null>(null);
  const [retryKey, setRetryKey] = React.useState(0);

  React.useEffect(() => {
    let active = true;
    (async () => {
      setError(null);
      try {
        const workspaces = await listWorkspaces();
        if (!active) return;
        history.replace(resolveWorkspaceEntryPath(workspaces, readRecentWorkspaceVisit()));
      } catch (e: unknown) {
        if (!active) return;
        setError(e instanceof Error ? e.message : '加载工作空间失败');
      }
    })();

    return () => {
      active = false;
    };
  }, [retryKey]);

  return (
    <main style={pageStyles.shell}>
      <section style={pageStyles.panel} aria-label="进入 Pudding 工作台">
        {!error ? (
          <>
            <Spin />
            <h1 style={pageStyles.title}>正在进入工作台</h1>
            <p style={pageStyles.description}>正在恢复最近的工作空间。</p>
          </>
        ) : (
          <>
            <Alert type="error" showIcon message="无法进入工作台" description={error} />
            <Button type="primary" onClick={() => setRetryKey((value) => value + 1)}>
              重试
            </Button>
            <Button type="link" onClick={() => history.replace(buildWorkspacePath())}>
              查看工作空间
            </Button>
          </>
        )}
      </section>
    </main>
  );
};

const WorkspaceEntryWrapper: React.FC = () => (
  <App>
    <WorkspaceEntry />
  </App>
);

export default WorkspaceEntryWrapper;
