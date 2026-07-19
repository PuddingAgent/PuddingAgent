import type { ChatMessageBlock, SubAgentCard } from '../types';

export type VirtualMessageHeightHint = 'compact' | 'normal' | 'rich' | 'streaming';

export type VirtualMessageItem =
  | {
      kind: 'message';
      id: string;
      createdAt: number;
      block: ChatMessageBlock;
      heightHint: VirtualMessageHeightHint;
    }
  | {
      kind: 'subagent-anchor';
      id: string;
      createdAt: number;
      cards: SubAgentCard[];
      heightHint: 'compact';
    }
  | {
      kind: 'loader';
      id: string;
      createdAt: number;
      direction: 'before';
      heightHint: 'compact';
    };

export type FollowMode = 'off' | 'auto' | 'pinned';

export type ScrollIntent =
  | { type: 'none' }
  | { type: 'user-send'; itemId: string; createdAt: number }
  | { type: 'manual-bottom'; behavior: ScrollBehavior }
  | { type: 'restore-anchor'; itemId: string; offset: number }
  | { type: 'load-before'; anchorItemId: string; anchorOffset: number };

export interface ViewportAnchor {
  itemId: string;
  offset: number;
}

export interface MessageViewportState {
  atBottom: boolean;
  nearTop: boolean;
  followMode: FollowMode;
  showBottomButton: boolean;
  anchorItemId?: string;
  pendingIntent: ScrollIntent;
}

export interface LoadBeforeRequest {
  anchor: ViewportAnchor;
}
