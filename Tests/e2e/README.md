# Pudding E2E Tests

## Quick Start
1. 启动被测实例（二选一）：
   - 源码开发栈：`python dev-up.py`
   - 产品态：`PuddingDesktop.exe`（WinUI 3 Shell + 独立 Core 子进程）
2. Run healthcheck: `.\Tests\e2e\healthcheck.ps1`
3. Run browser smoke: (Playwright/Python)

## Test Layers
- **Healthcheck**: API + Fake LLM available
- **Browser Smoke**: Login → Chat → Verify response
- **Full E2E**: Sub-agent run → Diagnostics API → Run archive

## Known Issues
- `PuddingWebApiTests` may fail with file-lock errors (CS2012) when run concurrently with a running app.
  Fix: Use isolated output directories or stop the app before running tests.

## Docker 链路已移除（2026-10-02）
按用户裁定，Docker 部署链路已废弃并删除：`build-and-up.ps1`、`Source/PuddingAgent/Dockerfile`、
`Source/PuddingPlatformAdmin/Dockerfile`、`.dockerignore`、`TestScripts/e2e/run-docker-smoke.ps1`。
E2E 一律在宿主机的源码开发栈（`python dev-up.py`）或产品态 Desktop 进程上运行，不再使用 `docker compose`。
