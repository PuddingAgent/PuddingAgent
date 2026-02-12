/**
 * PuddingAdminShell — Console 唯一壳层
 *
 * 替代 ProLayout 的默认壳层，提供 Pudding 设计语言的侧栏、顶栏和主内容区。
 * 当前保留 ProLayout 的路由菜单数据，仅接管视觉表现。
 */
import React from 'react';
import { Layout } from 'antd';
import styles from './styles';

const { Content } = Layout;

export interface PuddingAdminShellProps {
  children: React.ReactNode;
}

export const PuddingAdminShell: React.FC<PuddingAdminShellProps> = ({ children }) => {
  return (
    <div className={styles.shell}>
      <Content className={styles.content}>{children}</Content>
    </div>
  );
};

export default PuddingAdminShell;
