/**
 * PuddingPageHeader — 替代 PageContainer header
 *
 * 标题和说明保持紧凑，右侧承载主操作。
 * 不使用 Pro 的大页头和模板 footer。
 */
import React from 'react';
import classNames from 'classnames';
import styles from './styles';

export interface PuddingPageHeaderProps {
  title: React.ReactNode;
  description?: React.ReactNode;
  eyebrow?: React.ReactNode;
  actions?: React.ReactNode;
  meta?: React.ReactNode;
  className?: string;
}

export const PuddingPageHeader: React.FC<PuddingPageHeaderProps> = ({
  title,
  description,
  eyebrow,
  actions,
  meta,
  className,
}) => (
  <section className={classNames(styles.header, className)}>
    <div className={styles.copy}>
      {eyebrow && <div className={styles.eyebrow}>{eyebrow}</div>}
      <h1 className={styles.title}>{title}</h1>
      {description && <p className={styles.description}>{description}</p>}
      {meta && <div className={styles.meta}>{meta}</div>}
    </div>
    {actions && <div className={styles.actions}>{actions}</div>}
  </section>
);

export default PuddingPageHeader;
