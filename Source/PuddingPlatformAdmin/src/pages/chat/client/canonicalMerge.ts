/**
 * Canonical merge：Chat 客户端会话快照的单调合并器。
 * 设计依据：Docs/Features/Chat前端架构设计方案-2026-09-22.md §4（状态覆盖规则）、
 * §12 落地顺序第 1 项，实施约束见任务书 §2.(1)。
 *
 * 定位：把「看起来一样」换成「可判定的等价」。服务器权威 conversation 快照是唯一业务真值；
 * agent poll / SSE / 历史 bootstrap 只能提交候选快照，必须经过本文件的单调合并才能进入 store。
 * 本文件是纯逻辑：零 React、零 IO、不 mutate 入参（返回新对象或原引用）。
 *
 * 三条已确证事实（任务书 §1.1，父级实测；不要再假设别的）：
 * 1. 后端 conversation DTO **没有** message version / content revision 字段 ⇒ 只能用代位指纹
 *    （规范化正文 hash + 字符长度 + status + 过程块指纹），hash 不得命名为 version。
 * 2. 投影构造点（AgentConversationProjectionService.cs:683-702）把 status 硬编码为字面量
 *    "succeeded"、ProcessItems 恒传空数组 ⇒「pending < streaming < succeeded」的状态序合并
 *    在这条链路上是死规则，本文件**不实现状态序**。
 * 3. 服务端数据完整（四源对拍 2355 字符一致）⇒ 客户端只需要「不丢证据、不静默覆盖」。
 *
 * 合并身份键（message identity）：
 * - 首选 `messageId`（服务端权威、跨快照稳定），指纹不同即视为冲突。
 * - DTO 缺 id 时退化为 `${role}::${createdAt}::${turnId|runId|sourceId}`（这些字段同样由服务端
 *   给出，跨快照稳定）。失败面 = 服务端连 createdAt / turnId / runId / sourceId 都缺 ⇒ 身份为
 *   null，此时该消息按「只追加 + 按内容指纹去重」处理：既不与任何既有记录配对，也永远不会被
 *   候选覆盖（任务书 §3.5「证据不足不得覆盖已有完整记录」）。
 *
 * 等价性（任务书 §2.(1) 末条）：scope + message identity + 内容/状态/过程指纹 + cursor 全相等
 * 才算等价；**禁止**用 messages.length 当等价判据。条数只在身份映射比较中作为派生结果出现。
 *
 * 诊断（diagnostics）：只在本次合并**真的改变了存量记录**时产生。这条规则保证「同一候选快照
 * 合并两次」严格幂等（任务书 §3.6）：第二遍必然走 no-op 分支，不产生重复诊断。
 */

import type {
  AgentConversationView,
  ConversationMessageView,
  ProcessSummaryItem,
} from './types';

/** 文本归一化：仅做行尾归一（CRLF/CR → LF）与首尾空白裁剪，不做任何语义改写。 */
export const normalizeCanonicalText = (value: unknown): string =>
  (typeof value === 'string' ? value : '')
    .replace(/\r\n?/g, '\n')
    .trim();

/**
 * 代位指纹用的非加密 hash：FNV-1a 32bit（UTF-16 code unit），固定 8 位小写 hex。
 * 只用于「客户端内两条记录是否内容相同」的可判定比较，**不是**服务端版本号。
 */
export const hashCanonicalText = (value: string): string => {
  let hash = 0x811c9dc5;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16).padStart(8, '0');
};

const asText = (value: unknown): string =>
  typeof value === 'string' ? value : '';

const processItemSignature = (
  item: ProcessSummaryItem | null | undefined,
): string => {
  if (!item) return '';
  return [
    asText(item.id),
    asText(item.kind),
    asText(item.status),
    String(asText(item.text).length),
    String(item.sequence ?? ''),
  ].join(':');
};

const structureSignature = (message: ConversationMessageView): string => {
  const outcome = message.turnOutcome ?? null;
  const approval = message.approvalCard ?? null;
  const plan = message.planCard ?? null;
  return [
    asText(message.role),
    asText(message.createdAt),
    asText(message.turnId),
    asText(message.runId),
    asText(message.sourceId),
    asText(outcome?.status),
    approval
      ? `${asText(approval.approvalId)}:${asText(approval.status)}`
      : '',
    plan
      ? `${asText(plan.planId)}:${asText(plan.status)}:${plan.steps?.length ?? 0}`
      : '',
    Array.isArray(message.contentParts)
      ? String(message.contentParts.length)
      : '',
  ].join('|');
};

export interface MessageFingerprint {
  /** 规范化正文 hash（代位指纹，非服务端版本）。 */
  contentHash: string;
  /** 规范化正文字符数（截断前）。 */
  contentLength: number;
  status: string;
  /** 过程块数量：完整性单调的判据之一（不得减少）。 */
  processItemCount: number;
  /** 过程块身份/类型/状态/文本长度指纹。 */
  processHash: string;
  /** 正文与过程之外的已知字段结构指纹（审批卡/计划卡/turn outcome/内容部件）。 */
  structureHash: string;
}

export const buildMessageFingerprint = (
  message: ConversationMessageView | null | undefined,
): MessageFingerprint => {
  const normalized = normalizeCanonicalText(message?.content);
  const processItems = message?.processItems ?? [];
  return {
    contentHash: hashCanonicalText(normalized),
    contentLength: normalized.length,
    status: asText(message?.status),
    processItemCount: processItems.length,
    processHash: hashCanonicalText(
      processItems.map(processItemSignature).join('\n'),
    ),
    structureHash: hashCanonicalText(structureSignature(message as ConversationMessageView)),
  };
};

export const messageFingerprintsEqual = (
  a: MessageFingerprint,
  b: MessageFingerprint,
): boolean =>
  a.contentHash === b.contentHash &&
  a.contentLength === b.contentLength &&
  a.status === b.status &&
  a.processItemCount === b.processItemCount &&
  a.processHash === b.processHash &&
  a.structureHash === b.structureHash;

const fingerprintKey = (fingerprint: MessageFingerprint): string =>
  [
    fingerprint.contentHash,
    fingerprint.contentLength,
    fingerprint.status,
    fingerprint.processItemCount,
    fingerprint.processHash,
    fingerprint.structureHash,
  ].join('|');

/**
 * 完整性比较：先过程块数、后正文字符数。返回值 >0 表示 a 更完整，0 表示完全平局。
 * 只做「不得减少」的单调判定，不引入状态序（见文件头第 2 条事实）。
 */
export const compareMessageCompleteness = (
  a: MessageFingerprint,
  b: MessageFingerprint,
): number => {
  if (a.processItemCount !== b.processItemCount) {
    return a.processItemCount - b.processItemCount;
  }
  return a.contentLength - b.contentLength;
};

export type MessageIdentitySource = 'message-id' | 'derived' | 'none';

export interface MessageIdentity {
  key: string | null;
  source: MessageIdentitySource;
}

/**
 * 合并身份键；`key === null` 表示身份证据不足（只追加、不覆盖）。
 * 派生身份只用服务端给出的稳定字段（role / createdAt / turnId|runId|sourceId）；
 * **不使用数组下标**——下标随快照增删漂移，是伪身份。
 */
export const resolveMessageIdentity = (
  message: ConversationMessageView | null | undefined,
): MessageIdentity => {
  const messageId = asText(message?.messageId).trim();
  if (messageId) return { key: `id:${messageId}`, source: 'message-id' };
  const createdAt = asText(message?.createdAt).trim();
  const runKey = (
    asText(message?.turnId) ||
    asText(message?.runId) ||
    asText(message?.sourceId)
  ).trim();
  if (createdAt || runKey) {
    return {
      key: `derived:${asText(message?.role)}::${createdAt}::${runKey}`,
      source: 'derived',
    };
  }
  // 不使用下标参与身份：位置不是稳定身份。
  return { key: null, source: 'none' };
};

const indexMessagesByKey = (
  messages: ConversationMessageView[],
): Map<string, ConversationMessageView> => {
  const map = new Map<string, ConversationMessageView>();
  messages.forEach((message) => {
    const identity = resolveMessageIdentity(message);
    if (!identity.key || map.has(identity.key)) return;
    map.set(identity.key, message);
  });
  return map;
};

const indexAnonymousFingerprints = (
  messages: ConversationMessageView[],
): Set<string> => {
  const fingerprints = new Set<string>();
  messages.forEach((message) => {
    const identity = resolveMessageIdentity(message);
    if (identity.key) return;
    fingerprints.add(fingerprintKey(buildMessageFingerprint(message)));
  });
  return fingerprints;
};

export interface CanonicalMergeScope {
  workspaceId: string;
  ownerUserId: string;
  agentId: string;
  mainSessionId: string;
}

export const resolveConversationScope = (
  conversation: AgentConversationView,
): CanonicalMergeScope => ({
  workspaceId: asText(conversation.workspaceId),
  ownerUserId: asText(conversation.ownerUserId),
  agentId: asText(conversation.agentId),
  mainSessionId: asText(conversation.mainSessionId),
});

const isSameAgentScope = (
  a: AgentConversationView,
  b: AgentConversationView,
): boolean =>
  asText(a.workspaceId) === asText(b.workspaceId) &&
  asText(a.ownerUserId) === asText(b.ownerUserId) &&
  asText(a.agentId) === asText(b.agentId);

export type CanonicalMergeDiagnosticCode =
  | 'scope-mismatch'
  | 'session-rotated'
  | 'content-conflict-kept-longer'
  | 'fingerprint-conflict-adopted-latest'
  | 'content-shrink-on-newer-cursor'
  | 'content-rollback-rejected'
  | 'process-items-rollback-rejected';

export interface CanonicalMergeDiagnostic {
  code: CanonicalMergeDiagnosticCode;
  /** 合并身份键（`id:<messageId>` / `derived:...` / null）。 */
  messageId: string | null;
  currentCursor: number;
  candidateCursor: number;
  detail: string;
  kept: MessageFingerprint | null;
  discarded: MessageFingerprint | null;
}

export interface CanonicalMergeResult {
  conversation: AgentConversationView;
  /** false = 与存量可判定等价（no-op），调用方必须跳过 cache 写入与 React setState。 */
  changed: boolean;
  diagnostics: CanonicalMergeDiagnostic[];
}

/**
 * 可判定等价（任务书 §2.(1)）：scope + message identity + 内容/状态/过程指纹 + cursor 全相等。
 * `messages.length` 不是判据：身份映射相同 + 每条指纹相同才是等价。
 */
export const isConversationEquivalent = (
  current: AgentConversationView | null | undefined,
  candidate: AgentConversationView,
): boolean => {
  if (!current) return false;
  if (!isSameAgentScope(current, candidate)) return false;
  if (asText(current.mainSessionId) !== asText(candidate.mainSessionId)) {
    return false;
  }
  if ((current.eventCursor ?? 0) !== (candidate.eventCursor ?? 0)) return false;
  if (
    (current.activeRun?.runId ?? null) !== (candidate.activeRun?.runId ?? null)
  ) {
    return false;
  }
  const currentIndex = indexMessagesByKey(current.messages ?? []);
  const candidateIndex = indexMessagesByKey(candidate.messages ?? []);
  if (currentIndex.size !== candidateIndex.size) return false;
  for (const [key, message] of currentIndex) {
    const other = candidateIndex.get(key);
    if (!other) return false;
    if (
      !messageFingerprintsEqual(
        buildMessageFingerprint(message),
        buildMessageFingerprint(other),
      )
    ) {
      return false;
    }
  }
  const currentAnonymous = indexAnonymousFingerprints(current.messages ?? []);
  const candidateAnonymous = indexAnonymousFingerprints(
    candidate.messages ?? [],
  );
  if (currentAnonymous.size !== candidateAnonymous.size) return false;
  for (const fingerprint of currentAnonymous) {
    if (!candidateAnonymous.has(fingerprint)) return false;
  }
  return true;
};

export type ProjectionEvidenceCode =
  | 'active-run-in-flight'
  | 'conversation-empty-with-cursor'
  | 'message-body-not-materialized'
  | 'message-status-not-terminal'
  | 'user-message-awaiting-reply';

export interface ProjectionEvidence {
  code: ProjectionEvidenceCode;
  messageId: string | null;
  detail: string;
}

export const TERMINAL_MESSAGE_STATUSES: ReadonlySet<string> = new Set([
  'succeeded',
  'failed',
  'cancelled',
]);

/**
 * 环境中**未消费**的「待物化 / 待追平」证据（设计 §4/§12 切片 2）。
 *
 * 只扫**末条**消息：历史消息的完整性由游标推进时的合并覆盖；若扫全表，一条历史遗留的
 * `streaming` 行会让轮询永久停留在 1200ms 档并持续全量回拉（成本失控）。
 *
 * 残余风险：末条是 user 时的 `user-message-awaiting-reply` 依然无法在本地证伪「agent 不会
 * 再回复」；该启发式与旧实现一致（旧实现也把它当作未追平），只是不再是唯一判据。
 */
export const detectUnconsumedProjectionEvidence = (
  conversation: AgentConversationView | null | undefined,
): ProjectionEvidence[] => {
  const evidence: ProjectionEvidence[] = [];
  if (!conversation) return evidence;
  const messages = conversation.messages ?? [];
  const cursor = conversation.eventCursor ?? 0;
  if (conversation.activeRun) {
    evidence.push({
      code: 'active-run-in-flight',
      messageId: asText(conversation.activeRun.runId) || null,
      detail: 'authoritative snapshot 仍带 activeRun：终态尚未落到读模型。',
    });
  }
  if (messages.length === 0) {
    if (cursor > 0) {
      evidence.push({
        code: 'conversation-empty-with-cursor',
        messageId: null,
        detail: `cursor=${cursor} 但没有任何 message：读模型尚未物化。`,
      });
    }
    return evidence;
  }
  const last = messages[messages.length - 1];
  const identity = resolveMessageIdentity(last);
  const bodyLength = normalizeCanonicalText(last.content).length;
  if (asText(last.role) === 'user') {
    evidence.push({
      code: 'user-message-awaiting-reply',
      messageId: identity.key,
      detail: '末条是 user：不能凭「cursor 相同」判定读模型已追平。',
    });
  }
  if (asText(last.role) === 'agent' && bodyLength === 0) {
    evidence.push({
      code: 'message-body-not-materialized',
      messageId: identity.key,
      detail:
        '末条 agent 行正文为空：终态事件已到达但正文尚未物化，禁止短路请求。',
    });
  }
  if (
    asText(last.role) === 'agent' &&
    !TERMINAL_MESSAGE_STATUSES.has(asText(last.status))
  ) {
    evidence.push({
      code: 'message-status-not-terminal',
      messageId: identity.key,
      detail: `末条 agent 行 status=${asText(last.status)} 非终态。`,
    });
  }
  return evidence;
};

const fillUndefinedFields = (
  base: ConversationMessageView,
  donor: ConversationMessageView,
): ConversationMessageView => {
  let patched: ConversationMessageView | null = null;
  const baseRecord = base as unknown as Record<string, unknown>;
  const donorRecord = donor as unknown as Record<string, unknown>;
  for (const key of Object.keys(donorRecord)) {
    const donorValue = donorRecord[key];
    if (donorValue === undefined) continue;
    if (baseRecord[key] !== undefined) continue;
    if (!patched) patched = { ...base };
    (patched as unknown as Record<string, unknown>)[key] = donorValue;
  }
  return patched ?? base;
};

interface MessageMergeContext {
  identity: string;
  identitySource: MessageIdentitySource;
  currentCursor: number;
  candidateCursor: number;
}

interface MessageMergeOutcome {
  message: ConversationMessageView;
  changed: boolean;
  diagnostics: CanonicalMergeDiagnostic[];
}

const makeDiagnostic = (
  code: CanonicalMergeDiagnosticCode,
  context: MessageMergeContext,
  kept: MessageFingerprint | null,
  discarded: MessageFingerprint | null,
  detail: string,
): CanonicalMergeDiagnostic => ({
  code,
  messageId: context.identity,
  currentCursor: context.currentCursor,
  candidateCursor: context.candidateCursor,
  detail,
  kept,
  discarded,
});

const mergeMessageRecords = (
  existing: ConversationMessageView,
  incoming: ConversationMessageView,
  context: MessageMergeContext,
): MessageMergeOutcome => {
  const existingFingerprint = buildMessageFingerprint(existing);
  const incomingFingerprint = buildMessageFingerprint(incoming);
  if (messageFingerprintsEqual(existingFingerprint, incomingFingerprint)) {
    return { message: existing, changed: false, diagnostics: [] };
  }

  const diagnostics: CanonicalMergeDiagnostic[] = [];
  const candidateIsNewer = context.candidateCursor > context.currentCursor;
  const sameCursor = context.candidateCursor === context.currentCursor;
  const completeness = compareMessageCompleteness(
    incomingFingerprint,
    existingFingerprint,
  );

  // 采纳候选作为基底的三条合法路径：
  // ① 权威 messageId + 候选游标更新（服务端较新）；
  // ② 权威 messageId + 同游标且候选不更差（服务端无 version ⇒ 完整性决胜，平局取最新）；
  // ③ 派生身份（DTO 缺 id）⇒ 只有候选**严格更完整**才采纳，避免弱身份压掉完整记录。
  let adoptCandidate: boolean;
  if (context.identitySource === 'message-id') {
    adoptCandidate = candidateIsNewer || (sameCursor && completeness >= 0);
  } else {
    adoptCandidate = completeness > 0;
  }

  if (!adoptCandidate) {
    // 存量较新或更完整：保留存量，仅用候选补全存量里 undefined 的已知字段
    // （反向满足「已知字段不得被 undefined 覆盖」）。无变化 ⇒ 无诊断，保证幂等。
    const filled = fillUndefinedFields(existing, incoming);
    return { message: filled, changed: filled !== existing, diagnostics: [] };
  }

  let merged: ConversationMessageView = { ...incoming };
  const keepExistingContent =
    existingFingerprint.contentLength > 0 &&
    incomingFingerprint.contentLength === 0;
  const keepExistingProcessItems =
    incomingFingerprint.processItemCount < existingFingerprint.processItemCount;

  if (keepExistingContent) {
    merged = { ...merged, content: existing.content };
    diagnostics.push(
      makeDiagnostic(
        'content-rollback-rejected',
        context,
        existingFingerprint,
        incomingFingerprint,
        '候选正文为空而存量非空：按完整性单调拒绝空值覆盖。',
      ),
    );
  }
  if (keepExistingProcessItems) {
    merged = { ...merged, processItems: existing.processItems };
    diagnostics.push(
      makeDiagnostic(
        'process-items-rollback-rejected',
        context,
        existingFingerprint,
        incomingFingerprint,
        `候选过程块数 ${incomingFingerprint.processItemCount} < 存量 ${existingFingerprint.processItemCount}：拒绝减少。`,
      ),
    );
  }
  if (sameCursor && !keepExistingContent) {
    if (incomingFingerprint.contentLength > existingFingerprint.contentLength) {
      diagnostics.push(
        makeDiagnostic(
          'content-conflict-kept-longer',
          context,
          incomingFingerprint,
          existingFingerprint,
          '同游标指纹冲突：服务端无 message version，无法判定先后，保留正文字符数较大者。',
        ),
      );
    } else if (completeness === 0) {
      diagnostics.push(
        makeDiagnostic(
          'fingerprint-conflict-adopted-latest',
          context,
          incomingFingerprint,
          existingFingerprint,
          '同游标、完整性完全平局但指纹不同：采纳候选（服务端最新响应），不静默丢弃候选。',
        ),
      );
    }
  }
  if (
    candidateIsNewer &&
    !keepExistingContent &&
    incomingFingerprint.contentLength > 0 &&
    incomingFingerprint.contentLength < existingFingerprint.contentLength
  ) {
    diagnostics.push(
      makeDiagnostic(
        'content-shrink-on-newer-cursor',
        context,
        incomingFingerprint,
        existingFingerprint,
        '候选游标更新但正文更短：游标权威故采纳，记录证据以便观测。',
      ),
    );
  }

  const filled = fillUndefinedFields(merged, existing);
  return { message: filled, changed: true, diagnostics };
};

const mergeActiveRun = (
  current: AgentConversationView,
  candidate: AgentConversationView,
  candidateIsNewer: boolean,
) => {
  if (candidateIsNewer) return candidate.activeRun ?? null;
  // 同游标或候选较旧：无法证明 run 已结束 ⇒ 保守保留仍存在的那一个。
  return candidate.activeRun ?? current.activeRun ?? null;
};

/**
 * 单调合并：候选快照 → 存量快照。
 * - 只允许游标前进（`max(current, candidate)`），不允许回退。
 * - 逐消息按身份匹配：候选较新 / 同游标更完整才替换；否则保留存量并用候选补 undefined 字段。
 * - 身份缺失的消息只追加、不覆盖，并按内容指纹去重。
 */
export const mergeCanonicalConversation = (
  current: AgentConversationView | null | undefined,
  candidate: AgentConversationView,
): CanonicalMergeResult => {
  const diagnostics: CanonicalMergeDiagnostic[] = [];
  if (!current) {
    return { conversation: candidate, changed: true, diagnostics };
  }
  if (!isSameAgentScope(current, candidate)) {
    diagnostics.push({
      code: 'scope-mismatch',
      messageId: null,
      currentCursor: current.eventCursor ?? 0,
      candidateCursor: candidate.eventCursor ?? 0,
      detail: '候选与存量不属于同一 (workspace, owner, agent) scope：不合并、不覆盖。',
      kept: null,
      discarded: null,
    });
    return { conversation: current, changed: false, diagnostics };
  }
  if (asText(current.mainSessionId) !== asText(candidate.mainSessionId)) {
    diagnostics.push({
      code: 'session-rotated',
      messageId: null,
      currentCursor: current.eventCursor ?? 0,
      candidateCursor: candidate.eventCursor ?? 0,
      detail: `mainSessionId 变更（${asText(current.mainSessionId)} → ${asText(candidate.mainSessionId)}）：采纳候选快照。`,
      kept: null,
      discarded: null,
    });
    return { conversation: candidate, changed: true, diagnostics };
  }

  const currentMessages = current.messages ?? [];
  const candidateMessages = candidate.messages ?? [];
  const currentCursor = current.eventCursor ?? 0;
  const candidateCursor = candidate.eventCursor ?? 0;
  const candidateIndex = indexMessagesByKey(candidateMessages);
  const currentKeySet = new Set(
    currentMessages
      .map((message) => resolveMessageIdentity(message).key)
      .filter((key): key is string => Boolean(key)),
  );
  const emittedKeys = new Set<string>();
  const emittedAnonymousFingerprints = new Set<string>();
  const mergedMessages: ConversationMessageView[] = [];
  let messagesChanged = false;

  for (let index = 0; index < currentMessages.length; index += 1) {
    const existing = currentMessages[index];
    const identity = resolveMessageIdentity(existing);
    if (!identity.key) {
      const fingerprint = fingerprintKey(buildMessageFingerprint(existing));
      if (emittedAnonymousFingerprints.has(fingerprint)) {
        messagesChanged = true;
        continue;
      }
      emittedAnonymousFingerprints.add(fingerprint);
      mergedMessages.push(existing);
      continue;
    }
    if (emittedKeys.has(identity.key)) {
      // 同一快照内重复身份：只保留首次实体（设计 §4「重复事件只产生一次实体」）。
      messagesChanged = true;
      continue;
    }
    emittedKeys.add(identity.key);
    const incoming = candidateIndex.get(identity.key);
    if (!incoming) {
      mergedMessages.push(existing);
      continue;
    }
    const outcome = mergeMessageRecords(existing, incoming, {
      identity: identity.key,
      identitySource: identity.source,
      currentCursor,
      candidateCursor,
    });
    if (outcome.changed) messagesChanged = true;
    diagnostics.push(...outcome.diagnostics);
    mergedMessages.push(outcome.message);
  }

  for (let index = 0; index < candidateMessages.length; index += 1) {
    const incoming = candidateMessages[index];
    const identity = resolveMessageIdentity(incoming);
    if (identity.key) {
      if (currentKeySet.has(identity.key) || emittedKeys.has(identity.key)) {
        continue;
      }
      emittedKeys.add(identity.key);
      mergedMessages.push(incoming);
      messagesChanged = true;
      continue;
    }
    const fingerprint = fingerprintKey(buildMessageFingerprint(incoming));
    if (emittedAnonymousFingerprints.has(fingerprint)) continue;
    emittedAnonymousFingerprints.add(fingerprint);
    mergedMessages.push(incoming);
    messagesChanged = true;
  }

  const candidateIsNewer = candidateCursor > currentCursor;
  const eventCursor = Math.max(currentCursor, candidateCursor);
  const activeRun = mergeActiveRun(current, candidate, candidateIsNewer);
  const updatedAt = candidateIsNewer
    ? (candidate.updatedAt ?? current.updatedAt)
    : current.updatedAt;

  const changed =
    messagesChanged ||
    eventCursor !== currentCursor ||
    (activeRun?.runId ?? null) !== (current.activeRun?.runId ?? null) ||
    updatedAt !== current.updatedAt;

  if (!changed) {
    // 严格幂等：无变化 ⇒ 原引用 + 空诊断（重复回拉只产生 merge no-op）。
    return { conversation: current, changed: false, diagnostics: [] };
  }

  return {
    conversation: {
      ...current,
      messages: mergedMessages,
      eventCursor,
      activeRun,
      updatedAt,
    },
    changed: true,
    diagnostics,
  };
};
