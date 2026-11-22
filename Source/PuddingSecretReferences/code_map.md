# PuddingSecretReferences


> 源指纹: Source/PuddingSecretReferences/**=3-6dfda8368993 · 条目数: 2 · 最近整理: 2026-11-22
| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CredentialReference.cs` | 环境变量与 vault 引用的凭据解析 | `CredentialReference` | `BootstrapSecretFile.cs` | 未解析或解密失败必须明确拒绝，不得回传占位符 |
| `BootstrapSecretFile.cs` | 启动前凭据的当前用户 DPAPI 缓存 | `BootstrapSecretFile` | `CredentialReference.cs` | 与 KeyVault 主密钥独立；坏文件拒绝启动而不是覆盖 |

叶组件不引用 Core、Host、Platform、Runtime 或 Desktop；Guardian 可链接 BootstrapSecretFile 单个源文件保持独立进程边界。
