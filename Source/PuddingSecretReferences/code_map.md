# PuddingSecretReferences


> 源指纹: Source/PuddingSecretReferences/**=3-6dfda8368993 · 条目数: 2 · 最近整理: 2026-11-22
| 文件 | 契约 |
|---|---|
| `CredentialReference.cs` | 凭据字段的环境变量与 vault 引用解析；未解析/解密失败明确拒绝，不回传占位符 |
| `BootstrapSecretFile.cs` | 启动前所需凭据的当前 Windows 用户 DPAPI 缓存；与 KeyVault 主密钥独立 |

叶组件不引用 Core、Host、Platform、Runtime 或 Desktop；Guardian 可链接 BootstrapSecretFile 单个源文件保持独立进程边界。
