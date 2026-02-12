import type { SelectProps } from 'antd';

export function getAgentTemplatePopupContainer(triggerNode: HTMLElement): HTMLElement {
  return triggerNode.parentElement ?? document.body;
}

export function getAgentTemplateSelectPopupProps<T extends SelectProps>(
  popupClassName: string,
  fieldProps?: T,
): T & Pick<SelectProps, 'classNames' | 'getPopupContainer'> {
  return {
    ...fieldProps,
    getPopupContainer: getAgentTemplatePopupContainer,
    classNames: {
      ...fieldProps?.classNames,
      popup: {
        ...fieldProps?.classNames?.popup,
        root: popupClassName,
      },
    },
  } as T & Pick<SelectProps, 'classNames' | 'getPopupContainer'>;
}
