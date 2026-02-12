# Pudding E2E Tests

## Quick Start
1. Start with Docker: `.\build-and-up.ps1`
2. Run healthcheck: `.\Tests\e2e\healthcheck.ps1`
3. Run browser smoke: (Playwright/Python)

## Test Layers
- **Healthcheck**: API + Fake LLM available
- **Browser Smoke**: Login → Chat → Verify response
- **Full E2E**: Sub-agent run → Diagnostics API → Run archive

## Known Issues
- `PuddingWebApiTests` may fail with file-lock errors (CS2012) when run concurrently with a running app.
  Fix: Use isolated output directories or stop the app before running tests.
  Workaround: `docker compose down && dotnet test`
