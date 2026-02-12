export function getPuddingPopupContainer(triggerNode?: HTMLElement): HTMLElement {
  return triggerNode?.parentElement ?? document.body;
}
