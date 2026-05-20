// ── InputArea：Agent Console（玻璃 dock → 工具栏 → 输入行 → 状态栏）────
import { DownOutlined, DownloadOutlined, PaperClipOutlined, PictureOutlined, SendOutlined, SettingOutlined, StopOutlined } from '@ant-design/icons';
import { Button, Input, Popover, Select, Tooltip } from 'antd';
import React, { useCallback, useRef, useState } from 'react';
import { useChatStyles } from '../styles';
import CommandPalette, { COMMANDS, type Command } from './CommandPalette';
import StatusBarTokenIndicator from './StatusBarTokenIndicator';
import ThinkingIntensityIndicator from './ThinkingIntensityIndicator';
import ProviderBalanceIndicator from './ProviderBalanceIndicator';
import TokenStatsIndicator from './TokenStatsIndicator';
import AspLspIndicator from './AspLspIndicator';
import IndexIndicator from './IndexIndicator';
import SubconsciousLlmIndicator from './SubconsciousLlmIndicator';
import SubAgentIndicator from './SubAgentIndicator';
import VoiceInputButton from './VoiceInputButton';

/** Agent Console 的聊天状态（映射到自然语言文案） */
export type ChatStatus = 'idle' | 'composing' | 'thinking' | 'tool_executing' | 'streaming' | 'completed' | 'error';

/** chatState → 自然语言状态文案 */
const STATUS_LABEL: Record<ChatStatus, string> = {
  idle: '就绪',
  composing: '输入中',
  thinking: '正在整理上下文…',
  tool_executing: '正在调用工具…',
  streaming: '正在生成回复…',
  completed: '已完成',
  error: '出错了，可重试',
};

interface InputAreaProps {
  inputValue: string;
  onInputChange: (v: string) => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loading: boolean;
  onSend: () => void;
  onStop: () => void;
  onExport: () => void;
  disabled: boolean;
  tLimit: number;
  tUsed: number;
  tPct: number;
  status: ChatStatus;
  sessionId?: string | null;
  // cache stats
  cacheHitTokens?: number;
  cacheMissTokens?: number;
  cacheHitRate?: number;
}

const InputArea: React.FC<InputAreaProps> = ({
  inputValue, onInputChange, onKeyDown, loading, onSend, onStop, onExport,
  disabled, tLimit, tUsed, tPct, status, sessionId,
  cacheHitTokens, cacheMissTokens, cacheHitRate,
}) => {
  const { styles } = useChatStyles();
  const textAreaRef = useRef<HTMLTextAreaElement>(null);
  const [paletteVisible, setPaletteVisible] = useState(false);
  const [selectedIdx, setSelectedIdx] = useState(0);
  /** 发送时能量波动画触发器 */
  const [sendingFlash, setSendingFlash] = useState(false);
  /** 技术指标 Popover 显隐 */
  const [showMoreStatus, setShowMoreStatus] = useState(false);

  /** 当前文本中最后一个 / 后的内容（用于过滤命令）。仅在开头或空格后触发。 */
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

    // 检测 / 触发：光标前最近一个 / 是在开头或空格后
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
    // 替换从最近 / 开始到光标位置的文本为 shortcut + 空格
    const newBefore = before.replace(/\/([^\s]*)$/, cmd.shortcut + ' ');
    const newValue = newBefore + after;
    onInputChange(newValue);
    setPaletteVisible(false);
    // 将光标移到替换文本末尾
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
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setSelectedIdx(prev => Math.min(filteredCommands.length - 1, prev + 1));
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setSelectedIdx(prev => Math.max(0, prev - 1));
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        setPaletteVisible(false);
        return;
      }
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        const cmd = filteredCommands[selectedIdx];
        if (cmd) handleCommandSelect(cmd);
        return;
      }
    }
    onKeyDown(e);
  }, [paletteVisible, filteredCommands, selectedIdx, onKeyDown, handleCommandSelect]);

  const handleClosePalette = useCallback(() => {
    setPaletteVisible(false);
  }, []);

  /** 发送带能量波反馈 */
  const handleSendWithWave = useCallback(() => {
    setSendingFlash(true);
    onSend();
    setTimeout(() => setSendingFlash(false), 450);
  }, [onSend]);

  const statusText = STATUS_LABEL[status] ?? STATUS_LABEL.idle;

  /** "更多"Popover 内容：技术指标列表 */
  const moreStatusContent = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: '4px 0', minWidth: 220 }}>
      <StatusBarTokenIndicator tLimit={tLimit} tUsed={tUsed} tPct={tPct} status={status === 'streaming' || status === 'thinking' ? 'streaming' : 'idle'} loaded={tLimit > 0}
        cacheHitTokens={cacheHitTokens} cacheMissTokens={cacheMissTokens} cacheHitRate={cacheHitRate} />
      <ThinkingIntensityIndicator intensity="auto" />
      <ProviderBalanceIndicator provider="DeepSeek" />
      <TokenStatsIndicator />
      <AspLspIndicator aspActive={true} lspActive={false} />
      <IndexIndicator active={false} statusText="全文索引待启动" />
      <SubconsciousLlmIndicator active={false} statusText="潜意识 LLM 待机" />
      <SubAgentIndicator sessionId={sessionId} />
    </div>
  );

  return (
    <div className={styles.consoleDock}>
      <div className={styles.consoleInner}>
        <CommandPalette
          visible={paletteVisible}
          filterText={slashFilterText}
          selectedIdx={selectedIdx % Math.max(1, filteredCommands.length)}
          onSelectIndex={setSelectedIdx}
          onSelect={handleCommandSelect}
          onClose={handleClosePalette}
        />
        {/* ── 工具栏：高级功能（附件 | 图片 | 思考强度 | 导出 | 设置）── */}
        <div className={styles.toolbar}>
          <Tooltip title="上传附件（即将开放）"><Button size="small" type="text" icon={<PaperClipOutlined />} disabled /></Tooltip>
          <Tooltip title="上传图片（即将开放）"><Button size="small" type="text" icon={<PictureOutlined />} disabled /></Tooltip>
          <Tooltip title="思考强度"><Select size="small" variant="borderless" disabled value="auto" style={{ width: 64, fontSize: 11 }}
            options={[{ value: 'auto', label: '自动' }, { value: 'low', label: '低' }, { value: 'high', label: '高' }]} /></Tooltip>
          <div className={styles.toolbarSpacer} />
          <Tooltip title="导出对话"><Button size="small" type="text" icon={<DownloadOutlined />} onClick={onExport} /></Tooltip>
          <Tooltip title="Agent 设置（即将开放）"><Button size="small" type="text" icon={<SettingOutlined />} disabled /></Tooltip>
        </div>

        {/* ── 输入行：语音 + 输入框 + 发送 ── */}
        <div className={styles.inputArea}>
          <VoiceInputButton disabled={disabled} />
          <Input.TextArea
            ref={textAreaRef as any}
            value={inputValue}
            onChange={handleInputChange}
            onKeyDown={handleKeyDown}
            placeholder={loading ? '正在生成回复…' : '输入你的问题或任务…'}
            disabled={disabled}
            autoSize={{ minRows: 1, maxRows: 5 }}
            className={`${styles.input} ${styles.consoleTextarea} ${sendingFlash ? styles.consoleTextareaSending : ''}`}
            data-testid="chat-input"
          />
          <Button
            type={loading ? 'default' : 'primary'}
            danger={loading}
            icon={loading ? <StopOutlined /> : <SendOutlined />}
            onClick={loading ? onStop : handleSendWithWave}
            disabled={loading ? false : (!inputValue.trim() || disabled)}
            data-testid="chat-send"
          >
            {loading ? '停止' : '发送'}
          </Button>
        </div>

        {/* ── 状态栏：简约克制，默认仅显示状态胶囊 + "更多"入口 ── */}
        <div className={styles.consoleStatusBar}>
          {/* 状态指示点 + 文案 */}
          <span className={styles.consoleStatusBadge}>
            <span className={styles.consoleStatusDot} style={{
              background: status === 'streaming' || status === 'thinking' || status === 'tool_executing' ? '#22c55e'
                : status === 'error' ? '#ef4444'
                : status === 'idle' ? '#a78bfa'
                : '#f59e0b',
            }} />
            <span>{statusText}</span>
          </span>
          <span className={styles.statusDivider}>|</span>
          {/* 更多按钮：点击弹出技术指标列表 */}
          <Popover
            content={moreStatusContent}
            trigger="click"
            open={showMoreStatus}
            onOpenChange={setShowMoreStatus}
            placement="topRight"
          >
            <span style={{ cursor: 'pointer', fontSize: 10, display: 'flex', alignItems: 'center', gap: 2, opacity: 0.5 }}>
              更多 <DownOutlined style={{ fontSize: 8 }} />
            </span>
          </Popover>
        </div>
      </div>
    </div>
  );
};

export default InputArea;
