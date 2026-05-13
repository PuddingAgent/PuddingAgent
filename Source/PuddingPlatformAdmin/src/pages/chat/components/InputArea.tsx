// ── InputArea：输入区（工具栏 → 输入行 → 状态栏）────
import { DownloadOutlined, PaperClipOutlined, SendOutlined, SettingOutlined, StopOutlined } from '@ant-design/icons';
import { Button, Input, Select, Tooltip } from 'antd';
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
import VoiceInputButton from './VoiceInputButton';

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
  status: 'idle' | 'streaming';
}

const InputArea: React.FC<InputAreaProps> = ({
  inputValue, onInputChange, onKeyDown, loading, onSend, onStop, onExport,
  disabled, tLimit, tUsed, tPct, status,
}) => {
  const { styles } = useChatStyles();
  const textAreaRef = useRef<HTMLTextAreaElement>(null);
  const [paletteVisible, setPaletteVisible] = useState(false);
  const [selectedIdx, setSelectedIdx] = useState(0);

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

  return (
    <div className={styles.inputPanel} style={{ position: 'relative' }}>
      <CommandPalette
        visible={paletteVisible}
        filterText={slashFilterText}
        selectedIdx={selectedIdx % Math.max(1, filteredCommands.length)}
        onSelectIndex={setSelectedIdx}
        onSelect={handleCommandSelect}
        onClose={handleClosePalette}
      />
      {/* ── 工具栏（附件 | 思考强度 | 设置 | 导出 — 均占位）── */}
      <div className={styles.toolbar}>
        <Tooltip title="上传附件（即将开放）"><Button size="small" type="text" icon={<PaperClipOutlined />} disabled /></Tooltip>
        <Tooltip title="思考强度（即将开放）">
          <Select size="small" variant="borderless" disabled value="auto" style={{ width: 72, fontSize: 12 }}
            options={[{ value: 'auto', label: '自动' }, { value: 'low', label: '低' }, { value: 'high', label: '高' }]} />
        </Tooltip>
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
          placeholder={loading ? 'Agent 正在生成回复…' : '交给我吧。Enter 发送，Shift+Enter 换行'}
          disabled={disabled}
          autoSize={{ minRows: 1, maxRows: 5 }}
          className={styles.input}
        />
        <Button
          type={loading ? 'default' : 'primary'}
          danger={loading}
          icon={loading ? <StopOutlined /> : <SendOutlined />}
          onClick={loading ? onStop : onSend}
          disabled={loading ? false : (!inputValue.trim() || disabled)}
        >
          {loading ? '停止' : '发送'}
        </Button>
      </div>

      {/* ── 状态栏：Token 指示器 | 思考强度 | 余额 | 消耗 | ASP/LSP | 索引 | 潜意识 ── */}
      <div className={styles.statusBar}>
        <StatusBarTokenIndicator tLimit={tLimit} tUsed={tUsed} tPct={tPct} status={status} loaded={tLimit > 0} />
        <span className={styles.statusDivider}>|</span>
        <ThinkingIntensityIndicator intensity="auto" />
        <span className={styles.statusDivider}>|</span>
        <ProviderBalanceIndicator provider="DeepSeek" />
        <span className={styles.statusDivider}>|</span>
        <TokenStatsIndicator />
        <span className={styles.statusDivider}>|</span>
        <AspLspIndicator aspActive={true} lspActive={false} />
        <span className={styles.statusDivider}>|</span>
        <IndexIndicator active={false} statusText="全文索引待启动" />
        <span className={styles.statusDivider}>|</span>
        <SubconsciousLlmIndicator active={false} statusText="潜意识 LLM 待机" />
      </div>
    </div>
  );
};

export default InputArea;
