import type { WorkspaceWithPermDto } from '@/services/platform/api';
import { createHomeWorkspaceSummary, getHomeGreeting } from './viewModel';

const workspace = (
  workspaceId: string,
  patch: Partial<WorkspaceWithPermDto> = {},
): WorkspaceWithPermDto => ({
  id: 1,
  workspaceId,
  slug: workspaceId,
  teamId: 'team-a',
  teamName: 'Pudding',
  name: workspaceId,
  teamAccessPolicy: 'Manage',
  companyAccessPolicy: 'None',
  isEnabled: true,
  isFrozen: false,
  memberCount: 1,
  createdAt: '2026-08-02T00:00:00Z',
  ...patch,
});

describe('home view model', () => {
  it('summarizes available, frozen and team workspace counts', () => {
    const summary = createHomeWorkspaceSummary([
      workspace('default'),
      workspace('frozen', { isFrozen: true }),
      workspace('disabled', { isEnabled: false, teamId: 'team-b' }),
    ]);

    expect(summary).toEqual({
      total: 3,
      available: 1,
      frozen: 1,
      teamCount: 2,
    });
  });

  it.each([
    [2, '夜深了'],
    [8, '早上好'],
    [12, '中午好'],
    [16, '下午好'],
    [21, '晚上好'],
  ])('returns a greeting for hour %s', (hour, expected) => {
    expect(getHomeGreeting(hour)).toBe(expected);
  });
});
