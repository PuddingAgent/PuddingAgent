// ── UserAvatarUpload：用户头像上传组件（Upload + 裁剪预览）────────
//
// 受控组件：只负责“预览、裁剪、上传”，不读写全局 initialState。
// - 头像来源由父组件通过 avatarUrl 传入
// - 上传目标由 userId 指定：POST /api/users/{userId}/avatar
//   （multipart 字段 file，返回 { avatar }）
// - 上传成功后仅回调 onUploaded(newAvatarUrl)，是否同步顶栏由页面决定
// - antd Upload 选择本地图片（校验类型与大小）
// - Modal 内提供正方形裁剪预览（缩放 + 拖动），确认后按 1:1 输出
//
// 不引入额外裁剪库：裁剪几何为纯函数（computeCropSourceRect），
// 输出阶段用 Canvas 渲染，尺寸 OUTPUT_SIZE=512px。

import { CameraOutlined, UserOutlined } from '@ant-design/icons';
import type { UploadProps } from 'antd';
import {
  Avatar,
  Button,
  Modal,
  message,
  Slider,
  Space,
  Typography,
  Upload,
} from 'antd';
import React, { useCallback, useRef, useState } from 'react';
import { updateUserAvatar } from '@/services/platform/api';

export interface UserAvatarUploadProps {
  /** 目标用户 ID（上传地址 POST /api/users/{userId}/avatar） */
  userId: string;
  /** 受控头像 URL（由父组件提供，上传成功后由父组件更新） */
  avatarUrl?: string;
  /** 头像展示尺寸（px），默认 96 */
  size?: number;
  /** 最大文件大小（MB），默认 5 */
  maxSizeMb?: number;
  /** 上传成功回调（携带新头像 URL） */
  onUploaded?: (avatarUrl: string) => void;
  /** 禁用上传 */
  disabled?: boolean;
}

/** 裁剪预览视口边长（px） */
export const CROP_VIEWPORT = 280;
/** 输出头像边长（px） */
export const OUTPUT_SIZE = 512;
const DEFAULT_MAX_MB = 5;
const ACCEPTED_TYPES = ['image/png', 'image/jpeg', 'image/webp'];

export interface CropSourceRect {
  x: number;
  y: number;
  size: number;
}

/**
 * 根据视口、缩放与平移计算源图裁剪矩形（纯函数，便于测试）。
 * 图片按 cover 语义铺满正方形视口，再叠加 zoom 缩放与 offset 平移。
 */
export function computeCropSourceRect(opts: {
  naturalWidth: number;
  naturalHeight: number;
  viewport: number;
  zoom: number;
  offsetX: number;
  offsetY: number;
}): CropSourceRect {
  const { naturalWidth, naturalHeight, viewport, zoom, offsetX, offsetY } =
    opts;
  const baseScale = Math.max(viewport / naturalWidth, viewport / naturalHeight);
  const scale = baseScale * zoom;
  const displayW = naturalWidth * scale;
  const displayH = naturalHeight * scale;
  const maxOffsetX = Math.max(0, (displayW - viewport) / 2);
  const maxOffsetY = Math.max(0, (displayH - viewport) / 2);
  const dx = Math.min(maxOffsetX, Math.max(-maxOffsetX, offsetX));
  const dy = Math.min(maxOffsetY, Math.max(-maxOffsetY, offsetY));
  // 图片左上角在视口坐标中的位置
  const left = (viewport - displayW) / 2 + dx;
  const top = (viewport - displayH) / 2 + dy;
  // 视口左上角映射回源图坐标
  const x = -left / scale;
  const y = -top / scale;
  const size = viewport / scale;
  // 越界保护（理论上 cover + clamp 后不会发生）
  const maxSize = Math.min(naturalWidth - x, naturalHeight - y, size);
  return { x, y, size: Math.max(1, Math.floor(maxSize)) };
}

/** 校验待上传文件；返回错误信息（null 表示通过） */
export function validateAvatarFile(
  file: File,
  maxSizeMb = DEFAULT_MAX_MB,
): string | null {
  if (!file.type || !ACCEPTED_TYPES.includes(file.type.toLowerCase())) {
    return '仅支持 PNG / JPG / WebP 图片';
  }
  if (file.size > maxSizeMb * 1024 * 1024) {
    return `图片大小不能超过 ${maxSizeMb} MB`;
  }
  return null;
}

const UserAvatarUpload: React.FC<UserAvatarUploadProps> = ({
  userId,
  avatarUrl,
  size = 96,
  maxSizeMb = DEFAULT_MAX_MB,
  onUploaded,
  disabled = false,
}) => {
  const currentAvatar = avatarUrl;

  const [cropOpen, setCropOpen] = useState(false);
  const [sourceUrl, setSourceUrl] = useState<string>();
  const [naturalSize, setNaturalSize] = useState<{
    width: number;
    height: number;
  }>();
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [uploading, setUploading] = useState(false);
  const imageRef = useRef<HTMLImageElement | null>(null);
  const dragRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    baseX: number;
    baseY: number;
  } | null>(null);

  // 读取选中文件 → dataURL，并预载图片以获得自然尺寸
  const handleBeforeUpload: UploadProps['beforeUpload'] = (file) => {
    const error = validateAvatarFile(file, maxSizeMb);
    if (error) {
      message.error(error);
      return Upload.LIST_IGNORE;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = String(reader.result);
      const img = new Image();
      img.onload = () => {
        imageRef.current = img;
        setNaturalSize({ width: img.naturalWidth, height: img.naturalHeight });
        setZoom(1);
        setOffset({ x: 0, y: 0 });
        setSourceUrl(dataUrl);
        setCropOpen(true);
      };
      img.src = dataUrl;
    };
    reader.readAsDataURL(file as Blob);
    // 阻止 antd 自动上传，改由裁剪确认后统一提交
    return false;
  };

  const resetCrop = useCallback(() => {
    setCropOpen(false);
    setSourceUrl(undefined);
    setNaturalSize(undefined);
    setZoom(1);
    setOffset({ x: 0, y: 0 });
    imageRef.current = null;
  }, []);

  // ── 拖动平移（pointer 事件，容器捕获）──────────────────────────
  const clampOffset = useCallback(
    (x: number, y: number): { x: number; y: number } => {
      if (!naturalSize) return { x: 0, y: 0 };
      const baseScale = Math.max(
        CROP_VIEWPORT / naturalSize.width,
        CROP_VIEWPORT / naturalSize.height,
      );
      const scale = baseScale * zoom;
      const maxX = Math.max(0, (naturalSize.width * scale - CROP_VIEWPORT) / 2);
      const maxY = Math.max(
        0,
        (naturalSize.height * scale - CROP_VIEWPORT) / 2,
      );
      return {
        x: Math.min(maxX, Math.max(-maxX, x)),
        y: Math.min(maxY, Math.max(-maxY, y)),
      };
    },
    [naturalSize, zoom],
  );

  const handlePointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!naturalSize) return;
    e.currentTarget.setPointerCapture(e.pointerId);
    dragRef.current = {
      pointerId: e.pointerId,
      startX: e.clientX,
      startY: e.clientY,
      baseX: offset.x,
      baseY: offset.y,
    };
  };

  const handlePointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== e.pointerId) return;
    const next = clampOffset(
      drag.baseX + (e.clientX - drag.startX),
      drag.baseY + (e.clientY - drag.startY),
    );
    setOffset(next);
  };

  const handlePointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    if (dragRef.current?.pointerId === e.pointerId) {
      dragRef.current = null;
      e.currentTarget.releasePointerCapture?.(e.pointerId);
    }
  };

  // 缩放时保持视口中心对应的源图点不动
  const handleZoomChange = (next: number) => {
    if (!naturalSize || !sourceUrl) return;
    const baseScale = Math.max(
      CROP_VIEWPORT / naturalSize.width,
      CROP_VIEWPORT / naturalSize.height,
    );
    const prevScale = baseScale * zoom;
    const nextScale = baseScale * next;
    const displayW = naturalSize.width * prevScale;
    const displayH = naturalSize.height * prevScale;
    const left = (CROP_VIEWPORT - displayW) / 2 + offset.x;
    const top = (CROP_VIEWPORT - displayH) / 2 + offset.y;
    // 视口中心在源图上的坐标
    const cx = (CROP_VIEWPORT / 2 - left) / prevScale;
    const cy = (CROP_VIEWPORT / 2 - top) / prevScale;
    const newLeft = CROP_VIEWPORT / 2 - cx * nextScale;
    const newTop = CROP_VIEWPORT / 2 - cy * nextScale;
    const nextDisplayW = naturalSize.width * nextScale;
    const nextDisplayH = naturalSize.height * nextScale;
    setZoom(next);
    setOffset(
      clampOffset(
        newLeft - (CROP_VIEWPORT - nextDisplayW) / 2,
        newTop - (CROP_VIEWPORT - nextDisplayH) / 2,
      ),
    );
  };

  // ── 裁剪并上传 ────────────────────────────────────────────────
  const handleConfirm = async () => {
    const img = imageRef.current;
    if (!img || !naturalSize || !sourceUrl) return;
    setUploading(true);
    try {
      const rect = computeCropSourceRect({
        naturalWidth: naturalSize.width,
        naturalHeight: naturalSize.height,
        viewport: CROP_VIEWPORT,
        zoom,
        offsetX: offset.x,
        offsetY: offset.y,
      });
      const canvas = document.createElement('canvas');
      canvas.width = OUTPUT_SIZE;
      canvas.height = OUTPUT_SIZE;
      const ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('当前环境不支持 Canvas 裁剪');
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(
        img,
        rect.x,
        rect.y,
        rect.size,
        rect.size,
        0,
        0,
        OUTPUT_SIZE,
        OUTPUT_SIZE,
      );

      const blob = await new Promise<Blob | null>((resolve) =>
        canvas.toBlob(resolve, 'image/png'),
      );
      if (!blob) throw new Error('裁剪输出失败');
      const file = new File([blob], `avatar-${Date.now()}.png`, {
        type: 'image/png',
      });

      const formData = new FormData();
      formData.append('file', file);
      const result = await updateUserAvatar(userId, formData);
      const newAvatarUrl = result.avatar;
      if (!newAvatarUrl) {
        throw new Error('上传成功但响应缺少头像地址');
      }

      message.success('头像已更新');
      onUploaded?.(newAvatarUrl);
      resetCrop();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '头像上传失败');
    } finally {
      setUploading(false);
    }
  };

  // 裁剪预览几何（用于渲染图片位置）
  const displayGeometry = (() => {
    if (!naturalSize) return null;
    const baseScale = Math.max(
      CROP_VIEWPORT / naturalSize.width,
      CROP_VIEWPORT / naturalSize.height,
    );
    const scale = baseScale * zoom;
    const displayW = naturalSize.width * scale;
    const displayH = naturalSize.height * scale;
    return {
      displayW,
      displayH,
      left: (CROP_VIEWPORT - displayW) / 2 + offset.x,
      top: (CROP_VIEWPORT - displayH) / 2 + offset.y,
    };
  })();

  return (
    <div style={{ display: 'inline-flex', alignItems: 'center', gap: 16 }}>
      <div style={{ position: 'relative' }}>
        <Avatar
          size={size}
          src={currentAvatar}
          icon={!currentAvatar ? <UserOutlined /> : undefined}
        />
        {currentAvatar && (
          <CameraOutlined
            style={{
              position: 'absolute',
              right: -2,
              bottom: -2,
              fontSize: 14,
              color: 'var(--pudding-chat-text-muted, rgba(0,0,0,0.45))',
              background: 'var(--pudding-chat-surface, #fff)',
              borderRadius: '50%',
              padding: 2,
              boxShadow: '0 1px 2px rgba(0,0,0,0.15)',
            }}
          />
        )}
      </div>

      <Space direction="vertical" size={4}>
        <Upload
          accept="image/png,image/jpeg,image/webp"
          showUploadList={false}
          maxCount={1}
          beforeUpload={handleBeforeUpload}
          disabled={disabled || uploading}
        >
          <Button
            size="small"
            icon={<UserOutlined />}
            disabled={disabled || uploading}
          >
            {uploading ? '上传中…' : '更换头像'}
          </Button>
        </Upload>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          支持 PNG / JPG / WebP，≤ {maxSizeMb} MB
        </Typography.Text>
      </Space>

      <Modal
        title="裁剪头像"
        open={cropOpen}
        onCancel={resetCrop}
        onOk={handleConfirm}
        okText="确认上传"
        cancelText="取消"
        confirmLoading={uploading}
        okButtonProps={{ disabled: !naturalSize }}
        width={380}
        destroyOnHidden
      >
        {sourceUrl && (
          <>
            <div
              style={{
                width: CROP_VIEWPORT,
                height: CROP_VIEWPORT,
                margin: '0 auto',
                overflow: 'hidden',
                position: 'relative',
                borderRadius: 8,
                background: 'rgba(0,0,0,0.08)',
                cursor: naturalSize ? 'grab' : 'wait',
                touchAction: 'none',
                userSelect: 'none',
              }}
              onPointerDown={handlePointerDown}
              onPointerMove={handlePointerMove}
              onPointerUp={handlePointerUp}
              onPointerCancel={handlePointerUp}
            >
              {displayGeometry && (
                <img
                  src={sourceUrl}
                  alt="头像裁剪预览"
                  draggable={false}
                  style={{
                    position: 'absolute',
                    left: displayGeometry.left,
                    top: displayGeometry.top,
                    width: displayGeometry.displayW,
                    height: displayGeometry.displayH,
                    maxWidth: 'none',
                    pointerEvents: 'none',
                    userSelect: 'none',
                  }}
                />
              )}
              {/* 裁剪框遮罩：半透明外圈 + 中间透明正方形 */}
              <div
                style={{
                  position: 'absolute',
                  inset: 0,
                  boxShadow: '0 0 0 9999px rgba(0,0,0,0.45)',
                  pointerEvents: 'none',
                }}
              />
              <div
                style={{
                  position: 'absolute',
                  inset: 0,
                  border: '1px dashed rgba(255,255,255,0.85)',
                  pointerEvents: 'none',
                }}
              />
            </div>
            <div style={{ marginTop: 16 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  缩放
                </Typography.Text>
                <Slider
                  min={1}
                  max={3}
                  step={0.05}
                  value={zoom}
                  onChange={handleZoomChange}
                  style={{ flex: 1, margin: 0 }}
                />
                <Typography.Text
                  style={{ fontSize: 12, width: 40, textAlign: 'right' }}
                >
                  {zoom.toFixed(2)}x
                </Typography.Text>
              </div>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                拖动图片调整位置，结果按 1:1 正方形输出。
              </Typography.Text>
            </div>
          </>
        )}
      </Modal>
    </div>
  );
};

export default UserAvatarUpload;
