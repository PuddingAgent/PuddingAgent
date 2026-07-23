const PROVIDER_SAFE_IMAGE_TYPES = new Set([
  'image/jpeg',
  'image/jpg',
  'image/png',
  'image/webp',
]);

const replaceExtension = (fileName: string, extension: string): string => {
  const baseName = fileName.replace(/\.[^./\\]+$/, '') || 'image';
  return `${baseName}${extension}`;
};

/**
 * Convert browser-decodable formats such as BMP/GIF/AVIF to PNG before upload.
 * The server intentionally stores only formats accepted by vision providers.
 */
export async function normalizeVisionArtifactFile(file: File): Promise<File> {
  const mimeType = file.type.trim().toLowerCase();
  if (PROVIDER_SAFE_IMAGE_TYPES.has(mimeType)) return file;
  if (!mimeType.startsWith('image/')) {
    throw new Error(`不支持的图片格式：${file.type || file.name}`);
  }

  const objectUrl = URL.createObjectURL(file);
  try {
    const image = await new Promise<HTMLImageElement>((resolve, reject) => {
      const candidate = new Image();
      candidate.onload = () => resolve(candidate);
      candidate.onerror = () =>
        reject(new Error(`浏览器无法解析图片：${file.name}`));
      candidate.src = objectUrl;
    });
    if (image.naturalWidth <= 0 || image.naturalHeight <= 0) {
      throw new Error(`图片尺寸无效：${file.name}`);
    }

    const canvas = document.createElement('canvas');
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const context = canvas.getContext('2d');
    if (!context) throw new Error('浏览器无法创建图片转换画布');
    context.drawImage(image, 0, 0);

    const png = await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob((blob) => {
        if (blob) resolve(blob);
        else reject(new Error(`图片转换失败：${file.name}`));
      }, 'image/png');
    });

    return new File([png], replaceExtension(file.name, '.png'), {
      type: 'image/png',
      lastModified: file.lastModified,
    });
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}
