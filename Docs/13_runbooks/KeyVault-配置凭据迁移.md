---
title: KeyVault 配置凭据迁移
author: hyfree
date: 2026-11-17
last_reviewed: 2026-11-17
status: active
description: 先部署执行边界解析器，再单点迁移语音、验证并批量迁移配置凭据；恢复副本只保存 DPAPI 密文。
categories: [docs, runbooks]
tags: [keyvault, credentials, dpapi, migration]
related_docs: [Docs/13_runbooks/Desktop-Guardian.md, Docs/11_design/KeyVault明文凭据迁移-2026-11-17.md]
related_files: [Tools/Secrets/migrate_config_credentials.py, Source/PuddingSecretReferences/CredentialReference.cs, Source/PuddingSecretReferences/BootstrapSecretFile.cs, Source/PuddingHost/Services/KeyVaultStartupValidationService.cs]
slug: runbooks-keyvault-config-credentials
draft: false
---

# KeyVault 配置凭据迁移

## 边界与失败策略

凭据只在执行边界展开 `${ENV}` 和 `{{vault:name}}`。未解析或解密失败必须明确失败，不能把占位符当作密钥发送上游；管理读写仍使用原始引用。保留既有加密实现和主密钥取值顺序；已有主密钥损坏、持久化失败及启动解密校验失败均拒绝就绪，不生成临时替代密钥继续工作。

控制令牌与登录 JWT 签名密钥在 Core/保管箱就绪前即被使用，因此同值存入 KeyVault，并保留一份当前 Windows 用户范围的 DPAPI 启动缓存。Desktop/Guardian 链接缓存读取源文件，不引用 Host/Runtime，不通过 KeyVault HTTP 解锁自身。更换账户或机器需要通过原账户恢复，不能删除坏文件强行启动。该缓存不是主密钥，也不改变主密钥文件。

## 顺序

1. 独立组件与读取端测试通过后，按 [Guardian 运行手册](Desktop-Guardian.md) 升级 Guardian，再部署 Desktop/Core。Guardian 本身只能在停止后由进程外控制器替换。
2. 从标准输入传入管理员登录 JSON，运行 `py -3 -B Tools/Secrets/migrate_config_credentials.py --data-root <DataRoot> --stage voice --evidence-dir <ignored-recovery-directory>`。不要把 JWT、API Key 或控制令牌传命令行、放环境变量或写明文证据。
3. 真实调用宿主 TTS，并确认磁盘仍是引用。失败时先恢复该项，不能继续批量。
4. 运行同一工具 `--stage all --delete-historical-backups`。工具逐项写入保管箱并往返验证，再原子替换对应文件；同名不同值拒绝覆盖。配置若被并发修改则停止。每个修改文件先产生 DPAPI 恢复副本；历史 `.before-*` JSON 只删除、不复制到别处。明确传删除参数才执行非递归删除。
5. 通过 Guardian 重启 Desktop/Core，核对请求 ID 与 `healthy`，再 `--stage audit`，核对明文字段为零、引用数与保管箱条目数，并进行真实对话回执验收。

迁移范围包含配置目录中 LLM、搜索、语音、CalDAV、遗留飞书配置、系统/安全配置；实际飞书凭据还在 `channels/*/manifest.json`，Home Assistant 凭据在 Agent 私有配置。只迁移字段，不修改提供商、模型、账号或密钥值。独立 `keyVault.masterKeyRef` 是来源名称，不作为明文密钥迁移。

## 恢复

迁移工具不生成明文备份。`*.dpapi` 恢复副本以 `PuddingAgent.MigrationRecovery.v1` 为附加熵，通过 Windows 当前用户 DPAPI 封装原始 JSON 文本。可在原账户使用工具的 `dpapi("unprotect", base64Payload, entropy)` 函数在内存解开，定向原子恢复到原配置文件，再经 Guardian 重启；不要将解密文本打印到终端或临时报告。恢复会重新引入明文，只用于事故处置。

程序包回滚不回滚配置；批量迁移后不应直接启动不支持引用的旧包。先恢复对应配置，或恢复支持引用的已验收版本。启动缓存与 KeyVault 的启动凭据必须保持同值，本流程不执行轮换。
