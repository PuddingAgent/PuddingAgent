# PuddingAgent 初始化引导

此文件记录本地开发环境的冷启动初始化参数、LLM 测试服务商、默认登录信息、dev 启动方式、反向代理、日志位置和排障要点。内容面向本地开发环境，生产环境不要直接复用这里的测试密钥或默认密码。

## 本次初始化状态

- 当前入口: `http://localhost/admin/bootstrap`
- 当前状态: 待浏览器确认后提交初始化
- 数据目录: `data`
- 初始化会创建首个管理员、默认工作空间、默认模型服务配置，并关闭 bootstrap 入口。

## LLM 测试服务商

- Provider ID: `default-openai`
- Provider 名称: `Default OpenAI-Compatible`
- 协议: `openai`
- Base URL: `https://token-plan-cn.xiaomimimo.com/v1`
- API Key: `tp-ce721j9i657u64koeop5r6leh9xm9jnxnguyz5uhknhgyoyv`
- 默认聊天模型: `mimo-v2.5-pro`
- 记忆/总结模型: `mimo-v2-pro`

模型信息:

| 项目 | 值 |
| --- | --- |
| 模型名称 | `mimo-v2.5-pro`, `mimo-v2-pro` |
| 类别 | 文本生成-通用大语言模型 |
| 上下文长度 | 1 M |
| 最大输出长度 | 128 K |
| 能力 | 文本生成、深度思考、流式输出、函数调用、结构化输出、联网搜索 |
| 流控 | RPM: 100, TPM: 10 M |

## 默认管理员

- 用户名: `admin`
- 邮箱: `admin@pudding.local`
- 显示名称: `Pudding Admin`
- 初始密码: `Admin@123456`

完成初始化后，登录入口为:

- `http://localhost/admin/user/login`

## 默认工作空间

- 默认工作空间: `默认工作空间`
- 默认助手: `默认助手`

初始化流程会确保 `default` workspace 存在，并把首个管理员加入平台团队和默认空间。

## Dev 启动方式

推荐裸机开发模式:

```powershell
.\dev-up.ps1
```

常用命令:

```powershell
python dev-up.py --status
python dev-up.py --logs 80
python dev-up.py --guard-off
python dev-up.py --guard-on
python dev-up.py --restart
python dev-up.py --down
```

说明:

- `dev-up.py` 会启动 backend、frontend 和本地反向代理。
- 默认入口是 `http://localhost/`，也可以使用 `http://127.0.0.1/`。
- 后端默认端口: `5000`
- 前端 dev server 默认端口: `8000`
- 反向代理默认端口: `80`
- 守护模式会在后端或前端退出后自动拉起。
- 文件变化会经过 5 秒防抖后触发一次重启，用来模拟热更新。

## 反向代理

开发时通过 `http://localhost` 访问统一入口:

- `/api/*`, `/health`, `/swagger/*` 转发到后端。
- `/admin/*` 转发到前端。
- `/admin/bootstrap`、`/admin/user/login` 等前端深链会回退到前端 SPA 入口。

相关测试位于:

- `TestScripts/dev_up_tests.py`

## 日志和排障

程序日志位于:

- `data/logs`

启动器日志位于:

- `data/logs/dev-up-YYYY-MM-DD.log`

开发进程临时日志位于:

- `tmp/dev/backend.out.log`
- `tmp/dev/backend.err.log`
- `tmp/dev/frontend.out.log`
- `tmp/dev/frontend.err.log`
- `tmp/dev/proxy.out.log`
- `tmp/dev/proxy.err.log`
- `tmp/dev/supervisor.out.log`

排障建议:

- 先看 `python dev-up.py --status`，确认 Supervisor、Backend、Frontend、Proxy 是否运行。
- 再看 `python dev-up.py --logs 80`，确认健康检查和最近异常。
- 遇到复杂问题时，可以在后端插入结构化日志，再查看 `data/logs`。
- 手工编译或调试前建议先执行 `python dev-up.py --down`，避免运行中的 DLL 锁定构建输出。

## data 目录

`data` 目录包含本地数据库、配置、日志和运行状态。

冷启动要求:

- 第一次冷启动时，`data` 可以为空。
- 程序会初始化必要目录和默认配置。
- 初始化完成后，`data/bootstrap-state.json` 会记录 bootstrap 已关闭。

重置开发环境:

1. 停止服务: `python dev-up.py --down`
2. 清理 `data` 目录内容。
3. 重新启动: `.\dev-up.ps1`
4. 访问 `http://localhost/admin/bootstrap` 重新初始化。

## 容器部署模式

容器启动可使用:

```powershell
.\build-and-up.ps1
```

容器模式需要在 Docker Compose 中挂载 `data` 目录，确保配置、数据库和日志可以持久化。
