import { FlagFilled } from '@ant-design/icons';
import { Popover } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  buildPinnedMessageQuote,
  clearPinnedMessage,
  loadPinnedMessage,
  savePinnedMessage,
  subscribePinnedMessageChange,
  type PinnedMessage,
} from '../utils/pinnedMessage';

export {
  clearPinnedMessage,
  loadPinnedMessage,
  savePinnedMessage,
  type PinnedMessage,
};

interface PinnedMessageButtonProps {
  /** 回调：引用文本插入输入框 */
  onQuote: (text: string) => void;
  /** 当钉住状态变化时通知外部（可选） */
  onStateChange?: (pinned: boolean) => void;
  className?: string;
}

const PinnedMessageButton: React.FC<PinnedMessageButtonProps> = ({
  onQuote,
  onStateChange,
  className,
}) => {
  const [pinned, setPinned] = useState<PinnedMessage | null>(loadPinnedMessage);
  const clickTimerRef = useRef<number | null>(null);

  useEffect(() => {
    return subscribePinnedMessageChange(() => setPinned(loadPinnedMessage()));
  }, []);

  useEffect(() => {
    onStateChange?.(!!pinned);
  }, [pinned, onStateChange]);

  useEffect(
    () => () => {
      if (clickTimerRef.current !== null) {
        window.clearTimeout(clickTimerRef.current);
      }
    },
    [],
  );

  const handleClick = useCallback(() => {
    if (!pinned) return;
    if (clickTimerRef.current !== null) {
      window.clearTimeout(clickTimerRef.current);
    }
    clickTimerRef.current = window.setTimeout(() => {
      onQuote(buildPinnedMessageQuote(pinned));
      clickTimerRef.current = null;
    }, 220);
  }, [pinned, onQuote]);

  const handleDoubleClick = useCallback(() => {
    if (clickTimerRef.current !== null) {
      window.clearTimeout(clickTimerRef.current);
      clickTimerRef.current = null;
    }
    clearPinnedMessage();
    setPinned(null);
  }, []);

  const previewContent = useMemo(() => {
    if (!pinned) return null;
    return (
      <div style={{ maxWidth: 320, fontSize: 13, lineHeight: '20px', color: '#333' }}>
        <div style={{ fontWeight: 600, marginBottom: 8, color: '#191919' }}>
          已钉住的消息
        </div>
        <div
          style={{
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-all',
            maxHeight: 200,
            overflow: 'auto',
          }}
        >
          {pinned.preview}
        </div>
        <div
          style={{
            marginTop: 8,
            fontSize: 11,
            color: '#b0b0b0',
            display: 'flex',
            justifyContent: 'space-between',
          }}
        >
          <span>单击引用 · 双击取消</span>
          <span>
            {new Date(pinned.pinnedAt).toLocaleString('zh-CN', {
              month: '2-digit',
              day: '2-digit',
              hour: '2-digit',
              minute: '2-digit',
            })}
          </span>
        </div>
      </div>
    );
  }, [pinned]);

  if (!pinned) return null;

  return (
    <Popover content={previewContent} trigger="hover" placement="leftTop">
      <button
        type="button"
        className={className}
        aria-label="钉住的消息"
        onClick={handleClick}
        onDoubleClick={handleDoubleClick}
        title="钉住的消息（单击引用，双击取消）"
        style={{
          width: 40,
          height: 40,
          borderRadius: 8,
          border: '1px solid color-mix(in srgb, var(--earth-brown) 14%, transparent)',
          background: 'var(--soft-white)',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          cursor: 'pointer',
          boxShadow: '0 2px 8px rgba(0,0,0,0.08)',
          transition: 'box-shadow 0.15s',
          pointerEvents: 'auto',
        }}
        onMouseEnter={(e) => {
          e.currentTarget.style.boxShadow = '0 4px 16px rgba(0,0,0,0.12)';
        }}
        onMouseLeave={(e) => {
          e.currentTarget.style.boxShadow = '0 2px 8px rgba(0,0,0,0.08)';
        }}
      >
        <FlagFilled style={{ color: '#e8962e', fontSize: 18 }} />
      </button>
    </Popover>
  );
};

export default PinnedMessageButton;
