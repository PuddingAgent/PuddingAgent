import React from 'react';
import { useStyles } from './styles';
import {
  AGENT_TEMPLATE_SECTIONS,
  type AgentTemplateSectionKey,
  type SectionStatus,
} from './types';

export interface AgentTemplateSettingsNavProps {
  activeSection: AgentTemplateSectionKey;
  errorSections: Set<AgentTemplateSectionKey>;
  onNavigate: (key: AgentTemplateSectionKey) => void;
}

const AgentTemplateSettingsNav: React.FC<AgentTemplateSettingsNavProps> = ({
  activeSection,
  errorSections,
  onNavigate,
}) => {
  const { styles, cx } = useStyles();

  const getStatus = (key: AgentTemplateSectionKey): SectionStatus => {
    if (errorSections.has(key)) return 'error';
    if (key === activeSection) return 'active';
    return 'normal';
  };

  return (
    <nav className={styles.settingsNav} aria-label="设置分组导航">
      {AGENT_TEMPLATE_SECTIONS.map((section) => {
        const status = getStatus(section.key);
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
              <span className={styles.navBadge}>!</span>
            )}
          </button>
        );
      })}
    </nav>
  );
};

export default AgentTemplateSettingsNav;
