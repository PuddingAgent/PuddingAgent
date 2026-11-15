export const RECORDING_SAMPLE_RATE = 16000;
export const MAX_RECORDING_SECONDS = 120;

export function encodeWave(chunks: Float32Array[]): Blob {
  const count = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const buffer = new ArrayBuffer(44 + count * 2);
  const view = new DataView(buffer);
  const text = (offset: number, value: string) => {
    for (let i = 0; i < value.length; i++) view.setUint8(offset + i, value.charCodeAt(i));
  };
  text(0, 'RIFF'); view.setUint32(4, 36 + count * 2, true); text(8, 'WAVE');
  text(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true);
  view.setUint16(22, 1, true); view.setUint32(24, RECORDING_SAMPLE_RATE, true);
  view.setUint32(28, RECORDING_SAMPLE_RATE * 2, true); view.setUint16(32, 2, true);
  view.setUint16(34, 16, true); text(36, 'data'); view.setUint32(40, count * 2, true);
  let offset = 44;
  for (const chunk of chunks) for (const sample of chunk) {
    const bounded = Math.min(1, Math.max(-1, sample));
    view.setInt16(offset, bounded < 0 ? bounded * 32768 : bounded * 32767, true);
    offset += 2;
  }
  return new Blob([buffer], { type: 'audio/wav' });
}

export function insertTranscript(current: string, original: string, caret: number, transcript: string): string {
  const text = transcript.trim();
  if (!text) return current;
  if (current !== original) return current + (current && !/\s$/.test(current) ? ' ' : '') + text;
  const position = Math.max(0, Math.min(caret, current.length));
  return current.slice(0, position) + text + current.slice(position);
}
