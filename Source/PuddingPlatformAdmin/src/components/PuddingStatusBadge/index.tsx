/**
 * PuddingStatusBadge — 统一业务状态
 *
 * 使用低饱和色阶，不使用高饱和 Tag。
 * tone 映射：success / warning / danger / neutral / accent
 */
import React from 'react';
import classNames from 'classnames';
import styles from './styles';

export type PuddingStatusTone = 'success' | 'warning' | 'danger' | 'neutral' | 'accent';

export interface PuddingStatusBadgeProps {
  tone: PuddingStatusTone;
  children: React.ReactNode;
  className?: string;
}

export const PuddingStatusBadge: React.FC<PuddingStatusBadgeProps> = ({
  tone,
  children,
  className,
}) => (
  <span className={classNames(styles.badge, styles[tone], className)}>
    <span className={styles.dot} aria-hidden="true" />
    {children}
  </span>
);

export default PuddingStatusBadge;
