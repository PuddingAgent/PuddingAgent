## S5b锛?026-09-27锛夆€?鍏ㄦ枃绱㈠紩銆屽眬閮ㄧ淮鎶ゅ惊鐜€嶆帴鍏ュ涓伙紙**榛樿鍏抽棴 鈬?闆跺壇浣滅敤**锛?
渚涚粰渚у彧瑙ｅ喅**鍚姩鏃堕寤?*锛涙湰鍒€鎶婄粍浠跺凡浜や粯鐨?*杩愯鏈熷眬閮ㄧ淮鎶ゅ惊鐜?*锛坵atcher + mtime 琛ュ伩鎵弿 + 浣庨浣撴 鈫?鍚屼竴浠芥寜璺緞鍙樻洿闆?鈫?灞€閮ㄥ啓寮曟搸锛夋帴杩涘涓伙紝浣裤€岃鏂欏彉鏇?鈫?绱㈠紩鑷姩璺熻繘銆嶆垚涓哄彲寮€鍙叧鐨勮兘鍔涖€?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Hosting/IFullTextIndexMaintenanceComposition.cs` | 馃攽 瀹夸富渚х淮鎶ょ鍙ｏ細`IFullTextIndexMaintenanceComposition`锛坄Maintenance` / `Scopes` / `ComponentOptions` / `IndexOptions` / `LiveEngine`锛? **鎯版€?*宸ュ巶 `IFullTextIndexMaintenanceCompositionFactory`銆傞粯璁ゅ叧闂椂宸ュ巶**姘镐笉琚皟鐢?*锛堢粍鍚堝疄渚嬪寲鎺ㄨ繜鍒扮湡姝ｈ繘鍏ョ淮鎶よ矾寰勪箣鍚庯級銆?|
| `Hosting/FullTextIndexMaintenanceOptions.cs` | 閰嶇疆鑺傚悕 `FullTextIndex:Maintenance`锛堝敮涓€锛? 绾嚱鏁?`ApplySingleSource`锛?*鍗曚竴鐪熸簮**锛歚Scopes`/`WorkspaceRoot`/`MaxIndexBytes` 鍙栬嚜渚涚粰鑺傦紝`IndexRootDirectory` 鍙栬嚜鏌ヨ渚?`FullTextIndexOptions`锛涘叾浣欐棆閽€愬瓧淇濈暀缁戝畾鍊硷紝**涓嶅仛榛樿鍊煎厹搴?*锛? `BuildScopes`锛堣鑼冮敭璧扮粍浠剁湡婧?`FullTextChangeCoalescer.NormalizeComparisonKey`锛屽涓?*涓嶅鍒?*瑙勫垯锛沗IndexDirectory` 鐣?null 鐢辩粍浠惰嚜鎺級銆?|
| `Hosting/LuceneFullTextIndexMaintenanceCompositionFactory.cs` | 鐢熶骇瑁呴厤锛歚LuceneFullTextIndexMaintenanceEngine`锛堝眬閮ㄥ啓鍐呮牳 = **鏌ヨ渚у悓涓€ `LuceneSearchEngine` 瀹炰緥**锛屽惁鍒?`InvalidateScope` 鎵撳湪鍒殑瀹炰緥涓婄瓑浜庢病澶辨晥锛? `FileSupplyLease` + `SearchEngineScopeReaderInvalidation` + `LuceneFullTextIndexMaintenance`銆傜绾︾瓑寰呬笂鐣岄€愬瓧鍙?`MaintenanceOptions.LeaseWaitUpperBound`锛岄绠椾笉鍐欑浜岄亶瀛楅潰閲忋€?|
| `Services/FullTextIndexMaintenanceHostedService.cs` | 鐢熷懡鍛ㄦ湡澹筹紙褰㈡€佺収 `IndexPrebuildService`锛夛細`Enabled=false` 鈬?**棣栧彞杩斿洖**锛堜笉鏋勯€犵粍鍚堛€佷笉瑙ｆ瀽 scope銆佷笉纰扮储寮曟牴銆? watcher/绾跨▼/Error锛夛紱寮€鍚絾**渚涚粰鏈紑鍚?*鎴栫淮鎶ら厤缃潪娉?鈬?fail-closed 璁?Error 涓斾粈涔堥兘涓嶅仛锛堜笉闈欓粯鍙栭粯璁ゅ€硷級锛沗StartAsync` 姘镐笉闃诲瀹夸富锛涘惎鍔?鍋滄**骞傜瓑**锛沗StopAsync` 鍏堢瓑鍦ㄩ€斿惎鍔ㄦ敹灏撅紙涓婄晫 `StartCompletionTimeout`锛岄粯璁?30s锛夈€?|
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 娉ㄥ唽锛歚Configure<MaintenanceOptions>(GetSection(FullTextIndexMaintenanceOptions.SectionName))` + `AddSingleton<IFullTextIndexMaintenanceCompositionFactory>` + `AddHostedService<FullTextIndexMaintenanceHostedService>()`銆?|

娴嬭瘯锛坄../Tests/PuddingHost.Tests/Hosting/`锛?*瀹夸富 154 鈫?163 鐢ㄤ緥**锛汼5b 鏂板 9 鏉★級锛?`S5bFullTextIndexMaintenanceHostWiringTests`锛圛1 榛樿鍏抽棴闆跺壇浣滅敤锛氱粍鍚?0 鏋勯€?/ 缁存姢鍣?0 Start / 寮曟搸 0 璋冪敤 / 绱㈠紩鏍硅繛鐩綍閮戒笉寤?/ 0 Error锛汭2 `StartAsync` 鎭板ソ 1 娆′笖 scope 閿笌**鐪熷疄渚涚粰鍗忚皟鍣?*閫愬瓧绗︾浉鍚岋紱I3 寮曟搸涓庣储寮曢€夐」**寮曠敤鐩哥瓑**锛汭4 `StopAsync` 鎭板ソ 1 娆★紱I5 閰嶇疆娴佸叆 + **璇遍サ棰勭畻**琚緵缁欒妭瑕嗙洊锛汭6 闈炴硶閰嶇疆 fail-closed 涓斿瀹炶杩濊椤癸紱闄勫姞锛氫緵缁欐湭寮€鍚?鈬?鎷掔粷鑰屼笉鐚?scope锛夈€?`S5bFullTextIndexMaintenanceHostBindingTests`锛堢湡瀹炵粍鍚堟牴锛歚system.json` 鈫?缁戝畾 + `Assert.Same(engine, composition.LiveEngine)` + hosted 鎭板ソ娉ㄥ唽涓€娆?+ 鍏ㄧ▼涓嶈Е纰扮储寮曟牴锛夈€?`S5bMaintenanceTestDoubles.cs`锛堟浛韬級銆乣S5SupplyTestDoubles.cs`锛堣ˉ `ProbeDocuments` 鈥斺€?姝ゅ墠閬楁紡瀵艰嚧娴嬭瘯宸ョ▼ **CS0535**锛夈€?
**2026-09-29 杩藉姞锛堝榻愪粨搴撳悗鍙伴噸娲荤粺涓€鍏ュ彛锛?*锛歚FullTextIndexMaintenanceHostedService` 鐨勫惎鍔ㄤ綔涓氫粠 `Task.Run`
鏀硅蛋 `PuddingCode.Core.BackgroundWork`锛堜笓鐢?`BelowNormal` 绾跨▼ + 閫佸彇娑堟湁鐣屾敹鏁?+ 甯﹁€楁椂鏃ュ織锛夆€斺€?缁存姢鍚姩璺緞
鏈韩瑕佸仛璇枡鏍″噯鎵弿锛屽睘銆屽悗鍙伴噸娲汇€嶏紝涓嶅緱涓庨灞?/ 浜や簰鎶?CPU锛堢敤鎴?09-29 鏂瑰悜锛岃寖鏈 `b96ae88` 鐨?MCP 涓?jieba 涓ゅ锛夈€?琛屼负濂戠害涓嶅彉锛堥鍙?`Enabled` 闂ㄦ帶 / 鍚姩鍋滄骞傜瓑 / 鏈閰嶅垯涓嶈皟鐢ㄧ粍浠?`StopAsync`锛夛紱鏂板 **I7** 鏂█鎶婄嚎绋嬩簨瀹為拤浣?锛堝悕瀛?`pudding-bg-fulltext.maintenance` / `BelowNormal` / `IsBackground`锛夛紝瀹夸富娴嬭瘯 **163 鈫?164**銆?M3 鍙樺紓锛堟妸浣滀笟浣撴敼閬?`Task.Run`锛夆噿 鎭板ソ I7 鍙樼孩銆侀浂杩炲甫锛涘鍘熷悗 blob 閫愪綅鐩稿悓銆?
鈿狅笍 **鏈垁椤哄甫淇涓€澶勪富骞茬己闄?*锛歚Platform.cs` 鐨?4 澶勬敞鍐?瑁呴厤琛屾浘琚彁浜わ紝浣?*瀵瑰簲鐨?4 涓敓浜х被鍨嬫枃浠跺綋鏃舵湭鍏ュ簱** 鈬?骞插噣妫€鍑?`CS0246`銆丠EAD 涓嶅彲缂栬瘧銆傜敱 `c76c613` 琛ラ綈鍚庡涓荤紪璇戞仮澶?**0 閿欒**銆?
鈿狅笍 **鏈仛 / 鏈瘉瀹?*锛氣憼 銆岀淮鎶や笌渚涚粰鍏辩敤鍚屼竴寮曟搸瀹炰緥銆嶅湪**鐪熷疄杩愯鏈熷苟鍙?*涓嬬殑鍙鎬э紙澶?reader / 璺ㄨ繘绋嬬绾︼級鏈仛杩愯鎬侀獙璇侊紙浠呴潤鎬佽閰?+ 鍗曟祴灞傞潰鐨勫紩鐢ㄧ浉绛夛級锛涒憽 銆屽彉鏇村悗鑷姩鏇存柊銆?*灏氭湭鍦ㄧ敓浜х储寮曟牴涓婂疄璺?* 鈥斺€?闇€鐢ㄦ埛鏄惧紡寮€鍚?`FullTextIndex:Maintenance:Enabled=true` 骞堕噸鍚?Core銆?


`Hosting/PuddingApplicationHost.cs` 鐨?Desktop 妯″紡浣跨敤鏄庣‘ Host ApplicationName 涓庣▼搴忕洰褰曪紝閬垮厤 WinUI 鍏ュ彛鎴栧伐浣滅洰褰曟薄鏌?MVC/璧勬簮鍙戠幇銆俙PuddingDataRootLease` 鐢?Desktop Composition 涓?Console/鍘嗗彶 Child 鍏ュ彛鍏卞悓鎸佹湁锛岀姝㈡柊鐗堝叆鍙ｅ苟鍙戜娇鐢ㄥ悓涓€ DataRoot銆侱esktop 涓嶅啀鍚姩 PuddingAgent.exe锛汬ost 鐢熷懡鍛ㄦ湡鐢?Composition 椹卞姩銆?
# PuddingHost CodeMAP

> 鍞竴 Host 缁勫悎鏍?| Console 涓?Desktop 鍏辩敤 DI 路 Browser Bridge 路 椋炰功杩炴帴鍣?
## 缁勫悎鏍?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `PuddingHostAssemblyMarker.cs` | 绋嬪簭闆嗘爣璁?|
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 鎴愬搧 Host 鐨?Platform/Runtime 缁勫悎娉ㄥ唽锛涘唴缃?Agent 妯℃澘鐩存帴浣跨敤 PuddingCore 鍞竴鏉冨▉婧愶紱鍖呭惈 MOA銆乂2 component registry/compiler銆丼QLite store/signal銆丄dmin 鎵嬪姩 Run/HTTP Hook command service銆丼ubAgent/鍥剧墖鐢熸垚/灞曠ず executor銆佷复鏃跺瓙浠ｇ悊鐩綍涓ら樁娈?GC銆乭osted worker 涓?replay-to-live follower锛沗TaskAgentCommandService` 涓?Singleton `task_*` 宸ュ叿鍚岀敓鍛藉懆鏈燂紝鏈嶅姟鍐呴儴姣忔璋冪敤閫氳繃 DbContextFactory 鍒涘缓鐙珛 DbContext锛涗笉鑳藉彧鍦ㄦ湭琚骇鍝佸叆鍙ｈ皟鐢ㄧ殑 Runtime 鎵╁睍閲屾敞鍐?worker |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 鎴愬搧 Host 鐨?Runtime/Tool 缁勫悎娉ㄥ唽锛沘ssembly scan 鑷姩鍙戠幇鐨勬柊宸ュ叿锛屽叾鏋勯€犱緷璧栦篃蹇呴』鍦ㄨ繖閲屾敞鍐岋紙渚嬪 `SavePreferenceTool` 鈫?`IUserPreferenceService`銆乣SkillEnforcerService` 鐨勫彲閫?`ISkillUsageTelemetrySink`锛氭敞鍐岀己澶辨椂璇ュ彲閫夊弬鏁?*闈欓粯涓?null**锛屼笉鎶ラ敊涔熶笉鎶涘紓甯革級 |
| `Tools/ImageReaderTool.cs` + `Tools/ImageReaderSourceResolver.cs` | 鍘熺敓闃呰涓庨澶勭悊锛歮etadata/read/prepare锛屽洓妗?detail锛岀缉鐣ュ浘銆佽鍓€?0搴︽棆杞€佺伆搴︺€丟aussian闄嶅櫔銆乯peg/png/webp缂栫爜锛涙簮鏀寔鏈湴/URL/鑱婂ぉartifact锛涘鐢?Platform ImagePreprocessing 鍜屾淳鐢熺紦瀛橈紝涓嶈皟鐢ㄦā鍨?Agent锛屾棤 helper 璺敱銆備綆鏉冮檺鍙婧愭枃浠讹紝URL姣忚烦SSRF鏍￠獙锛涜緭鍑烘簮/娲剧敓寮曠敤渚涘娆″尯鍩熻鍙?|
| `Hosting/PuddingApplicationInitializer.cs` | 鍚姩鏈熸暟鎹簱鍒濆鍖栵紱鍖呭惈 AppUsers銆乄orkspaceTask銆乀askPlanning/WorkUnit/AwaitHandle銆丟oal 鍙婇€氱敤缂栨帓 SQLite schema bootstrap锛屽凡鏈夋暟鎹簱涔熷繀椤诲箓绛夊崌绾э紱GoalSchemaBootstrapper 鍚庢墽琛?GoalRestartReconciler 鍚姩 reconcile锛堟寜 `goal_runs.resume_policy` 鍒嗘祦锛氶粯璁?disarm 涓?paused锛沗auto_resume_on_restart` 鍒欎繚鎸?Active 骞舵崲鍙?activation fence锛涜緭鍑?disarmed / auto-resumed 璁℃暟锛?|
| `Storage/StorageMaintenanceService.cs` | 馃攽 Core 鎵€鏈夌殑 SQLite/浠ｇ爜绱㈠紩鏄庣粏涓庡畨鍏ㄦ竻鐞嗭紱鍥哄畾璇箟鐧藉悕鍗曘€佹湇鍔＄棰勮銆佹壒閲忓垹闄ゃ€乧heckpoint/VACUUM |
| `Controllers/StorageManagementController.cs` | `/api/admin/storage/databases` 鍒嗘瀽銆佹竻鐞嗛瑙堜笌鎵ц API |
| `Hosting/StorageManagementAuthorization.cs` | 骞冲彴 admin JWT锛屾垨 DesktopChild Loopback + ControlToken 鐨勭鐞嗙瓥鐣?|

## Browser Bridge锛圥hase 2A锛?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `BrowserBridge/RemoteBrowserRuntime.cs` | Core 渚?Browser 浠ｇ悊锛堚啋 璁よ瘉 Bridge锛?|
| `BrowserBridge/RemoteBrowserContext.cs` | Remote Context 浠ｇ悊 |
| `BrowserBridge/RemoteBrowserPage.cs` | Remote Page 浠ｇ悊 |
| `BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs` | 鏉′欢娉ㄥ唽锛堜粎 DesktopChild + BrowserAutomationEnabled锛?|

## 椋炰功杩炴帴鍣?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Services/FeishuConnectorFactory.cs` | 椋炰功杩炴帴鍣ㄥ伐鍘?|
| `Services/FeishuStreamingProjectionWorker.cs` | 椋炰功娴佸紡鎶曞奖锛?1KB锛?|
| `Services/FeishuImageUploadPreparationService.cs` | 椋炰功鍥剧墖涓婁紶鍑嗗 |
| `Services/FeishuTtsDeliveryService.cs` | 椋炰功 TTS 鎶曢€?|
| `Services/FeishuConnectorIdentity.cs` | 椋炰功韬唤鏍囪瘑 |

## 杩炴帴鍣?& 娑堟伅

| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Connectors/` | 杩炴帴鍣ㄥ疄鐜?|
| `Services/ConnectorHost.cs` | 杩炴帴鍣ㄥ涓?|
| `Hosting/ConnectorHostLifecycleService.cs` | 杩炴帴鍣ㄧ敓鍛藉懆鏈?hosted service锛氭湰鍦版敞鍐屽悓姝ャ€乣StartAllAsync` 鍚庡彴鎵ц锛圓pplicationStopping 缁戝畾锛夛紝Ready 涓嶈 Feishu WS 鎻℃墜闃诲锛涘崟杩炴帴鍣ㄥけ璐ラ殧绂昏繘 Faulted |
| `Services/ConnectorDeliveryDispatcher.cs` | 鎶曢€掑垎鍙?|
| `Services/MessageGatewayIngress.cs` | 娑堟伅缃戝叧鍏ュ彛锛?9KB锛?|
| `Extensions/` | 鎵╁睍娉ㄥ唽 |

椋炰功 WS 搴曞骇鍦?`../../src/HarnessAgent/Core/Connectors/Feishu/FeishuWebSocket.cs`锛氱鐐瑰彂鐜?HttpClient 涓?WS 鎻℃墜鍚?15s 涓婇檺锛岄伩鍏嶅缃戦粦娲炴妸杩炴帴鍣ㄥ崱鍦?Starting 100s銆?
## 鏈嶅姟娌荤悊

| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Services/HeartbeatService.cs` | 褰撳墠 Agent 蹇冭烦缂栨帓锛堢被鍚?`HeartbeatOrchestrator`锛屼笌鏂囦欢鍚嶄笉涓€鑷达紱鏃ュ織鍒嗙被瀛楃涓蹭害涓?`[HeartbeatOrchestrator]`锛夈€傚惎鍔ㄦ椂 + 姣忔绌洪棽 tick 瀵?*鍏ㄩ儴**銆屽惎鐢?+ 鏈喕缁?+ 宸茬粦瀹氫富浼氳瘽銆嶇殑 Agent 骞傜瓑琛ュ叏鐧昏锛?026-09-20锛涙鍓嶅彧鐧昏鍗曚釜鈥滈粯璁?Agent鈥濓紝涓斺€滈槦鍒椾负绌烘墠琛ュ叏鈥濅笉鍙揪锛屽鑷村叾浣?Agent 姘歌繙娌℃湁蹇冭烦锛夛紱瀹炰緥鎻愮ず璇嶅悗杩藉姞鑷富鎵ц濂戠害锛?026-08-26 澧炲姞鎸佷箙 Availability gate锛岀瓑寰?SubAgent/Task/Goal銆佹秷鎭帓闃熴€丷eservation銆乁nknown 鎴栭噸寤哄け璐ュ潎璺宠繃骞堕噸鏂版帓闃燂紝閬垮厤鎶?runtime 鏆傚仠璇垽涓虹┖闂?|
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 缁勫悎 Goal outbox/settlement workers銆乀ask-bound 鍘熷瓙 Store銆丄vailability/Reservation/Dependency/Window/Auto Worker锛沘uthoritative flag 鍓嶇疆鏉′欢 ValidateOnStart |
| `Services/CronSchedulerService.cs` | Cron 璋冨害 |
| `Services/ConfigHotReloadService.cs` | 閰嶇疆鐑噸杞?|
| `Services/IndexPrebuildService.cs` | 绱㈠紩棰勬瀯寤?|
| `Hosting/PuddingHostOptionsFactory.cs` | DesktopChild 鍥哄畾 `0.0.0.0:<port>` 鍚姩绾︽潫 |
| `Hosting/PuddingServerAddressAccessor.cs` | 鍏ㄧ綉鍗＄洃鍚湴鍧€鎶曞奖涓哄悓绔彛 Loopback 鎺у埗鍦板潃 |
| `Hosting/PuddingApplicationHost.cs` | 缁勫悎鏍广€並estrel 鍦板潃缁戝畾涓庢湰鏈烘帶鍒跺湴鍧€鎹曡幏锛涘湪鍙戝竷鍖?appsettings 榛樿涔嬩笂鍔犺浇 `<DataRoot>/config/system.json` 骞舵敮鎸?hot reload锛岀幆澧冨彉閲?鍛戒护琛屼粛涓烘渶楂樹紭鍏堢骇锛涙敞鍐?ADR-075/082 External Token 鐨?tasks/workspaces/agents/messages scope+workspace Policies |
| `Config/` | 榛樿閰嶇疆 |
| `Prompts/` | 绯荤粺鎻愮ず妯℃澘 |
| `P2P/` | P2P 閫氫俊 |

## 娴嬭瘯

`../Tests/PuddingHost.Tests/` 鈥?Browser Bridge銆丷emote proxy銆丼torage 绠＄悊涓?DesktopChild 浜у搧缁勫悎鏍规瀯寤洪獙璇侊紱缁勫悎鏍规祴璇曟樉寮忛獙璇?Singleton `task_*` 宸ュ叿鍙婂叾鍛戒护鏈嶅姟鐢熷懡鍛ㄦ湡锛汼torage 瀹氬悜娴嬭瘯 4/4 鉁?
## U3-B2a锛?026-09-23锛夆€?浠ｇ爜绱㈠紩缁存姢鐨勫涓荤敓鍛藉懆鏈熼┍鍔?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Hosting/CodeIndexMaintenanceHostedService.cs` | 馃攽 U3-B2a锛氱储寮曠淮鎶ょ粍浠剁殑**鍞竴鐢熷懡鍛ㄦ湡椹卞姩**銆俙StartAsync` 闈為樆濉炲惎鍔ㄧ粍浠堕┍鍔紙`ICodeIndexMaintenance`锛夛紝骞舵妸銆屽凡娉ㄥ唽 scope銆嶆寕涓婂彉鏇存簮 鈥斺€?闄勭潃鍦ㄥ惎鍔ㄨ矾寰勪箣澶栵紙`ScopeAttachmentCompleted` 鍙娴嬶級锛屽け璐ュ彧璁版棩蹇楋紝鏃?scope 鏃跺畨鍏?no-op锛沗StopAsync` 鏈夌晫锛堝灞?10s 涓婇檺锛変笖涓嶆姏寮傚父閫冮€搞€佷笉涓㈠凡鍏ラ槦璇锋眰銆?*鏈韩涓嶅惈浠讳綍寰幆 / 闃熷垪 / 绱㈠紩閫昏緫**锛氭车涓庡彉鏇存崟鑾风暀鍦?`PuddingCodeIndex`锛圓DR-089 搂2 椹卞姩褰掑睘锛夈€傛敞鍐岀偣锛歚PuddingServiceCollectionExtensions.Platform.cs` 绱ч偦 `AddPuddingCodeIntelligence()`銆?|

缁勫悎鏍瑰悓鏃舵柊澧?`../Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs`锛? 鐢ㄤ緥锛夛細椹卞姩鍙В鏋愩€佹车绔彛涓?`ICodeIndexScheduler` 鍚屽疄渚嬨€侀┍鍔ㄥ凡娉ㄥ唽涓旀寔鏈夊悓涓€瀹炰緥銆佹棤 scope 鏃跺畨鍏?no-op銆乣Enqueue 鈫?娉?鈫?ICodeIndexer`锛堟浛韬鏁帮級闂悎銆佸凡娉ㄥ唽 scope 琚寕涓婂彉鏇存簮銆?
椤哄甫淇锛歚Storage/StorageMaintenanceServiceTests.cs` 涓?`Storage/StorageManagementAdministrationTests.cs` 鍚勮ˉ 1 琛?`using PuddingCodeIndex.Contracts;` 鈥斺€?姝ゅ墠 `ICodeIndexScheduler` 宸茶縼鍑?`PuddingCodeIntelligence.Contracts`锛屾暣涓?`PuddingHost.Tests` 缂栨帓鏈熺紪璇戜笉杩囥€?
## U4-7锛?026-09-25锛夆€?鍏ㄦ枃绱㈠紩銆屼緵缁欏弬鏁般€嶉厤缃寲 + fail-closed 鏍￠獙锛?*榛樿鍏抽棴**锛?
鐢ㄦ埛瑁佸畾锛?026-09-25锛夛細銆?GB 璇蜂娇鐢ㄩ厤缃枃浠剁‘瀹氬弬鏁帮紝鏂逛究鍚庢湡鏇挎崲涓?XXGB鈥︹€︾敤**椤圭洰鐩綍缁熻 json** 鎴?**Data 鐩綍閰嶇疆鏂囦欢**鍐冲畾锛岃€屼笉鏄€夋嫨涓€涓浐瀹氬€笺€傘€?鈬?鏈垁鍙?**Data 鐩綍閰嶇疆鏂囦欢**锛歚<DataRoot>/config/system.json` 鐨?`FullTextIndex` 鑺傦紙瀹夸富 `PuddingApplicationHost.CreateBuilder` 宸插姞杞藉畠骞舵敮鎸?hot reload锛夈€?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Hosting/FullTextIndexSupplyOptions.cs` | 馃攽 渚涚粰鍙傛暟绫诲瀷锛堣妭鍚?`FullTextIndex`锛夛細`Enabled`锛?*榛樿 `false`**锛? `Scopes`锛堢洰鏍囩洰褰曪紝鍙负缁濆鎴栫浉瀵?`WorkspaceRoot`锛? `WorkspaceRoot`锛堢浉瀵归」鐨?*鏄惧紡缁濆鍩哄噯**锛?*绂佺敤杩涚▼ CWD**锛? `MaxIndexBytes`锛?*榛樿 `1_073_741_824` = 1 GiB = 2^30**锛涚‖澶╄姳鏉?`1L << 40` = 1 TiB锛? `MinRebuildInterval`锛堥粯璁?12h锛孞SON 鍐?`"hh:mm:ss"`锛夈€?*鍗曚竴鐪熸簮**锛? GiB 瀛楅潰閲忓叏浠撶敓浜т唬鐮佸彧鍦ㄦ鍑虹幇涓€娆°€?|
| `Hosting/FullTextIndexSupplyResolver.cs` | 馃攽 fail-closed **绾嚱鏁?*鏍￠獙锛堜笉渚濊禆 DI / 鏂囦欢绯荤粺 / 缃戠粶锛夛細鍏抽棴 鈬?鎴愬姛涓旂┖鍔ㄤ綔銆?*杩?scope 鎺㈤拡閮戒笉璋冪敤**锛涘紑鍚€?`Scopes` 涓虹┖ / 鍚┖涓猜蜂笉瀛樺湪路闈炵洰褰暵烽噸澶嶉」 / 鐩稿椤规棤缁濆鍩哄噯 / `MaxIndexBytes <= 0` 鎴栬秴 1 TiB / `MinRebuildInterval < 0` 鈬?**缁撴瀯鍖栨嫆缁?*锛坄ParameterName` + `Value` + 鍘熷洜鏋氫妇 + 鍙娑堟伅锛夛紝涓?accepted / rejected **鍒嗗埆鍒楀嚭**銆俿cope 鎺㈤拡鏄笁鎬佸鎵橈紙`Missing`/`NotDirectory`/`Directory`锛夛紝鍙敞鍏ユ浛韬?鈬?鍗曟祴闆舵枃浠剁郴缁熻闂€?|
| `Hosting/IndexPrebuildFreshness.cs` | `MinRebuildInterval` 鐨勬秷璐圭偣锛堢函鍑芥暟銆佹敞鍏ユ椂閽燂級锛氭棤绱㈠紩 / 绱㈠紩杩囨棫 / 闂撮殧 鈮?0 鈬?閲嶅缓锛涚储寮曡冻澶熸柊 鈬?璺宠繃銆傗殸锔?浠櫒鍙ｅ緞 = **绱㈠紩鏍圭洰褰?mtime**锛坧er-scope 绱㈠紩鐩綍鍦?`PuddingFullTextIndex` 鍐呬笖 `internal`锛夆噿 绮楃矑搴︿唬鐞嗭紝宸插湪浜や粯鎶ュ憡鐧昏鐣欑櫧銆?|
| `Services/IndexPrebuildService.cs` | 鐢遍厤缃棬鎺х殑棰勫缓鏈嶅姟锛堟鍓嶆槸 `HOSTED-DISABLED`锛夈€俙StartAsync` **姘镐笉闃诲瀹夸富**锛?*榛樿閰嶇疆涓嬩笉寤虹储寮曘€侀浂绱㈠紩 I/O銆佷笉璁?Error**锛涙牎楠屼笉杩?鈬?璁?Error 涓斾粈涔堥兘涓嶅仛锛坒ail-closed锛屼笉閮ㄥ垎鐢熸晥锛夛紱閫氳繃 鈬?鍚姩璺緞涔嬪鎸?`Scopes` 閫愪釜棰勫缓 鈥斺€?**鐩爣鏉ヨ嚜閰嶇疆锛屼笉鍐嶆槸 `Directory.GetCurrentDirectory()`**锛堝巻鍙茬己闄凤級銆?|
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 鎺ョ嚎锛歚Configure<FullTextIndexSupplyOptions>(builder.Configuration.GetSection(FullTextIndexSupplyOptions.SectionName))`銆?*蹇呴』鏄?`builder.Configuration`**锛歚bootstrapConfiguration` 鍙惈 appsettings/鐜鍙橀噺锛宍system.json` 鍙姞鍦?`builder.Configuration` 涓娿€傚悓澶勬妸 `IndexPrebuildService` 浠?`HOSTED-DISABLED` 娉ㄩ噴鏀逛负甯搁┗娉ㄥ唽锛堥粯璁ら厤缃笅绛変环 no-op锛岀幇缃戣涓轰笉鍙橈級銆?|

娴嬭瘯锛坄../Tests/PuddingHost.Tests/Hosting/`锛?*20 鐢ㄤ緥**锛夛細`FullTextIndexSupplyResolverTests`锛圓1~A5 + 鐩稿/缁濆鍩哄噯 + 闆堕棿闅旇竟鐣岋紝10 鏉★級銆乣IndexPrebuildServiceTests`锛圓6 + 寮€鍚矾寰?+ 鎷掔粷鍙娴嬶紝3 鏉★級銆乣IndexPrebuildFreshnessTests`锛? 鏉★級銆乣FullTextIndexSupplyHostBindingTests`锛坰ystem.json 鈫?IOptions 缁戝畾 + hosted 娉ㄥ唽 + 鏃犺鑺傚嵆榛樿鍏抽棴锛? 鏉★級銆傝瘉鎹笌鍙樺紓杈撳嚭瑙?`temp/U4-7-REPORT.md`銆乣temp/u4-7-evidence/`銆?
鈿狅笍 **鐣欑櫧锛圧5锛?*锛氫綋绉姢鏍忕殑**鎵ц**琛屼负锛堣揪 `MaxIndexBytes` 鏃跺憡璀?/ 鎷掑啓 / GC锛?*鏈疄鐜?*锛屽睘鍚庣画鍒囩墖锛圓DR-089 搂7.2銆岃Е鍙?GC 鐣欏埌鍚庣画鍒囩墖銆嶏級銆傛湰鍒€鍙彁渚涘弬鏁颁笌鏍￠獙銆?
## S5锛?026-09-25锛夆€?棰勫缓绱㈠紩鏀硅蛋銆屽崗璋冨櫒 + 鏆傚瓨渚涚粰銆嶏紙**榛樿鍏抽棴 鈬?闆?I/O**锛?
U4-7 鐨勯寤?*鐩村啓** `IFullTextSearchEngine.BuildIndexAsync`锛屼笖鐢?*绱㈠紩鏍?mtime** 鍒ゆ墍鏈?scope 鐨勬柊椴滃害銆?S5 鎶婁袱鏉￠兘鎹㈡帀锛氬啓璺緞缁熶竴杩涚粍浠跺崗璋冨櫒锛堣法杩涚▼绉熺害 / 骞傜瓑鍚堝苟 / 棰勭畻纭檺 / staging / 鍘熷瓙鍒囨崲锛夛紝
鏂伴矞搴︽敼涓?**per-scope**锛堣 scope **鑷繁**鐨?live 绱㈠紩鐩綍锛夈€?
| 鏂囦欢 | 鐢ㄩ€?|
|------|------|
| `Hosting/IFullTextIndexSupplyComposition.cs` | 馃攽 瀹夸富渚т緵缁欑鍙ｏ細`IFullTextIndexSupplyComposition`锛坄Coordinator` / `ComponentOptions` / `LiveIndexLastWriteUtc`锛? `IFullTextIndexSupplyCompositionFactory`銆?*涓轰粈涔堢敤宸ュ巶**锛歊4 瑕佹眰 `Enabled=false` 鏃躲€屼笉瑙ｆ瀽 scope銆?*涓嶆瀯閫犲崗璋冨櫒缁勫悎**銆佷笉 touch 绱㈠紩鏍广€嶁€斺€?鍗忚皟鍣?/ staged builder / 娓呯偣 / 绉熺害鐨勫疄渚嬪寲琚帹杩熷埌鐪熸杩涘叆渚涚粰璺緞涔嬪悗锛堥粯璁ゅ叧闂椂姘镐笉鍙戠敓锛夈€?|
| `Hosting/LuceneFullTextIndexSupplyCompositionFactory.cs` | 鐢熶骇瑁呴厤锛歚FileSystemSupplyInventory` + `StagedFullTextIndexBuilder`锛坙ive 寮曟搸 = **鏌ヨ渚у悓涓€瀹炰緥**锛屽惁鍒?reader 澶辨晥鎵撶┖锛? `FileSupplyLease` + `FullTextIndexSupplyCoordinator`锛?*閰嶇疆娴佸叆缁勪欢**锛圧2锛夛細`DefaultBudgetBytes = FullTextIndexSupplyOptions.MaxIndexBytes`銆乣MinRebuildInterval = MinRebuildInterval` 鈥斺€?瀹夸富渚с€? GiB銆嶄粛鍙湁 `FullTextIndexSupplyOptions.DefaultMaxIndexBytes` 涓€澶勭湡婧愶紝缁勪欢甯搁噺閫€鍖栦负鏈帴绾挎椂鐨勫厹搴曘€?*per-scope 鏂伴矞搴?*锛圧3锛? `IFullTextIndexRootedEngine.ResolveIndexDirectory(scope)` 鎸囧悜鐨?**live 绱㈠紩鐩綍** mtime锛堢粍浠跺唴鐨勬槧灏勫崟涓€鐪熸簮锛沗SupplyIndexDirectoryLayout` 浠嶆槸 internal锛屽涓?*涓嶅鍒?*鍝堝笇瑙勫垯锛夈€?|
| `Services/IndexPrebuildService.cs` | 鍐欒矾寰勬敼涓恒€屾彁浜?`SupplyScopeRequest` 鈫?杞 `GetStatusAsync` 鍒扮粓鎬併€嶏紙`StartupDelay` / `StatusPollInterval`(榛樿 250ms) / `BuildWaitTimeout`(榛樿 **30min 涓婇檺**) 鍙敞鍏ワ紱**瓒呮椂鍙仠姝㈣娴嬨€佷笉鍙栨秷 job銆佷笉閲嶈瘯**锛夛紱`Busy` / `Rejected`(鍚?OverBudget) / `Failed`(鍚?RolledBack / 鍒囨崲澶辫触锛屽師鍥犲師鏂囩収鐧? / 瓒呮椂**閫愮被濡傚疄璁板綍**锛堝甫 jobId锛夛紱**姣忎釜 scope 鏈€澶氭彁浜や竴娆?*銆傚紩鎿庡彧鍓?*鍙**鐢ㄩ€旓紙`HasIndex`锛夈€?|
| `Hosting/IndexPrebuildFreshness.cs` | 娉ㄩ噴鍙ｅ緞淇锛氭柊椴滃害杈撳叆浠庛€岀储寮曟牴 mtime銆嶆敼涓?*璇?scope 鑷繁鐨?live 绱㈠紩鐩綍 mtime** 鈥斺€?U4-7 鐧昏鐨勩€岀矖绮掑害浠ｇ悊銆嶇暀鐧藉氨姝ゅ叧闂紙鏃у彛寰勪笅寤哄嚭浠讳竴 scope 灏变細鎶婂叾浣?scope 闆嗕綋璇垽涓烘柊椴滐級銆?|
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 鏂板 `AddSingleton<IFullTextIndexSupplyCompositionFactory>(鈥?`锛氬彇 `FullTextIndexOptions` + 鏂█寮曟搸瀹炵幇 `IFullTextIndexRootedEngine`锛坰taged 鍒囨崲闇€瑕併€岃鏂欐牴 鈫?绱㈠紩鐩綍銆嶆槧灏勪笌 reader 缂撳瓨澶辨晥涓や釜鎺ョ紳锛夛紝staging 寮曟搸宸ュ巶 = `new LuceneSearchEngine(stagingOptions)`銆?|

娴嬭瘯锛坄../Tests/PuddingHost.Tests/Hosting/`锛屽涓?**154 鐢ㄤ緥**锛汼5 鏂板 8 鏉★級锛?`S5IndexSupplyHostWiringTests`锛圓1 榛樿鍏抽棴闆?I/O銉诲伐鍘?鍗忚皟鍣?寮曟搸 0 璋冪敤銉荤储寮曟牴涓嶅缓锛汚2 **鐪熷疄 Lucene** 绔埌绔彲鏌ヨ + live 寮曟搸**闆剁洿鍐?*锛汚3 OverBudget 濡傚疄璁板綍涓?live 閫愬瓧鑺備笉鍙樸€佹棫绱㈠紩浠嶅彲鏌ワ紱A4 **鐪熷疄璺ㄨ繘绋嬫枃浠剁绾?* Busy 鈬?鍙彁浜や竴娆°€佽褰?owner/PID銆佺户缁笅涓€ scope锛汚5 per-scope 鏂伴矞搴﹀彧鎻愪氦闄堟棫鑰咃紙A 鏂伴矞鍙烦 A锛夛紱A6 閰嶇疆娴佸叆缁勪欢锛涜疆璇㈣秴鏃舵湁鐣岋級銆?`S5FullTextIndexSupplyHostCompositionTests`锛堢粍鍚堟牴锛歚system.json` 鈫?缁勪欢绛栫暐 + `IFullTextIndexRootedEngine` 鎺ョ紳鎴愮珛锛屽叏绋嬮浂绱㈠紩鍐欏叆锛夈€?`S5SupplyTestDoubles.cs`锛堝す鍏蜂笌鏇胯韩锛夈€乣IndexPrebuildServiceTests`锛圲4-7 涓夋潯鎸?S5 璇箟閫傞厤锛夈€?璇佹嵁锛歚temp/S5-REPORT.md`銆乣temp/s5-evidence/`銆?
鈿狅笍 **杈圭晫**锛欳LI 涓庡叾瀹冨伐绋嬩竴寰嬫湭鏀癸紙缁勪欢闆舵敼鍔級锛?*閲嶅惎鍚庣殑杩愯鎬侀獙璇佺敱鐖剁骇鎵ц**锛堟湰鍒€绂佹閲嶅惎浠讳綍杩涚▼锛夈€?
## 变更（2026-09-30，启动阶段埋点 `StartupPhaseTracker`：让「启动耗时」可归因 · 组件 + 宿主入口）

**动机（实测）**：Core 启动耗时 **21.5 s**（Desktop 显示口径 = Core 进程 `ReadyAt − StartedAt`，见 `Source/PuddingDesktop/MainWindow.xaml.cs:123`），
而系统日志文件 `D:\data\logs\system\pudding-*.log` 的**第一行**出现在进程启动后 **17.5 s**（07:00:31 进程启动 vs 07:00:48.460 首行）
⇒ **日志管线建立之前的那段启动时间无法从任何日志归因**。

**新增**：`Hosting/StartupPhaseTracker.cs` —— 纯逻辑（时钟与输出通道可注入；无文件 IO / 无静态可变状态 / 无线程）
+ `StartupPhases` 阶段名常量（唯一真源，调用点与测试都引用它）。每个阶段点输出一行
`[StartupPhase] <name> total=<N>ms delta=<N>ms`，同时进入 **stdout**（Desktop 捕获；日志管线就绪前唯一可用通道）
与 **Serilog**（落系统日志文件，供事后取证）。

**打点位置（11 处，已用 grep 复核）**：
- `Source/PuddingAgent/Program.cs` 6 处：`process-start` / `options-resolved` / `data-root-lease` / `initialized` / `server-started` / `ready`
- `PuddingApplicationHost.CreateBuilder` 3 处：`data-root-bootstrapped` / `logging-ready` / `services-registered`（后者 = DI 注册完成）
- `PuddingApplicationHost.Build` 2 处：`host-built` / `middleware-mapped`

`CreateBuilder` / `Build` / `InitializeAsync` 新增**可选**参数 `StartupPhaseTracker? phases = null` ⇒ 既有调用点零改动。

**验证**：`Tests/PuddingHost.Tests/Hosting/StartupPhaseTrackerTests.cs`（xUnit，4 用例）全绿；宿主全量 **159/159**（failed 0）；
**M1**（`Format` 去掉 delta）⇒ 红点**恰好** `Mark_WritesExactlyOneLinePerMark_InOrder_ThroughTheSink`；
**M2**（删掉单调兜底）⇒ 红点**恰好** `Mark_ClampsNegativeDelta_WhenClockMovesBackwards`（Expected 500 / Actual 100）；
`MUTATION` 残留 **0**（活对照 `StartupPhaseTracker` 14 / `StartupPhases.` 20）。

**未证实（如实登记）**：
- 埋点**真实触发**只能在下次 Core 启动后才可见。本次 Core 一直在运行，且 `Source/PuddingAgent/bin/Debug/net10.0` 被 PID 26676 锁定
  ⇒ `dotnet build PuddingAgent` 报 **30×MSB3021 + 30×MSB3027，CS 错误 0**（编译通过、仅输出复制失败）。
- 变异复原后宿主 DLL 哈希**未**回到变异前基线（`57E70653…` → `F1D19D2F…`），原因**未证实** ⇒ 本片**不声称**逐位复原，只声称「源码语义正确 + 全量绿」。
