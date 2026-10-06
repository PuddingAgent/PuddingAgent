---
title: Pudding Agent Web 用户认证系统
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-06
status: active
description: Pudding Agent 使用基于 Web UI 的 JWT 认证系统，通过 Bootstrap 引导初始化 → 登录 → 受控用户管理 三阶段流程，替代了早期传统桌面端登录窗口。公开注册被禁止，仅 admin 用户可创建其他用户。
categories: [docs, features]
tags: [loginfeature, features]
related_docs: []
related_files: [Source/PuddingPlatform/Controllers/Api/BootstrapApiController.cs, Source/PuddingPlatform/Services/BootstrapStateService.cs, Source/PuddingPlatform/Services/JwtTokenFactory.cs, Source/PuddingHost/default-data/config/security.json, Source/PuddingAgent/Program.cs, Source/PuddingPlatformAdmin/src/pages/bootstrap/index.tsx, Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx, Source/PuddingPlatformAdmin/src/app.tsx]
slug: features-loginfeature
draft: false
---

# Pudding Agent Web 用户认证系统

> **关联任务**：task-20260502-011  
> **实现日期**：2026-05-02

## 概述

Pudding Agent 使用基于 Web UI 的 JWT 认证系统，通过 **Bootstrap 引导初始化 → 登录 → 受控用户管理** 三阶段流程，替代了早期传统桌面端登录窗口。公开注册被禁止，仅 admin 用户可创建其他用户。

## 认证流程

```mermaid
graph TD
    A[首次启动] --> B[GET /api/bootstrap/status]
    B --> C{needsSetup?}
    C -->|true| D[Bootstrap 页 /bootstrap]
    D --> E[POST /api/bootstrap/admin]
    E --> F[密码强度 ≥8位，含大小写+数字]
    F --> G[Serializable 事务防并发]
    G --> H[创建 admin → 设置 Initialized=true → 签发 JWT → 跳转 /]
    C -->|false| I[登录页 /user/login]
    I --> J[POST /api/login/account]
    J --> K[获取 JWT token → 存入 localStorage]
    
    L[已初始化] --> M[GET /api/bootstrap/status]
    M --> N[Bootstrap:Initialized=true → 403]
    N --> I
```

### 路由守卫三态分发（app.tsx）

| 状态 | 路由 | 行为 |
|------|------|------|
| 无 admin 用户 | `/bootstrap` | 显示 Bootstrap 初始化向导 |
| 有用户但未登录 | `/user/login` | 显示登录页 |
| 已登录（有效 token） | 正常页面 | 进入管理后台 |

## 核心 API

| 端点 | 方法 | 说明 | 鉴权 |
|------|------|------|------|
| `/api/bootstrap/status` | GET | 检查系统是否需要初始化（返回 `needsSetup`、`hasAdmin`、`userCount`） | 匿名 |
| `/api/bootstrap/admin` | POST | 创建首个管理员账号（需系统未初始化） | 匿名 |
| `/api/login/account` | POST | 已有用户登录 | 匿名 |

### POST /api/bootstrap/admin 请求体

```json
{
  "userId": "admin",
  "email": "admin@example.com",
  "password": "StrongP@ss1",
  "displayName": "管理员"
}
```

**安全约束**：
- 密钥在首次启动时由 CSPRNG 自动生成（32 字节 base64），无需手动配置
- admin 创建后 `Bootstrap:Initialized` 置为 `true`，后续所有 bootstrap API 返回 403
- 密码强度正则：`^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$`
- 使用 `Serializable` 事务隔离级别防止 TOCTOU 并发创建多个 admin
- 仅当数据库中无任何 Admin 用户时允许调用

## 关键文件

| 文件 | 说明 |
|------|------|
| `Source/PuddingPlatform/Controllers/Api/BootstrapApiController.cs` | Bootstrap 状态检查 + admin 创建 API |
| `Source/PuddingPlatform/Services/BootstrapStateService.cs` | bootstrap-state.json 读写与初始化锁定 |
| `Source/PuddingPlatform/Program.cs` | JWT 认证中间件注册、Bootstrap 密钥自动生成 |
| `Source/PuddingAgent/Program.cs` | Agent 进程 Bootstrap 密钥自动生成 |
| `Source/PuddingPlatformAdmin/src/pages/bootstrap/index.tsx` | Bootstrap 初始化向导页面 |
| `Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx` | 登录页 |
| `Source/PuddingPlatformAdmin/src/app.tsx` | 路由守卫三态分发逻辑 |
| `.env.example` | 环境变量配置示例 |

## 用户模型（AppUserEntity）

| 字段 | 说明 |
|------|------|
| `UserId` | 登录用户 ID（唯一） |
| `Username` | 用户名（同 UserId） |
| `Email` | 邮箱 |
| `DisplayName` | 显示名称 |
| `PasswordHash` | PBKDF2 哈希密码 |
| `UserType` | `Admin` / `User` |
| `IsEnabled` | 是否启用 |

## 安全特性

- 密码使用 PBKDF2 哈希存储（`PasswordHasher.Hash`）
- JWT Bearer 认证：密钥 / Issuer / Audience / 有效期只来自配置链的 `Jwt:*` 键，
  真源是 `<DataRoot>/config/security.json` 的 `jwt` 段（**有效期缺省 7 天 = 168 小时**）；
  签发统一走 `JwtTokenFactory`（登录与 Bootstrap 首次初始化共用），代码内**没有**硬编码密钥兜底——
  密钥缺失或短于 16 字符即启动失败（fail closed），错误信息直接指出该文件与字段
- 公开注册禁止，用户管理仅限 admin
- Bootstrap 初始化密钥在首次启动时由 CSPRNG 自动生成
- Session Cookie `HttpOnly` + `SameSite=None`
- Serializable 事务防并发竞态

## 部署注意事项

- Bootstrap 初始化密钥在首次启动时自动生成（`data/bootstrap-state.json`），无需手动配置
- admin 创建成功后，`bootstrap-state.json` 的 `Bootstrap:Initialized` 自动置为 `true`，后续 bootstrap API 返回 403
- **JWT 签名密钥**：随包模板 `security.json` 的 `jwt.key` 为空；首次启动由 `PuddingDataRootBootstrapper`
  生成 48 字节随机密钥并原子写回 `<DataRoot>/config/security.json`（占位符/过短值同样会被替换，其余字段保留）。
  因此无需手工配置；如需轮换，改该文件的 `jwt.key` 后重启 Core 即可（历史令牌全部失效）。密钥内容不写日志。
- 前端不解析令牌 `exp`、也没有刷新令牌：到期表现为下一个请求返回 401，被 `requestErrorHandler` 清除本地令牌并跳回登录页
- Bootstrap 成功后即可通过登录页使用已创建的 admin 账号