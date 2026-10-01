import React from 'react';
import { useStyles } from './styles';
import {
  AGENT_TEMPLATE_SECTIONS,
  type AgentTemplateSectionKey,
  type SettingsSectionMeta,
  type SectionStatus,
} from './types';

export interface AgentTemplateSettingsNavProps {
  activeSection: AgentTemplateSectionKey;
  errorSections: Set<AgentTemplateSectionKey>;
  onNavigate: (key: AgentTemplateSectionKey) => void;
  sections?: SettingsSectionMeta[];
  /**
   * 调用方附加的容器类名。
   *
   * 工作区 Agent 编辑工作台需要在同一份导航结构上换一套容器外观（背景、宽度、
   * 窄屏横向滚动），通过类名覆盖而不是改共享样式，避免影响全局模板抽屉。
   */
  className?: string;
  /** 额外高亮的分区（例如存在未保存修改），与校验错误态使用同样的标记位。 */
  markedSections?: Set<AgentTemplateSectionKey>;
  /** 分区徽标数字，例如未保存修改数量。 */
  badges?: Partial<Record<AgentTemplateSectionKey, number>>;
}

const AgentTemplateSettingsNav: React.FC<AgentTemplateSettingsNavProps> = ({
  activeSection,
  errorSections,
  onNavigate,
  sections = AGENT_TEMPLATE_SECTIONS,
  className,
  markedSections,
  badges,
}) => {
  const { styles, cx } = useStyles();

  const getStatus = (key: AgentTemplateSectionKey): SectionStatus => {
    if (errorSections.has(key)) return 'error';
    if (key === activeSection) return 'active';
    return 'normal';
  };

  return (
    <nav className={className ? `${styles.settingsNav} ${className}` : styles.settingsNav} aria-label="设置分组导航">
      {sections.map((section) => {
        const status = getStatus(section.key);
        const dirty = status !== 'error' && Boolean(markedSections?.has(section.key));
        const badge = badges?.[section.key];
        const statusLabel = status === 'error'
          ? '，存在校验错误'
          : dirty
            ? '，有未保存修改'
            : '';
        return (
          <button
            key={section.key}
            type="button"
            className={cx(
              styles.navItem,
              status === 'active' && styles.navItemActive,
              status === 'error' && styles.navItemError,
            )}
            onClick={() => onNavigate(section.key)}
            aria-current={status === 'active' ? 'page' : undefined}
            aria-label={`${section.label}${statusLabel}`}
          >
            <span
              className={cx(
                styles.navDot,
                status === 'active' && 'dot-active',
                status === 'error' && 'dot-error',
                status === 'normal' && 'dot-normal',
              )}
            />
            <span>{section.label}</span>
            {status === 'error' && (
              <span className={styles.navBadge}>{badge && badge > 1 ? badge : '!'}</span>
            )}
            {dirty && !badge && <span className={cx(styles.navDot, 'dot-dirty')} />}
          </button>
        );
      })}
    </nav>
  );
};

export default AgentTemplateSettingsNav;
