import {
  getAgentTemplatePopupContainer,
  getAgentTemplateSelectPopupProps,
} from '../selectPopup';

describe('agent template select popup', () => {
  it('keeps avatar dropdown popups anchored inside the form field container', () => {
    const parent = document.createElement('div');
    const trigger = document.createElement('div');
    parent.appendChild(trigger);

    expect(getAgentTemplatePopupContainer(trigger)).toBe(parent);
  });

  it('falls back to document body when the trigger is detached', () => {
    const trigger = document.createElement('div');

    expect(getAgentTemplatePopupContainer(trigger)).toBe(document.body);
  });

  it('applies the shared popup container and positioning class', () => {
    expect(getAgentTemplateSelectPopupProps('popup-class', { allowClear: true })).toEqual(
      expect.objectContaining({
        allowClear: true,
        getPopupContainer: getAgentTemplatePopupContainer,
        classNames: {
          popup: {
            root: 'popup-class',
          },
        },
      }),
    );
  });
});
