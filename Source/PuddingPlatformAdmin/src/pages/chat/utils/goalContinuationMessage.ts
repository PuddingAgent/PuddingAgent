const GOAL_PAYLOAD_OPEN = '<goal_payload>';
const GOAL_PAYLOAD_CLOSE = '</goal_payload>';

interface GoalPayloadTask {
  taskId?: unknown;
  status?: unknown;
  workUnit?: {
    objective?: unknown;
  } | null;
}

interface GoalPayload {
  objective?: unknown;
  iteration?: unknown;
  maxIterations?: unknown;
  task?: GoalPayloadTask | null;
}

const asText = (value: unknown): string | undefined =>
  typeof value === 'string' && value.trim() ? value.trim() : undefined;

const asInteger = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isInteger(value) && value >= 0
    ? value
    : undefined;

/**
 * Goal continuation turns are canonical user-role messages for the Agent, but
 * their transport envelope is not useful chat text. Only server-marked Goal
 * messages are parsed; ordinary user text that happens to contain the same XML
 * tag remains untouched. JSON.parse also decodes legacy `\uXXXX` sequences.
 */
export function formatGoalContinuationMessage(
  content: string,
  metadata?: Record<string, string>,
): string {
  if (
    metadata?.goal_managed !== 'true' ||
    metadata?.automation_origin !== 'goal_continuation'
  ) {
    return content;
  }

  const openIndex = content.indexOf(GOAL_PAYLOAD_OPEN);
  const closeIndex = content.lastIndexOf(GOAL_PAYLOAD_CLOSE);
  if (openIndex < 0 || closeIndex <= openIndex) return content;

  const payloadText = content.slice(
    openIndex + GOAL_PAYLOAD_OPEN.length,
    closeIndex,
  );
  try {
    const payload = JSON.parse(payloadText) as GoalPayload;
    const objective = asText(payload.objective);
    if (!objective) return content;

    const iteration = asInteger(payload.iteration);
    const maxIterations = asInteger(payload.maxIterations);
    const heading =
      iteration !== undefined && maxIterations !== undefined
        ? `Goal 自动续行 · 第 ${iteration}/${maxIterations} 轮`
        : 'Goal 自动续行';
    const taskId = asText(payload.task?.taskId);
    const taskStatus = asText(payload.task?.status);
    const taskLine = taskId
      ? `Task ${taskId}${taskStatus ? ` · ${taskStatus}` : ''}`
      : undefined;
    const workUnitObjective = asText(payload.task?.workUnit?.objective);

    return [
      heading,
      taskLine,
      workUnitObjective ? `当前工作单元：${workUnitObjective}` : undefined,
      objective,
    ]
      .filter((value): value is string => Boolean(value))
      .join('\n\n');
  } catch {
    // Malformed canonical data is evidence. Preserve the original text instead
    // of attempting a broad escape replacement that could corrupt user input.
    return content;
  }
}
