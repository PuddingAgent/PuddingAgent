import {
  hasComponentNodeOutput,
  IMAGE_GENERATE_COMPONENT,
  IMAGE_PREVIEW_COMPONENT,
  SUB_AGENT_COMPONENT,
} from './componentUiRegistry';

describe('orchestration component UI registry', () => {
  it('lets image components own their output renderer', () => {
    expect(hasComponentNodeOutput(IMAGE_GENERATE_COMPONENT)).toBe(true);
    expect(hasComponentNodeOutput(IMAGE_PREVIEW_COMPONENT)).toBe(true);
    expect(hasComponentNodeOutput(SUB_AGENT_COMPONENT)).toBe(true);
    expect(hasComponentNodeOutput('pudding.unknown')).toBe(false);
  });
});
