# PuddingHomeAssistant.Probe · code_map
> 源指纹: *.cs=1-0875aac1b59a, *.csproj=1-6bd786ee045f · 条目数: 1 · 最近整理: 2026-11-22

Home Assistant 组件的 S3 无宿主验证程序（控制台 net10.0）。只引用 `PuddingHomeAssistant` 一个项目，不装配 Core、不读 DataRoot。

**入口与配置**：`PuddingHomeAssistant.Probe <baseUrl> <token>`。`token` 缺省时读环境变量 `HA_TOKEN`，两者皆无则打印用法并以退出码 2 结束。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 对真实 HA 实例验证 REST 客户端与输出预算 | `HaRestClient`、`HaOutputBudget`、`HaInstance` | `Source/PuddingHomeAssistant/code_map.md` | 只允许引用 PuddingHomeAssistant；装配 Core 或读 DataRoot 即破坏 S3 无宿主判据 |

**项目约束**：断言失败以退出码 1 表达，用法错误以退出码 2 表达；对白名单域只做只读探测，不实际改变设备状态。
