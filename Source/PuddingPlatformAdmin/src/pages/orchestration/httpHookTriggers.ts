import type {
  OrchestrationCatalog,
  OrchestrationGraphDefinition,
  OrchestrationTriggerDefinition,
} from './types';

export const HTTP_HOOK_TRIGGER_TYPE = 'pudding.trigger.webhook';

export interface HttpHookDraftValues {
  triggerId: string;
  targetInputId?: string;
  sourcePath?: string;
}

export interface HttpHookMutationResult {
  definition: OrchestrationGraphDefinition;
  error?: string;
}

export function listHttpHookTriggers(
  definition: OrchestrationGraphDefinition,
): OrchestrationTriggerDefinition[] {
  return (definition.triggers ?? []).filter(
    (trigger) => trigger.trigger.triggerType === HTTP_HOOK_TRIGGER_TYPE,
  );
}

export function addHttpHookTrigger(
  definition: OrchestrationGraphDefinition,
  catalog: OrchestrationCatalog | undefined,
  values: HttpHookDraftValues,
): HttpHookMutationResult {
  const triggerId = values.triggerId.trim();
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(triggerId)) {
    return {
      definition,
      error: 'Trigger ID 仅允许字母、数字、点、下划线和连字符。',
    };
  }
  if (
    (definition.triggers ?? []).some(
      (trigger) => trigger.triggerId.toLowerCase() === triggerId.toLowerCase(),
    )
  ) {
    return { definition, error: `Trigger ID “${triggerId}” 已存在。` };
  }
  const registered = catalog?.triggers.find(
    (trigger) => trigger.descriptor.triggerType === HTTP_HOOK_TRIGGER_TYPE,
  );
  if (!registered) {
    return { definition, error: '组件目录未注册 HTTP/Webhook Trigger。' };
  }
  const targetInputId = values.targetInputId?.trim();
  if (
    targetInputId &&
    !(definition.inputs ?? []).some((input) => input.inputId === targetInputId)
  ) {
    return { definition, error: `Graph Input “${targetInputId}” 不存在。` };
  }

  const trigger: OrchestrationTriggerDefinition = {
    triggerId,
    trigger: {
      triggerType: registered.descriptor.triggerType,
      version: registered.descriptor.version,
      contractHash: registered.contractHash,
    },
    enabled: true,
    configuration: {
      mode: 'authenticatedDebugHook',
      method: 'POST',
      maxBodyBytes: 1048576,
    },
    inputBindings: targetInputId
      ? [
          {
            sourcePath: values.sourcePath?.trim() || '$',
            targetInputId,
          },
        ]
      : [],
  };
  return {
    definition: {
      ...definition,
      triggers: [...(definition.triggers ?? []), trigger],
    },
  };
}

export function setHttpHookEnabled(
  definition: OrchestrationGraphDefinition,
  triggerId: string,
  enabled: boolean,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    triggers: (definition.triggers ?? []).map((trigger) =>
      trigger.triggerId === triggerId ? { ...trigger, enabled } : trigger,
    ),
  };
}

export function removeHttpHookTrigger(
  definition: OrchestrationGraphDefinition,
  triggerId: string,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    triggers: (definition.triggers ?? []).filter(
      (trigger) => trigger.triggerId !== triggerId,
    ),
  };
}

export function buildHttpHookEndpoint(
  graphId: string,
  revisionId: string,
  triggerId: string,
): string {
  return `/api/orchestrations/hooks/${encodeURIComponent(graphId)}/${encodeURIComponent(
    triggerId,
  )}?revisionId=${encodeURIComponent(revisionId)}`;
}
