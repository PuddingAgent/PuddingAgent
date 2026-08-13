import type {
  OrchestrationGraphDefinition,
  OrchestrationGraphInput,
  OrchestrationGraphInputBinding,
} from './types';

/**
 * Pure function layer for graph-level inputs (S2-B4-A).
 *
 * Id matching mirrors the backend compiler (AgentOrchestrationGraphCompiler.cs):
 * input ids and binding input ids are compared after Trim() with OrdinalIgnoreCase
 * (`graph.input_id_duplicate`, `graph.node_input_unknown`). All functions are
 * immutable: they return new definition objects and never mutate their input.
 */

export interface OrchestrationInputReference {
  nodeId: string;
  targetPortId: string;
  targetKey?: string;
}

/** Result of parsing the defaultValue editor text into a ValueEnvelope-shaped value. */
export interface OrchestrationGraphInputDefaultValueParse {
  /** Parsed value; undefined means "no default" (empty input). */
  value?: unknown;
  /** Set when the text is not valid JSON. */
  error?: string;
}

/**
 * Serializes an input's defaultValue for the panel editor. Empty string means no default;
 * the value is kept loosely typed because defaultValue mirrors the server-side
 * ValueEnvelope and stays `unknown` until S7 lands the envelope mirror.
 */
export function formatGraphInputDefaultValue(
  input: OrchestrationGraphInput,
): string {
  if (input.defaultValue === undefined || input.defaultValue === null) {
    return '';
  }
  return JSON.stringify(input.defaultValue, null, 2);
}

/**
 * Parses the defaultValue editor text. Empty/whitespace text yields `{ value: undefined }`
 * (no default); anything else must be valid JSON or an error is returned. The parsed value
 * is passed through unchanged; the server-side compiler validates it against the contract.
 */
export function parseGraphInputDefaultValue(
  text: string,
): OrchestrationGraphInputDefaultValueParse {
  const trimmed = text.trim();
  if (!trimmed) {
    return { value: undefined };
  }
  try {
    const parsed: unknown = JSON.parse(trimmed);
    return { value: parsed };
  } catch (error) {
    return {
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

/** One node binding that a removal cleans up, kept for the caller to surface in the UI. */
export interface OrchestrationAffectedNodeBinding {
  nodeId: string;
  binding: OrchestrationGraphInputBinding;
}

export interface OrchestrationGraphInputRemoval {
  definition: OrchestrationGraphDefinition;
  /** Distinct node ids that referenced the removed input. */
  affectedNodeIds: string[];
  /** The graphInputBindings removed from those nodes. */
  affectedBindings: OrchestrationAffectedNodeBinding[];
}

function matchesId(candidate: string, inputId: string): boolean {
  return candidate.trim().toLowerCase() === inputId.trim().toLowerCase();
}

function findInputIndex(
  definition: OrchestrationGraphDefinition,
  inputId: string,
): number {
  const inputs = definition.inputs ?? [];
  return inputs.findIndex(
    (input) => input !== undefined && matchesId(input.inputId, inputId),
  );
}

/**
 * Appends a graph input. Deduplicates by inputId (trimmed, case-insensitive, mirroring
 * the compiler's OrdinalIgnoreCase index): when an input with the same id already exists
 * the definition is returned unchanged, so the caller can treat it as a duplicate signal.
 */
export function addGraphInput(
  definition: OrchestrationGraphDefinition,
  input: OrchestrationGraphInput,
): OrchestrationGraphDefinition {
  if (findInputIndex(definition, input.inputId) !== -1) {
    return definition;
  }
  return {
    ...definition,
    inputs: [...(definition.inputs ?? []), input],
  };
}

export type OrchestrationGraphInputPatch = Partial<
  Omit<OrchestrationGraphInput, 'inputId'>
>;

/**
 * Replaces the input identified by inputId (trimmed, case-insensitive) with a patched copy.
 * inputId itself is immutable through update (rename = remove + add); unknown ids no-op.
 */
export function updateGraphInput(
  definition: OrchestrationGraphDefinition,
  inputId: string,
  patch: OrchestrationGraphInputPatch,
): OrchestrationGraphDefinition {
  const index = findInputIndex(definition, inputId);
  if (index === -1) {
    return definition;
  }
  const inputs = definition.inputs ?? [];
  const existing = inputs[index];
  if (!existing) {
    return definition;
  }
  const nextInputs = [...inputs];
  nextInputs[index] = { ...existing, ...patch };
  return { ...definition, inputs: nextInputs };
}

/**
 * Removes a graph input and every node binding that references it. Returns the cleaned
 * definition plus the affected node reference list ("受影响节点引用清单" with the bindings
 * "待清理"/cleaned) so the caller can surface confirmation or diagnostics.
 * Unknown input ids leave the definition untouched and report empty lists.
 */
export function removeGraphInput(
  definition: OrchestrationGraphDefinition,
  inputId: string,
): OrchestrationGraphInputRemoval {
  const index = findInputIndex(definition, inputId);
  if (index === -1) {
    return { definition, affectedNodeIds: [], affectedBindings: [] };
  }

  const inputs = definition.inputs ?? [];
  const nextInputs = inputs.filter((_, inputIndex) => inputIndex !== index);

  const affectedNodeIds: string[] = [];
  const affectedBindings: OrchestrationAffectedNodeBinding[] = [];
  const nextNodes = (definition.nodes ?? []).map((node) => {
    const bindings = node.graphInputBindings ?? [];
    const remaining = bindings.filter(
      (binding) => !matchesId(binding.inputId, inputId),
    );
    if (remaining.length === bindings.length) {
      return node;
    }
    for (const binding of bindings) {
      if (matchesId(binding.inputId, inputId)) {
        affectedNodeIds.push(node.nodeId);
        affectedBindings.push({ nodeId: node.nodeId, binding });
      }
    }
    return { ...node, graphInputBindings: remaining };
  });

  return {
    definition: { ...definition, inputs: nextInputs, nodes: nextNodes },
    affectedNodeIds,
    affectedBindings,
  };
}

/**
 * Returns every node reference (nodeId + targetPortId + optional targetKey) that binds the
 * given inputId, mirroring the node-side graphInputBindings the compiler validates.
 */
export function listInputReferences(
  definition: OrchestrationGraphDefinition,
  inputId: string,
): OrchestrationInputReference[] {
  const references: OrchestrationInputReference[] = [];
  for (const node of definition.nodes ?? []) {
    for (const binding of node.graphInputBindings ?? []) {
      if (matchesId(binding.inputId, inputId)) {
        references.push({
          nodeId: node.nodeId,
          targetPortId: binding.targetPortId,
          targetKey: binding.targetKey,
        });
      }
    }
  }
  return references;
}

/**
 * Replaces all graph-input bindings for one node input port. Ids are trimmed and deduplicated
 * case-insensitively; an empty list clears the port. Port/cardinality compatibility remains a
 * caller/compiler concern because this pure operation has no component catalog dependency.
 */
export function setNodeGraphInputBindings(
  definition: OrchestrationGraphDefinition,
  nodeId: string,
  targetPortId: string,
  inputIds: string[],
): OrchestrationGraphDefinition {
  const uniqueInputIds: string[] = [];
  const seen = new Set<string>();
  for (const inputId of inputIds) {
    const normalized = inputId.trim();
    const key = normalized.toLowerCase();
    if (!normalized || seen.has(key)) continue;
    seen.add(key);
    uniqueInputIds.push(normalized);
  }

  let found = false;
  const nodes = definition.nodes.map((node) => {
    if (!matchesId(node.nodeId, nodeId)) return node;
    found = true;
    const otherBindings = (node.graphInputBindings ?? []).filter(
      (binding) => !matchesId(binding.targetPortId, targetPortId),
    );
    return {
      ...node,
      graphInputBindings: [
        ...otherBindings,
        ...uniqueInputIds.map((inputId) => ({ inputId, targetPortId })),
      ],
    };
  });
  return found ? { ...definition, nodes } : definition;
}
