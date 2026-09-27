# Admin 设置迁移源证据附录（2026-09-27）

配套任务书：`Desktop-Admin-Settings-DeepSeek-2026-09-27.md`。本附录按实际源文件静态抽取 UI 字段、选项/表格列、卡片标题与事件入口，供 DeepSeek 对照避免漏项。它不是运行态验收，也不是新原生 API 规范。

路径相对于 `Source/PuddingPlatformAdmin/src/`。行号为本次源代码快照；动态表达式必须回到源文件阅读，不得把所有 title/label 都当可写字段。API 调用仅为定位 Core 行为的线索；客户端直接调用 Core 方法。服务端的校验、权限、版本与幂等要求仍须逐项验证。

主任务书的卡片是交付分组：例如存储总览卡下的多个图表/统计子卡、角色文档下的多个编辑器，均在本附录展开。下面也包含独立工作页的工具栏与弹窗；不要求把它们都塞入设置面板。

## `components/GlobalActions/index.tsx`

```tsx
64: aria-label="用户菜单"
```

## `components/RightContent/index.tsx`

本组件使用动态定义/子组件；请沿 imports 和 render 返回继续核对。

## `pages/access-token-management/components/SecretOnceModal.tsx`

```tsx
42: title={
74: <Button
81: <Button
93: <Button
```

## `pages/access-token-management/index.tsx`

行为追踪：@/services/platform/api: getExternalApiStatus, listAccessTokens, createAccessToken, renameAccessToken, revokeAccessToken, listWorkspaces, type ExternalAccessTokenDto, type CreatedAccessTokenDto, type ExternalAccessTokenStatusWire, type ExternalApiStatusDto,。

```tsx
51: const SCOPE_OPTIONS: { value: string; label: string; risk: 'low' | 'medium' | 'high' }[] = [
52: { value: 'tasks.read', label: 'tasks.read — 读取任务/评论/评价/Watch', risk: 'low' },
53: { value: 'tasks.write', label: 'tasks.write — 创建任务、修改元数据、导入', risk: 'medium' },
54: { value: 'tasks.comment', label: 'tasks.comment — 追加评论', risk: 'medium' },
55: { value: 'tasks.evaluate', label: 'tasks.evaluate — 追加结构化评价（不改任务状态）', risk: 'medium' },
56: { value: 'tasks.command', label: 'tasks.command — 状态/执行命令（高风险）', risk: 'high' },
57: { value: 'workspaces.read', label: 'workspaces.read — 列出授权工作空间及详情', risk: 'low' },
58: { value: 'agents.read', label: 'agents.read — 读取授权工作空间的 Agent 目录', risk: 'low' },
59: { value: 'messages.send', label: 'messages.send — 向 Agent 投递消息并读取执行回执', risk: 'high' },
87: const [workspaceOptions, setWorkspaceOptions] = useState<{ label: string; value: string }[]>([]);
117: workspaces.map((w) => ({ label: w.workspaceId, value: w.workspaceId })),
218: { title: '名称', dataIndex: 'name', width: 160, copyable: false },
220: title: '前缀',
221: dataIndex: 'displayPrefix',
229: title: 'Workspaces',
230: dataIndex: 'workspaces',
241: title: 'Scopes',
242: dataIndex: 'scopes',
255: title: '状态',
256: dataIndex: 'status',
261: title: '到期时间',
262: dataIndex: 'expiresAtUtc',
267: title: '最后使用',
268: dataIndex: 'lastUsedAtUtc',
273: title: '创建者/创建时间',
274: dataIndex: 'ownerUserId',
286: title: '操作',
291: <Button type="link" size="small" icon={<EyeOutlined />} onClick={() => setDetailToken(record)}>
294: <Button type="link" size="small" icon={<EditOutlined />} onClick={() => setRenaming(record)}>
298: <Button type="link" size="small" danger icon={<StopOutlined />} onClick={() => setRevoking(record)}>
339: placeholder="状态过滤"
348: label: meta.text,
351: <Button key="create" type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
361: title="新建 Access Token"
367: <Button onClick={() => setCreateOpen(false)}>取消</Button>
368: <Button type="primary" loading={creating} onClick={handleCreate}>
382: <Descriptions.Item label="有效期上限">
385: <Descriptions.Item label="每人 Active 上限">{apiStatus.maxActiveTokensPerOwner} 个</Descriptions.Item>
390: name="name"
391: label="名称"
396: placeholder="如 codex-readonly"
399: name="workspaceIds"
400: label="Workspace 允许清单（多选，至少一项，创建后不可扩大）"
406: name="scopes"
407: label="Scope（默认仅最小只读权限；创建后不可扩大）"
414: label: (
425: name="lifetimeDays"
426: label="有效期（天）"
429: .map((d) => ({ value: d, label: `${d} 天` }))}
444: title={detailToken ? `Token 详情：${detailToken.name}` : ''}
451: <Descriptions.Item label="TokenId">{detailToken.tokenId}</Descriptions.Item>
452: <Descriptions.Item label="KeyId">{detailToken.keyId}</Descriptions.Item>
453: <Descriptions.Item label="前缀">
456: <Descriptions.Item label="状态">{statusTag(detailToken.status)}</Descriptions.Item>
457: <Descriptions.Item label="Scope">{detailToken.scopes.join(', ')}</Descriptions.Item>
458: <Descriptions.Item label="Workspaces">{detailToken.workspaces.join(', ')}</Descriptions.Item>
459: <Descriptions.Item label="创建者">{detailToken.ownerUserId}</Descriptions.Item>
460: <Descriptions.Item label="创建时间">{formatUtc(detailToken.createdAtUtc)}</Descriptions.Item>
461: <Descriptions.Item label="到期时间">{formatUtc(detailToken.expiresAtUtc)}</Descriptions.Item>
462: <Descriptions.Item label="最后使用">{formatUtc(detailToken.lastUsedAtUtc)}</Descriptions.Item>
463: <Descriptions.Item label="管理版本">v{detailToken.version}</Descriptions.Item>
466: <Descriptions.Item label="撤销时间">{formatUtc(detailToken.revokedAtUtc)}</Descriptions.Item>
467: <Descriptions.Item label="撤销人">{detailToken.revokedByUserId ?? '-'}</Descriptions.Item>
468: <Descriptions.Item label="撤销原因">{detailToken.revocationReason ?? '-'}</Descriptions.Item>
478: title={renaming ? `重命名：${renaming.name}` : ''}
487: name="name"
488: label="新名称（不改变任何安全事实）"
502: title={revoking ? `撤销 Token：${revoking.name}` : ''}
518: name="reason"
519: label="撤销原因（可选，最长 500 字符）"
522: <Input.TextArea rows={3} maxLength={500} placeholder="如：密钥疑似泄漏 / 集成下线" />
```

## `pages/agent-template-settings/AgentTemplateSettingsDrawer.tsx`

```tsx
31: title: string;
47: workspaces?: { label: string; value: string }[];
48: globalTemplates?: { label: string; value: string }[];
180: title={title}
186: <Button type="primary" loading={saving} onClick={handleSave}>
214: globalTemplates={globalTemplates?.map((g) => ({ label: g.label, value: g.value }))}
```

## `pages/agent-template-settings/AgentTemplateSettingsNav.tsx`

```tsx
32: <nav className={styles.settingsNav} aria-label="设置分组导航">
```

## `pages/agent-template-settings/sections/BasicSection.tsx`

```tsx
18: { label: '服务型 (Service)', value: 'Service' },
19: { label: '任务型 (Task)', value: 'Task' },
20: { label: '审计型 (Audit)', value: 'Audit' },
21: { label: '自定义 (Custom)', value: 'Custom' },
29: workspaces?: { label: string; value: string }[];
30: globalTemplates?: { label: string; value: string }[];
65: name="workspaceId"
66: label="所属工作区"
76: name="baseGlobalTemplateId"
77: label="继承自全局模板"
79: placeholder="不选则创建独立模板"
89: name="templateId"
90: label="模板 ID"
96: placeholder="如 code-reviewer"
102: <ProFormText name="name" label="模板名称" rules={[{ required: true }]} />
106: name="role"
107: label="角色类型"
117: name="description"
118: label="模板描述"
120: placeholder="描述这一类 Agent 的职责范围和适用场景"
125: name="avatarId"
126: label="默认头像"
131: placeholder="选择系统头像"
135: label: a.name,
154: <ProFormSwitch name="isEnabled" label="启用" />
157: <ProFormDigit name="sortOrder" label="排序权重" min={0} />
```

## `pages/agent-template-settings/sections/CapabilitySkillSection.tsx`

```tsx
50: label: string;
138: const title = kind === 'capability' ? '选择高权限工具' : '选择 Skill 包';
145: title={title}
158: placeholder={kind === 'capability' ? '搜索工具名、toolId、描述' : '搜索 Skill 名称、ID、版本'}
225: aria-label={item.name}
256: aria-label={item.name}
278: <Button onClick={onCancel}>取消</Button>
279: <Button data-testid="resource-picker-apply" type="primary" onClick={() => onApply(draftKeys)}>
324: <Form.Item name={capabilityFieldName} hidden>
327: <Form.Item name={skillFieldName} hidden>
348: <GrantChip key={item.capabilityId} label={item.name} code={item.toolName} color="green" />
369: <Button size="small" icon={<ApiOutlined />} onClick={() => setActivePicker('capability')}>
378: label={item.name}
386: <Button size="small" type="link" onClick={() => setActivePicker('capability')}>
409: <Button size="small" icon={<AppstoreOutlined />} onClick={() => setActivePicker('skill')}>
418: label={item.name}
426: <Button size="small" type="link" onClick={() => setActivePicker('skill')}>
```

## `pages/agent-template-settings/sections/GuardrailSection.tsx`

```tsx
21: name="maxRounds"
22: label="最大轮次"
30: name="maxElapsedSeconds"
31: label="最大耗时(秒)"
39: name="maxToolCallsTotal"
40: label="最大工具调用"
49: name="containerImage"
50: label="运行环境"
51: placeholder="宿主模式暂不使用，留空即可"
```

## `pages/agent-template-settings/sections/ModelMemorySection.tsx`

```tsx
38: .map((p) => ({ label: p.name, value: p.providerId }));
48: name="preferredProviderId"
49: label="默认服务商"
51: placeholder="不选则使用平台默认"
60: name="preferredModelId"
61: label="默认模型"
63: label: `${m.name} (${(m.maxContextTokens / 1000).toFixed(0)}K)`,
66: placeholder="不选则使用服务商默认"
79: name="memoryLlmProviderId"
80: label="默认潜意识模型服务商"
82: placeholder="不选则跟随主聊天模型"
92: name="memoryLlmModelId"
93: label="默认潜意识模型"
95: label: `${m.name} (${m.modelId})`,
98: placeholder="不选则使用该服务商默认模型"
109: name="memorySearchMode"
110: label="默认记忆搜索模式"
112: { label: '关闭（仅关键词+标签检索）', value: 'off' },
113: { label: '即时（关键词+标签+后台异步探索）', value: 'instant' },
114: { label: '深度（同步探索，首次冷启动≤60s，上下文最精准）', value: 'deep' },
121: name="reasoningEffort"
122: label="默认推理深度"
124: { label: '跟随模型默认', value: '' },
125: { label: '低（快速响应）', value: 'low' },
126: { label: '中（平衡）', value: 'medium' },
127: { label: '高（深度思考）', value: 'high' },
140: name="embeddingProviderId"
141: label="Embedding 服务商"
143: placeholder="不选则使用平台默认"
153: name="embeddingModelId"
154: label="Embedding 模型"
156: label: `${m.name} (${m.modelId})`,
159: placeholder="不选则使用服务商默认"
```

## `pages/agent-template-settings/sections/PromptPersonaSection.tsx`

```tsx
17: name="systemPrompt"
18: label="模板角色定义"
20: placeholder="定义这一类 Agent 的核心职责、能力边界和行为准则…"
24: name="personaPrompt"
25: label="默认语气与边界（SOUL）"
27: placeholder="定义模板默认表达风格和边界；具体 Agent 可在实例中覆盖"
31: name="toolsDescription"
32: label="工具使用约定（TOOLS）"
34: placeholder="约定何时调用工具、如何解释结果、失败时如何降级"
38: name="bootstrapTemplate"
39: label="首次引导模板（BOOTSTRAP）"
41: placeholder="定义首次对话的开场与引导模板"
45: name="agentsPrompt"
46: label="子 Agent 协作规范（AGENTS.md）"
48: placeholder="定义多 Agent 协作时的角色职责、边界与交付约束"
52: name="memoryPrompt"
53: label="记忆策略（MEMORY.md）"
55: placeholder="定义记忆写入、检索、压缩与优先级策略"
59: name="userPromptTemplate"
60: label="用户 Prompt 模板"
62: placeholder="可选，支持 {{variable}} 占位符"
```

## `pages/capability-management/index.tsx`

行为追踪：@/services/platform/api: listCapabilities, listPluginDiagnostics, listPlugins, reloadPlugin, reloadPluginCatalog, uploadPluginPackage, type CapabilityDto, type PluginCatalogItemDto, type PluginDiagnosticEventDto, type PluginToolItemDto,。

```tsx
313: title: '工具 ID',
314: dataIndex: 'toolName',
320: title: '能力 ID',
321: dataIndex: 'capabilityId',
326: title: '名称',
327: dataIndex: 'name',
331: title: '权限需求',
336: title: '来源',
341: title: '描述',
346: title: '注册状态',
358: <Button key="reload" size="small" icon={<ReloadOutlined />} onClick={reloadTools}>
394: <Button size="small" icon={<UploadOutlined />}>选择 ZIP</Button>
396: <Button
407: <Button key="reloadPlugins" size="small" icon={<ReloadOutlined />} onClick={handleReloadPluginCatalog}>
410: <Button key="reloadPluginDiagnostics" size="small" icon={<ReloadOutlined />} onClick={reloadPluginDiagnostics}>
549: <Statistic title="插件包" value={pluginSummary.totalPlugins} />
554: <Statistic title="工具声明" value={pluginSummary.totalTools} />
559: <Statistic title="ManifestOnly" value={pluginSummary.manifestOnlyPlugins} />
564: <Statistic title="无效插件" value={pluginSummary.invalidPlugins} />
595: title={
602: <Button
649: <Button size="small" icon={<ReloadOutlined />} onClick={reloadPluginDiagnostics}>
676: title="工具管理"
685: label: '工具注册表',
690: label: '插件包',
```

## `pages/diagnostics/DiagnosticsPage.tsx`

行为追踪：./api: getComponentHealth, getEventStats,。

```tsx
91: title: '诊断概览',
109: title="系统状态"
123: <Statistic title="活跃组件" value={components.length} prefix={<ApiOutlined />} />
132: title="运行时时间线"
145: title="子代理运行"
160: title={
182: title="成功"
189: title="失败"
196: title="重试"
234: <Card size="small" title="按状态分布">
250: <Card size="small" title="按组件分布">
```

## `pages/diagnostics/RuntimeTimelinePage.tsx`

行为追踪：./api: getRuntimeTimeline。

```tsx
57: title: '开始时间',
58: dataIndex: 'startedAtUtc',
63: title: '组件',
64: dataIndex: 'component',
69: title: '操作',
70: dataIndex: 'operation',
74: <Tooltip title={text}>
80: title: '类型',
81: dataIndex: 'kind',
96: title: '状态',
97: dataIndex: 'status',
106: title: '耗时',
107: dataIndex: 'durationMs',
112: title: '摘要',
113: dataIndex: 'summary',
119: title: 'Session ID',
120: dataIndex: 'sessionId',
129: <PageContainer header={{ title: '运行时时间线' }}>
136: title: (
139: placeholder="Trace ID"
147: placeholder="Session ID"
155: placeholder="组件"
163: placeholder="状态"
169: { label: '成功', value: 'succeeded' },
170: { label: '失败', value: 'failed' },
171: { label: '运行中', value: 'running' },
172: { label: '已取消', value: 'cancelled' },
173: { label: '待处理', value: 'pending' },
```

## `pages/diagnostics/SubAgentRunsPage.tsx`

行为追踪：./api: getSubAgentRuns, getSubAgentRunDetail。

```tsx
75: title: 'Run ID',
76: dataIndex: 'runId',
86: title: '模板',
87: dataIndex: 'templateId',
92: title: '状态',
93: dataIndex: 'status',
102: title: '开始时间',
103: dataIndex: 'startedAt',
108: title: '耗时',
109: dataIndex: 'totalDurationMs',
114: title: '轮次',
115: dataIndex: 'totalRounds',
125: title: '工具调用',
126: dataIndex: 'totalToolCalls',
136: title: 'Session ID',
137: dataIndex: 'parentSessionId',
145: <PageContainer header={{ title: '子代理运行' }}>
152: title: (
155: placeholder="Parent Session ID"
163: placeholder="状态"
169: { label: '成功', value: 'succeeded' },
170: { label: '完成', value: 'completed' },
171: { label: '失败', value: 'failed' },
172: { label: '运行中', value: 'running' },
173: { label: '待处理', value: 'pending' },
174: { label: '已取消', value: 'cancelled' },
222: title="运行详情"
228: <Descriptions.Item label="Run ID">
231: <Descriptions.Item label="状态">
236: <Descriptions.Item label="耗时">{fmtDuration(detailData.summary.totalDurationMs)}</Descriptions.Item>
237: <Descriptions.Item label="父会话">
242: <Descriptions.Item label="子会话">
247: <Descriptions.Item label="工作区">{detailData.summary.workspaceId}</Descriptions.Item>
248: <Descriptions.Item label="模板">{detailData.summary.templateId}</Descriptions.Item>
249: <Descriptions.Item label="Agent 实例">{detailData.summary.agentInstanceId}</Descriptions.Item>
250: <Descriptions.Item label="轮次">{detailData.summary.totalRounds}</Descriptions.Item>
251: <Descriptions.Item label="开始时间">{fmtTime(detailData.summary.startedAt)}</Descriptions.Item>
252: <Descriptions.Item label="完成时间">
255: <Descriptions.Item label="事件数">{detailData.eventCount}</Descriptions.Item>
256: <Descriptions.Item label="工具调用">{detailData.toolCallCount}</Descriptions.Item>
258: <Descriptions.Item label="错误信息">
263: <Descriptions.Item label="任务" span={3}>
268: <Descriptions.Item label="输出" span={3}>
```

## `pages/global-agent-template/index.tsx`

行为追踪：@/services/platform/api: listGlobalAgentTemplates, createGlobalAgentTemplate, updateGlobalAgentTemplate, deleteGlobalAgentTemplate, listGlobalAgentTemplatePresets, importGlobalAgentTemplatePreset, listAgentAvatars, listLlmProviders, listLlmModels, listCapabilities, listSkillPackages, type AgentAvatarDto, type GlobalAgentTemplateDto, type UpsertGlobalAgentTemplateRequest, type LlmProviderDto, type LlmModelDto, type CapabilityDto, type SkillPackageDto,。

```tsx
71: { label: '服务型 (Service)', value: 'Service' },
72: { label: '任务型 (Task)', value: 'Task' },
73: { label: '审计型 (Audit)', value: 'Audit' },
74: { label: '自定义 (Custom)', value: 'Custom' },
365: title: '模板 ID',
366: dataIndex: 'templateId',
372: title: '名称',
373: dataIndex: 'name',
386: title: '角色类型',
387: dataIndex: 'role',
392: title: '能力',
407: title: '首选模型',
419: title: '执行护栏',
430: title: '运行环境',
441: title: '系统提示词',
454: title: '状态',
455: dataIndex: 'isEnabled',
461: title: '操作',
465: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
467: <Popconfirm title="系统内置模板不允许删除" showCancel={false} okText="知道了">
468: <Button size="small" danger icon={<DeleteOutlined />} disabled />
471: <Popconfirm title="确认删除该模板？" onConfirm={() => handleDelete(r.templateId)}>
472: <Button size="small" danger icon={<DeleteOutlined />} />
530: <Button size="small" icon={<EditOutlined />} type="text" onClick={() => openEdit(item)} />
532: <Popconfirm title="确认删除该模板？" onConfirm={() => handleDelete(item.templateId)}>
533: <Button size="small" danger icon={<DeleteOutlined />} type="text" />
572: <Button
602: <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>
605: <Button icon={<ImportOutlined />} onClick={openPresetDrawer}>
613: title="全局 Agent 模板"
644: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
647: <Button key="import" icon={<ImportOutlined />} onClick={openPresetDrawer}>
658: title={editItem ? '编辑 Agent 模板' : '创建 Agent 模板'}
685: title="导入系统预制模板"
```

## `pages/home/index.tsx`

行为追踪：@/services/platform/api: listWorkspaces, type WorkspaceWithPermDto,。

```tsx
96: label: '工作空间',
101: label: '可用空间',
106: label: '协作团队',
110: { label: 'Core 状态', value: '就绪', icon: <ThunderboltOutlined /> },
115: title: '开始对话',
121: title: '工作空间',
127: title: '模型服务',
133: title: '系统诊断',
160: <Button
168: <Button
178: <section className={styles.statusPanel} aria-label="Pudding 运行状态">
202: <section className={styles.statsGrid} aria-label="工作台概览">
222: <Button
247: <Button
256: <Skeleton active paragraph={{ rows: 4 }} title={false} />
```

## `pages/keyvault/index.tsx`

行为追踪：@/services/platform/api: createKeyVaultSecret, deleteKeyVaultSecret, listKeyVaultSecrets, updateKeyVaultSecret, type KeyVaultSecretDto,。

```tsx
60: const CATEGORY_META: Record<string, { label: string; color: string }> = {
61: general: { label: '通用', color: 'blue' },
62: api: { label: 'API Key', color: 'red' },
63: token: { label: 'Token', color: 'orange' },
68: label: meta.label,
129: title: `确认删除密钥「${record.name}」？`,
150: title: `轮换密钥「${record.name}」？`,
160: const placeholder = `{{vault:${record.name}}}`;
171: title: '名称',
172: dataIndex: 'name',
182: title: '描述',
183: dataIndex: 'description',
188: title: '分类',
189: dataIndex: 'category',
197: title: '密钥值',
203: <Tooltip title="安全设计：列表不返回明文。如需更换密钥值，请使用「轮换」（旧值将被替换）">
211: title: '占位符',
216: <Tooltip title="复制占位符">
217: <Button size="small" icon={<CopyOutlined />} onClick={() => handleCopyPlaceholder(record)} type="text" />
223: title: '标签',
236: title: '创建时间',
237: dataIndex: 'createdAt',
242: title: '操作',
246: <Tooltip title="编辑元数据">
247: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} type="text" />
249: <Tooltip title="轮换密钥（需确认）">
250: <Button
258: <Tooltip title="删除密钥（危险操作）">
259: <Button
274: title="密钥保管箱"
290: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
298: title={editing ? '编辑密钥' : '新建密钥'}
303: <Button type="primary" onClick={handleSubmit}>
314: name="name"
315: label="密钥名称"
320: placeholder="例如 openai-api-key"
324: name="description"
325: label="描述"
327: placeholder="可选，简要描述该密钥用途"
331: name="category"
332: label="分类"
339: name="tagsInput"
340: label="标签（逗号分隔）"
341: placeholder="例如 production, openai, billing"
345: name="value"
346: label="密钥值"
349: placeholder={editing ? '留空则保持原值不变' : '请输入密钥值（仅创建/更新时提交）'}
```

## `pages/llm-resource-pool/index.tsx`

行为追踪：@/services/platform/api: listLlmProviders, getLlmProvider, createLlmProvider, updateLlmProvider, deleteLlmProvider, updateLlmProviderQuota, resetDailyQuota, listLlmModels, createLlmModel, updateLlmModel, deleteLlmModel, type LlmProviderDto, type LlmProviderDetailDto, type LlmModelDto, type UpsertLlmProviderRequest, type UpsertLlmModelRequest, type UpdateQuotaRequest,。

```tsx
66: { label: 'Chat Completions', value: 'openai' },
67: { label: 'Responses API', value: 'responses' },
68: { label: 'Anthropic Messages', value: 'anthropic' },
78: { label: '文本', value: 'text' },
79: { label: '视觉', value: 'vision' },
80: { label: '函数调用', value: 'function-calling' },
81: { label: 'JSON 模式', value: 'json-mode' },
82: { label: '流式输出', value: 'streaming' },
83: { label: '长文本', value: 'long-context' },
84: { label: '代码', value: 'code' },
85: { label: '推理', value: 'reasoning' },
151: title: '模型 ID',
152: dataIndex: 'modelId',
165: title: '协议',
166: dataIndex: 'protocol',
173: { title: '名称', dataIndex: 'name', width: 160 },
175: title: '上下文长度',
176: dataIndex: 'maxContextTokens',
181: title: '输出长度',
182: dataIndex: 'maxOutputTokens',
187: title: '输入上限',
188: dataIndex: 'maxInputTokens',
195: title: '输入价格 (RMB/1M)',
196: dataIndex: 'inputPricePer1MTokens',
201: title: '输出价格 (RMB/1M)',
202: dataIndex: 'outputPricePer1MTokens',
207: title: '缓存命中价格 (RMB/1M)',
208: dataIndex: 'cacheHitPricePer1MTokens',
216: title: '能力标签',
217: dataIndex: 'capabilityTags',
227: title: '操作',
231: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
232: <Popconfirm title="确认删除该模型？" onConfirm={() => handleDelete(r)}>
233: <Button size="small" danger icon={<DeleteOutlined />} />
252: <Button key="add" type="primary" size="small" icon={<PlusOutlined />} onClick={openCreate}>
259: title={editModel ? '编辑模型' : '添加模型'}
264: <Button type="primary" onClick={handleSave}>
270: <ProFormText name="modelId" label="模型 ID" rules={[{ required: true }]}
271: disabled={!!editModel} placeholder="如 gpt-4o-mini" />
272: <ProFormText name="name" label="显示名称" rules={[{ required: true }]} />
274: name="protocol"
275: label="API 协议"
279: <ProFormTextArea name="description" label="描述" rows={3} />
280: <ProFormDigit name="maxContextTokens" label="上下文长度 (tokens)"
282: <ProFormDigit name="maxInputTokens" label="Provider 输入上限 (tokens)"
285: <ProFormDigit name="maxOutputTokens" label="输出长度 (tokens)"
287: <ProFormDigit name="inputPricePer1MTokens" label="输入价格 (RMB/1M tokens)"
289: <ProFormDigit name="outputPricePer1MTokens" label="输出价格 (RMB/1M tokens)"
291: <ProFormDigit name="cacheHitPricePer1MTokens" label="缓存命中价格 (RMB/1M tokens)"
294: placeholder="0" />
296: name="capabilityTags"
297: label="能力标签"
302: <ProFormSwitch name="isDefault" label="设为默认" />
303: <ProFormSwitch name="isDeprecated" label="标记已弃用" />
304: <ProFormSwitch name="isEmbedding" label="Embedding 模型" />
306: <ProFormDigit name="sortOrder" label="排序权重" min={0} />
307: <ProFormDigit name="maxConcurrentRequests" label="最大并发请求数"
310: placeholder="留空使用服务商默认值" />
417: title: '服务商',
418: dataIndex: 'name',
429: title: 'API 地址',
430: dataIndex: 'baseUrl',
435: title: 'API Key',
441: title: '状态',
442: dataIndex: 'isEnabled',
448: title: '操作',
452: <Button size="small" onClick={() => openDetail(r)}>
455: <Tooltip title="配置配额">
456: <Button size="small" icon={<BarChartOutlined />} onClick={() => openQuota(r.providerId)} />
458: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
459: <Popconfirm title="确认删除该服务商及其所有模型？" onConfirm={() => handleDelete(r.providerId)}>
460: <Button size="small" danger icon={<DeleteOutlined />} />
468: <PageContainer title="LLM 资源池" subTitle="管理 LLM 服务商、模型配置与 API 配额">
479: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
487: title={editProvider ? '编辑服务商' : '添加服务商'}
492: <Button type="primary" onClick={handleSaveProvider}>
499: <Form.Item label="服务商模板">
504: label: template.label,
507: placeholder="选择预设模板"
513: name="providerId"
514: label="服务商标识 (ID)"
517: placeholder="如 openai、deepseek、my-provider"
519: <ProFormText name="name" label="名称" rules={[{ required: true }]} />
520: <ProFormText name="baseUrl" label="API 基础地址" rules={[{ required: true }]}
521: placeholder="如 https://api.openai.com/v1" />
522: <Form.Item label="API Key" name="apiKey">
524: placeholder={editProvider ? '留空表示不修改现有 Key' : '输入 API Key'}
529: <ProFormTextArea name="description" label="描述" rows={3} />
531: name="maxConcurrentRequests"
532: label="最大并发请求数"
536: placeholder="留空使用默认值 (50)"
540: name="tokensPerMinute"
541: label="TPM（每分钟 Token 配额）"
546: name="requestsPerMinute"
547: label="RPM（每分钟请求配额）"
551: <ProFormSwitch name="isEnabled" label="启用" />
557: title={detailProvider ? `${detailProvider.name} — 模型管理` : '模型管理'}
567: label: '模型列表',
572: label: '配额状态',
579: title="今日已用 tokens"
588: title="本月已用 tokens"
615: <Button onClick={() => handleResetDaily(detailProvider.providerId)}>
618: <Button type="primary" onClick={() => openQuota(detailProvider.providerId)}>
636: title="配置配额限制"
644: name="dailyTokenLimit"
645: label="每日 Token 限额"
647: placeholder="留空表示不限制"
651: name="monthlyTokenLimit"
652: label="每月 Token 限额"
654: placeholder="留空表示不限制"
```

## `pages/memory-library/components/MemoryInspector.tsx`

```tsx
69: label: <span><InfoCircleOutlined /> 信息</span>,
76: <Descriptions.Item label="Book ID">
79: <Descriptions.Item label="Library ID">
82: <Descriptions.Item label="Workspace">
85: <Descriptions.Item label="Status">
88: <Descriptions.Item label="章节数">{book.chapters.length}</Descriptions.Item>
93: <Descriptions.Item label="Node ID">
96: <Descriptions.Item label="Node Type">
99: <Descriptions.Item label="Title">{nodeTitle}</Descriptions.Item>
107: label: <span><FileSearchOutlined /> 来源 {uniqueSources.length || ''}</span>,
126: label: <span><LinkOutlined /> 链接 {uniquePointers.length || ''}</span>,
```

## `pages/memory-library/components/MemoryPageEditor.tsx`

```tsx
39: values: { title: string; content: string; importance: number },
78: title: draftTitle.trim(),
137: <Button
146: <Popconfirm
147: title="归档此章节？"
150: <Button
151: aria-label={`归档章节 ${selectedChapter.title}`}
152: title="归档章节"
167: placeholder="章节标题"
174: placeholder="使用 Markdown 编写章节内容"
189: <Button
196: <Button
220: <Button type="primary" icon={<PlusOutlined />} onClick={onCreateChapter}>
```

## `pages/memory-library/components/MemoryPageTree.tsx`

```tsx
33: title: (
34: <span className="memory-tree-chapter-title" title={chapter.title}>
47: title: (
50: title={node.title}
```

## `pages/memory-library/components/MemorySearchResults.tsx`

```tsx
42: title={
```

## `pages/memory-library/index.tsx`

行为追踪：@/services/platform/api: listWorkspaces, listWorkspaceAgents, listAgentMemoryLibraries, ensureAgentDefaultMemoryLibrary, getAgentMemoryLibraryTree, getAgentMemoryBookPage, searchAgentMemoryLibrary, createAgentMemoryTreeNode, createAgentMemoryBook, updateAgentMemoryBook, createAgentMemoryChapter, updateAgentMemoryChapter, archiveAgentMemoryBook, archiveAgentMemoryChapter, listAgentMemorySources, listAgentMemoryPointers, type WorkspaceWithPermDto, type WorkspaceAgentDto,。

```tsx
342: title: values.title,
362: const result = await updateAgentMemoryBook(selectedWorkspaceId, selectedAgentId, bookPage.bookId, { title: values.title, summary: values.summary });
379: title: values.title,
398: values: { title: string; content: string; importance: number },
404: title: values.title,
448: label: w.name,
453: label: a.displayName || a.name,
472: aria-label="选择工作区"
473: placeholder="选择工作区"
480: aria-label="选择 Agent"
481: placeholder="选择 Agent"
490: aria-label="选择图书馆"
491: placeholder="选择图书馆"
494: options={libraries.map((l) => ({ label: l.name, value: l.libraryId }))}
500: placeholder="搜索当前 Agent 的记忆..."
510: <Tooltip title="刷新记忆树">
511: <Button
512: aria-label="刷新记忆树"
519: <Button onClick={handleEnsureDefaultLibrary} loading={editLoading}>
523: <Button icon={<PlusOutlined />} onClick={() => setNewPageModalOpen(true)} disabled={!selectedLibraryId}>
526: <Button type="primary" icon={<PlusOutlined />} onClick={() => setNewBookModalOpen(true)} disabled={!selectedLibraryId}>
529: <Tooltip title={inspectorOpen ? '关闭页面详情' : '打开页面详情'}>
530: <Button
531: aria-label={inspectorOpen ? '关闭页面详情' : '打开页面详情'}
541: title={selectionLabel ?? '未选中节点'}
587: <Button size="small" icon={<EditOutlined />} onClick={() => {
588: editBookForm.setFieldsValue({ title: bookPage.title, summary: bookPage.summary });
591: <Button size="small" icon={<PlusOutlined />} onClick={() => setNewChapterModalOpen(true)}>
594: <Popconfirm title="归档后将不可见，确认归档？" onConfirm={handleArchiveBook}>
595: <Button size="small" danger icon={<DeleteOutlined />}>归档 Book</Button>
621: title="页面详情"
640: label: p.targetLabel,
648: title="新建 Page"
655: <Form.Item name="name" label="名称" rules={[{ required: true }]}>
656: <Input placeholder="页面名称" />
658: <Form.Item name="summary" label="摘要">
659: <Input.TextArea rows={3} placeholder="页面摘要" />
661: <Form.Item name="nodeType" label="类型" initialValue="category">
663: { label: '分类', value: 'category' },
664: { label: '主题', value: 'topic' },
665: { label: '系统', value: 'system' },
666: { label: '书架', value: 'shelf' },
673: title="新建 Book"
680: <Form.Item name="title" label="标题" rules={[{ required: true }]}>
681: <Input placeholder="Book 标题" />
683: <Form.Item name="summary" label="摘要">
684: <Input.TextArea rows={3} placeholder="Book 摘要" />
690: title="编辑 Book 信息"
697: <Form.Item name="title" label="标题" rules={[{ required: true }]}>
698: <Input placeholder="Book 标题" />
700: <Form.Item name="summary" label="摘要">
701: <Input.TextArea rows={3} placeholder="Book 摘要" />
707: title="添加章节"
714: <Form.Item name="title" label="章节标题" rules={[{ required: true }]}>
715: <Input placeholder="章节标题" />
717: <Form.Item name="content" label="内容" rules={[{ required: true }]}>
718: <Input.TextArea rows={6} placeholder="章节内容" />
720: <Form.Item name="importance" label="重要性 (0-1)" initialValue={0.5}>
```

## `pages/orchestration/componentUiRegistry.tsx`

行为追踪：./api: getVisionArtifactUrl。

```tsx
154: title={result ?? data.outputSummary}
```

## `pages/orchestration/EdgeInspector.tsx`

```tsx
109: <Form.Item label="evaluatorId（必填）" style={{ marginBottom: 8 }}>
113: placeholder="例如 pudding.schema.gate"
117: <Form.Item label="version（必填）" style={{ marginBottom: 8 }}>
121: placeholder="例如 1"
125: <Form.Item label="contractHash（可选）" style={{ marginBottom: 8 }}>
129: placeholder="由注册表冻结，可留空"
137: <Form.Item label="sourcePortId（必填）" style={{ marginBottom: 8 }}>
141: placeholder="上游输出端口 id"
147: <Form.Item label="sourcePath（必填）" style={{ marginBottom: 8 }}>
151: placeholder="$"
157: <Form.Item label="parameters（JSON 对象）" style={{ marginBottom: 8 }}>
186: <Button
250: <Button
264: <Form.Item label="sourcePortId" style={{ marginBottom: 8 }}>
273: <Form.Item label="sourcePath" style={{ marginBottom: 8 }}>
282: <Form.Item label="targetPortId" style={{ marginBottom: 8 }}>
291: <Form.Item label="targetKey（可选）" style={{ marginBottom: 8 }}>
302: <Form.Item label="聚合" style={{ marginBottom: 8 }}>
307: { value: 'replace', label: 'replace（单值覆盖）' },
308: { value: 'append', label: 'append（追加到多值）' },
316: <Button
332: <Descriptions.Item label="解析后的源契约">
337: <Descriptions.Item label="解析后的目标契约">
342: <Descriptions.Item label="示例值预览">
383: <Descriptions.Item label="源节点">{edge.fromNodeId}</Descriptions.Item>
384: <Descriptions.Item label="目标节点">{edge.toNodeId}</Descriptions.Item>
387: <Form.Item label="触发条件" style={{ marginBottom: 10 }}>
392: { value: 'onSuccess', label: '上游成功（onSuccess）' },
393: { value: 'onCompletion', label: '上游完成（onCompletion）' },
394: { value: 'always', label: '始终（always）' },
415: <Button
477: <Button
```

## `pages/orchestration/GraphInputsNode.tsx`

本组件使用动态定义/子组件；请沿 imports 和 render 返回继续核对。

## `pages/orchestration/GraphInputsPanel.tsx`

```tsx
133: title: `删除 Graph Input ${input.inputId}？`,
152: title={`Graph Inputs（${definition.inputs?.length ?? 0}）`}
154: <Button size="small" disabled={disabled} onClick={() => openEditor()}>
204: <Button
212: <Button
230: title={
243: name="inputId"
244: label="Input ID"
256: name="dataType"
257: label="Data Type"
260: <Input placeholder="pudding.content" />
262: <Form.Item name="mediaTypes" label="MIME（逗号分隔）">
263: <Input placeholder="text/plain, image/png" />
267: name="cardinality"
268: label="Cardinality"
277: name="deliveries"
278: label="Delivery"
290: <Form.Item name="requiredAtActivation" valuePropName="checked">
294: name="defaultValue"
295: label="Default Value（ValueEnvelope JSON，可选）"
310: placeholder={'{\n  "dataType": "pudding.content",\n  "contentType": "text/plain",\n  "inlineValue": "..."\n}'}
```

## `pages/orchestration/HttpHookPanel.tsx`

```tsx
94: title={`HTTP Hooks（${hooks.length}）`}
96: <Button size="small" disabled={disabled} onClick={openCreate}>
155: <Button
163: title: `删除 HTTP Hook ${hook.triggerId}？`,
180: title={
235: title="新增 HTTP Hook"
247: name="triggerId"
248: label="Trigger ID"
263: <Form.Item name="targetInputId" label="映射到 Graph Input（可选）">
266: placeholder="无输入映射"
269: label: `${input.inputId} · ${input.contract.dataType}`,
282: name="sourcePath"
283: label="Payload 路径"
287: <Input placeholder="$.message" />
```

## `pages/orchestration/ImageGenerateNodeSettings.tsx`

```tsx
25: <Form.Item label="模式" style={{ flex: 1, marginBottom: 8 }}>
33: { value: 'default', label: '默认' },
34: { value: 'precision', label: '精准编辑' },
35: { value: 'sequence', label: '序列图' },
39: <Form.Item label="尺寸" style={{ flex: 1, marginBottom: 8 }}>
46: label: value,
51: <Form.Item label="Provider（可选）" style={{ marginBottom: 8 }}>
56: placeholder="留空使用图片服务默认路由"
60: <Form.Item label="Model（可选）" style={{ marginBottom: 8 }}>
65: placeholder="留空使用图片服务默认模型"
69: <Form.Item label="水印" style={{ marginBottom: 0 }}>
```

## `pages/orchestration/index.tsx`

行为追踪：./api: createOrchestrationGraph, deleteOrchestrationGraph, getLatestOrchestrationRevision, getOrchestrationCatalog, getOrchestrationEvents, getOrchestrationLayout, getOrchestrationRevision, getOrchestrationRun, listOrchestrationGraphs, listOrchestrationRuns, putOrchestrationLayout, putOrchestrationRevision, startOrchestrationRun, validateOrchestrationDraft, watchOrchestrationRun,。

```tsx
158: { color: string; label: string }
160: draft: { color: 'default', label: '草稿' },
161: active: { color: 'processing', label: '运行中' },
162: awaitingInput: { color: 'warning', label: '等待输入' },
163: completed: { color: 'success', label: '已完成' },
164: failed: { color: 'error', label: '失败' },
165: cancelled: { color: 'default', label: '已取消' },
170: { color: string; label: string }
172: pending: { color: 'default', label: '等待' },
173: ready: { color: 'blue', label: '就绪' },
174: claimed: { color: 'purple', label: '已认领' },
175: running: { color: 'cyan', label: '运行中' },
176: awaitingInput: { color: 'gold', label: '等待输入' },
177: completed: { color: 'green', label: '完成' },
178: failed: { color: 'red', label: '失败' },
179: skipped: { color: 'default', label: '跳过' },
180: cancelled: { color: 'default', label: '取消' },
1022: title: nodeEditTitle.trim(),
1244: title: 'Agent 编排',
1267: placeholder="工作区（全部）"
1277: placeholder="选择 Graph"
1282: label: `${graph.objective} · r${graph.currentRevision} · ${graph.runCount} runs`,
1294: placeholder={
1305: label: `${item.runId} · ${runStatusMeta[item.status].label} · #${item.headSequence}`,
1311: <Button
1324: <Button size="small" onClick={() => setRunIdModalOpen(true)}>
1327: <Button
1334: <Tooltip title={graphDeletionBlocker}>
1336: <Button
1344: title: `删除 Graph ${selectedGraphSummary.graphId}？`,
1400: <Button
1448: <Button
1454: title: '放弃本地草稿并加载最新 Revision？',
1465: <Button
1501: title={
1553: title={
1586: title={
1593: <Button
1604: <Button
1611: <Button
1621: title={
1628: <Button
1639: <Button
1652: <Button
1663: <Button
1676: <Button
1687: <Button
1722: <Button
1730: <Button
1738: <Button
1744: title: '放弃本地草稿？',
1842: <Button
1851: title={selectedEdgeDefinition ? '连线检查器' : '节点检查器'}
1884: <Descriptions.Item label="节点">
1887: <Descriptions.Item label="目标">
1890: <Descriptions.Item label="尝试">
1893: <Descriptions.Item label="精确路由">
1896: <Descriptions.Item label="Execution Run">
1899: <Descriptions.Item label="Sub-session">
1902: <Descriptions.Item label="更新时间">
1928: <Form.Item label="标题" style={{ marginBottom: 8 }}>
1937: <Form.Item label="目标" style={{ marginBottom: 8 }}>
1948: <Button
1956: <Button
1962: title: `删除节点 ${selectedNodeDefinition.nodeId}？`,
2054: <Card title={`运行事件（最近 ${events.length} 条）`}>
2119: title="按 Run ID 打开运行图"
2137: name="runId"
2138: label="Run ID"
2150: placeholder="高级入口；通常直接使用 Graph/Run 选择器"
2157: title="新建 Graph"
2179: name="templateId"
2180: label="模板"
2187: label: '图片生成（Prompt → 生成图片 → 展示图片）',
2189: { value: 'blank', label: '空白（Human Input 占位）' },
2194: name="graphId"
2195: label="Graph ID"
2209: name="workspaceId"
2210: label="工作区 ID"
2224: name="rootSessionId"
2225: label="Root Session ID"
2239: name="objective"
2240: label="目标"
2249: <Input.TextArea rows={3} placeholder="描述这个编排要完成的目标" />
2252: name="maxConcurrency"
2253: label="最大并发"
2267: title="从组件目录新增节点"
2300: placeholder="搜索组件类型或名称"
2304: label: `${component.descriptor.displayName} · ${component.descriptor.componentType}@${component.descriptor.version}`,
2321: <Descriptions.Item label="类型">
2324: <Descriptions.Item label="版本">
2327: <Descriptions.Item label="分类">
2330: <Descriptions.Item label="Node Kind">
2333: <Descriptions.Item label="副作用">
2336: <Descriptions.Item label="Contract Hash">
2347: title="保留草稿并查看差异"
2350: <Button
2368: <Descriptions.Item label="目标变化">
2371: <Descriptions.Item label="新增节点">
2376: <Descriptions.Item label="删除节点">
2381: <Descriptions.Item label="新增边">
2386: <Descriptions.Item label="删除边">
2391: <Descriptions.Item label="新增 Graph Input">
2396: <Descriptions.Item label="删除 Graph Input">
2401: <Descriptions.Item label="新增 Trigger">
2406: <Descriptions.Item label="删除 Trigger">
```

## `pages/orchestration/ManualRunModal.tsx`

```tsx
44: title="运行编排"
82: const label = (
92: name={input.inputId}
93: label={label}
105: name={input.inputId}
106: label={label}
116: name={input.inputId}
117: label={label}
128: placeholder={
```

## `pages/orchestration/NodeGraphInputBindings.tsx`

```tsx
73: label={
98: placeholder={
105: label: `${input.inputId} · ${input.contract.mediaTypes.join(', ') || '*'}`,
```

## `pages/orchestration/OrchestrationComponentNode.tsx`

本组件使用动态定义/子组件；请沿 imports 和 render 返回继续核对。

## `pages/orchestration/SubAgentNodeSettings.tsx`

行为追踪：@/services/platform/api: listGlobalAgentTemplates, listLlmModels, listLlmProviders, listWorkspaceAgentTemplates,。

```tsx
25: label: string;
69: label: `${provider.name} / ${model.name} · ${model.protocol}`,
86: label: `${template.name} · ${template.role}`,
121: { value: executor.routeKey, label: `${executor.routeKey} · 当前值` },
140: <Form.Item label="角色" required style={{ marginBottom: 8 }}>
144: placeholder="例如 copy-planner / storyboard-director"
148: <Form.Item label="Agent 模板" required style={{ marginBottom: 8 }}>
153: placeholder="选择或输入 Template ID"
163: label="精确模型路由"
172: placeholder="provider/model"
```

## `pages/role-management/index.tsx`

行为追踪：@/services/platform/api: listRoles, createRole, updateRole, deleteRole, type AppRoleDto, type UpsertRoleRequest,。

```tsx
39: { label: '查看 Workspace', value: 'workspace:read' },
40: { label: '使用 Workspace（写入）', value: 'workspace:write' },
41: { label: '管理 Workspace', value: 'workspace:manage' },
47: { label: '查看团队', value: 'team:read' },
48: { label: '管理团队', value: 'team:manage' },
54: { label: '查看用户', value: 'user:read' },
55: { label: '管理用户', value: 'user:manage' },
61: { label: '运行 Agent', value: 'agent:run' },
62: { label: '管理 Agent', value: 'agent:manage' },
68: { label: '查看模板', value: 'template:read' },
69: { label: '管理模板', value: 'template:manage' },
75: { label: '查看 LLM 资源', value: 'llm:read' },
76: { label: '管理 LLM 资源', value: 'llm:manage' },
91: { title: 'RoleId', dataIndex: 'roleId', width: 160, copyable: true },
92: { title: '名称', dataIndex: 'name', width: 160 },
93: { title: '描述', dataIndex: 'description', ellipsis: true },
95: title: '权限',
96: dataIndex: 'permissions',
103: title: '系统内置',
104: dataIndex: 'isSystemRole',
109: title: '操作',
114: <Button
122: <Popconfirm
123: title={`确认删除角色 "${r.name}"？`}
129: <Button
192: <PageContainer title="权限角色" subTitle="管理平台角色与权限配置">
202: <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
211: title={editingRole ? `编辑角色：${editingRole.name}` : '新建角色'}
217: <Button onClick={() => setDrawerOpen(false)}>取消</Button>
218: <Button type="primary" loading={drawerLoading} onClick={handleSave}>
227: name="roleId"
228: label="RoleId（英文唯一标识）"
230: placeholder="e.g. workspace-viewer"
233: <ProFormText name="name" label="角色名称" rules={[{ required: true }]} />
234: <ProFormTextArea name="description" label="描述" rows={2} />
```

## `pages/runtime-management/index.tsx`

行为追踪：@/services/platform/api: freezeRuntimeNode, getRuntimeNodeCapabilities, listRuntimeNodes, unfreezeRuntimeNode, type NativeCapabilityDescriptor, type RuntimeNodeInfo, type RuntimeNodeStatus,。

```tsx
44: { badge: 'success' | 'processing' | 'error' | 'warning' | 'default'; label: string }
46: Online: { badge: 'success', label: '在线' },
47: Offline: { badge: 'error', label: '离线' },
48: Degraded: { badge: 'warning', label: '降级' },
255: title: 'Node ID',
256: dataIndex: 'nodeId',
265: title: '端点 / IP',
266: dataIndex: 'endpoint',
278: title: '连接状态',
279: dataIndex: 'status',
296: title: '活跃会话',
297: dataIndex: 'activeSessionCount',
307: title: '节点模式',
308: dataIndex: 'embeddedMode',
318: title: '宿主类型',
319: dataIndex: 'hostType',
329: title: '最近心跳',
330: dataIndex: 'lastHeartbeat',
333: <Tooltip title={new Date(record.lastHeartbeat).toLocaleString('zh-CN')}>
341: title: '操作',
349: <Popconfirm
351: title="确认解冻此节点？"
363: title="冻结后该嵌入式节点将拒绝所有原生能力调用，会话将被强制断开"
379: { title: '能力 ID', dataIndex: 'capabilityId', width: 180, render: (v: string) => <Text code>{v}</Text> },
380: { title: '名称', dataIndex: 'name', width: 140 },
382: title: '分类',
383: dataIndex: 'category',
390: title: '需审批',
391: dataIndex: 'requiresApproval',
397: { title: '说明', dataIndex: 'description', ellipsis: true },
404: title: 'Runtime 节点管理',
408: <Button
428: <Statistic title="总节点数" value={nodes.length} prefix={<CloudServerOutlined />} />
431: <Statistic title="在线节点" value={onlineCount} valueStyle={{ color: 'var(--success-signal, #22C55E)' }} />
435: title="降级 / 离线"
441: <Statistic title="活跃会话总数" value={totalSessions} />
550: title: (
566: title={
584: <Descriptions.Item label="Node ID">
587: <Descriptions.Item label="端点">
590: <Descriptions.Item label="IP / 主机名">
593: <Descriptions.Item label="连接状态">
599: <Descriptions.Item label="冻结状态">
606: <Descriptions.Item label="节点模式">
614: <Descriptions.Item label="宿主类型">
618: <Descriptions.Item label="活跃会话">
621: <Descriptions.Item label="最近心跳">
622: <Tooltip title={new Date(detailNode.lastHeartbeat).toLocaleString('zh-CN')}>
651: title={
669: placeholder="请填写冻结原因（必填）"
```

## `pages/session/index.tsx`

行为追踪：@/services/platform/api: listSessions, listWorkspaces, type SessionRecord, type SessionStatus, type WorkspaceWithPermDto。

```tsx
26: const statusConfig: Record<SessionStatus, { badge: 'success' | 'processing' | 'default' | 'error' | 'warning'; label: string }> = {
27: Active: { badge: 'processing', label: '活跃' },
28: Idle: { badge: 'warning', label: '空闲' },
29: Completed: { badge: 'success', label: '已完成' },
30: Failed: { badge: 'error', label: '失败' },
31: Frozen: { badge: 'default', label: '已冻结' },
69: const [workspaceOptions, setWorkspaceOptions] = useState<{ label: string; value: string }[]>([]);
73: setWorkspaceOptions(ws.map((w) => ({ label: w.name, value: w.workspaceId })));
80: title: 'Session ID',
81: dataIndex: 'sessionId',
90: title: '场景',
91: dataIndex: 'workspaceId',
96: title: 'Agent 模板',
97: dataIndex: 'agentTemplateId',
102: title: '渠道',
103: dataIndex: 'channelId',
108: title: '类型',
109: dataIndex: 'sessionType',
114: title: '状态',
115: dataIndex: 'status',
118: const cfg = statusConfig[record.status] ?? { badge: 'default', label: record.status };
123: title: '用户',
124: dataIndex: 'ownerUserId',
129: title: '创建时间',
130: dataIndex: 'createdAt',
136: title: '最近活跃',
137: dataIndex: 'lastActiveAt',
148: placeholder="筛选场景"
156: placeholder="筛选 Agent"
164: placeholder="筛选状态"
170: { label: '活跃', value: 'Active' },
171: { label: '空闲', value: 'Idle' },
172: { label: '已完成', value: 'Completed' },
173: { label: '失败', value: 'Failed' },
174: { label: '已冻结', value: 'Frozen' },
193: title: '会话记录',
249: const cfg = statusConfig[session.status] ?? { badge: 'default' as const, label: session.status };
```

## `pages/skill-management/EventsTab.tsx`

行为追踪：@/services/platform/api: listHubEvents。

```tsx
11: const LIMIT_OPTIONS = [50, 100, 200, 500].map((n) => ({ value: n, label: `${n} 条` }));
55: title: '事件类型',
56: dataIndex: 'eventType',
65: title: '技能',
66: dataIndex: 'skillId',
74: title: '版本',
75: dataIndex: 'version',
80: title: '操作者',
86: title: '工作区',
87: dataIndex: 'workspaceId',
93: title: 'payload',
94: dataIndex: 'payloadJson',
106: title: '时间',
107: dataIndex: 'createdAt',
118: placeholder="按技能 ID 过滤事件"
125: <Button icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
```

## `pages/skill-management/EvoMapTab.tsx`

行为追踪：@/services/platform/api: getHubLineage, getHubSkillVersion, listHubSkills。

```tsx
105: title: '技能',
106: dataIndex: 'name',
116: title: '最新',
117: dataIndex: 'latestVersion',
139: <Button
155: <Card size="small" title={`选择技能（已选 ${selectedIds.length}）`}>
173: <Card size="small" title="进化图谱">
201: title={node ? node.nodeId : '节点详情'}
```

## `pages/skill-management/index.tsx`

```tsx
19: { key: 'overview', label: '概览', children: <OverviewTab /> },
20: { key: 'skills', label: '技能库', children: <SkillsTab /> },
21: { key: 'evomap', label: 'EVO MAP', children: <EvoMapTab /> },
22: { key: 'installs', label: '安装台账', children: <InstallsTab /> },
23: { key: 'events', label: '事件审计', children: <EventsTab /> },
26: label: '技能包（旧）',
40: title="SKILL Hub 管理台"
```

## `pages/skill-management/InstallsTab.tsx`

行为追踪：@/services/platform/api: listHubInstalls, listHubSkills。

```tsx
35: title: '技能',
36: dataIndex: 'skillId',
43: { title: 'Agent 实例', dataIndex: 'agentInstanceId', ellipsis: true },
45: title: '工作区',
46: dataIndex: 'workspaceId',
50: title: '已装版本',
51: dataIndex: 'installedVersion',
56: title: '是否落后最新',
70: title: '安装人',
71: dataIndex: 'installedBy',
76: title: '安装时间',
77: dataIndex: 'installedAt',
120: placeholder="按 Agent 实例 ID 过滤"
129: <Button
137: <Button key="checkUpdates" icon={<SearchOutlined />} onClick={() => setUpdatesOpen(true)}>
140: <Button key="updateAll" icon={<ThunderboltOutlined />} onClick={handleUpdateAllHint}>
143: <Button key="reload" onClick={() => tableRef.current?.reload()}>
```

## `pages/skill-management/LegacySkillPackages.tsx`

行为追踪：@/services/platform/api: listSkillPackages, createSkillPackage, updateSkillPackage, updateSkillPackageFile, deleteSkillPackage, getSkillPackageDownloadUrl, type SkillPackageDto, type UpdateSkillPackageRequest,。

```tsx
73: const name = fileName.toLowerCase();
200: title: 'SKILL ID',
201: dataIndex: 'skillPackageId',
207: title: '名称',
208: dataIndex: 'name',
212: title: '版本',
213: dataIndex: 'version',
218: title: '描述',
223: title: '文件',
233: title: '状态',
239: title: '操作',
243: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} title="编辑信息" />
244: <Button size="small" icon={<UploadOutlined />} onClick={() => openUpdateFile(r)} title="更新文件" />
245: <Button size="small" icon={<DownloadOutlined />} onClick={() => handleDownload(r.skillPackageId)} title="下载" />
246: <Popconfirm title="确认删除该 SKILL 包？" onConfirm={() => handleDelete(r.skillPackageId)}>
247: <Button size="small" danger icon={<DeleteOutlined />} />
287: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
339: <Button size="small" icon={<EditOutlined />} type="text" onClick={() => openEdit(item)} title="编辑信息" />
340: <Button size="small" icon={<DownloadOutlined />} type="text" onClick={() => handleDownload(item.skillPackageId)} title="下载" />
341: <Popconfirm title="确认删除该 SKILL 包？" onConfirm={() => handleDelete(item.skillPackageId)}>
342: <Button size="small" danger icon={<DeleteOutlined />} type="text" />
382: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
391: title={editItem ? '编辑 SKILL 信息' : '上传新 SKILL'}
396: <Button type="primary" onClick={handleSave}>
403: name="skillPackageId"
404: label="SKILL ID"
410: placeholder="如 my-python-utils"
413: name="name"
414: label="名称"
416: placeholder="如 Python 工具集"
418: <ProFormTextArea name="description" label="用途描述" rows={3} placeholder="简要说明该 SKILL 的功能，将注入到 Agent 提示词中" />
421: name="version"
422: label="版本"
424: placeholder="如 1.0.0"
428: <ProFormDigit name="sortOrder" label="排序权重" min={0} />
429: <ProFormSwitch name="isEnabled" label="启用" />
433: <Form.Item label="SKILL 包文件" required>
440: const name = file.name.toLowerCase();
450: <Button icon={<UploadOutlined />}>选择文件</Button>
459: title={`更新文件 — ${fileDrawerItem?.name ?? ''}`}
464: <Button type="primary" onClick={handleUpdateFile}>
481: placeholder="如 1.1.0"
490: const name = file.name.toLowerCase();
500: <Button icon={<UploadOutlined />}>选择新文件</Button>
```

## `pages/skill-management/OverviewTab.tsx`

行为追踪：@/services/platform/api: getSkillHubStats。

```tsx
37: const metrics: { title: string; value: number | undefined }[] = [
38: { title: '技能总数', value: stats?.totalSkills },
39: { title: '活跃', value: stats?.activeSkills },
40: { title: '已退役', value: stats?.retiredSkills },
41: { title: '版本总数', value: stats?.totalVersions },
42: { title: '安装总数', value: stats?.totalInstalls },
43: { title: '覆盖 Agent 数', value: stats?.distinctAgents },
44: { title: '已进化技能数', value: stats?.evolvedSkills },
54: title: '技能',
55: dataIndex: 'name',
63: { title: '最新版本', dataIndex: 'latestVersion', width: 100 },
65: title: '状态',
66: dataIndex: 'status',
70: { title: '装机 Agent 数', dataIndex: 'installCount', width: 120 },
77: title="核心指标"
79: <Button size="small" icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
88: <Statistic title={m.title} value={m.value ?? 0} />
99: <Card size="small" title="进化动作分布" style={{ marginTop: 16 }}>
126: <Card size="small" title="Top 安装榜" style={{ marginTop: 16 }}>
```

## `pages/skill-management/skillHubShared.tsx`

```tsx
131: title: React.ReactNode;
190: title: evoNodeTitle(n),
```

## `pages/skill-management/SkillsTab.tsx`

行为追踪：@/services/platform/api: getHubSkill, getHubSkillLineage, getHubSkillVersion, listHubSkills, retireHubSkill, updateHubSkillMeta,。

```tsx
35: { value: 'active', label: '活跃' },
36: { value: 'deprecated', label: '已停用' },
37: { value: 'retired', label: '已退役' },
107: .map((t) => ({ value: t, label: t }));
184: title: `确认退役「${item.name}」？`,
207: <Button size="small" type="text" icon={<EyeOutlined />} onClick={() => void openDetail(item)}>
210: <Button
218: <Button
228: <Button size="small" type="text" onClick={() => void toggleDeprecated(item)}>
231: <Button size="small" type="text" danger onClick={() => retire(item)}>
243: placeholder="搜索名称 / 摘要 / 关键词"
251: placeholder="按标签过滤"
259: placeholder="按状态过滤"
279: <Button size="small" icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
282: <Button
295: title: '技能',
296: dataIndex: 'name',
307: title: '标签',
308: dataIndex: 'tags',
324: title: '最新版本',
325: dataIndex: 'latestVersion',
329: { title: '状态', dataIndex: 'status', width: 90, render: (s: string) => renderSkillStatusTag(s) },
330: { title: '版本数', dataIndex: 'versionCount', width: 80 },
331: { title: '装机数', dataIndex: 'installCount', width: 80 },
333: title: '更新时间',
334: dataIndex: 'updatedAt',
338: { title: '操作', key: 'actions', width: 320, render: (_: unknown, r: HubSkillSummaryDto) => actionButtons(r) },
363: title={
409: title={detail ? `技能详情 — ${detail.skill.name}` : '技能详情'}
415: <Button
522: title={versionTitle}
536: <Button
547: title={
579: title={`SKILL.md — v${versionFull.version}`}
599: <Drawer title={lineageTitle} open={lineageOpen} width={720} onClose={() => setLineageOpen(false)}>
```

## `pages/skill-management/SkillWriteModals.tsx`

行为追踪：@/services/platform/api: listHubUpdates, publishHubSkill, publishHubSkillVersion, registerHubInstall,。

```tsx
105: title="发布技能到 SKILL Hub"
123: <Form.Item name="skillId" label="Skill ID" rules={SKILL_ID_RULES}>
124: <Input placeholder="如 my-skill（小写 + 连字符）" />
126: <Form.Item name="name" label="名称" rules={[{ required: true, message: '请填写名称' }]}>
127: <Input placeholder="展示用名称" />
130: name="version"
131: label="版本号"
135: <Input placeholder="1.0.0" />
137: <Form.Item name="summary" label="摘要">
138: <Input placeholder="一句话说明（可空）" />
140: <Form.Item name="tags" label="标签" tooltip="逗号分隔，如 a,b,c">
141: <Input placeholder="逗号分隔（可空）" />
144: name="skillMarkdown"
145: label="SKILL.md 内容"
150: placeholder="粘贴 SKILL.md 全文；服务端按内容计算 ContentHash（SHA256 前 16 字节）"
225: title={`发布新版本 — ${skillName || skillId}${latestVersion ? `（当前 v${latestVersion}）` : ''}`}
244: name="version"
245: label="新版本号"
249: <Input placeholder="如 1.1.0" />
252: name="evolutionAction"
253: label="进化动作"
259: label: `${EVO_ACTION_TEXT[a] ?? a}（${a}）`,
263: <Form.Item name="parentVersion" label="父版本" tooltip="构成血缘边；留空表示无父版本">
264: <Input placeholder={latestVersion || '如 1.0.0'} />
267: name="skillMarkdown"
268: label="SKILL.md 内容"
271: <TextArea rows={10} placeholder="粘贴本版本的 SKILL.md 全文" />
273: <Form.Item name="publishNote" label="发布说明">
274: <Input placeholder="本次变更说明（可空）" />
327: title="登记安装台账"
345: <Form.Item name="skillId" label="Skill ID" rules={SKILL_ID_RULES}>
346: <Input placeholder="必须是 Hub 中已存在的技能，如 ppt-master" />
349: name="agentInstanceId"
350: label="Agent 实例 ID"
353: <Input placeholder="如 8fd0f96f82224bc0a282b4626ea4e4f5-sub-xxxx" />
356: name="installedVersion"
357: label="已安装版本"
360: <Input placeholder="如 6.6.0" />
362: <Form.Item name="contentHash" label="ContentHash" tooltip="可空：留空表示未提供本地内容指纹">
363: <Input placeholder="SHA256 前 16 字节 hex（可空）" />
374: title: '技能',
375: dataIndex: 'skillId',
386: title: '已装版本',
387: dataIndex: 'installedVersion',
392: title: '最新版本',
393: dataIndex: 'latestVersion',
398: title: '最新动作',
399: dataIndex: 'latestEvolutionAction',
404: title: '发布时间',
405: dataIndex: 'latestPublishedAt',
410: title: '发布说明',
411: dataIndex: 'publishNote',
449: title="检查待更新技能（GET /api/skill-hub/updates）"
453: <Button type="primary" onClick={onClose}>
462: placeholder="Agent 实例 ID"
468: <Button type="primary" icon={<SearchOutlined />} loading={loading} onClick={() => void query()}>
```

## `pages/stats/tokens/index.tsx`

行为追踪：@/services/platform/api: getContextLayerTokenStats, getMonthlyTokenStats, getTokenStatsSeries, rebuildTokenEvents, type ContextLayerTokenStatsLayer, type MonthlyTokenStatsResponse, type TokenSeriesPoint, type TokenStatsSeriesResponse,。

```tsx
184: const formatUnknownLabel = (value: string, label: string) =>
188: { key: 'cacheMissTokens', label: '输入（未命中缓存）', color: '#3b82f6' },
189: { key: 'cacheHitTokens', label: '输入（命中缓存）', color: '#8fd3f4' },
190: { key: 'completionTokens', label: '输出', color: '#1677ff' },
194: tokenItems: Array<{ label: string; value: string; color: string }>;
195: costItems: Array<{ label: string; value: string; color: string }>;
197: const renderSection = (title: string, items: Array<{ label: string; value: string; color: string }>) => (
237: title="消耗构成"
271: title: string;
296: title={title}
327: aria-label={`${title} Token 图表`}
416: aria-label={`${point.period} Token 详情`}
511: { label: '全部 Provider', value: ALL_PROVIDER_VALUE },
513: label: formatUnknownLabel(providerId, '未知 Provider'),
525: { label: '全部模型', value: ALL_MODEL_VALUE },
527: label: formatUnknownLabel(modelId, '未知模型'),
592: { title: 'Provider', dataIndex: 'providerId', key: 'providerId', width: 120 },
593: { title: '模型', dataIndex: 'modelId', key: 'modelId', width: 160 },
595: title: 'Prompt Tokens',
596: dataIndex: 'promptTokens',
602: title: '缓存命中',
603: dataIndex: 'cacheHitTokens',
609: title: '缓存未命中',
610: dataIndex: 'cacheMissTokens',
616: title: '缓存命中率',
617: dataIndex: 'cacheHitRate',
627: title: 'Completion Tokens',
628: dataIndex: 'completionTokens',
634: title: '请求次数',
635: dataIndex: 'requestCount',
640: title: '费用构成 (RMB)',
652: title: '总费用 (RMB)',
653: dataIndex: 'totalCost',
667: title: '层',
668: dataIndex: 'layerName',
681: title: '职责',
682: dataIndex: 'layerRole',
688: title: '影响',
689: dataIndex: 'impactScore',
695: title: 'Token 压力',
696: dataIndex: 'tokenCount',
709: title: '缓存表现',
710: dataIndex: 'cacheHitRate',
743: title: '变化',
744: dataIndex: 'changeRate',
757: title: '主要原因',
758: dataIndex: 'changeReasons',
781: title="Token 消耗统计"
807: <Button
833: title="Token 消耗"
843: title="缓存命中率"
859: title="总费用"
869: title="已计量请求"
881: label: '命中输入',
886: label: '未命中输入',
891: label: '输出',
898: label: '命中输入',
903: label: '未命中输入',
908: label: '输出',
918: title="按月趋势"
925: title="按日趋势"
```

## `pages/storage/CleanupPreviewModal.tsx`

行为追踪：./api: createCleanupJob, formatBytes, formatCount, formatUtc。

```tsx
40: title: '数据类型',
41: dataIndex: 'displayName',
45: title: '动作',
46: dataIndex: 'actionSummary',
49: title: '约候选量',
50: dataIndex: 'estimatedCandidateRows',
61: title: '约占用',
62: dataIndex: 'estimatedBytes',
71: title="确认清理（估算口径）"
77: <Button onClick={onClose}>取消</Button>
78: <Popconfirm
79: title="清理不可逆，确认创建作业？"
84: <Button
```

## `pages/storage/index.tsx`

行为追踪：./api: cancelCleanupJob, confirmCleanupJob, createCleanupPreview, formatBytes, formatCount, formatUtc, getCleanupJobEvents, getInventoryTrend, getProtectedObjects, getRetentionPolicy, getStorageDataClasses, getStorageOverview, listCleanupJobs, requestInventoryRefresh,。

```tsx
78: { value: 7, label: '早于 7 天' },
79: { value: 14, label: '早于 14 天' },
80: { value: 30, label: '早于 30 天' },
81: { value: 90, label: '早于 90 天' },
268: title: '类型',
269: dataIndex: 'displayName',
279: title: '约占空间',
280: dataIndex: 'estimatedBytes',
287: title: '占类合计',
288: dataIndex: 'share',
297: title: '约记录数',
298: dataIndex: 'estimatedRows',
311: title: '最早数据',
312: dataIndex: 'oldestUtc',
317: title: '保留策略',
318: dataIndex: 'policy',
328: title: '更新于',
329: dataIndex: 'updatedAtUtc',
334: title: '状态',
335: dataIndex: 'estimateState',
350: title: '时间',
351: dataIndex: 'createdAtUtc',
356: title: '触发',
357: dataIndex: 'trigger',
363: title: '数据类型',
364: dataIndex: 'targetIds',
369: title: '状态',
370: dataIndex: 'status',
378: title: '进度（已处理/发现）',
379: dataIndex: 'progress',
400: title: '库内可复用',
401: dataIndex: 'reusable',
407: title: '操作',
408: dataIndex: 'actions',
413: <Popconfirm
414: title="确认取消？"
426: <Button size="small" danger>
432: <Popconfirm
433: title="确认继续处理超出预算的数据？"
444: <Button size="small" type="primary">
449: <Button
455: title: '作业事件',
485: title: '存储管理',
488: <Button
496: <Button key="policy" icon={<SettingOutlined />} onClick={() => setPolicyOpen(true)}>
521: <Card size="small" title="存储空间总览">
541: { title: '数据库', dataIndex: 'displayName' },
543: title: '大小',
544: dataIndex: 'totalBytes',
549: title: '可复用页',
550: dataIndex: 'reusableFreeBytes',
562: title="分类占比（估算）"
568: { label: '7 天', value: 7 },
569: { label: '30 天', value: 30 },
570: { label: '90 天', value: 90 },
580: <Card size="small" title={`近 ${trendDays} 天存储趋势（堆叠面积，按历史快照聚合）`}>
585: <Card size="small" title="分类统计报表" extra={<Typography.Text type="secondary">全部为估算约数（≈）</Typography.Text>}>
598: title="可清理数据"
608: <Button
659: <Card size="small" title={<Space><SafetyCertificateOutlined /> 受保护数据（不可选择）</Space>}>
680: title={
```

## `pages/storage/StorageOverviewCharts.tsx`

行为追踪：./api: formatBytes。

```tsx
59: <svg width={180} height={180} viewBox="0 0 180 180" role="img" aria-label="存储分类占比圆环图">
93: title={`${inventory?.displayName ?? '其他'}：约 ${formatBytes(inventory?.estimatedBytes ?? (total * segment.fraction))}`}
210: aria-label={`${days} 天存储趋势堆叠面积图`}
```

## `pages/storage/StoragePolicyDrawer.tsx`

行为追踪：./api: formatUtc, updateRetentionPolicy。

```tsx
82: title: '数据类型',
83: dataIndex: 'displayName',
95: title: '自动清理',
96: dataIndex: 'enabled',
114: title: '保留期（天）',
115: dataIndex: 'retentionDays',
135: title: '允许范围',
136: dataIndex: 'range',
147: title="自动清理策略"
153: <Button onClick={onClose}>取消</Button>
154: <Popconfirm title="确认保存自动清理策略？" onConfirm={save} disabled={saving}>
155: <Button type="primary" loading={saving} disabled={saving}>
```

## `pages/team-management/index.tsx`

行为追踪：@/services/platform/api: listTeams, getTeam, createTeam, updateTeam, deleteTeam, addTeamMember, removeTeamMember, listTeamWorkspaces, createTeamWorkspace, updateWorkspacePerm, deleteWorkspacePerm, listWorkspaceMembers, addWorkspaceMember, removeWorkspaceMember, listUsers, type TeamDto, type TeamDetailDto, type TeamMemberDto, type WorkspaceWithPermDto, type WorkspaceMemberDto, type AppUserDto, type UpsertTeamRequest, type CreateWorkspaceRequest, type UpdateWorkspaceRequest, type AddTeamMemberRequest, type AddWorkspaceMemberRequest, type WorkspaceAccessPolicy,。

```tsx
69: const POLICY_OPTIONS: { label: string; value: WorkspaceAccessPolicy }[] = [
70: { label: '无访问（白名单模式）', value: 'None' },
71: { label: '只读', value: 'ReadOnly' },
72: { label: '可读写', value: 'Write' },
73: { label: '可管理', value: 'Manage' },
116: { title: 'TeamId', dataIndex: 'teamId', width: 140, copyable: true },
117: { title: '名称', dataIndex: 'name', width: 140 },
118: { title: '描述', dataIndex: 'description', ellipsis: true },
120: title: '成员数', dataIndex: 'memberCount', width: 80,
124: title: '场景数', dataIndex: 'workspaceCount', width: 90,
128: title: '状态', dataIndex: 'isEnabled', width: 80,
132: title: '操作', key: 'action', width: 200,
135: <Button size="small" onClick={() => openDetail(r.teamId)}>详情</Button>
136: <Button size="small" icon={<EditOutlined />} onClick={() => openEditTeam(r)}>编辑</Button>
137: <Popconfirm
138: title={`确认删除团队 "${r.name}"？（需先删除所有场景）`}
142: <Button size="small" danger icon={<DeleteOutlined />} />
330: <PageContainer title="团队管理" subTitle="管理平台团队、场景及访问权限">
340: <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreateTeam}>
350: title={editingTeam ? `编辑团队：${editingTeam.name}` : '新建团队'}
356: <Button onClick={() => setTeamDrawerOpen(false)}>取消</Button>
357: <Button type="primary" loading={teamSaving} onClick={handleSaveTeam}>保存</Button>
364: name="teamId"
365: label="TeamId（英文 slug）"
367: placeholder="e.g. platform-team"
370: <ProFormText name="name" label="团队名称" rules={[{ required: true }]} />
371: <ProFormTextArea name="description" label="描述" rows={2} />
372: <ProFormSwitch name="isEnabled" label="启用" initialValue />
378: title={teamDetail ? `团队：${teamDetail.name}` : '团队详情'}
389: label: <><TeamOutlined /> 成员（{teamDetail.members.length}）</>,
393: <Button
410: { title: 'UserId', dataIndex: 'userId', width: 130 },
411: { title: '用户名', dataIndex: 'username', width: 120 },
412: { title: '显示名', dataIndex: 'displayName' },
414: title: '角色', dataIndex: 'role', width: 90,
420: title: '操作', width: 80,
422: <Popconfirm
423: title="确认移除此成员？"
427: <Button size="small" danger>移除</Button>
439: label: <><AppstoreOutlined /> 场景（{teamDetail.workspaces.length}）</>,
443: <Button icon={<PlusOutlined />} onClick={openCreateWs} size="small">
457: title: '场景', dataIndex: 'name', width: 140,
468: title: '团队权限', dataIndex: 'teamAccessPolicy', width: 110,
474: title: '全公司权限', dataIndex: 'companyAccessPolicy', width: 110,
480: title: '白名单', dataIndex: 'memberCount', width: 80,
484: title: '状态', dataIndex: 'isEnabled', width: 70,
490: title: '操作', width: 190,
493: <Button size="small" onClick={() => openEditWs(r)}>编辑</Button>
494: <Button size="small" onClick={() => openWsMembers(r.workspaceId)}>
497: <Popconfirm
498: title="确认删除此场景？"
502: <Button size="small" danger>删除</Button>
520: title={editingWs ? `编辑场景：${editingWs.name}` : '新建场景'}
526: <Button onClick={() => setWsDrawerOpen(false)}>取消</Button>
527: <Button type="primary" loading={wsSaving} onClick={handleSaveWs}>保存</Button>
534: name="workspaceId"
535: label="WorkspaceId（全局唯一 slug）"
537: placeholder="e.g. my-workspace"
540: <ProFormText name="name" label="场景名称" rules={[{ required: true }]} />
541: <ProFormTextArea name="description" label="描述" rows={2} />
543: name="teamAccessPolicy"
544: label="团队默认权限"
551: name="companyAccessPolicy"
552: label="全公司默认权限"
558: {editingWs && <ProFormSwitch name="isEnabled" label="启用" initialValue />}
564: title="添加团队成员"
572: <Form.Item name="userId" label="选择用户" rules={[{ required: true }]}>
575: placeholder="搜索用户"
577: label: `${u.username}（${u.userId}）`,
585: <Form.Item name="role" label="团队角色" initialValue="Member" rules={[{ required: true }]}>
587: { label: 'Member（普通成员）', value: 'Member' },
588: { label: 'Admin（团队管理员）', value: 'Admin' },
596: title="场景白名单成员"
603: <Button
617: { title: 'UserId', dataIndex: 'userId', width: 120 },
618: { title: '用户名', dataIndex: 'username', width: 110 },
620: title: '权限', dataIndex: 'accessLevel', width: 100,
624: title: '操作', width: 80,
626: <Popconfirm
627: title="确认移除？"
631: <Button size="small" danger>移除</Button>
641: title="添加白名单成员"
649: <Form.Item name="userId" label="选择用户" rules={[{ required: true }]}>
652: placeholder="搜索用户"
654: label: `${u.username}（${u.userId}）`,
662: <Form.Item name="accessLevel" label="权限级别" initialValue="ReadOnly" rules={[{ required: true }]}>
```

## `pages/tool-approval/allowlist/index.tsx`

行为追踪：@/services/platform/api: createToolApprovalAllowlistRule, disableToolApprovalAllowlistRule, listToolApprovalAllowlist, updateToolApprovalAllowlistRule, type ToolApprovalAllowlistRuleDto, type ToolApprovalAllowlistRuleMutation, type ToolApprovalAllowlistSource, type ToolApprovalAllowlistStatus,。

```tsx
39: const SOURCE_OPTIONS: { label: string; value: ToolApprovalAllowlistSource }[] = (
47: const STATUS_OPTIONS: { label: string; value: ToolApprovalAllowlistStatus }[] = [
48: { label: '启用', value: 'enabled' },
49: { label: '禁用', value: 'disabled' },
149: title: '规则',
150: dataIndex: 'ruleId',
165: title: '工具',
166: dataIndex: 'toolId',
173: title: '命令 / 参数',
174: dataIndex: 'command',
190: title: '来源',
191: dataIndex: 'source',
197: title: '状态',
198: dataIndex: 'status',
207: title: '命中',
208: dataIndex: 'hitCount',
221: title: '原因',
222: dataIndex: 'reason',
226: title: '更新时间',
227: dataIndex: 'updatedAtUtc',
232: title: '操作',
237: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} />
238: <Popconfirm
239: title="禁用白名单条目"
244: <Button
258: title="审批白名单"
260: <Button key="reload" icon={<ReloadOutlined />} onClick={load}>
263: <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
280: title={editing ? '编辑白名单条目' : '新建白名单条目'}
291: name="toolId"
292: label="工具 ID"
296: <Input placeholder="shell" />
298: <Form.Item name="workspaceId" label="工作区" style={{ width: 220 }}>
299: <Input placeholder="留空表示全局" />
301: <Form.Item name="status" label="状态" rules={[{ required: true }]} style={{ width: 140 }}>
305: <Form.Item name="command" label="命令字符串">
306: <Input placeholder="例如 ls -la 或 Get-ChildItem" />
308: <Form.Item name="argumentsJson" label="精确参数 JSON">
309: <Input.TextArea rows={4} placeholder='例如 {"command":"pwd","shell":"auto"}' />
312: <Form.Item name="source" label="来源" rules={[{ required: true }]} style={{ width: 180 }}>
315: <Form.Item name="approvedByAgentInstanceId" label="审计 Agent" style={{ width: 240 }}>
316: <Input placeholder="可选" />
318: <Form.Item name="approvedByUserId" label="批准用户" style={{ width: 180 }}>
319: <Input placeholder="可选" />
322: <Form.Item name="approvalTicketId" label="关联工单">
323: <Input placeholder="可选" />
325: <Form.Item name="reason" label="批准原因">
326: <Input.TextArea rows={3} placeholder="为什么可以快速审批这条命令或参数" />
```

## `pages/tool-approval/audit/index.tsx`

行为追踪：@/services/platform/api: getToolApprovalStats, listToolApprovalAuditEvents, type ToolApprovalAuditEventDto, type ToolApprovalStatsDto,。

```tsx
20: { label: '全部事件', value: '' },
21: { label: '工单提交', value: 'ticket_submitted' },
22: { label: '工单批准', value: 'ticket_approved' },
23: { label: '工单拒绝', value: 'ticket_denied' },
24: { label: '需要人工', value: 'ticket_need_human' },
25: { label: '工单匹配', value: 'ticket_matched' },
26: { label: '工单消费', value: 'ticket_consumed' },
27: { label: '工单不匹配', value: 'ticket_mismatch' },
28: { label: '依赖等待', value: 'ticket_deferred_dependency' },
29: { label: '隐式批准', value: 'implicit_approved' },
30: { label: '隐式拒绝', value: 'implicit_denied' },
31: { label: '白名单命中', value: 'allowlist_hit' },
32: { label: '规则创建', value: 'allowlist_rule_created' },
33: { label: '规则更新', value: 'allowlist_rule_updated' },
34: { label: '规则禁用', value: 'allowlist_rule_disabled' },
35: { label: '黑名单规则创建', value: 'denylist_rule_created' },
36: { label: '黑名单规则禁用', value: 'denylist_rule_disabled' },
37: { label: '规则冲突', value: 'rule_conflict_detected' },
38: { label: '定义漂移', value: 'definition_drift_detected' },
39: { label: '分类器已裁决', value: 'classifier_invoked' },
40: { label: '分类器不可用', value: 'classifier_unavailable' },
41: { label: '完全访问：申请', value: 'full_access_requested' },
42: { label: '完全访问：授予', value: 'full_access_granted' },
43: { label: '完全访问：拒绝', value: 'full_access_denied' },
44: { label: '完全访问：到期', value: 'full_access_expired' },
45: { label: '完全访问：撤销', value: 'full_access_revoked' },
46: { label: '完全访问：放行', value: 'full_access_gate_bypass' },
118: title: '时间',
119: dataIndex: 'createdAtUtc',
124: title: '事件',
125: dataIndex: 'eventType',
130: title: '范围',
131: dataIndex: 'workspaceId',
143: title: '工具',
144: dataIndex: 'toolId',
149: title: '原始命令 / 参数',
150: dataIndex: 'command',
166: title: '决策',
167: dataIndex: 'decision',
172: title: '来源',
173: dataIndex: 'source',
178: title: '引用',
179: dataIndex: 'ticketId',
204: title: '原因',
205: dataIndex: 'reason',
212: title="审批审计"
221: <Button key="reload" icon={<ReloadOutlined />} onClick={load}>
230: <Statistic title="工单提交" value={stats.ticketSubmittedCount} prefix={<AuditOutlined />} />
235: <Statistic title="批准" value={stats.ticketApprovedCount} prefix={<CheckCircleOutlined />} />
240: <Statistic title="拒绝" value={stats.ticketDeniedCount} prefix={<StopOutlined />} />
245: <Statistic title="需人工" value={stats.ticketNeedHumanCount} />
250: <Statistic title="隐式批准" value={stats.implicitApprovedCount ?? 0} />
255: <Statistic title="隐式拒绝" value={stats.implicitDeniedCount ?? 0} />
260: <Statistic title="白名单放行" value={stats.allowlistHitCount} />
266: title="启用规则"
```

## `pages/tool-approval/components/ClassifierHealthBanner.tsx`

行为追踪：@/services/platform/api: getClassifierHealth, type ClassifierHealthItemDto, type ClassifierHealthSnapshotDto, type ClassifierHealthState,。

```tsx
128: title: '分类器',
129: dataIndex: 'classifierId',
137: title: '健康状态',
138: dataIndex: 'health',
145: title: '连续失败',
146: dataIndex: 'consecutiveFailures',
150: title: '最近探测',
151: dataIndex: 'lastCheckedAtUtc',
156: title: '最近延迟',
157: dataIndex: 'lastLatencyMs',
162: title: '说明',
163: dataIndex: 'detail',
227: label: '分类器健康明细',
```

## `pages/user-management/index.tsx`

行为追踪：@/services/platform/api: listUsers, createUser, updateUser, deleteUser, changeUserPassword, assignUserRoles, listRoles, type AppUserDto, type CreateUserRequest, type UpdateUserRequest, type AppRoleDto,。

```tsx
44: { label: 'Admin（平台管理员）', value: 'Admin' },
45: { label: 'SimpleUser（普通用户）', value: 'SimpleUser' },
75: { title: 'UserId', dataIndex: 'userId', width: 140, copyable: true },
76: { title: '用户名', dataIndex: 'username', width: 120 },
77: { title: '邮箱', dataIndex: 'email', width: 200 },
78: { title: '显示名', dataIndex: 'displayName', width: 120 },
80: title: '类型',
81: dataIndex: 'userType',
90: title: '角色',
91: dataIndex: 'roleIds',
99: title: '状态',
100: dataIndex: 'isEnabled',
107: title: '创建时间',
108: dataIndex: 'createdAt',
113: title: '操作',
118: <Button
125: <Button
130: <Button
135: <Popconfirm
136: title={`确认删除用户 "${r.username}"？`}
141: <Button size="small" danger icon={<DeleteOutlined />} />
250: <PageContainer title="用户管理" subTitle="管理平台用户账号与权限角色分配">
260: <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
270: title={editingUser ? `编辑用户：${editingUser.username}` : '新建用户'}
276: <Button onClick={() => setDrawerOpen(false)}>取消</Button>
277: <Button type="primary" loading={drawerLoading} onClick={handleDrawerSave}>
285: <Form.Item label="头像">
300: name="userId"
301: label="UserId（英文唯一标识）"
303: placeholder="e.g. zhangsan"
306: name="password"
307: label="初始密码"
313: name="username"
314: label="用户名"
318: name="email"
319: label="邮箱"
322: <ProFormText name="displayName" label="显示名（可选）" />
324: name="userType"
325: label="用户类型"
331: <ProFormSwitch name="isEnabled" label="启用" initialValue />
338: title="修改密码"
347: name="newPassword"
348: label="新密码"
351: <Input.Password placeholder="输入新密码" />
354: name="confirm"
355: label="确认密码"
367: <Input.Password placeholder="再次输入新密码" />
374: title="分配角色"
389: label: `${r.name}（${r.roleId}）`,
392: placeholder="选择角色"
```

## `pages/voice-models/index.tsx`

行为追踪：@/services/platform/api: listVoiceProviders, getVoiceProvider, createVoiceProvider, updateVoiceProvider, deleteVoiceProvider, listTtsModels, createTtsModel, updateTtsModel, deleteTtsModel, listAsrModels, createAsrModel, updateAsrModel, deleteAsrModel, type VoiceProviderDto, type VoiceProviderDetailDto, type TtsModelDto, type AsrModelDto, type UpsertVoiceProviderRequest, type UpsertTtsModelRequest, type UpsertAsrModelRequest,。

```tsx
67: { label: 'WAV', value: 'wav' },
68: { label: 'MP3', value: 'mp3' },
69: { label: 'PCM', value: 'pcm' },
70: { label: 'OGG', value: 'ogg' },
74: { label: '中文', value: 'zh-CN' },
75: { label: '英文', value: 'en-US' },
76: { label: '日文', value: 'ja-JP' },
77: { label: '韩文', value: 'ko-KR' },
101: { title: '名称', dataIndex: 'name', key: 'name', width: 180 },
103: title: '服务商ID',
104: dataIndex: 'providerId',
109: { title: '接入点', dataIndex: 'endpoint', key: 'endpoint', width: 220, ellipsis: true },
111: title: '密钥',
112: dataIndex: 'hasApiKey',
119: title: 'TTS',
120: dataIndex: 'ttsModelCount',
126: title: 'ASR',
127: dataIndex: 'asrModelCount',
133: title: '状态',
134: dataIndex: 'isEnabled',
141: title: '操作',
146: <Button
153: <Button
160: <Popconfirm
161: title="确认删除此语音服务商？"
168: <Button size="small" danger icon={<DeleteOutlined />}>
353: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreateProvider}>
361: title={editingProvider ? '编辑语音服务商' : '新增语音服务商'}
371: <Button onClick={() => { setDrawerOpen(false); setEditingProvider(null); }}>
374: <Button type="primary" onClick={handleProviderSubmit}>
382: <Form.Item label="预设模板" style={{ marginBottom: 16 }}>
385: placeholder="选择预设模板（可选）"
390: label: t.label,
404: name="providerId"
405: label="服务商ID"
408: placeholder="例如: dashscope"
411: name="name"
412: label="名称"
414: placeholder="例如: 阿里云百炼-语音"
417: name="endpoint"
418: label="接入点 URL"
420: placeholder="https://dashscope.aliyuncs.com"
423: name="apiKey"
424: label={
430: placeholder="输入 API Key（留空则不修改）"
432: <ProFormTextArea name="description" label="描述" placeholder="服务商描述（可选）" />
433: <ProFormSwitch name="isEnabled" label="启用" />
439: title="语音模型管理"
463: title={editingTts ? '编辑 TTS 模型' : '新增 TTS 模型'}
469: <Button onClick={() => { setTtsDrawerOpen(false); setEditingTts(null); }}>取消</Button>
470: <Button type="primary" onClick={handleTtsSubmit}>
478: name="modelId"
479: label="模型ID"
482: placeholder="cosyvoice-v3-flash"
484: <ProFormText name="name" label="名称" rules={[{ required: true }]} />
485: <ProFormText name="path" label="API 路径" placeholder="/api/v1/services/audio/tts/SpeechSynthesizer" />
486: <Form.Item name="voices" label="音色">
487: <Select mode="tags" placeholder="输入音色名称后回车" />
489: <Form.Item name="audioFormats" label="音频格式">
490: <Select mode="multiple" options={AUDIO_FORMAT_OPTIONS} placeholder="选择格式" />
492: <Form.Item name="sampleRates" label="采样率">
495: placeholder="输入采样率后回车（如 24000）"
499: <ProFormSwitch name="supportsStreaming" label="支持流式" />
500: <ProFormSwitch name="supportsInstructions" label="支持指令" />
501: <ProFormSwitch name="supportsVoiceCloning" label="支持声音克隆" />
502: <ProFormSwitch name="supportsVoiceDesign" label="支持声音设计" />
503: <ProFormSwitch name="isDeprecated" label="已弃用" />
504: <ProFormSwitch name="isDefault" label="默认模型" />
505: <Form.Item name="sortOrder" label="排序">
513: title={editingAsr ? '编辑 ASR 模型' : '新增 ASR 模型'}
519: <Button onClick={() => { setAsrDrawerOpen(false); setEditingAsr(null); }}>取消</Button>
520: <Button type="primary" onClick={handleAsrSubmit}>
528: name="modelId"
529: label="模型ID"
532: placeholder="qwen3-asr-flash-realtime"
534: <ProFormText name="name" label="名称" rules={[{ required: true }]} />
535: <ProFormText name="path" label="API 路径" />
536: <Form.Item name="languages" label="支持语言">
537: <Select mode="multiple" options={LANGUAGE_OPTIONS} placeholder="选择语言" />
539: <Form.Item name="sampleRates" label="采样率">
542: placeholder="输入采样率后回车（如 16000）"
546: <ProFormSwitch name="supportsEmotion" label="支持情感识别" />
547: <ProFormSwitch name="supportsTimestamps" label="支持时间戳" />
548: <ProFormSwitch name="supportsHotWords" label="支持热词" />
549: <ProFormSwitch name="isDeprecated" label="已弃用" />
550: <ProFormSwitch name="isDefault" label="默认模型" />
551: <Form.Item name="sortOrder" label="排序">
571: { title: '模型ID', dataIndex: 'modelId', key: 'modelId', width: 160, render: (_, r) => <Text copyable>{r.modelId}</Text> },
572: { title: '名称', dataIndex: 'name', key: 'name', width: 140 },
573: { title: '音色', dataIndex: 'voices', key: 'voices', width: 180, render: (_, r) => r.voices?.join(', ') || '-' },
575: title: '格式', dataIndex: 'audioFormats', key: 'audioFormats', width: 100,
579: title: '采样率', dataIndex: 'sampleRates', key: 'sampleRates', width: 100,
583: title: '特性', key: 'features', width: 120,
593: { title: '默认', dataIndex: 'isDefault', key: 'isDefault', width: 60, render: (_, r) => r.isDefault ? <Tag color="gold">默认</Tag> : null },
595: title: '操作', key: 'action', width: 140,
598: <Button size="small" icon={<EditOutlined />} onClick={() => onEditTts(providerId, record.modelId)}>编辑</Button>
599: <Popconfirm
600: title="确认删除？"
607: <Button size="small" danger icon={<DeleteOutlined />}>删除</Button>
615: { title: '模型ID', dataIndex: 'modelId', key: 'modelId', width: 180, render: (_, r) => <Text copyable>{r.modelId}</Text> },
616: { title: '名称', dataIndex: 'name', key: 'name', width: 160 },
618: title: '语言', dataIndex: 'languages', key: 'languages', width: 120,
622: title: '采样率', dataIndex: 'sampleRates', key: 'sampleRates', width: 100,
626: title: '特性', key: 'features', width: 120,
635: { title: '默认', dataIndex: 'isDefault', key: 'isDefault', width: 60, render: (_, r) => r.isDefault ? <Tag color="gold">默认</Tag> : null },
637: title: '操作', key: 'action', width: 140,
640: <Button size="small" icon={<EditOutlined />} onClick={() => onEditAsr(providerId, record.modelId)}>编辑</Button>
641: <Popconfirm
642: title="确认删除？"
649: <Button size="small" danger icon={<DeleteOutlined />}>删除</Button>
661: label: 'TTS 模型',
665: <Button type="primary" size="small" icon={<PlusOutlined />} onClick={() => onCreateTts(providerId)}>
683: label: 'ASR 模型',
687: <Button type="primary" size="small" icon={<PlusOutlined />} onClick={() => onCreateAsr(providerId)}>
```

## `pages/workspace/[id]/index.tsx`

行为追踪：@/services/platform/api: addWorkspaceMember, createWorkspaceAgent, createWorkspaceChannel, createWorkspaceSkill, createKnowledgeBase, createWorkflow, deleteWorkspaceAgent, getWorkspaceAgent, deleteWorkspaceChannel, deleteWorkspaceSkill, deleteKnowledgeBase, deleteWorkflow, getWorkspace, listAgentAvatars, listCapabilities, listGlobalAgentTemplates, listLlmModels, listLlmProviders, listSkillPackages, listKnowledgeBases, listUsers, listWorkspaceAgents, listWorkspaceChannels, listWorkspaceMembers, listWorkspaceSkills, listChannelProviders, listWorkflows, removeWorkspaceMember, updateWorkspace, updateWorkspaceAgent, updateWorkspaceChannel, updateChannelProvider, updateWorkspaceSkill, updateKnowledgeBase, updateWorkflow, type AddWorkspaceMemberRequest, type AgentAvatarDto, type AppUserDto, type CapabilityDto, type ChannelProviderDto, type CreateWorkspaceAgentRequest, type GlobalAgentTemplateDto, type KnowledgeBaseDto, type LlmModelDto, type LlmProviderDto, type SkillPackageDto, type UpsertKnowledgeBaseRequest, type UpsertWorkflowRequest, type UpsertWorkspaceChannelRequest, type UpsertWorkspaceSkillRequest, type UpdateWorkspaceAgentRequest, type UpdateChannelProviderRequest, type UpdateWorkspaceRequest, type WorkspaceAccessPolicy, type WorkspaceAgentDto, type WorkspaceChannelDto, type WorkflowDto, type WorkspaceMemberDto, type WorkspaceSkillDto, type WorkspaceWithPermDto,。

```tsx
126: const ACCESS_OPTIONS: { label: string; value: WorkspaceAccessPolicy }[] = [
127: { label: '只读', value: 'ReadOnly' },
128: { label: '可读写', value: 'Write' },
129: { label: '可管理', value: 'Manage' },
135: { label: '草稿', value: 'Draft' },
136: { label: '激活', value: 'Active' },
137: { label: '暂停', value: 'Paused' },
190: { title: '名称', dataIndex: 'name', width: 160 },
192: title: '状态',
193: dataIndex: 'status',
197: { title: '描述', dataIndex: 'description', ellipsis: true },
199: title: '启用',
200: dataIndex: 'isEnabled',
207: title: '创建时间',
208: dataIndex: 'createdAt',
216: title: '操作',
220: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
221: <Popconfirm title="确认删除？" onConfirm={() => handleDelete(r.workflowId)} okButtonProps={{ danger: true }}>
222: <Button size="small" danger icon={<DeleteOutlined />} />
256: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新建工作流</Button>,
261: title={editItem ? '编辑工作流' : '新建工作流'}
265: extra={<Button type="primary" onClick={handleSave}>保存</Button>}
268: <ProFormText name="name" label="名称" rules={[{ required: true }]} />
269: <ProFormSelect name="status" label="状态" options={WORKFLOW_STATUSES} rules={[{ required: true }]} />
270: <ProFormTextArea name="description" label="描述" rows={2} />
271: <ProFormTextArea name="definitionJson" label="工作流定义 JSON" rows={8} placeholder='{"nodes": [], "edges": []}' />
272: <ProFormSwitch name="isEnabled" label="启用" />
502: { title: '名称', dataIndex: 'name', width: 140 },
504: title: '来源模板',
505: dataIndex: 'sourceTemplateId',
512: title: '首选模型',
520: title: '状态',
529: title: '操作',
533: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
534: <Popconfirm title="确认删除该 Agent？" onConfirm={() => handleDelete(r.agentId)} okButtonProps={{ danger: true }}>
535: <Button size="small" danger icon={<DeleteOutlined />} />
562: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新增 Agent</Button>,
610: { label: '向量存储 (VectorStore)', value: 'VectorStore' },
611: { label: '知识图谱 (Graph)', value: 'Graph' },
612: { label: '文件索引 (FileIndex)', value: 'FileIndex' },
665: { title: '名称', dataIndex: 'name', width: 160 },
667: title: '类型',
668: dataIndex: 'kbType',
672: { title: '文档数', dataIndex: 'documentCount', width: 90 },
673: { title: '描述', dataIndex: 'description', ellipsis: true },
675: title: '启用',
685: title: '操作',
689: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
690: <Popconfirm title="确认删除知识库？" onConfirm={() => handleDelete(r.kbId)} okButtonProps={{ danger: true }}>
691: <Button size="small" danger icon={<DeleteOutlined />} />
725: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新建知识库</Button>,
730: title={editItem ? '编辑知识库' : '新建知识库'}
734: extra={<Button type="primary" onClick={handleSave}>保存</Button>}
737: <ProFormText name="name" label="名称" rules={[{ required: true }]} />
738: <ProFormSelect name="kbType" label="知识库类型" options={KB_TYPES} rules={[{ required: true }]} />
739: <ProFormTextArea name="description" label="描述" rows={2} />
740: <ProFormSwitch name="isEnabled" label="启用" />
751: { label: 'MCP Server', value: 'MCP' },
752: { label: '内置工具 (BuiltIn)', value: 'BuiltIn' },
753: { label: '自定义脚本 (CustomScript)', value: 'CustomScript' },
754: { label: 'HTTP 工具 (HttpTool)', value: 'HttpTool' },
809: { title: '名称', dataIndex: 'name', width: 140 },
811: title: '类型',
812: dataIndex: 'skillType',
816: { title: '描述', dataIndex: 'description', ellipsis: true },
818: title: '启用',
825: title: '操作',
829: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
830: <Popconfirm title="确认删除 Skill？" onConfirm={() => handleDelete(r.skillId)} okButtonProps={{ danger: true }}>
831: <Button size="small" danger icon={<DeleteOutlined />} />
858: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>注册 Skill</Button>,
862: title={editItem ? '编辑 Skill' : '注册 Skill'}
866: extra={<Button type="primary" onClick={handleSave}>保存</Button>}
869: <ProFormText name="name" label="名称" rules={[{ required: true }]} />
871: name="skillType"
872: label="技能类型"
877: <ProFormTextArea name="description" label="描述" rows={2} />
879: name="configJson"
880: label={skillType === 'MCP' ? 'MCP Server 配置 JSON' : skillType === 'HttpTool' ? 'HTTP 工具配置 JSON' : '配置 JSON（可选）'}
882: placeholder={
890: <ProFormSwitch name="isEnabled" label="启用" />
939: title: '服务商',
948: title: '渠道类型',
949: dataIndex: 'channelType',
954: title: '接入能力',
963: { title: '说明', dataIndex: 'description', ellipsis: true },
965: title: '状态',
972: title: '操作',
975: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(item)} />
999: title="编辑渠道服务商"
1003: extra={<Button type="primary" onClick={handleSave}>保存</Button>}
1006: <ProFormText name="name" label="显示名称" rules={[{ required: true }]} />
1007: <ProFormTextArea name="description" label="说明" rows={3} />
1008: <ProFormSwitch name="isEnabled" label="启用服务商" />
1092: { title: '名称', dataIndex: 'name', width: 140 },
1094: title: '服务商',
1099: title: '绑定 Agent',
1110: title: '连接配置',
1123: title: '启用',
1130: title: '操作',
1134: <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
1135: <Popconfirm title="确认删除渠道？" onConfirm={() => handleDelete(r.channelId)} okButtonProps={{ danger: true }}>
1136: <Button size="small" danger icon={<DeleteOutlined />} />
1163: <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新增渠道</Button>,
1167: title={editItem ? '编辑渠道' : '新增渠道'}
1171: extra={<Button type="primary" onClick={handleSave}>保存</Button>}
1174: <ProFormText name="name" label="渠道名称" rules={[{ required: true }]} />
1176: name="providerId"
1177: label="渠道服务商"
1179: label: provider.name,
1185: <ProFormTextArea name="description" label="描述" rows={2} />
1187: name="boundAgentId"
1188: label="绑定 Agent"
1190: label: a.name, value: a.agentId,
1192: placeholder="选择这个渠道接收消息时运行的 Agent"
1203: name="appId"
1204: label="App ID"
1205: placeholder="cli_xxx"
1209: name="appSecret"
1210: label="App Secret"
1211: placeholder={editItem ? '留空则保留当前 Secret' : '请输入飞书 App Secret'}
1216: name="privilegedUserOpenIds"
1217: label="特权用户 Open ID"
1218: placeholder="输入 open_id 后回车，可添加多个"
1226: <ProFormSwitch name="streamingRepliesEnabled" label="CardKit 流式回复" />
1228: name="ttsRepliesEnabled"
1229: label="Agent 语音回复"
1233: name="ttsVoice"
1234: label="TTS 音色"
1235: placeholder="Cherry"
1238: <ProFormSwitch name="isEnabled" label="启用" />
1397: title: '用户 ID',
1398: dataIndex: 'userId',
1402: title: '用户名',
1403: dataIndex: 'username',
1406: title: '显示名称',
1407: dataIndex: 'displayName',
1411: title: '访问级别',
1412: dataIndex: 'accessLevel',
1418: title: '操作',
1422: <Popconfirm
1423: title="确认移除该成员？"
1428: <Button type="link" size="small" danger>移除</Button>
1448: <Button style={{ marginTop: 16 }} onClick={() => history.push(buildWorkspacePath())}>
1465: title: (
1467: <Button
1478: <Button
1485: <Button key="edit" icon={<SettingOutlined />} onClick={openEdit}>
1496: label: '概览',
1500: <Descriptions.Item label="场景 ID">{workspace.workspaceId}</Descriptions.Item>
1501: <Descriptions.Item label="状态">{statusBadge}</Descriptions.Item>
1502: <Descriptions.Item label="名称">{workspace.name}</Descriptions.Item>
1503: <Descriptions.Item label="成员数">{workspace.memberCount}</Descriptions.Item>
1504: <Descriptions.Item label="描述" span={2}>
1507: <Descriptions.Item label="创建时间">
1516: label: (
1526: label: (
1536: label: '知识库',
1541: label: (
1551: label: (
1561: label: '渠道管理',
1566: label: (
1576: <Button
1601: title="添加白名单成员"
1612: name="userId"
1613: label="用户"
1617: placeholder="选择用户"
1621: label: `${u.username}${u.displayName ? ` (${u.displayName})` : ''}`,
1627: name="accessLevel"
1628: label="访问级别"
1638: title="编辑场景设置"
1650: name="name"
1651: label="名称"
1654: <Input placeholder="场景显示名称" />
1656: <Form.Item name="description" label="描述">
1657: <Input.TextArea rows={2} maxLength={512} placeholder="可选描述" />
1659: <Form.Item name="userProfile" label="用户画像">
1663: placeholder="描述该场景下的用户偏好、背景、习惯..."
1666: <Form.Item name="isEnabled" label="启用状态">
1669: { label: '启用', value: true },
1670: { label: '停用', value: false },
```

## `pages/workspace/[id]/SmartRoleModelFields.tsx`

行为追踪：@/services/platform/api: listLlmModels, listLlmProviders, type LlmModelDto, type LlmProviderDto,。

```tsx
16: label: 'Explorer 探索者',
21: label: 'Researcher 研究员',
26: label: 'Planner 规划者',
31: label: 'Reviewer 审查者',
36: label: 'Developer 开发者',
41: label: 'Deployer 部署者',
46: label: 'Tester 测试者',
52: label: string;
64: label: `${provider.name} / ${model.name} (${Math.round(model.maxContextTokens / 1024)}K)`,
165: placeholder="选择服务商 / 模型"
170: <Button disabled={!batchModel} onClick={() => applyBatchModel(true)}>
173: <Button disabled={!batchModel} onClick={() => applyBatchModel(false)}>
196: name={role.name}
197: label={role.label}
199: placeholder="选择服务商 / 模型"
```

## `pages/workspace/[id]/WorkspaceAgentSettingsDrawer.tsx`

```tsx
34: { label: '服务型 (Service)', value: 'Service' },
35: { label: '任务型 (Task)', value: 'Task' },
36: { label: '审计型 (Audit)', value: 'Audit' },
37: { label: '自定义 (Custom)', value: 'Custom' },
41: { key: 'basic', label: '基础信息', fieldNames: ['name', 'role', 'description', 'sourceTemplateId', 'avatarId', 'isEnabled'] },
42: { key: 'capabilities', label: '能力与 Skill', fieldNames: ['selectedCapabilityIds', 'skillPackageIds'] },
43: { key: 'prompts', label: '角色与 Prompt', fieldNames: ['systemPrompt', 'heartbeatPrompt', 'soulMdContent', 'agentsMdContent', 'toolsMdContent', 'bootstrapMdContent', 'memoryMdContent', 'userPromptTemplate'] },
44: { key: 'models', label: '模型与记忆', fieldNames: ['preferredProviderId', 'preferredModelId', 'memoryLlmProviderId', 'memoryLlmModelId', 'embeddingProviderId', 'embeddingModelId', 'memorySearchMode', 'reasoningEffort'] },
45: { key: 'smartModels', label: 'Smart 子代理', fieldNames: ['explorerModel', 'researcherModel', 'plannerModel', 'reviewerModel', 'developerModel', 'deployerModel', 'testerModel'] },
46: { key: 'guardrails', label: '执行护栏', fieldNames: ['maxRounds', 'maxElapsedSeconds', 'maxToolCallsTotal', 'containerImage'] },
184: title: '放弃未保存的修改？',
212: title={editMode ? '编辑 Agent' : '新增 Agent'}
218: <Button type="primary" loading={saving} onClick={handleSave}>
245: name="name"
246: label="Agent 名称"
252: name="role"
253: label="角色类型"
262: name="description"
263: label="实例职责"
265: placeholder="描述这个 Agent 在当前工作区负责什么"
269: name="sourceTemplateId"
270: label="来源模板"
273: label: `${template.name} (${template.templateId})`,
298: <Form.Item name="avatarId" label="头像">
301: placeholder="选择 Agent 头像"
304: label: avatar.name,
319: <ProFormSwitch name="isEnabled" label="启用" />
347: name="systemPrompt"
348: label="系统提示词"
350: placeholder="定义 Agent 的核心职责、能力边界和行为准则"
353: name="userPromptTemplate"
354: label="用户 Prompt 模板"
356: placeholder="可选，支持 {{variable}} 占位符"
363: label: 'heartbeatPrompt.md · 心跳恢复流程',
366: name="heartbeatPrompt"
369: placeholder="Agent 空闲心跳时收到的提示词；留空使用默认提示词"
375: label: 'SOUL.md · 人设与边界',
376: children: <ProFormTextArea name="soulMdContent" rows={8} fieldProps={{ showCount: true }} />,
380: label: 'AGENTS.md · 协作规范',
381: children: <ProFormTextArea name="agentsMdContent" rows={10} fieldProps={{ showCount: true }} />,
385: label: 'TOOLS.md · 工具约定',
386: children: <ProFormTextArea name="toolsMdContent" rows={8} fieldProps={{ showCount: true }} />,
390: label: 'BOOTSTRAP.md · 首次引导',
391: children: <ProFormTextArea name="bootstrapMdContent" rows={8} fieldProps={{ showCount: true }} />,
395: label: 'MEMORY.md · 记忆策略',
396: children: <ProFormTextArea name="memoryMdContent" rows={8} fieldProps={{ showCount: true }} />,
428: name="maxRounds"
429: label="最大轮次"
438: name="maxElapsedSeconds"
439: label="最大耗时"
448: name="maxToolCallsTotal"
449: label="最大工具调用"
462: label: '高级运行环境',
465: name="containerImage"
466: label="容器镜像"
467: placeholder="宿主模式暂不使用，留空即可"
```

## `pages/workspace/index.tsx`

行为追踪：@/services/platform/api: createWorkspace, deleteWorkspace, listWorkspaceAgents, listTeams, listWorkspaces, type CreateWorkspaceRequest, type WorkspaceWithPermDto,。

```tsx
72: label: string;
77: if (workspace.isFrozen) return { tone: 'danger', label: '已冻结' };
78: if (!workspace.isEnabled) return { tone: 'neutral', label: '已停用' };
79: return { tone: 'success', label: '运行中' };
91: { value: 'all', label: '全部状态' },
92: { value: 'success', label: '运行中' },
93: { value: 'danger', label: '已冻结' },
94: { value: 'neutral', label: '已停用' },
209: title: '工作空间',
210: dataIndex: 'name',
226: title: '状态',
234: title: 'Agent',
240: title: 'Workspace ID',
241: dataIndex: 'workspaceId',
246: title: '创建时间',
247: dataIndex: 'createdAt',
252: title: '操作',
257: <Tooltip title={`进入 ${record.name} 对话`}>
258: <Button
259: aria-label={`进入 ${record.name} 对话`}
264: <Tooltip title={`设置 ${record.name}`}>
265: <Button
266: aria-label={`设置 ${record.name}`}
271: <Popconfirm
272: title="确认删除此工作空间？"
279: <Tooltip title={record.workspaceId === 'default' ? '内置工作空间不可删除' : '删除'}>
280: <Button
281: aria-label={`删除 ${record.name}`}
299: <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
324: <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
339: title={ws.name}
343: { label: 'Agent', value: agentCounts[ws.workspaceId] ?? '—' },
344: { label: '成员', value: ws.memberCount ?? 0 },
345: { label: '创建', value: dayjs(ws.createdAt).format('YYYY-MM-DD') },
349: <Button
357: <Button
364: <Popconfirm
365: title="确认删除此工作空间？"
372: <Button
376: aria-label={`删除 ${ws.name}`}
392: <WorkspaceNavigationHeader crumbs={[{ label: '工作空间', title: '工作空间' }]} />
397: title="工作空间"
400: <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
410: placeholder="搜索名称或 Workspace ID"
420: aria-label="按状态筛选工作空间"
429: { label: '表格', value: 'table', icon: <TableOutlined /> },
430: { label: '卡片', value: 'card', icon: <AppstoreOutlined /> },
441: title="新建工作空间"
453: name="name"
454: label="名称"
460: <Input autoFocus placeholder="例如：默认工作空间" />
464: name="description"
465: label="描述"
468: <Input.TextArea placeholder="可选" rows={3} maxLength={512} showCount />
```

## `pages/workspace-tasks/index.tsx`

行为追踪：@/services/platform/api: archiveTask, assignTask, cancelTask, deleteTask, getTask, getWorkspace, listTasks, listWorkspaceAgents, markFailedTask, reopenTask, requeueTask, resumeTask, runNowTask, updateTask, type WorkspaceAgentDto, type WorkspaceWithPermDto,; ./api: watchTasks。

```tsx
83: { value: 'p0', label: 'P0' },
84: { value: 'p1', label: 'P1' },
85: { value: 'p2', label: 'P2' },
86: { value: 'p3', label: 'P3' },
378: const label = COMMAND_LABELS[command];
380: title: `${label}该任务？`,
610: label: agent.displayName || agent.name || agent.agentId,
630: title: (
638: <Button
645: <Button
652: <Button
686: { label: '看板', value: 'board' },
687: { label: '列表', value: 'table' },
692: placeholder="优先级"
704: placeholder="Agent"
714: placeholder="搜索标题/描述"
797: title="标记失败"
834: title="任务看板"
891: label: agent.displayName || agent.name || agent.agentId,
903: title={mode === 'assign' ? '指派任务' : '执行任务'}
923: placeholder="选择 Agent"
980: placeholder="失败原因（必填）"
985: <Button onClick={onCancel}>取消</Button>
986: <Button
```

## `pages/workspace-tasks/SchedulerDrawer.tsx`

行为追踪：@/services/platform/api: evaluateTaskAutoDispatch, executeTaskSchedulerAction, getTaskSchedulerStatus, updateTaskSchedulerPolicy,。

```tsx
201: title: '任务',
202: dataIndex: 'taskId',
213: { title: '判定', dataIndex: 'verdict', width: 82, render: verdictLabel },
215: title: '原因码',
216: dataIndex: 'code',
221: title: 'Agent / 下一时间',
239: title={
249: extra={<Button icon={<ReloadOutlined />} onClick={() => void load()}>刷新</Button>}
286: <Button
296: <Button
305: <Button
313: <Button
329: <Card size="small"><Statistic title="Idle Agent" value={last?.idleAgents ?? 0} /></Card>
330: <Card size="small"><Statistic title="候选 / 可派发" value={`${last?.candidates ?? decisions.length} / ${last?.eligible ?? decisions.filter((item) => item.verdict === 'Eligible' || item.verdict === 0).length}`} /></Card>
331: <Card size="small"><Statistic title="本轮启动" value={last?.started ?? 0} /></Card>
332: <Card size="small"><Statistic title="本轮修复" value={last?.repaired ?? 0} /></Card>
336: <Descriptions.Item label="最近扫描">{formatTime(last?.completedAtUtc)}</Descriptions.Item>
337: <Descriptions.Item label="预计下次">{formatTime(status.nextScanEstimateUtc)}</Descriptions.Item>
338: <Descriptions.Item label="Busy / Unknown">{last?.busyAgents ?? 0} / {last?.unknownAgents ?? 0}</Descriptions.Item>
339: <Descriptions.Item label="Tracked / Cleanup">{last?.tracked ?? 0} / {last?.cleanupRequired ?? 0}</Descriptions.Item>
370: <Form.Item name="enabled" label="自动调度" valuePropName="checked">
373: <Form.Item name="eventDrivenEnabled" label="事件驱动" valuePropName="checked">
376: <Form.Item name="mode" label="运行模式">
381: { label: 'Shadow', value: 'shadow' },
383: label: 'Authoritative',
393: name="scanIntervalSeconds"
394: label="恢复扫描周期（秒）"
400: name="candidateLimit"
401: label="候选上限"
407: name="maxStartsPerScan"
408: label="每轮最多启动"
421: <Button
```

## `pages/workspace-tasks/TaskBoard.tsx`

本组件使用动态定义/子组件；请沿 imports 和 render 返回继续核对。

## `pages/workspace-tasks/TaskCard.tsx`

```tsx
83: { key: 'open', label: '查看详情', onClick: () => actions.onOpen(task) },
87: label: '编辑',
95: label: task.autoDispatchEnabled ? '退出自动调度' : '纳入自动调度',
102: label: '指派',
110: label: '执行',
118: label: '重新打开',
125: label: '恢复',
132: label: '重新排队',
139: label: '标记失败',
147: label: '取消',
155: label: '归档',
180: aria-label={task.title}
208: <Button
212: aria-label="任务操作"
```

## `pages/workspace-tasks/TaskColumn.tsx`

```tsx
75: title={`已加载 ${slice.items.length} / 共 ${slice.totalCount}`}
133: <Button
```

## `pages/workspace-tasks/TaskDetailsDrawer.tsx`

行为追踪：@/services/platform/api: createTaskComment, deleteTask, listTaskComments, updateTask,。

```tsx
116: title: '删除该任务？',
196: label: TASK_STATUS_LABELS[status],
202: title={task ? `任务详情：${task.title}` : '任务详情'}
210: <Button type="primary" onClick={() => onCommand(task, 'Reopen')}>
214: <Button onClick={() => onEdit(task)}>编辑</Button>
224: <Descriptions.Item label="任务 ID">{task.taskId}</Descriptions.Item>
225: <Descriptions.Item label="状态">
228: <Descriptions.Item label="看板列">{task.boardColumn}</Descriptions.Item>
229: <Descriptions.Item label="优先级">
232: <Descriptions.Item label="执行窗口">
235: <Descriptions.Item label="描述">
238: <Descriptions.Item label="验收标准">
241: <Descriptions.Item label="偏好 Agent">
244: <Descriptions.Item label="自动调度">
249: <Descriptions.Item label="任务类型">
252: <Descriptions.Item label="所需能力">
259: <Descriptions.Item label="Provider / Model">
264: <Descriptions.Item label="兼容 Agent 回退">
267: <Descriptions.Item label="活跃 Assignment">
270: <Descriptions.Item label="最早可执行">
273: <Descriptions.Item label="截止">
276: <Descriptions.Item label="下次可派发">
279: <Descriptions.Item label="排序序号">{task.sortOrder}</Descriptions.Item>
280: <Descriptions.Item label="进度">
286: <Descriptions.Item label="阻塞">
291: <Descriptions.Item label="失败">
295: <Descriptions.Item label="版本">{task.version}</Descriptions.Item>
296: <Descriptions.Item label="创建者">{task.createdBy ?? '—'}</Descriptions.Item>
297: <Descriptions.Item label="更新者">{task.updatedBy ?? '—'}</Descriptions.Item>
298: <Descriptions.Item label="创建时间">
301: <Descriptions.Item label="更新时间">
304: <Descriptions.Item label="完成时间">
307: <Descriptions.Item label="失败时间">
310: <Descriptions.Item label="归档时间">
330: placeholder="选择目标状态"
340: placeholder="流转备注（可选）"
344: <Button
398: placeholder="添加备注…"
403: <Button
419: <Button danger onClick={handleDelete}>
```

## `pages/workspace-tasks/TaskEditorDrawer.tsx`

行为追踪：@/services/platform/api: createTask, getTask, updateTask, type WorkspaceAgentDto,。

```tsx
41: title: string;
94: title: task.title,
113: title: '',
133: title: values.title,
203: title: serverTask.title,
259: label: TASK_PRIORITY_LABELS[value],
263: label: TASK_EXECUTION_WINDOW_LABELS[value],
267: label: agent.displayName || agent.name || agent.agentId,
273: title={isEdit ? '编辑任务' : '新建任务'}
279: <Button onClick={onClose}>取消</Button>
280: <Button type="primary" loading={saving} onClick={handleSave}>
299: name="title"
300: label="标题"
303: <Input placeholder="任务标题" />
305: <Form.Item name="description" label="描述">
306: <TextArea rows={3} placeholder="任务描述" />
308: <Form.Item name="acceptanceCriteria" label="验收标准">
309: <TextArea rows={3} placeholder="验收标准（Acceptance Criteria）" />
312: <Form.Item name="priority" label="优先级" style={{ width: 140 }}>
315: <Form.Item name="executionWindow" label="执行窗口" style={{ width: 160 }}>
319: <Form.Item name="preferredAgentId" label="偏好 Agent">
325: placeholder="可选"
329: name="autoDispatchEnabled"
330: label="自动调度"
338: name="taskType"
339: label="任务类型"
348: { value: 'general', label: '通用 general' },
349: { value: 'implementation', label: '开发 implementation' },
350: { value: 'test', label: '测试 test' },
351: { value: 'review', label: '评审 review' },
352: { value: 'research', label: '研究 research' },
353: { value: 'documentation', label: '文档 documentation' },
354: { value: 'deployment', label: '部署 deployment' },
355: { value: 'operations', label: '运维 operations' },
357: placeholder="general 或自定义类型"
361: name="allowAgentFallback"
362: label="兼容 Agent 回退"
369: name="requiredCapabilityIds"
370: label="所需能力"
376: placeholder="例如 cap-file-write、cap-shell"
381: name="requiredProviderId"
382: label="限定 Provider"
385: <Input placeholder="可选" />
388: name="requiredModelId"
389: label="限定 Model"
392: <Input placeholder="可选" />
399: <Form.Item name="notBeforeUtc" label="最早可执行">
402: <Form.Item name="dueAtUtc" label="截止时间">
406: <Form.Item name="sortOrder" label="排序序号（小值在前）">
413: title="版本冲突"
417: <Button key="keep" onClick={() => setConflict(null)}>
420: <Button key="server" onClick={handleConflictUseServer}>
423: <Button
```

## `pages/workspace-tasks/TaskEventTimeline.tsx`

```tsx
29: label: `事件时间线（${sorted.length}）`,
```

## `pages/workspace-tasks/TaskExecutionLink.tsx`

本组件使用动态定义/子组件；请沿 imports 和 render 返回继续核对。

## `pages/workspace-tasks/TaskTable.tsx`

```tsx
86: title: `移除所选 ${selectedTasks.length} 个任务？`,
108: title: '标题',
109: dataIndex: 'title',
112: render: (title: string, record) => (
113: <Button type="link" size="small" onClick={() => onOpen(record)}>
119: title: '优先级',
120: dataIndex: 'priority',
128: title: '状态',
129: dataIndex: 'status',
139: title: '列',
140: dataIndex: 'boardColumn',
148: title: 'Agent',
149: dataIndex: 'preferredAgentId',
156: title: '截止',
157: dataIndex: 'dueAtUtc',
163: title: '更新时间',
164: dataIndex: 'updatedAtUtc',
193: placeholder="批量设置状态"
202: <Button
210: <Button
```
