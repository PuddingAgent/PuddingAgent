import type { ChatMessageBlock } from '../types';

export type VirtualMessageHeightHint =
  | 'compact'
  | 'normal'
  | 'rich'
  | 'streaming';

export type VirtualMessageItem =
  | {
      kind: 'message';
      id: string;
      createdAt: number;
      block: ChatMessageBlock;
      heightHint: VirtualMessageHeightHint;
      /** 已水合 canonical 行为链的近似渲染成本；用于补足 block 文本权重。 */
      renderWeight?: number;
    }
  | {
      kind: 'loader';
      id: string;
      createdAt: number;
      direction: 'before';
      heightHint: 'compact';
    }
  | {
      kind: 'divider';
      id: string;
      createdAt: number;
      /** 日期分隔线标签：今天 / 昨天 / MM-DD */
      label: string;
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
