// ── InputArea：安静 Composer（单行输入 → 条件状态行）────────────
import { PlusOutlined, SendOutlined, StopOutlined } from '@ant-design/icons';
import { Button, Input, Popover, Tooltip } from 'antd';
import React, { useCallback, useRef, useState } from 'react';
import { useChatStyles } from '../styles';
import CommandPalette, { COMMANDS, type Command } from './CommandPalette';
import ComposerActionMenu from './ComposerActionMenu';
import ComposerStatusDetails, { type ComposerRuntimeSummary } from './ComposerStatusDetails';
import VoiceInputButton from './VoiceInputButton';

/** Composer 的聊天状态 */
export type ChatStatus = 'idle' | 'composing' | 'thinking' | 'tool_executing' | 'streaming' | 'completed' | 'error';

/** chatState → 自然语言状态文案 */
const STATUS_LABEL: Record<ChatStatus, string> = {
  idle: '',
  composing: '',
  thinking: '· 正在整理上下文…',
  tool_executing: '· 正在调用工具…',
  streaming: '· 正在生成回复…',
  completed: '· 已完成',
  error: '· 出错了，可重试',
};

interface InputAreaProps {
  inputValue: string;
  onInputChange: (v: string) => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loading: boolean;
  onSend: () => void;
  onStop: () => void;
  onExport: () => void;
  onOpenDevDetails?: () => void;
  disabled: boolean;
  tLimit: number;
  tUsed: number;
  tPct: number;
  status: ChatStatus;
  sessionId?: string | null;
  cacheHitTokens?: number;
  cacheMissTokens?: number;
  cacheHitRate?: number;
}

const InputArea: React.FC<InputAreaProps> = ({
  inputValue, onInputChange, onKeyDown, loading, onSend, onStop, onExport, onOpenDevDetails,
  disabled, tLimit, tUsed, tPct, status, sessionId,
  cacheHitTokens, cacheMissTokens, cacheHitRate,
}) => {
  const { styles } = useChatStyles();
  const textAreaRef = useRef<HTMLTextAreaElement>(null);
  const [paletteVisible, setPaletteVisible] = useState(false);
  const [selectedIdx, setSelectedIdx] = useState(0);
  /** `+` 动作菜单 Popover */
  const [showComposerMenu, setShowComposerMenu] = useState(false);
  /** 运行状态详情 Popover */
  const [showStatusDetails, setShowStatusDetails] = useState(false);
  /** 已完成状态短暂显示后自动消失 */
  const [completedVisible, setCompletedVisible] = useState(false);

  // 当 status 变为 completed 时，短暂显示后自动隐藏
  React.useEffect(() => {
    if (status === 'completed') {
      setCompletedVisible(true);
      const timer = setTimeout(() => setCompletedVisible(false), 2000);
      return () => clearTimeout(timer);
    }
  }, [status]);

  /** 当前文本中最后一个 / 后的内容（用于过滤命令） */
  const slashFilterText = React.useMemo(() => {
    if (!paletteVisible) return '';
    const cursorPos = textAreaRef.current?.selectionStart ?? inputValue.length;
    const beforeCursor = inputValue.slice(0, cursorPos);
    const match = beforeCursor.match(/(?:^|\s)\/([^\s]*)$/);
    return match ? match[1] : '';
  }, [inputValue, paletteVisible]);

  /** 根据过滤文本匹配的命令列表 */
  const filteredCommands = React.useMemo(() => {
    if (!slashFilterText) return COMMANDS;
    const lower = slashFilterText.toLowerCase();
    return COMMANDS.filter(
      (c) =>
        c.label.toLowerCase().includes(lower) ||
        c.shortcut.toLowerCase().includes(lower) ||
        c.description.toLowerCase().includes(lower),
    );
  }, [slashFilterText]);

  const handleInputChange = useCallback((e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const v = e.target.value;
    onInputChange(v);

    const pos = e.target.selectionStart ?? v.length;
    const before = v.slice(0, pos);
    const slashMatch = before.match(/(?:^|\s)\/([^\s]*)$/);
    setPaletteVisible(!!slashMatch);
    if (slashMatch) {
      setSelectedIdx(0);
    }
  }, [onInputChange]);

  const handleCommandSelect = useCallback((cmd: Command) => {
    const pos = textAreaRef.current?.selectionStart ?? inputValue.length;
    const before = inputValue.slice(0, pos);
    const after = inputValue.slice(pos);
    const newBefore = before.replace(/\/([^\s]*)$/, cmd.shortcut + ' ');
    const newValue = newBefore + after;
    onInputChange(newValue);
    setPaletteVisible(false);
    requestAnimationFrame(() => {
      if (textAreaRef.current) {
        const newPos = newBefore.length;
        textAreaRef.current.selectionStart = newPos;
        textAreaRef.current.selectionEnd = newPos;
        textAreaRef.current.focus();
      }
    });
  }, [inputValue, onInputChange]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (paletteVisible) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setSelectedIdx(prev => Math.min(filteredCommands.length - 1, prev + 1)); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setSelectedIdx(prev => Math.max(0, prev - 1)); return; }
      if (e.key === 'Escape') { e.preventDefault(); setPaletteVisible(false); return; }
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        const cmd = filteredCommands[selectedIdx];
        if (cmd) handleCommandSelect(cmd);
        return;
      }
    }
    onKeyDown(e);
  }, [paletteVisible, filteredCommands, selectedIdx, onKeyDown, handleCommandSelect]);

  const handleClosePalette = useCallback(() => { setPaletteVisible(false); }, []);

  /** 是否显示状态行 */
  const shouldShowStatus =
    status === 'thinking' || status === 'tool_executing' || status === 'streaming' || status === 'error' || completedVisible;

  const displayStatusText = completedVisible ? STATUS_LABEL.completed : (STATUS_LABEL[status] ?? '');

  /** 组装运行摘要视图模型 */
  const runtimeSummary: ComposerRuntimeSummary = React.useMemo(() => ({
    status,
    statusLabel: displayStatusText,
    token: tLimit > 0 ? { used: tUsed, limit: tLimit, percentage: tPct } : undefined,
    cacheHitRate,
    contextService: 'available',
    index: 'disabled',
    backgroundMemory: 'idle',
    subAgentsRunning: 0,
    modelService: 'available',
  }), [status, displayStatusText, tLimit, tUsed, tPct, cacheHitRate]);

  return (
    <div className={styles.composerSurface}>
      <CommandPalette
        visible={paletteVisible}
        filterText={slashFilterText}
        selectedIdx={selectedIdx % Math.max(1, filteredCommands.length)}
        onSelectIndex={setSelectedIdx}
        onSelect={handleCommandSelect}
        onClose={handleClosePalette}
      />

      {/* ── 条件状态行：仅 thinking / tool_executing / streaming / error / completed 短暂显示 ── */}
      {shouldShowStatus && (
        <Popover
          content={<ComposerStatusDetails summary={runtimeSummary} onOpenDevDetails={onOpenDevDetails} />}
          trigger="click"
          open={showStatusDetails}
          onOpenChange={setShowStatusDetails}
          placement="topLeft"
        >
          <div className={styles.composerStatusPill} role="button" tabIndex={0} aria-label="查看运行状态详情">
            <span className={styles.composerStatusDot} style={{
              background: status === 'error' ? 'var(--pudding-warning, #c4944c)' : '#6f8f72',
            }} />
            <span>{displayStatusText}</span>
          </div>
        </Popover>
      )}

      {/* ── Composer 主行：+ 菜单 / 语音 / 输入框 / 发送 ── */}
      <div className={styles.composerRow}>
        <Popover
          content={<ComposerActionMenu onExport={onExport} onClose={() => setShowComposerMenu(false)} />}
          trigger="click"
          open={showComposerMenu}
          onOpenChange={setShowComposerMenu}
          placement="topLeft"
        >
          <Button
            type="text"
            icon={<PlusOutlined />}
            className={styles.composerIconButton}
            aria-label="打开输入动作菜单"
            data-testid="composer-menu"
          />
        </Popover>

        <VoiceInputButton disabled={disabled} />

        <Input.TextArea
          ref={textAreaRef as any}
          value={inputValue}
          onChange={handleInputChange}
          onKeyDown={handleKeyDown}
          placeholder={loading ? '正在生成回复…' : '输入你的问题或任务…'}
          disabled={disabled}
          autoSize={{ minRows: 1, maxRows: 5 }}
          className={styles.composerTextarea}
          data-testid="chat-input"
        />

        <Tooltip title={loading ? '停止生成' : '发送'}>
          <Button
            type={loading ? 'default' : 'primary'}
            icon={loading ? <StopOutlined /> : <SendOutlined />}
            onClick={loading ? onStop : onSend}
            disabled={loading ? false : (!inputValue.trim() || disabled)}
            className={styles.composerIconButton}
            data-testid="chat-send"
          />
        </Tooltip>
      </div>
    </div>
  );
};

export default InputArea;
