import fs from 'node:fs';
import path from 'node:path';

describe('workspace detail template entry removal', () => {
  it('does not expose workspace-scoped Agent template management in the workspace detail tabs', () => {
    const workspaceDetailSource = fs.readFileSync(path.join(__dirname, '[id]', 'index.tsx'), 'utf8');

    expect(workspaceDetailSource).not.toContain("key: 'agent-templates'");
    expect(workspaceDetailSource).not.toContain('模板管理');
    expect(workspaceDetailSource).not.toContain('AgentTemplatesTab');
  });

  it('does not expose the embedded Chat tab because admin chat owns conversation UI', () => {
    const workspaceDetailSource = fs.readFileSync(path.join(__dirname, '[id]', 'index.tsx'), 'utf8');

    expect(workspaceDetailSource).not.toContain("key: 'chat'");
    expect(workspaceDetailSource).not.toContain('ChatTab');
    expect(workspaceDetailSource).not.toContain('sendAdminChatMessage');
  });

  it('does not route studio actions to the deprecated workspace template page', () => {
    const studioSource = fs.readFileSync(path.join(__dirname, '..', 'workspace-studio', 'index.tsx'), 'utf8');

    expect(studioSource).not.toContain('/workspace-agent-template');
  });
});
