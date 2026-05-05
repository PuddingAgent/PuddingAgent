// ── CommandPalette：输入 / 触发命令面板，键盘导航，Esc 关闭 ──
import {
  ApiOutlined,
  BarChartOutlined,
  FileOutlined,
  RobotOutlined,
  SearchOutlined,
  SettingOutlined,
  StarOutlined,
} from '@ant-design/icons';
import { Typography } from 'antd';
import React, { useEffect, useRef } from 'react';

const { Text } = Typography;

export interface Command {
  id: string;
  icon: React.ReactNode;
  label: string;
  shortcut: string;
  description: string;
}

const COMMANDS: Command[] = [
  { id: 'analyze-log', icon: <SearchOutlined />, label: '分析日志', shortcut: '/log', description: '分析指定日志文件' },
  { id: 'generate-report', icon: <BarChartOutlined />, label: '生成报告', shortcut: '/report', description: '基于数据生成报告' },
  { id: 'call-api', icon: <ApiOutlined />, label: '调用API', shortcut: '/api', description: '调用外部API接口' },
  { id: 'switch-agent', icon: <RobotOutlined />, label: '切换Agent', shortcut: '/agent', description: '切换到其他Agent' },
  { id: 'memory', icon: <StarOutlined />, label: '记忆', shortcut: '/memory', description: '搜索或添加记忆' },
  { id: 'file', icon: <FileOutlined />, label: '文件', shortcut: '/file', description: '浏览工作区文件' },
  { id: 'settings', icon: <SettingOutlined />, label: '设置', shortcut: '/settings', description: '修改当前会话设置' },
];

interface CommandPaletteProps {
  visible: boolean;
  filterText: string;
  selectedIdx: number;
  onSelectIndex: (idx: number) => void;
  onSelect: (cmd: Command) => void;
  onClose: () => void;
}

const CommandPalette: React.FC<CommandPaletteProps> = ({ visible, filterText, selectedIdx, onSelectIndex, onSelect, onClose }) => {
  const listRef = useRef<HTMLDivElement>(null);

  const filtered = React.useMemo(() => {
    if (!filterText) return COMMANDS;
    const lower = filterText.toLowerCase();
    return COMMANDS.filter(
      (c) =>
        c.label.toLowerCase().includes(lower) ||
        c.shortcut.toLowerCase().includes(lower) ||
        c.description.toLowerCase().includes(lower),
    );
  }, [filterText]);

  // 滚动选中项到可见区域
  useEffect(() => {
    if (!listRef.current) return;
    const items = listRef.current.querySelectorAll<HTMLDivElement>('[data-cmd-item]');
    items[selectedIdx]?.scrollIntoView({ block: 'nearest' });
  }, [selectedIdx]);

  if (!visible) return null;

  /** 高亮匹配文本 */
  const highlight = (text: string): React.ReactNode => {
    if (!filterText) return text;
    const idx = text.toLowerCase().indexOf(filterText.toLowerCase());
    if (idx === -1) return text;
    return (
      <>
        {text.slice(0, idx)}
        <mark style={{ background: 'var(--pale-yellow-sunlight)', padding: '0 2px', borderRadius: 2, color: 'inherit' }}>
          {text.slice(idx, idx + filterText.length)}
        </mark>
        {text.slice(idx + filterText.length)}
      </>
    );
  };

  return (
    <div
      style={{
        position: 'absolute',
        bottom: '100%',
        left: 0,
        right: 0,
        marginBottom: 8,
        background: 'color-mix(in srgb, var(--soft-white) 95%, transparent)',
        backdropFilter: 'blur(20px)',
        border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
        borderRadius: 8,
        maxHeight: 360,
        overflowY: 'auto',
        boxShadow: '0 8px 32px rgba(0,0,0,0.08)',
        zIndex: 1000,
      }}
      ref={listRef}
    >
      {filtered.length === 0 ? (
        <div style={{ padding: '16px', textAlign: 'center' }}>
          <Text type="secondary" style={{ fontSize: 12 }}>无匹配命令</Text>
        </div>
      ) : (
        filtered.map((cmd, idx) => (
          <div
            key={cmd.id}
            data-cmd-item
            onClick={() => onSelect(cmd)}
            onMouseEnter={() => onSelectIndex(idx)}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              padding: '10px 12px',
              cursor: 'pointer',
              transition: 'background 0.15s',
              background: idx === selectedIdx ? 'color-mix(in srgb, var(--misty-blue) 50%, transparent)' : 'transparent',
              borderBottom: '1px solid color-mix(in srgb, var(--earth-brown) 5%, transparent)',
            }}
          >
            <span style={{ fontSize: 16, color: 'var(--accent-purple)', flexShrink: 0 }}>{cmd.icon}</span>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 13, fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {highlight(cmd.label)}
              </div>
              <Text type="secondary" style={{ fontSize: 11 }}>{cmd.description}</Text>
            </div>
            <Text code style={{ fontSize: 11, flexShrink: 0 }}>{cmd.shortcut}</Text>
          </div>
        ))
      )}
    </div>
  );
};

export { COMMANDS };
export default CommandPalette;
