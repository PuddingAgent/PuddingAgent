// ── CommandPalette：输入 / 触发系统指令面板，键盘导航，Esc 关闭 ──
import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  CompressOutlined,
  DatabaseOutlined,
  InfoCircleOutlined,
  QuestionCircleOutlined,
  SafetyCertificateOutlined,
  StopOutlined,
  ThunderboltOutlined,
  UndoOutlined,
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
  group: 'System' | 'Authorization' | 'Context';
}

const COMMANDS: Command[] = [
  {
    id: 'help',
    icon: <QuestionCircleOutlined />,
    label: '指令帮助',
    shortcut: '/help',
    description: '查看可用系统指令',
    group: 'System',
  },
  {
    id: 'authorize-help',
    icon: <QuestionCircleOutlined />,
    label: '授权帮助',
    shortcut: '/authorize -help',
    description: '查看授权指令用法',
    group: 'System',
  },
  {
    id: 'status',
    icon: <InfoCircleOutlined />,
    label: '运行状态',
    shortcut: '/status',
    description: '查看 Runtime、会话、工具和安全状态',
    group: 'System',
  },
  {
    id: 'mode',
    icon: <InfoCircleOutlined />,
    label: '当前模式',
    shortcut: '/mode',
    description: '查看当前运行模式',
    group: 'System',
  },
  {
    id: 'mode-list',
    icon: <QuestionCircleOutlined />,
    label: '模式列表',
    shortcut: '/mode list',
    description: '查看全部运行模式和示例',
    group: 'System',
  },
  {
    id: 'mode-safe',
    icon: <SafetyCertificateOutlined />,
    label: '进入安全模式',
    shortcut: '/mode safe',
    description: '阻止 Agent 和 Tool 继续运行',
    group: 'System',
  },
  {
    id: 'mode-normal',
    icon: <CheckCircleOutlined />,
    label: '恢复正常模式',
    shortcut: '/mode normal',
    description: '退出安全模式，恢复正常调度',
    group: 'System',
  },
  {
    id: 'mode-yolo',
    icon: <ThunderboltOutlined />,
    label: 'YOLO 无限制模式',
    shortcut: '/yolo',
    description: '绕过所有工具权限检查（仅内存态，重启失效）',
    group: 'System',
  },
  {
    id: 'stop-session',
    icon: <StopOutlined />,
    label: '停止当前会话',
    shortcut: '/stop',
    description: '停止当前会话的 Agent 输出和工具调用',
    group: 'System',
  },
  {
    id: 'stop-all',
    icon: <StopOutlined />,
    label: '停止所有会话',
    shortcut: '/stop all',
    description: '停止所有活跃会话',
    group: 'System',
  },
  {
    id: 'authorize-shell-10m',
    icon: <ClockCircleOutlined />,
    label: '授权 Shell 10 分钟',
    shortcut: '/authorize shell 10m',
    description: '临时允许当前 Agent 使用 Shell 工具',
    group: 'Authorization',
  },
  {
    id: 'authorize-shell-once',
    icon: <CheckCircleOutlined />,
    label: '授权 Shell 一次',
    shortcut: '/authorize shell once',
    description: '只允许当前会话下一次 Shell 调用',
    group: 'Authorization',
  },
  {
    id: 'authorize-shell-session',
    icon: <SafetyCertificateOutlined />,
    label: '本会话授权 Shell',
    shortcut: '/authorize shell session',
    description: '允许当前会话内使用 Shell 工具',
    group: 'Authorization',
  },
  {
    id: 'authorize-shell-permanent',
    icon: <SafetyCertificateOutlined />,
    label: '永久授权 Shell',
    shortcut: '/authorize shell permanent',
    description: '为当前用户和 Agent 持久授权 Shell',
    group: 'Authorization',
  },
  {
    id: 'authorize-file-write-once',
    icon: <CheckCircleOutlined />,
    label: '授权写文件一次',
    shortcut: '/authorize file_write once',
    description: '只允许当前会话下一次文件写入',
    group: 'Authorization',
  },
  {
    id: 'authorize-file-write-session',
    icon: <SafetyCertificateOutlined />,
    label: '本会话授权写文件',
    shortcut: '/authorize file_write session',
    description: '允许当前会话内使用文件写入工具',
    group: 'Authorization',
  },
  {
    id: 'authorize-file-patch-once',
    icon: <CheckCircleOutlined />,
    label: '授权文件补丁一次',
    shortcut: '/authorize file_patch once',
    description: '只允许当前会话下一次文件补丁',
    group: 'Authorization',
  },
  {
    id: 'authorize-file-patch-session',
    icon: <SafetyCertificateOutlined />,
    label: '本会话授权文件补丁',
    shortcut: '/authorize file_patch session',
    description: '允许当前会话内使用文件补丁工具',
    group: 'Authorization',
  },
  {
    id: 'deny-shell',
    icon: <StopOutlined />,
    label: '拒绝 Shell 授权',
    shortcut: '/deny shell',
    description: '拒绝并清除 Shell 授权',
    group: 'Authorization',
  },
  {
    id: 'revoke-shell',
    icon: <UndoOutlined />,
    label: '撤回 Shell 授权',
    shortcut: '/revoke shell',
    description: '撤回当前 Shell 授权',
    group: 'Authorization',
  },
  {
    id: 'revoke-file-write',
    icon: <UndoOutlined />,
    label: '撤回写文件授权',
    shortcut: '/revoke file_write',
    description: '撤回当前文件写入授权',
    group: 'Authorization',
  },
  {
    id: 'revoke-file-patch',
    icon: <UndoOutlined />,
    label: '撤回文件补丁授权',
    shortcut: '/revoke file_patch',
    description: '撤回当前文件补丁授权',
    group: 'Authorization',
  },
  {
    id: 'compact',
    icon: <CompressOutlined />,
    label: '压缩上下文',
    shortcut: '/compact',
    description: '总结早期对话，释放上下文窗口',
    group: 'Context',
  },
  {
    id: 'memory',
    icon: <DatabaseOutlined />,
    label: '记忆',
    shortcut: '/memory',
    description: '管理或写入记忆（暂未实现）',
    group: 'Context',
  },
];

export function filterCommands(
  filterText: string,
  commands: Command[] = COMMANDS,
): Command[] {
  if (!filterText) return commands;
  const lower = filterText.toLowerCase();
  return commands.filter(
    (c) =>
      c.label.toLowerCase().includes(lower) ||
      c.shortcut.toLowerCase().includes(lower) ||
      c.description.toLowerCase().includes(lower) ||
      c.group.toLowerCase().includes(lower),
  );
}

interface CommandPaletteProps {
  visible: boolean;
  filterText: string;
  selectedIdx: number;
  onSelectIndex: (idx: number) => void;
  onSelect: (cmd: Command) => void;
  onClose: () => void;
}

const CommandPalette: React.FC<CommandPaletteProps> = ({
  visible,
  filterText,
  selectedIdx,
  onSelectIndex,
  onSelect,
  onClose,
}) => {
  const listRef = useRef<HTMLDivElement>(null);

  const filtered = React.useMemo(
    () => filterCommands(filterText),
    [filterText],
  );

  // 滚动选中项到可见区域
  useEffect(() => {
    if (!listRef.current) return;
    const items =
      listRef.current.querySelectorAll<HTMLDivElement>('[data-cmd-item]');
    const selected = items[selectedIdx];
    if (typeof selected?.scrollIntoView === 'function') {
      selected.scrollIntoView({ block: 'nearest' });
    }
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
        <mark
          style={{
            background: 'var(--pale-yellow-sunlight)',
            padding: '0 2px',
            borderRadius: 2,
            color: 'inherit',
          }}
        >
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
        marginBottom: 8,
        width: 'min(560px, calc(100vw - 32px))',
        background: 'color-mix(in srgb, var(--soft-white) 95%, transparent)',
        backdropFilter: 'blur(20px)',
        border:
          '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
        borderRadius: 8,
        maxHeight: 340,
        overflowY: 'auto',
        boxShadow: '0 8px 32px rgba(0,0,0,0.08)',
        zIndex: 1000,
      }}
      ref={listRef}
    >
      {filtered.length === 0 ? (
        <div style={{ padding: '16px', textAlign: 'center' }}>
          <Text type="secondary" style={{ fontSize: 12 }}>
            无匹配命令
          </Text>
        </div>
      ) : (
        filtered.map((cmd, idx) => {
          const previous = filtered[idx - 1];
          const showGroup = !previous || previous.group !== cmd.group;
          return (
            <React.Fragment key={cmd.id}>
              {showGroup && (
                <div
                  style={{
                    padding: idx === 0 ? '10px 12px 4px' : '12px 12px 4px',
                    fontSize: 11,
                    fontWeight: 600,
                    color: 'var(--muted-text)',
                    textTransform: 'uppercase',
                    letterSpacing: 0,
                  }}
                >
                  {cmd.group}
                </div>
              )}
              <div
                data-cmd-item
                onClick={() => onSelect(cmd)}
                onMouseEnter={() => onSelectIndex(idx)}
                style={{
                  display: 'grid',
                  gridTemplateColumns: '24px minmax(0, 1fr) auto',
                  alignItems: 'center',
                  gap: 10,
                  padding: '9px 12px',
                  cursor: 'pointer',
                  transition: 'background 0.15s, border-color 0.15s',
                  background:
                    idx === selectedIdx
                      ? 'color-mix(in srgb, var(--misty-blue) 50%, transparent)'
                      : 'transparent',
                  borderTop:
                    '1px solid color-mix(in srgb, var(--earth-brown) 5%, transparent)',
                }}
              >
                <span
                  style={{
                    fontSize: 16,
                    color: 'var(--accent-purple)',
                    flexShrink: 0,
                  }}
                >
                  {cmd.icon}
                </span>
                <div style={{ minWidth: 0 }}>
                  <div
                    style={{
                      fontSize: 13,
                      fontWeight: 600,
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {highlight(cmd.label)}
                  </div>
                  <Text
                    type="secondary"
                    style={{
                      display: 'block',
                      fontSize: 11,
                      lineHeight: 1.35,
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {cmd.description}
                  </Text>
                </div>
                <Text
                  code
                  style={{
                    fontSize: 11,
                    flexShrink: 0,
                    maxWidth: 190,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                  }}
                >
                  {cmd.shortcut}
                </Text>
              </div>
            </React.Fragment>
          );
        })
      )}
    </div>
  );
};

export { COMMANDS };
export default CommandPalette;
