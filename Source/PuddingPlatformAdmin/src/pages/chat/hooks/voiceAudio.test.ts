import { encodeWave, insertTranscript } from './voiceAudio';

describe('voice audio and draft boundaries', () => {
  it('encodes real mono PCM WAV rather than renaming WebM', async () => {
    const blob = encodeWave([new Float32Array([-1, 0, 1])]);
    const bytes = await new Promise<ArrayBuffer>((resolve) => {
      const reader = new FileReader(); reader.onload = () => resolve(reader.result as ArrayBuffer); reader.readAsArrayBuffer(blob);
    });
    const view = new DataView(bytes);
    expect(blob.type).toBe('audio/wav');
    expect(view.getUint32(24, true)).toBe(16000);
    expect(view.getUint16(22, true)).toBe(1);
    expect(view.getInt16(44, true)).toBe(-32768);
    expect(view.getInt16(48, true)).toBe(32767);
  });
  it('inserts at the captured cursor and preserves subsequent typing', () => {
    expect(insertTranscript('ab', 'ab', 1, '语音')).toBe('a语音b');
    expect(insertTranscript('ab typed later', 'ab', 1, '语音')).toBe('ab typed later 语音');
    expect(insertTranscript('draft', 'draft', 1, '')).toBe('draft');
  });
});
