import agentSpriteManifest from '../../../../public/assets/agent-sprites/manifest.json';

export const DEFAULT_WORKSPACE_AGENT_SPRITESHEET =
  '/admin/assets/pets/pudding/spritesheet.png';

interface WorkspaceAgentSpriteManifestEntry {
  avatarId: string;
  spritesheet: string;
}

const workspaceAgentSpriteManifest = agentSpriteManifest as Record<
  string,
  WorkspaceAgentSpriteManifestEntry
>;

export const generatedWorkspaceAgentAvatarIds = Object.freeze(
  Object.keys(workspaceAgentSpriteManifest).sort(),
);

export function resolveWorkspaceAgentSpriteSheetUrl(avatarId?: string): string {
  const normalizedAvatarId = avatarId?.trim();
  if (!normalizedAvatarId) return DEFAULT_WORKSPACE_AGENT_SPRITESHEET;
  return (
    workspaceAgentSpriteManifest[normalizedAvatarId]?.spritesheet ??
    DEFAULT_WORKSPACE_AGENT_SPRITESHEET
  );
}
