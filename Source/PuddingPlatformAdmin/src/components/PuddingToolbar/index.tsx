/**
 * PuddingToolbar — 统一筛选、搜索、视图切换
 *
 * 承载搜索、筛选、视图切换、刷新、批量动作。
 * 移动端垂直堆叠。
 * 视图切换使用 Segmented，不使用 solid Radio.Button。
 */
import React from 'react';
import classNames from 'classnames';
import styles from './styles';

export interface PuddingToolbarProps {
  leading?: React.ReactNode;
  filters?: React.ReactNode;
  actions?: React.ReactNode;
  className?: string;
}

export const PuddingToolbar: React.FC<PuddingToolbarProps> = ({
  leading,
  filters,
  actions,
  className,
}) => (
  <div className={classNames(styles.toolbar, className)}>
    <div className={styles.leading}>{leading}</div>
    <div className={styles.filters}>{filters}</div>
    <div className={styles.actions}>{actions}</div>
  </div>
);

export default PuddingToolbar;
