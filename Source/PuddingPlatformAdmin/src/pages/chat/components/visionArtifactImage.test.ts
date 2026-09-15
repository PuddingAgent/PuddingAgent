import { normalizeVisionArtifactFile } from './visionArtifactImage';

describe('original image preservation', () => {
  it.each(['image/jpeg', 'image/png', 'image/gif', 'image/webp', 'image/bmp'])(
    'uploads %s unchanged for server preprocessing',
    async (type) => {
      const original = new File(['original bytes'], 'original-image', { type });
      expect(await normalizeVisionArtifactFile(original)).toBe(original);
    },
  );
});
