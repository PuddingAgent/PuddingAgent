import { SettingOutlined } from '@ant-design/icons';
import { history, useModel } from '@umijs/max';
import { Button, Tooltip } from 'antd';
import React from 'react';
import { PuddingGlobalActions } from '@/components/GlobalActions';
import { buildWorkspacePath } from '@/utils/workspaceNavigation';

export interface WorkspaceNavigationCrumb {
  label: React.ReactNode;
  title?: string;
  path?: string;
  onClick?: () => void;
  disabled?: boolean;
}

export interface WorkspaceNavigationHeaderProps {
  crumbs: WorkspaceNavigationCrumb[];
  leading?: React.ReactNode;
  controls?: React.ReactNode;
  primaryAction?: React.ReactNode;
  extraActions?: React.ReactNode;
  puddingPath?: string;
}

export const headerStyles: Record<string, React.CSSProperties> = {
  header: {
    minHeight: 48,
    display: 'flex',
    alignItems: 'center',
    gap: 10,
    padding: '0 20px',
    borderBottom: '1px solid var(--pudding-chat-border)',
    background: 'var(--pudding-chat-header-bg)',
    backdropFilter: 'blur(12px)',
    position: 'sticky',
    top: 0,
    zIndex: 20,
    flexWrap: 'wrap',
    color: 'var(--pudding-chat-text)',
    transition: 'background 180ms ease, border-color 180ms ease, color 180ms ease',
  },
  leading: {
    display: 'inline-flex',
    alignItems: 'center',
    flexShrink: 0,
  },
  logo: {
    width: 24,
    height: 24,
    objectFit: 'contain',
    flexShrink: 0,
  },
  breadcrumb: {
    display: 'flex',
    alignItems: 'center',
    gap: 4,
    minWidth: 0,
    flexShrink: 1,
  },
  crumbButton: {
    height: 28,
    padding: '0 2px',
    minWidth: 0,
    maxWidth: 220,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    color: 'var(--pudding-chat-accent)',
    fontWeight: 650,
  },
  crumbText: {
    minWidth: 0,
    maxWidth: 220,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontSize: 13,
    fontWeight: 650,
    color: 'var(--pudding-chat-text)',
  },
  separator: {
    color: 'var(--pudding-chat-text-subtle)',
    flexShrink: 0,
  },
  controls: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 8,
    minWidth: 0,
    flexShrink: 1,
  },
  spacer: {
    flex: 1,
    minWidth: 12,
  },
  actions: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    flexShrink: 0,
  },
};

const renderCrumb = (crumb: WorkspaceNavigationCrumb, index: number) => {
  const key = `${index}-${crumb.title ?? String(crumb.label)}`;
  const content = crumb.path || crumb.onClick ? (
    <Button
      type="link"
      size="small"
      style={headerStyles.crumbButton}
      title={crumb.title}
      disabled={crumb.disabled}
      onClick={() => {
        if (crumb.onClick) {
          crumb.onClick();
          return;
        }
        if (crumb.path) history.push(crumb.path);
      }}
    >
      {crumb.label}
    </Button>
  ) : (
    <span style={headerStyles.crumbText} title={crumb.title}>
      {crumb.label}
    </span>
  );

  return (
    <React.Fragment key={key}>
      <span style={headerStyles.separator}>/</span>
      {content}
    </React.Fragment>
  );
};

const WorkspaceNavigationHeader: React.FC<WorkspaceNavigationHeaderProps> = ({
  crumbs,
  leading,
  controls,
  primaryAction,
  extraActions,
  puddingPath = buildWorkspacePath(),
}) => {
  const { initialState } = useModel('@@initialState');
  const canOpenSystemConsole = initialState?.currentUser?.access === 'admin';

  return (
    <header style={headerStyles.header}>
      {leading && <div style={headerStyles.leading}>{leading}</div>}
      <img src="/admin/assets/images/logo.png" alt="P" style={headerStyles.logo} />
      <nav style={headerStyles.breadcrumb} aria-label="当前位置">
        <Button
          type="link"
          size="small"
          style={headerStyles.crumbButton}
          title="Pudding"
          onClick={() => history.push(puddingPath)}
        >
          Pudding
        </Button>
        {crumbs.map(renderCrumb)}
      </nav>
      {controls && <div style={headerStyles.controls}>{controls}</div>}
      <div style={headerStyles.spacer} />
      <div style={headerStyles.actions}>
        {primaryAction}
        {extraActions}
        {canOpenSystemConsole && (
          <Tooltip title="系统管理">
            <Button
              type="text"
              size="small"
              icon={<SettingOutlined />}
              aria-label="系统管理"
              onClick={() => history.push('/')}
            />
          </Tooltip>
        )}
        <PuddingGlobalActions variant="chat" />
      </div>
    </header>
  );
};

export default WorkspaceNavigationHeader;
