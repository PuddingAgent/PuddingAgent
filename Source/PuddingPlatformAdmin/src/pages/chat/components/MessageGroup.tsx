// ── MessageGroup：单轮对话（用户消息 + AI 回复）─────────────
import { CopyOutlined, DeleteOutlined, PushpinOutlined, ReloadOutlined, UserOutlined } from '@ant-design/icons';
import { Button, Space, Tooltip, Typography } from 'antd';
import dayjs from 'dayjs';
import React, { useState } from 'react';
import { useChatStyles } from '../styles';
import type { ChatTurn } from '../types';
import { assistantStatusLabel } from '../types';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import { stringToColor } from '../hooks/useChatState';
import MessageItem from './MessageItem';

const { Text } = Typography;

// ── 头像内容生成：avatarUrl > 首字母 ─────────────────────────
function getAvatarContent(agent: { avatarUrl?: string; displayName?: string; name?: string }) {
  if (agent?.avatarUrl) return { type: 'image' as const, src: agent.avatarUrl };
  const name = agent?.displayName || agent?.name || 'A';
  return { type: 'initial' as const, char: name.charAt(0).toUpperCase() };
}

function getAgentDisplayName(agent: { displayName?: string; name?: string }) {
  return agent?.displayName || agent?.name || 'Agent';
}

interface MessageGroupProps {
  turn: ChatTurn;
  turns: ChatTurn[];
  isLatest?: boolean;
  selectedAgent?: WorkspaceAgentDto;
  formatTime: (ts: number) => string;
  getStepTone: (status?: string) => 'executing' | 'success' | 'error';
  onDeleteTurn: (turnId: string) => void;
  onToggleReasoning: (turnId: string, blockId: string) => void;
  onContextMenu: (e: React.MouseEvent, turnId: string, role: 'user' | 'assistant') => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
}

const MessageGroup: React.FC<MessageGroupProps> = ({
  turn, turns, isLatest, selectedAgent, formatTime, getStepTone, onDeleteTurn, onToggleReasoning, onContextMenu,
  onRerunTurn, onPinTurn,
}) => {
  const { styles, cx } = useChatStyles();
  const { assistant, userMessage } = turn;
  const [collapsedThinkingCards, setCollapsedThinkingCards] = useState<Record<string, boolean>>({});

  const toggleThinkingCard = (cardId: string) => {
    setCollapsedThinkingCards((prev) => ({
      ...prev,
      [cardId]: !prev[cardId],
    }));
  };

  const isLegacyAssistant = assistant.renderMode === 'legacy' && assistant.reasoningBlocks.length === 0 && assistant.stepCards.length === 0;
  const showUserBubble = Boolean(userMessage.text.trim()) || assistant.renderMode === 'structured';
  const showAssistant = assistant.renderMode === 'structured' || Boolean(assistant.answerMarkdown) || assistant.isStreaming || assistant.status === 'error' || assistant.status === 'cancelled';

  const agentAvatar = selectedAgent ? getAvatarContent(selectedAgent) : { type: 'initial' as const, char: 'A' };
  const agentName = selectedAgent ? getAgentDisplayName(selectedAgent) : 'Agent';

  // ── 渲染 Agent 头像 ─────────────────────────────────────────
  const renderAgentAvatar = () => (
    <div className={styles.avatarCol}>
      {agentAvatar.type === 'image' ? (
        <img src={agentAvatar.src} alt={agentName} className={styles.avatarImg} />
      ) : (
        <div className={styles.avatarPlaceholder} style={{ background: stringToColor(agentName) }}>{agentAvatar.char}</div>
      )}
    </div>
  );

  // ── 渲染用户头像（固定图标） ─────────────────────────────
  const renderUserAvatar = () => (
    <div className={styles.avatarCol}>
      <div className={styles.avatarUserIcon}><UserOutlined /></div>
    </div>
  );

  return (
    <div className={isLatest ? styles.latestTurn : undefined}>
      {/* ── 用户消息 ─────────────────────────────────────── */}
      {showUserBubble && (
        <div className={styles.userAvatarGroup}>
          {renderUserAvatar()}
          <div className={styles.userGroupContent}>
            <div className={styles.avatarNameRow}>
              <span className={styles.avatarName}>我</span>
              <Tooltip title={dayjs(userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}>
                <Text className={styles.timeText}>{formatTime(userMessage.timestamp)}</Text>
              </Tooltip>
              {userMessage.status === 'sending' && <Text className={styles.sendingText}>发送中...</Text>}
            </div>
            <div
              className={cx(styles.bubble, styles.userBubble, userMessage.status === 'error' && styles.errorBubble)}
              onContextMenu={(e) => onContextMenu(e, turn.turnId, 'user')}
            >
              {userMessage.text}
            </div>
            <Space size={2} className={`${styles.messageActions} message-actions`}>
              <Tooltip title="复制">
                <Button size="small" type="text" icon={<CopyOutlined />} onClick={() => navigator.clipboard.writeText(userMessage.text)} />
              </Tooltip>
              <Tooltip title="重新生成">
                <Button size="small" type="text" icon={<ReloadOutlined />} onClick={() => onRerunTurn?.(turn.turnId)} />
              </Tooltip>
              <Tooltip title="固定">
                <Button size="small" type="text" icon={<PushpinOutlined />} onClick={() => onPinTurn?.(turn.turnId)} />
              </Tooltip>
              <Tooltip title="删除">
                <Button size="small" type="text" danger icon={<DeleteOutlined />} onClick={() => onDeleteTurn(turn.turnId)} />
              </Tooltip>
            </Space>
          </div>
        </div>
      )}

      {/* ── Agent 回复 ───────────────────────────────────── */}
      {showAssistant && (
        <div className={styles.avatarGroup}>
          {renderAgentAvatar()}
          <div className={styles.groupContent}>
            <div className={styles.avatarNameRow}>
              <span className={styles.avatarName}>{agentName}</span>
              <Tooltip title={dayjs(userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}>
                <Text className={styles.timeText}>{formatTime(userMessage.timestamp)}</Text>
              </Tooltip>
            </div>

            {isLegacyAssistant ? (
              <div
                className={cx(styles.bubble, styles.agentBubble, assistant.status === 'error' && styles.errorBubble)}
                onContextMenu={(e) => onContextMenu(e, turn.turnId, 'assistant')}
              >
                <MessageItem markdownText={assistant.answerMarkdown} isStreaming={assistant.isStreaming} />
              </div>
            ) : (
              <div className={styles.turnContainer}>
                <div className={styles.turnTimeline} />
                <div className={styles.turnBody}>
                  {assistant.reasoningBlocks.length > 0 && (
                    <div className={styles.reasoningPanel}>
                      <div
                        className={styles.reasoningHeader}
                        onClick={() => onToggleReasoning(turn.turnId, '_all')}
                      >
                        💭 思维链
                      </div>
                      {!assistant._reasoningCollapsed && (
                        <div className={styles.reasoningStream}>
                          {assistant.reasoningBlocks.map((block) => block.text).join('')}
                        </div>
                      )}
                    </div>
                  )}

                  {assistant.stepCards.length > 0 && (
                    <div className={styles.stepCardList}>
                      <div className={styles.stepCardLine} />
                      {assistant.stepCards.map((card, cardIdx) => {
                        const isThinkingCard = card.status === 'thinking';
                        const isCollapsed = collapsedThinkingCards[card.id] === true;
                        const tone = getStepTone(card.status);
                        return (
                          <div
                            key={card.id}
                            className={cx(
                              styles.stepCard,
                              styles.stepCardAnimated,
                              isThinkingCard && styles.thinkingStepCard,
                              tone === 'success' && styles.stepCardSuccess,
                              tone === 'error' && styles.stepCardError,
                              tone === 'executing' && styles.stepCardExecuting,
                            )}
                            style={{ animationDelay: `${cardIdx * 100}ms` }}
                          >
                            <span className={styles.stepCardDot} />
                            <div
                              className={cx(styles.stepCardTitle, isThinkingCard && styles.thinkingStepHeader)}
                              onClick={isThinkingCard ? () => toggleThinkingCard(card.id) : undefined}
                            >
                              <span className={cx(styles.stepCardStatus, card.status === 'success' && styles.stepCardCompleteIcon)}>
                                {isThinkingCard ? '💭 思考过程:' : (card.status || 'step')}
                              </span>
                              <span className={styles.stepCardTime}>{formatTime(card.timestamp)}</span>
                            </div>
                            {!isCollapsed && (
                              <div className={cx(styles.stepCardMessage, isThinkingCard && styles.thinkingStepMessage)}>{card.message}</div>
                            )}
                          </div>
                        );
                      })}
                    </div>
                  )}

                  <div
                    className={styles.assistantAnswer}
                    onContextMenu={(e) => onContextMenu(e, turn.turnId, 'assistant')}
                  >
                    <MessageItem markdownText={assistant.answerMarkdown} isStreaming={assistant.isStreaming} />
                  </div>

                  <div className={cx(styles.messageMeta, styles.assistantStatusMeta)}>
                    <span className={cx(styles.assistantStatusTag, assistant.status === 'thinking' && styles.thinkingPulse)}>{assistantStatusLabel[assistant.status]}</span>
                    {assistant.usage?.totalTokens ? (
                      <Text className={styles.sendingText}>{assistant.usage.totalTokens.toLocaleString()} tokens</Text>
                    ) : null}
                  </div>
                </div>
              </div>
            )}

            <Space size={2} className={`${styles.messageActions} message-actions`}>
              <Tooltip title="复制">
                <Button size="small" type="text" icon={<CopyOutlined />} onClick={() => navigator.clipboard.writeText(assistant.answerMarkdown)} />
              </Tooltip>
              <Tooltip title="重新生成">
                <Button size="small" type="text" icon={<ReloadOutlined />} onClick={() => onRerunTurn?.(turn.turnId)} />
              </Tooltip>
              <Tooltip title="固定">
                <Button size="small" type="text" icon={<PushpinOutlined />} onClick={() => onPinTurn?.(turn.turnId)} />
              </Tooltip>
              <Tooltip title="删除">
                <Button size="small" type="text" danger icon={<DeleteOutlined />} onClick={() => onDeleteTurn(turn.turnId)} />
              </Tooltip>
            </Space>
            {isLegacyAssistant && (
              <div className={styles.messageMeta}>
                {assistant.isStreaming && <Text className={styles.sendingText}>生成中...</Text>}
                {assistant.usage?.totalTokens ? (
                  <Text className={styles.sendingText}>{assistant.usage.totalTokens.toLocaleString()} tokens</Text>
                ) : null}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
};

export default MessageGroup;
