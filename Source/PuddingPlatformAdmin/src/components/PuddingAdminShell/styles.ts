import { createStyles } from 'antd-style';

/**
 * PuddingAdminShell styles
 */

export default createStyles(() => ({
  shell: {
    display: 'flex',
    flexDirection: 'column' as const,
    minHeight: '100%',
  },
  content: {
    flex: 1,
    padding: 0,
  },
}));
