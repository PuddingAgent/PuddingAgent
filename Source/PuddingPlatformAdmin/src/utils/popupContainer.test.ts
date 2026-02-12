import { getPuddingPopupContainer } from './popupContainer';

describe('getPuddingPopupContainer', () => {
  it('anchors popups to the trigger parent', () => {
    const parent = document.createElement('div');
    const trigger = document.createElement('div');
    parent.appendChild(trigger);

    expect(getPuddingPopupContainer(trigger)).toBe(parent);
  });

  it('falls back to document body for detached triggers', () => {
    const trigger = document.createElement('div');

    expect(getPuddingPopupContainer(trigger)).toBe(document.body);
  });
});
