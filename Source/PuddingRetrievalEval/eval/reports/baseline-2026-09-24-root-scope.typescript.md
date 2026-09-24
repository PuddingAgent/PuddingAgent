# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `root-scope:typescript` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent` |
| started (UTC) | 2026-09-24T02:15:47.1532218+00:00 |
| total elapsed (ms) | 2722 |
| cases | 22 |
| failed calls | 0 |
| repetition-stable | yes |

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.0682 |
| recall@5 | 0.4318 |
| recall@10 | 0.5455 |
| MRR | 0.2477 |
| precision@5 | 0.1091 |
| precision@10 | 0.0727 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| TypeScript | 22 | 0.0682 | 0.4318 | 0.5455 | 0.2477 | 0.1091 | 0.0727 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 22 | 14.902 | 15.941 | 45.668 | 70.125 | 70.125 | 24.65 |
| warm (all later calls) | 88 | 14.444 | 16.475 | 45.297 | 72.619 | 72.619 | 24.75 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 16.2426 | 20 | yes |  |
| 1 | 16.0119 | 20 | yes |  |
| 2 | 15.7725 | 20 | yes |  |
| 3 | 15.9406 | 20 | yes |  |
| 4 | 15.3217 | 20 | yes |  |
| 5 | 15.468 | 20 | yes |  |
| 6 | 14.9016 | 20 | yes |  |
| 7 | 14.9109 | 20 | yes |  |
| 8 | 41.4724 | 14 | yes |  |
| 9 | 70.1249 | 0 | yes |  |
| 10 | 45.6683 | 11 | yes |  |
| 11 | 36.9555 | 0 | yes |  |
| 12 | 34.9375 | 15 | yes |  |
| 13 | 38.144 | 0 | yes |  |
| 14 | 31.3285 | 0 | yes |  |
| 15 | 26.7627 | 20 | yes |  |
| 16 | 15.0358 | 20 | yes |  |
| 17 | 15.1757 | 20 | yes |  |
| 18 | 15.0371 | 20 | yes |  |
| 19 | 15.4091 | 20 | yes |  |
| 20 | 15.645 | 20 | yes |  |
| 21 | 16.0243 | 16 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | TypeScript | PuddingDataTable | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 17.1132 | yes |
| 1 | Symbol | TypeScript | PuddingStatusBadge | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 16.4904 | yes |
| 2 | Symbol | TypeScript | TaskBoard | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 16.218 | yes |
| 3 | Symbol | TypeScript | TaskCard | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 17.0061 | yes |
| 4 | Symbol | TypeScript | syncEngine | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 16.1845 | yes |
| 5 | Symbol | TypeScript | canonicalMerge | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 16.3931 | yes |
| 6 | Symbol | TypeScript | workspaceNavigation | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 15.8931 | yes |
| 7 | Symbol | TypeScript | autoReviewClassifier | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 16.5816 | yes |
| 8 | Intent | TypeScript | where is the chat client store that keeps local cache in sync | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 9 | 0 | 43.5468 | yes |
| 9 | Intent | TypeScript | how does the chat client merge canonical server state | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 66.4304 | yes |
| 10 | Intent | TypeScript | where is the checkpoint store used when resuming a chat session | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 9 | 0 | 44.1185 | yes |
| 11 | Intent | TypeScript | which module renders the workspace task board columns | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 35.3774 | yes |
| 12 | Intent | TypeScript | where is the scheduler drawer for recurring tasks | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 5 | 0 | 34.3254 | yes |
| 13 | Intent | TypeScript | how is the agent template settings drawer organized into sections | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 35.3939 | yes |
| 14 | Intent | TypeScript | where is the access token secret shown once modal | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 31.023 | yes |
| 15 | Intent | TypeScript | which util builds the workspace navigation menu | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 26.8367 | yes |
| 16 | Crossref | TypeScript | canonicalMerge | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 16.3373 | yes |
| 17 | Crossref | TypeScript | checkpointStore | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 16.3655 | yes |
| 18 | Crossref | TypeScript | TaskCard | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 15.8801 | yes |
| 19 | Crossref | TypeScript | PuddingStatusBadge | 2/2 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.2000 | 0.0000 | 10 | 0 | 16.4748 | yes |
| 20 | Crossref | TypeScript | TaskEventTimeline | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 16.4834 | yes |
| 21 | Crossref | TypeScript | PuddingAdminShell | 2/2 | 0.0000 | 0.5000 | 1.0000 | 0.2500 | 0.2000 | 0.2000 | 0.0000 | 8 | 0 | 17.1901 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | PuddingDataTable | `Source/PuddingPlatformAdmin/src/components/PuddingDataTable/index.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingDataTable\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingDataTable\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\inde...` |
| 1 | PuddingStatusBadge | `Source/PuddingPlatformAdmin/src/components/PuddingStatusBadge/index.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...` |
| 2 | TaskBoard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskBoard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskBoard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...` |
| 3 | TaskCard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...` |
| 4 | syncEngine | `Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\syncEngine.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\chat\clie...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\syncEngine.ts` |
| 5 | canonicalMerge | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\chat\clie...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts` |
| 6 | workspaceNavigation | `Source/PuddingPlatformAdmin/src/utils/workspaceNavigation.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\utils\workspaceNavigation.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\utils\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\utils\workspaceNavigation.ts` |
| 7 | autoReviewClassifier | `Source/PuddingPlatformAdmin/src/pages/chat/classifier/autoReviewClassifier.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\classifier\autoReviewClassifier.te...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\chat\clas...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\classifier\autoReviewClassifier.ts` |
| 8 | where is the chat client store that keeps local cache in sync | `Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\plan-version-core\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\skillhub-agent-out\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-test-out\heartbeat-host\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\compaction-status\core\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\heartbeat-deploy-20260920\core\.playwright\package\types\protocol....`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\revisionEditor.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\orchestra...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MessageList.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\chat\comp...` |
| 9 | how does the chat client merge canonical server state | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts` | _(none)_ |
| 10 | where is the checkpoint store used when resuming a chat session | `Source/PuddingPlatformAdmin/src/pages/chat/client/checkpointStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\services\swagger\store.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\services\swagge...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\plan-version-core\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\skillhub-agent-out\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-test-out\heartbeat-host\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\compaction-status\core\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\heartbeat-deploy-20260920\core\.playwright\package\types\protocol....`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\connectionDrag.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\orchestra...` |
| 11 | which module renders the workspace task board columns | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskColumn.tsx` | _(none)_ |
| 12 | where is the scheduler drawer for recurring tasks | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/SchedulerDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\plan-version-core\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\skillhub-agent-out\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-test-out\heartbeat-host\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\compaction-status\core\.playwright\package\types\protocol.d.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\heartbeat-deploy-20260920\core\.playwright\package\types\protocol....` |
| 13 | how is the agent template settings drawer organized into sections | `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/AgentTemplateSettingsDrawer.tsx` | _(none)_ |
| 14 | where is the access token secret shown once modal | `Source/PuddingPlatformAdmin/src/pages/access-token-management/components/SecretOnceModal.tsx` | _(none)_ |
| 15 | which util builds the workspace navigation menu | `Source/PuddingPlatformAdmin/src/utils/workspaceNavigation.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\bn-BD\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\en-US\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\fa-IR\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\id-ID\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\ja-JP\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\pt-BR\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\zh-TW\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\locales\bn-BD\m...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\locales\en-US\m...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\locales\fa-IR\m...` |
| 16 | canonicalMerge | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts`<br>`Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\chat\clie...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts` |
| 17 | checkpointStore | `Source/PuddingPlatformAdmin/src/pages/chat/client/checkpointStore.ts`<br>`Source/PuddingPlatformAdmin/src/pages/chat/hooks/useCheckpointTimeline.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\checkpointStore.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\chat\clie...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\checkpointStore.ts` |
| 18 | TaskCard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskColumn.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...` |
| 19 | PuddingStatusBadge | `Source/PuddingPlatformAdmin/src/components/PuddingStatusBadge/index.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...` |
| 20 | TaskEventTimeline | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskEventTimeline.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskDetailsDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskEventTimeline.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\pages\workspace...` |
| 21 | PuddingAdminShell | `Source/PuddingPlatformAdmin/src/components/PuddingAdminShell/index.tsx`<br>`Source/PuddingPlatformAdmin/src/components/index.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminShell\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminShell\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\Pudd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.pnpm-store\v11\projects\80002e53f952d4818ac335df74a0bd0d\src\components\inde...` |
