import { render, screen } from '@testing-library/react';
import React from 'react';
import PuddingAdminShell from './PuddingAdminShell';
import PuddingEntityCard from './PuddingEntityCard';
import PuddingPageHeader from './PuddingPageHeader';
import PuddingStatusBadge, {
  type PuddingStatusTone,
} from './PuddingStatusBadge';
import PuddingToolbar from './PuddingToolbar';

describe('Pudding admin primitives generated styles', () => {
  it('applies layout and typography rules instead of object-valued class names', () => {
    const { container } = render(
      <PuddingAdminShell>
        <PuddingPageHeader title="Task overview" className="custom-header" />
        <PuddingToolbar leading="Search tasks" className="custom-toolbar" />
        <PuddingEntityCard title="Scheduler" className="custom-card" />
      </PuddingAdminShell>,
    );

    expect(container.innerHTML).not.toContain('[object Object]');
    expect(getComputedStyle(container.firstElementChild!).display).toBe('flex');
    expect(
      getComputedStyle(container.querySelector('.custom-header')!).display,
    ).toBe('flex');
    expect(getComputedStyle(screen.getByRole('heading')).fontSize).toBe('22px');
    expect(
      getComputedStyle(container.querySelector('.custom-toolbar')!).display,
    ).toBe('flex');
    expect(
      getComputedStyle(container.querySelector('.custom-card')!).height,
    ).toBe('100%');
    expect(getComputedStyle(screen.getByText('Scheduler')).fontSize).toBe(
      '15px',
    );
  });

  it.each<PuddingStatusTone>([
    'success',
    'warning',
    'danger',
    'neutral',
    'accent',
  ])('renders the %s badge and a dot inheriting its tone', (tone) => {
    render(<PuddingStatusBadge tone={tone}>{tone}</PuddingStatusBadge>);
    const badge = screen.getByText(tone);
    const dot = badge.querySelector('[aria-hidden="true"]')!;
    expect(getComputedStyle(badge).display).toBe('inline-flex');
    expect(getComputedStyle(dot).width).toBe('6px');
    expect(getComputedStyle(dot).backgroundColor.toLowerCase()).toBe(
      'currentcolor',
    );
  });
});
