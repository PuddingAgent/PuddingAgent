import { useSyncExternalStore } from 'react';

/**
 * S2-B5-2: transient ReactFlow connection-drag state shared between the canvas
 * event handlers (index.tsx) and custom node port Handles
 * (OrchestrationComponentNode.tsx). Module-level so no React tree re-plumbing
 * is required; custom nodes subscribe with useSyncExternalStore and render
 * per-handle compatibility classes (doc 84 §8.2:256-259).
 */

/** CSS class for a target port that can accept the dragged connection. */
export const ORCHESTRATION_HANDLE_COMPATIBLE_CLASS =
  'orchestration-handle-compatible';

/** CSS class for a target port that cannot accept the dragged connection. */
export const ORCHESTRATION_HANDLE_INCOMPATIBLE_CLASS =
  'orchestration-handle-incompatible';

export interface ConnectionDragState {
  connecting: boolean;
  /** The handle the user grabbed (may be an input handle for reverse drags). */
  startNodeId: string | null;
  startHandleId: string | null;
  /** `${nodeId}::${handleId}` -> true when a drop on that handle is valid. */
  compatibility: Record<string, boolean> | null;
}

const idleState: ConnectionDragState = {
  connecting: false,
  startNodeId: null,
  startHandleId: null,
  compatibility: null,
};

let currentState: ConnectionDragState = idleState;
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

/** Starts a connection drag with a precomputed per-handle compatibility map. */
export function beginConnectionDrag(
  startNodeId: string,
  startHandleId: string,
  compatibility: Record<string, boolean>,
): void {
  currentState = {
    connecting: true,
    startNodeId,
    startHandleId,
    compatibility,
  };
  notify();
}

/** Idempotently ends the connection drag and clears the compatibility map. */
export function endConnectionDrag(): void {
  if (currentState === idleState) return;
  currentState = idleState;
  notify();
}

export function getConnectionDragState(): ConnectionDragState {
  return currentState;
}

export function subscribeConnectionDrag(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** React hook used by custom nodes to re-render port highlights on drag events. */
export function useConnectionDragState(): ConnectionDragState {
  return useSyncExternalStore(
    subscribeConnectionDrag,
    getConnectionDragState,
    getConnectionDragState,
  );
}

/**
 * Pure helper: per-handle compatibility className during an active drag.
 * Returns undefined when idle or when the handle is not a candidate endpoint,
 * so the node keeps its default styling otherwise.
 */
export function buildHandleClassName(
  nodeId: string,
  handleId: string,
  drag: ConnectionDragState,
): string | undefined {
  if (!drag.connecting || !drag.compatibility) return undefined;
  const compatible = drag.compatibility[`${nodeId}::${handleId}`];
  if (compatible === undefined) return undefined;
  return compatible
    ? ORCHESTRATION_HANDLE_COMPATIBLE_CLASS
    : ORCHESTRATION_HANDLE_INCOMPATIBLE_CLASS;
}
