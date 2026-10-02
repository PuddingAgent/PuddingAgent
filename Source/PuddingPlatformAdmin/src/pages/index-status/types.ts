// ── Slice B：全文索引状态只读面板 wire DTO ───────────────────────────
// 唯一契约 = 后端 `PuddingHost/Services/FullTextIndexStatusProbe.cs`
// （record 字段名即 wire 字段名；camelCase 由 ASP.NET 默认序列化策略产生）。
// ⚠️ 本文件是**既有 wire 契约**：`fullText` 相关的类型（含字段名/顺序/可空性）自 Slice B 起冻结，
//    一律**不得改动或删除**（后端 A1 断言同样要求它一个字段都不变）。
// S-A2/P3 起新增**符号（代码）索引块** `codeIndex`：只**新增**类型与一个**可选**根字段，
//    既有定义逐字未动（见文件末尾「符号索引块」一节）。
//
// R4 可空性纪律：`null` = **未知**（探测失败 / 不可知），与 `false` / `0` 是不同事实。
// 因此所有「可为 null」的字段一律显式写 `| null`，禁止用 `?:` 或不可空类型糊过去。

/** 全文索引状态响应根对象。 */
export interface FullTextIndexStatusSnapshot {
  /** 快照生成时刻（UTC）。 */
  generatedAtUtc: string;
  /** 全文索引块（Slice B 起冻结）。 */
  fullText: FullTextIndexStatusDetail;
  /**
   * 符号（代码）索引块（S-A2 新增）。
   *
   * ⚠️ **可选**是刻意的：该块由 Core 上的 `CodeIndexStatusProbe` 产出，**要重启 Core 才生效**。
   * 因此在未重启的宿主上，响应里**根本没有这个键** —— `undefined`（键缺失）= 「本块未接入」，
   * 与「块存在但 `projects` 为空」是**两个不同事实**，前端必须分开渲染（不得用 `?? { projects: [] }` 折叠）。
   */
  codeIndex?: CodeIndexStatusDetail | null;
}

/** 局部维护循环（`FullTextIndex:Maintenance`，S5b）的如实上报。 */
export interface FullTextIndexMaintenanceStatus {
  /** 有效配置里是否存在该子节。 */
  configured: boolean;
  /** 生效开关；**不可知为 `null`**（不得拿默认值冒充生效值）。 */
  enabled: boolean | null;
  /** 人可读说明（为什么是这个值）。 */
  note: string;
}

/** 单个 scope 的只读观测。 */
export interface FullTextIndexScopeStatus {
  /** scope 的规范化绝对路径（配置受理结果）。 */
  scopePath: string;
  /** scope 目录当前是否存在。 */
  scopeExists: boolean;
  /** 组件的 `HasIndex`；**探测失败为 `null`**。 */
  hasIndex: boolean | null;
  /** 索引目录（唯一真源：`IFullTextIndexRootedEngine.ResolveIndexDirectory`）；解析不出来为 `null`。 */
  indexDirectory: string | null;
  /** 索引目录是否存在；**读不出来为 `null`**。 */
  indexDirectoryExists: boolean | null;
  /** 索引目录条目数（文件 + 子目录，递归）；**未知为 `null`**（「没有」不等于「0」）。 */
  indexEntryCount: number | null;
  /** 索引目录字节合计（递归）；**未知为 `null`**。 */
  indexBytes: number | null;
  /** 索引目录最后写入时间（UTC）；**未知为 `null`**。 */
  indexDirectoryLastWriteUtc: string | null;
}

/** 供给 job 的可观测状态（逐字取自 `IFullTextIndexSupplyCoordinator.ListStatusAsync`，不加工、不推断）。 */
export interface FullTextIndexJobStatus {
  /** job 标识（同 scope 的幂等合并共享同一个）。 */
  jobId: string;
  /** 状态机状态（组件枚举名，逐字）。 */
  state: string;
  /** 阶段（组件字符串枚举，逐字）。 */
  phase: string;
  /** 进入状态机的时刻（UTC）。 */
  startedAt: string;
  /** 终态时刻（UTC）；**非终态为 `null`**。 */
  finishedAt: string | null;
  /** 最近一次可读说明（组件原文）；无说明为 `null`。 */
  message: string | null;
  /** 清点到的可索引文件数（组件口径：`DiscoveredFileCount`）。 */
  indexedFileCount: number;
  /** 清点到的语料字节数（组件口径：`DiscoveredBytes`）。 */
  totalBytes: number;
  /** 终态耗时（毫秒）；**非终态为 `null`**（不拿「现在」推算，避免随时间漂移的假事实）。 */
  elapsedMs: number | null;
}

/**
 * 台账为空时的**如实原因**（I4：绝不伪造空台账）。
 * 后端 wire 类型是 `string?`，故此处保持 `string | null`
 * （不写成封闭 union，以免把未登记的取值伪装成「不合法」）。
 * 已知取值（`FullTextIndexStatusJobReasons`）：
 * - `composition-not-created`：供给组合**从未被构造**，因此进程内不存在任何 job；
 * - `no-jobs-recorded`：组合已存在（台账可读），但里面确实一条 job 都没有；
 * - `ledger-read-failed`：台账**读取失败** ⇒ 内容不可知（≠ 没有 job）。
 */
export type FullTextIndexJobsReasonText = string;

/** 全文索引块的配置真值 + 观测真值（每一项都可追溯到单一真源）。 */
export interface FullTextIndexStatusDetail {
  /** 有效配置里是否存在 `FullTextIndex` 节。 */
  configured: boolean;
  /** `FullTextIndex:Enabled` 真值。 */
  enabled: boolean;
  /** 索引根目录（`FullTextIndexOptions.IndexRootDirectory`，宿主编译期绑定）。 */
  indexRoot: string;
  /** 索引根目录当前是否存在（只读探测）。 */
  indexRootExists: boolean;
  /** 相对 scope 的解析基准（配置值）；**未配置为 `null`**。 */
  workspaceRoot: string | null;
  /** 单个索引库体积上限（配置值，字节）。 */
  maxIndexBytes: number;
  /** 最小重建间隔（配置值，TimeSpan ⇒ 字符串，如 `"12:00:00"`）。 */
  minRebuildInterval: string;
  /** 经 `FullTextIndexSupplyResolver.Resolve` 的**受理结果**。 */
  acceptedScopes: string[];
  /** 逐条拒绝原因；**空数组表示无拒绝**。 */
  rejectedReasons: string[];
  /** 供给组合是否**已经**被构造（= accessor 的 Current 非空）。 */
  compositionCreated: boolean;
  /** S5b 的局部维护循环开关（如实上报）。 */
  maintenance: FullTextIndexMaintenanceStatus;
  /** 逐 scope 观测（只读）。 */
  scopes: FullTextIndexScopeStatus[];
  /** 供给 job 台账（逐字取自 `ListStatusAsync`）。 */
  jobs: FullTextIndexJobStatus[];
  /** 台账为空时的**如实原因**；有 job 时为 `null`。 */
  jobsReason: FullTextIndexJobsReasonText | null;
}

// ══ 符号索引块（S-A2 / P3 新增）════════════════════════════════════════
// 唯一契约 = 后端 `Source/PuddingHost/Services/CodeIndexStatusProbe.cs`（探针 + 两个 record）。
// 权威字段名清单（含「维护态**前 23 个** camelCase 字段 = 冻结前缀」的逐个点名断言）：
//   · `Tests/PuddingHost.Tests/Hosting/SA2CodeIndexStatusTests.cs`（A5 / A6，第 205~250 行）
//   · `Tests/PuddingHost.Tests/Hosting/SA2CodeIndexTestDoubles.cs`（`Sa2Samples.MaintenanceStatus` 逐字段赋值，第 120~150 行）
// ⇒ 本节的字段名与**声明顺序**都是**从后端反读**的，不是猜的；顺序即 wire 顺序（ASP.NET camelCase）。

/**
 * 维护态（`ICodeIndexMaintenance.GetScopeStatuses` 的记录，后端**原样透传**）。
 *
 * ⚠️ **前 23 个字段是冻结核心**：名字、顺序、可空性与 `SA2CodeIndexStatusTests.A5` 的断言数组
 * **逐位一一对应**，不得改名 / 删除 / 重排；L2 证据层的列即由此顺序生成
 * （见 `health.ts` 的 `CODE_INDEX_MAINTENANCE_FIELDS`）。
 * 末尾 9 个是 D4「源维护」追加字段（对应后端 `CodeIndexMaintenanceScopeStatus` 记录尾部的默认参数）。
 *
 * ⚠️ 枚举字段（`sourceMaintenanceMode` / `lastSourceMaintenanceCommitOutcome`）在 wire 上是
 * **number**（ASP.NET Core 默认枚举序列化；宿主未注册 `JsonStringEnumConverter`）。
 */
export interface CodeIndexMaintenanceStatus {
  /** 拥有该 scope 的 workspace。 */
  workspaceId: string;
  /** scope / 项目标识（注册表与维护驱动共用同一 id 空间）。 */
  scopeId: string;
  /** 驱动观测到的根路径。 */
  rootPath: string;
  /** 观测到的文件系统版本号。 */
  observedVersion: number;
  /** 期望（目标）版本号。 */
  desiredVersion: number;
  /** 已提交（落库）版本号。 */
  committedVersion: number;
  /** 在「进行中」期间被标记的次数。 */
  markedWhileInFlightCount: number;
  /** **待索引**（驱动待办）；D3 里被误当成注册态的 `Pending` 就是它。 */
  indexPending: boolean;
  /** **索引进行中**（真正的活动态；B 卡据此画涟漪）。 */
  indexInFlight: boolean;
  /** 需要重建索引。 */
  needsReconcile: boolean;
  /** 需要重建的**如实原因**；无需重建为 `null`。 */
  reconcileReason: string | null;
  /** 触发重建请求的批次数（累计）。 */
  reconcileRequestCount: number;
  /** 观测到的删除路径数（累计）。 */
  removalObservationCount: number;
  /** 最近一批的删除路径（**只读原样**，不做截断/聚合）。 */
  lastRemovalPaths: string[];
  /** 真正被删除的已索引文件数（累计）。 */
  removedFileCount: number;
  /** 逐个增量重建的文件数（累计）。 */
  incrementallyIndexedFileCount: number;
  /** 升级为整 scope 重建的批次数（累计）。 */
  scopeEscalationCount: number;
  /** 校准真正清掉的陈旧索引文件数（累计）。 */
  sweptFileCount: number;
  /** 累计校准运行次数（被拒绝的也算 —— 数字不吞掉尝试）。 */
  calibrationRunCount: number;
  /** 因根路径缺失/不可读而被**拒绝**的校准次数（累计）；`> 0` ⇒ B 卡 warn。 */
  rejectedCalibrationRunCount: number;
  /** 最近一次校准完成时刻（UTC）；**从未校准过为 `null`**（≠ 时间戳 0）。 */
  lastCalibrationAtUtc: string | null;
  /** 当前处于校准宽限窗口内的观测路径条数。 */
  recentObservationCount: number;
  /** 是否已挂接变更源（watcher）；`false` ⇒ 不会被增量感知。 */
  watcherAttached: boolean;

  // ── D4「源维护」（2026-10-02）：让「索引可能不全」读得到，而不是只能翻日志 ──
  /** 本轮生效的变更施用链路（枚举 **number**：`0 = Legacy` 逐文件 / `1 = Coordinator` 源维护协调器）。 */
  sourceMaintenanceMode: number;
  /** 源维护协调器已运行轮数（仅在 `Coordinator` 模式递增）。 */
  sourceMaintenanceRunCount: number;
  /** 累计「提取并原子提交」的文件数。 */
  sourceMaintenanceExtractedFileCount: number;
  /** 累计「内容指纹一致 ⇒ 跳过提取、只刷新消费者视图」的文件数。 */
  sourceMaintenanceReusedFileCount: number;
  /** 累计清掉的索引孤儿行数。 */
  sourceMaintenanceOrphanFileCount: number;
  /** **最近一轮**的未定论路径数（失败 + 本轮没有定论）；`> 0` ⇒ 索引**可能不全**。 */
  sourceMaintenanceUnresolvedPathCount: number;
  /** **最近一轮**确认删除的文件数。 */
  sourceMaintenanceDeletedFileCount: number;
  /** **最近一轮**账本提交结果（枚举 **number**：`0 = Committed` / `1 = Superseded` / `2 = StaleEpoch`；`null` = 从未跑过）。 */
  lastSourceMaintenanceCommitOutcome: number | null;
  /** **最近一轮**语言侧复用的工程/编译快照标识；**为 `null` ⇒ 这一轮退化成逐文件提取**。 */
  lastSourceMaintenanceSessionKey: string | null;
}

/**
 * 单个项目的只读条目。
 *
 * **D3：两个状态字段、两个真源，绝不合并** —— `registration*` 来自**索引注册表**（SQLite 项目记录表），
 * `maintenance` 来自**维护驱动的进程内状态**。二者在 UI 上必须分别呈现为不同视觉语义，
 * 不得合成一个「状态」（这也是 B 卡的设计约束）。
 *
 * **陈旧**：`stale === true` 的**唯一**来源是「未在注册表登记」**或**「`rootPathExists === false`」
 * （后端 fail-closed，D1/D2）。`maintenance === null`（未挂到驱动）**不**导致陈旧。
 */
export interface CodeIndexProjectStatus {
  /** 拥有该项目的 workspace。 */
  workspaceId: string;
  /** 项目 / scope 标识（注册表与维护驱动同 id 空间）。 */
  projectId: string;
  /** 展示名；**仅来自维护驱动的条目为 `null`**（不是空串）。 */
  displayName: string | null;
  /** 项目根路径。 */
  rootPath: string;
  /** 是否在注册表登记（D1 判定输入）。 */
  registered: boolean;
  /** 注册表**投影**（`Active` / `Covered` / `Removed` / `Failed`）；未登记为 `null`。 */
  registrationState: string | null;
  /** 项目记录**原始**生命周期状态（含 `Registering`）；未登记为 `null`。 */
  registrationStatus: string | null;
  /** scope 来源（`Manual` / `Auto` / `Pinned`）；未知为 `null`。 */
  registrationSource: string | null;
  /** 维护态 32 字段（23 冻结核心 + 9 个 D4「源维护」）原样透传；**未挂到驱动为 `null`**（原因见 `maintenanceReason`）。 */
  maintenance: CodeIndexMaintenanceStatus | null;
  /** 维护态缺席时的**如实原因**（`scope-not-attached` / `maintenance-driver-not-running`）；正常为 `null`。 */
  maintenanceReason: string | null;
  /** 根路径的**目录存在性**判定（D2 判定输入）。 */
  rootPathExists: boolean;
  /** 陈旧：未登记 **或** 根路径不存在。 */
  stale: boolean;
}

/**
 * 符号（代码）索引块（`CodeIndexStatusDetailSnapshot`）。
 *
 * R5 三态纪律：计数类字段为 `null` = **读不出来（未知）**，与 `0`（真的是 0）是不同事实；
 * `note` 非空 = 整块降级，其内容就是**如实原因**（已知原因码见 `code-index-status-unavailable`）。
 */
export interface CodeIndexStatusDetail {
  /** 本次快照遍历的 workspace（真源：数据根 `workspaces` 下的目录名）。 */
  workspaceIds: string[];
  /** 维护驱动是否在跑；**整块降级为 `null`**（不得折叠成 `false` = 「没在跑」）。 */
  maintenanceRunning: boolean | null;
  /** 驱动自构造以来处理的合并批次总数；读不出来为 `null`。 */
  batchesProcessed: number | null;
  /** 需要重建索引的批次总数；读不出来为 `null`。 */
  reconcileRequests: number | null;
  /** 观测到的删除路径总数；读不出来为 `null`。 */
  removalObservations: number | null;
  /** 当前待重建的 scope 数；读不出来为 `null`。 */
  pendingReconcileScopeCount: number | null;
  /** 逐项目条目（注册表 ∪ 维护驱动，按 workspace/projectId 稳定排序）。 */
  projects: CodeIndexProjectStatus[];
  /** 整块读不出来时的**如实原因**；正常为 `null`。 */
  note: string | null;
}
