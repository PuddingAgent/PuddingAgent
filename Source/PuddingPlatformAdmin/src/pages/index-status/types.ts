// ── Slice B：全文索引状态只读面板 wire DTO ───────────────────────────
// 唯一契约 = 后端 `PuddingHost/Services/FullTextIndexStatusProbe.cs`
// （record 字段名即 wire 字段名；camelCase 由 ASP.NET 默认序列化策略产生）。
// ⚠️ 本切片**只做全文索引块**：这里不得出现 codeIndex（符号索引属 S-A2）。
//
// R4 可空性纪律：`null` = **未知**（探测失败 / 不可知），与 `false` / `0` 是不同事实。
// 因此所有「可为 null」的字段一律显式写 `| null`，禁止用 `?:` 或不可空类型糊过去。

/** 全文索引状态响应根对象。 */
export interface FullTextIndexStatusSnapshot {
  /** 快照生成时刻（UTC）。 */
  generatedAtUtc: string;
  /** 全文索引块（本切片唯一的块）。 */
  fullText: FullTextIndexStatusDetail;
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
