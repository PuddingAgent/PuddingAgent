import { createVoiceDraft } from './voiceDraft';

it('replaces intermediate words and commits the final sentence once', () => {
  const draft = createVoiceDraft('前后', 1);
  expect(draft.replace('前后', '你')).toBe('前你后');
  expect(draft.replace('前你后', '你好')).toBe('前你好后');
  expect(draft.replace('前你好后', '你好。')).toBe('前你好。后');
});

it('preserves edits before and after the voice range when cancelling', () => {
  const draft = createVoiceDraft('前后', 1);
  draft.replace('前后', '你好');
  expect(draft.observe('新增前你好后')).toBe(false);
  expect(draft.replace('新增前你好后补充', '你好啊')).toBe('新增前你好啊后补充');
  expect(draft.rollback('新增前你好啊后补充')).toBe('新增前后补充');
});

it('does not overwrite a manually edited voice range', () => {
  const draft = createVoiceDraft('', 0);
  draft.replace('', '你好');
  expect(draft.observe('你棒')).toBe(true);
  expect(draft.replace('你棒', '你好啊')).toBe('你棒');
  expect(draft.rollback('你棒')).toBe('你棒');
});

it('supports an empty capture and typing before the first recognition', () => {
  const draft = createVoiceDraft('原稿', 2);
  expect(draft.replace('原稿补充', '语音')).toBe('原稿补充语音');
  expect(draft.rollback('原稿补充语音')).toBe('原稿补充');
});
