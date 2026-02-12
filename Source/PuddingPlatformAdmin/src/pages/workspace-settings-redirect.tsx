import { history, useLocation, useParams } from '@umijs/max';
import React from 'react';
import { buildWorkspaceSettingsPath } from '@/utils/workspaceNavigation';

const WorkspaceSettingsRedirect: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const location = useLocation();

  React.useEffect(() => {
    if (id) {
      history.replace(buildWorkspaceSettingsPath(id) + location.search);
    } else {
      history.replace(buildWorkspaceSettingsPath('default'));
    }
  }, [id, location.search]);

  return null;
};

export default WorkspaceSettingsRedirect;
