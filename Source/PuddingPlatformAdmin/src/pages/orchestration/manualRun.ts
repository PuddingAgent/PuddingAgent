import type {
  OrchestrationGraphDefinition,
  OrchestrationValueEnvelope,
} from './types';

export function createManualRunRequestId(): string {
  return (
    globalThis.crypto?.randomUUID?.() ??
    `run-${Date.now()}-${Math.random().toString(16).slice(2)}`
  );
}

export function buildManualRunInputs(
  definition: OrchestrationGraphDefinition,
  values: Record<string, unknown>,
): Record<string, OrchestrationValueEnvelope> {
  return Object.fromEntries(
    (definition.inputs ?? []).flatMap((input) => {
      const raw = values[input.inputId];
      if (raw === undefined || raw === null || raw === '') return [];
      const inlineValue = normalizeInlineValue(input.contract.dataType, raw);
      return [
        [
          input.inputId,
          {
            dataType: input.contract.dataType,
            contentType: resolveContentType(
              input.contract.mediaTypes,
              inlineValue,
            ),
            inlineValue,
          } satisfies OrchestrationValueEnvelope,
        ],
      ];
    }),
  );
}

function normalizeInlineValue(dataType: string, value: unknown): unknown {
  if (dataType === 'pudding.json' && typeof value === 'string') {
    try {
      return JSON.parse(value);
    } catch {
      throw new Error('JSON 输入格式不正确');
    }
  }
  if (dataType === 'pudding.number' && typeof value === 'string') {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) throw new Error('数字输入格式不正确');
    return parsed;
  }
  return value;
}

function resolveContentType(mediaTypes: string[], value: unknown): string {
  if (typeof value === 'string' && mediaTypes.includes('text/plain'))
    return 'text/plain';
  return mediaTypes.find((item) => !item.includes('*')) ?? 'application/json';
}
