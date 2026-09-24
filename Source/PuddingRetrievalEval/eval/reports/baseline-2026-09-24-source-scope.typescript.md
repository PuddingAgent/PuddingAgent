# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `source-scope:typescript` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source` |
| started (UTC) | 2026-09-24T00:51:03.6433414+00:00 |
| total elapsed (ms) | 692 |
| cases | 22 |
| failed calls | 0 |
| repetition-stable | yes |

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.1136 |
| recall@5 | 0.6136 |
| recall@10 | 0.6136 |
| MRR | 0.3636 |
| precision@5 | 0.1636 |
| precision@10 | 0.0818 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| TypeScript | 22 | 0.1136 | 0.6136 | 0.6136 | 0.3636 | 0.1636 | 0.0818 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 22 | 2.153 | 4.975 | 11.555 | 19.738 | 19.738 | 6.51 |
| warm (all later calls) | 88 | 2.001 | 5.197 | 13.528 | 17.844 | 17.844 | 6.2 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 7.8869 | 11 | yes |  |
| 1 | 6.9635 | 20 | yes |  |
| 2 | 4.9747 | 20 | yes |  |
| 3 | 2.6389 | 20 | yes |  |
| 4 | 3.1604 | 20 | yes |  |
| 5 | 2.7783 | 20 | yes |  |
| 6 | 3.7166 | 20 | yes |  |
| 7 | 2.4019 | 20 | yes |  |
| 8 | 11.5554 | 13 | yes |  |
| 9 | 10.8304 | 20 | yes |  |
| 10 | 19.7377 | 8 | yes |  |
| 11 | 10.2551 | 14 | yes |  |
| 12 | 8.9701 | 20 | yes |  |
| 13 | 10.4023 | 6 | yes |  |
| 14 | 7.2789 | 1 | yes |  |
| 15 | 8.3155 | 17 | yes |  |
| 16 | 4.3507 | 20 | yes |  |
| 17 | 2.3426 | 20 | yes |  |
| 18 | 2.1529 | 20 | yes |  |
| 19 | 7.5028 | 20 | yes |  |
| 20 | 2.3674 | 20 | yes |  |
| 21 | 2.7206 | 8 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | TypeScript | PuddingDataTable | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 5.7633 | yes |
| 1 | Symbol | TypeScript | PuddingStatusBadge | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 7.2952 | yes |
| 2 | Symbol | TypeScript | TaskBoard | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.3971 | yes |
| 3 | Symbol | TypeScript | TaskCard | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.9869 | yes |
| 4 | Symbol | TypeScript | syncEngine | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.1071 | yes |
| 5 | Symbol | TypeScript | canonicalMerge | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 3.1017 | yes |
| 6 | Symbol | TypeScript | workspaceNavigation | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.2148 | yes |
| 7 | Symbol | TypeScript | autoReviewClassifier | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 5.197 | yes |
| 8 | Intent | TypeScript | where is the chat client store that keeps local cache in sync | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 11.5981 | yes |
| 9 | Intent | TypeScript | how does the chat client merge canonical server state | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 10.8713 | yes |
| 10 | Intent | TypeScript | where is the checkpoint store used when resuming a chat session | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 5 | 0 | 17.8435 | yes |
| 11 | Intent | TypeScript | which module renders the workspace task board columns | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 9.25 | yes |
| 12 | Intent | TypeScript | where is the scheduler drawer for recurring tasks | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 9.9548 | yes |
| 13 | Intent | TypeScript | how is the agent template settings drawer organized into sections | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 4 | 0 | 9.5039 | yes |
| 14 | Intent | TypeScript | where is the access token secret shown once modal | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 1 | 0 | 7.1633 | yes |
| 15 | Intent | TypeScript | which util builds the workspace navigation menu | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 9 | 0 | 7.4886 | yes |
| 16 | Crossref | TypeScript | canonicalMerge | 2/2 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.4000 | 0.2000 | 0.0000 | 3 | 0 | 2.4057 | yes |
| 17 | Crossref | TypeScript | checkpointStore | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 3.0864 | yes |
| 18 | Crossref | TypeScript | TaskCard | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.2917 | yes |
| 19 | Crossref | TypeScript | PuddingStatusBadge | 2/2 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.4000 | 0.2000 | 0.0000 | 7 | 0 | 7.3571 | yes |
| 20 | Crossref | TypeScript | TaskEventTimeline | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.1789 | yes |
| 21 | Crossref | TypeScript | PuddingAdminShell | 2/2 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.4000 | 0.2000 | 0.0000 | 4 | 0 | 3.3904 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | PuddingDataTable | `Source/PuddingPlatformAdmin/src/components/PuddingDataTable/index.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingDataTable\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingDataTable\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
| 1 | PuddingStatusBadge | `Source/PuddingPlatformAdmin/src/components/PuddingStatusBadge/index.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
| 2 | TaskBoard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskBoard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskBoard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\index.tsx` |
| 3 | TaskCard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx` |
| 4 | syncEngine | `Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\syncEngine.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\syncEngine.ts` |
| 5 | canonicalMerge | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\chatClientStore.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts` |
| 6 | workspaceNavigation | `Source/PuddingPlatformAdmin/src/utils/workspaceNavigation.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\utils\workspaceNavigation.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\utils\workspaceNavigation.ts` |
| 7 | autoReviewClassifier | `Source/PuddingPlatformAdmin/src/pages/chat/classifier/autoReviewClassifier.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\classifier\autoReviewClassifier.te...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\classifier\autoReviewClassifier.ts` |
| 8 | where is the chat client store that keeps local cache in sync | `Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\revisionEditor.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MessageList.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\localCache.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\ChatMain.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MessageList.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\chatClientStore.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\requestSequenceGuard.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\reducer\subAgentReducer.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\viewport\useMessageViewportRuntime.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\services\swagger\typings.d.ts` |
| 9 | how does the chat client merge canonical server state | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\hooks\useCompaction.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\services\platform\api.sessionEvents.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\earthVectorData.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\hooks\useChatModals.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\state\conversationStore.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\hooks\useMessageInteractionQueue.t...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\AgentMessageBubble.test...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\projections\canonical-collector.te...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\projections\messageProjection.test.ts` |
| 10 | where is the checkpoint store used when resuming a chat session | `Source/PuddingPlatformAdmin/src/pages/chat/client/checkpointStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\services\swagger\store.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\connectionDrag.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\requestSequenceGuard.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\edgeEditor.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\hooks\usePollingLoader.ts` |
| 11 | which module renders the workspace task board columns | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskColumn.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\ChatMain.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\[id]\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskBoard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\StateDot.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\e2e\admin-workspace-responsive.spec.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\edgeEditor.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\UserAvatarUpload.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\FocusViewToggle.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\GoalBanner.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MarkdownBlock.artifactI...` |
| 12 | where is the scheduler drawer for recurring tasks | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/SchedulerDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\reducer\subAgentReducer.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MessageList.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\revisionEditor.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\edgeEditor.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\access-token-management\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\agent-template-settings\AgentTemplateSe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\global-agent-template\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\keyvault\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\llm-resource-pool\index.tsx` |
| 13 | how is the agent template settings drawer organized into sections | `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/AgentTemplateSettingsDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\agent-template-settings\AgentTemplateSe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\en-US\settings.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\agent-template-settings\types.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\[id]\WorkspaceAgentSettingsDr...` |
| 14 | where is the access token secret shown once modal | `Source/PuddingPlatformAdmin/src/pages/access-token-management/components/SecretOnceModal.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\access-token-management\index.tsx` |
| 15 | which util builds the workspace navigation menu | `Source/PuddingPlatformAdmin/src/utils/workspaceNavigation.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\bn-BD\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\en-US\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\fa-IR\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\id-ID\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\ja-JP\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\pt-BR\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\zh-TW\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\zh-CN\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\issueLocator.ts` |
| 16 | canonicalMerge | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts`<br>`Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\chatClientStore.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts` |
| 17 | checkpointStore | `Source/PuddingPlatformAdmin/src/pages/chat/client/checkpointStore.ts`<br>`Source/PuddingPlatformAdmin/src/pages/chat/hooks/useCheckpointTimeline.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\checkpointStore.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\checkpointStore.ts` |
| 18 | TaskCard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskColumn.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx` |
| 19 | PuddingStatusBadge | `Source/PuddingPlatformAdmin/src/components/PuddingStatusBadge/index.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
| 20 | TaskEventTimeline | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskEventTimeline.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskDetailsDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskEventTimeline.tsx` |
| 21 | PuddingAdminShell | `Source/PuddingPlatformAdmin/src/components/PuddingAdminShell/index.tsx`<br>`Source/PuddingPlatformAdmin/src/components/index.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminShell\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminShell\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
