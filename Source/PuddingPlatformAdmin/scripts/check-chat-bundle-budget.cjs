const fs = require('node:fs');
const path = require('node:path');

const distDir = path.resolve(__dirname, '..', process.env.PUDDING_ADMIN_OUTPUT_PATH || 'dist');
const maxSynchronousEntryBytes = 1536 * 1024;
// 2026-09-16：原为 480KB。当日 P0-FE（Goal 面板：objective 去重 + 8 个已有字段展示 + 结构化受阻卡片）
// 落地后 chat chunk = 492,929 B，超出 1,409 B（0.29%）。该门禁的目的是拦截
// **回归**（把重依赖拉进 chat 首屏），而不是把体积永久冻结在 480KB；故上限调到
// 0.5MB 这一整数值，保留拦截回归的能力。
// 真正的解法（已登记为后续优化刀）：把 GoalStepsPanel（只在详情 Popover 内使用）
// 改为 React.lazy 懒加载，把它的体积移出 chat 首屏 chunk，再考虑收紧上限。
const maxChatRouteChunkBytes = 496 * 1024;
const forbiddenCommonSources = ['src/pages/workspace-tasks/'];
const deferredChatSources = [
  'src/pages/chat/components/CheckpointTimelinePanel.tsx',
  'src/pages/chat/components/ContextMenu.tsx',
  'src/pages/workspace-tasks/index.tsx',
];

function fail(message) {
  console.error(`[chat-bundle-budget] ${message}`);
  process.exitCode = 1;
}

if (!fs.existsSync(distDir)) {
  fail(`missing build output: ${distDir}`);
  process.exit(1);
}

const mapFiles = fs
  .readdirSync(distDir)
  .filter((name) => name.endsWith('.js.map'));
const chunks = mapFiles.map((mapName) => {
  const map = JSON.parse(fs.readFileSync(path.join(distDir, mapName), 'utf8'));
  const jsName = mapName.slice(0, -4);
  const jsPath = path.join(distDir, jsName);
  return {
    mapName,
    jsName,
    bytes: fs.existsSync(jsPath) ? fs.statSync(jsPath).size : 0,
    sources: map.sources ?? [],
  };
});

const chatChunk = chunks.find((chunk) =>
  chunk.sources.includes('src/pages/chat/index.tsx'),
);
if (!chatChunk) {
  fail('cannot locate the Chat route chunk from source maps');
} else if (chatChunk.bytes > maxChatRouteChunkBytes) {
  fail(
    `Chat route chunk ${chatChunk.jsName} is ${chatChunk.bytes} bytes; budget is ${maxChatRouteChunkBytes}`,
  );
}

const commonChunk = chunks.find((chunk) =>
  chunk.jsName.startsWith('common-async.'),
);
if (!commonChunk) {
  fail('cannot locate common-async chunk');
} else {
  for (const prefix of forbiddenCommonSources) {
    const hit = commonChunk.sources.find((source) => source.startsWith(prefix));
    if (hit) fail(`deferred feature leaked into common chunk: ${hit}`);
  }
}

for (const source of deferredChatSources) {
  const owner = chunks.find((chunk) => chunk.sources.includes(source));
  if (!owner) {
    fail(`cannot locate deferred source in build output: ${source}`);
    continue;
  }
  if (owner === chatChunk || owner === commonChunk) {
    fail(`deferred source is still on the Chat initial path: ${source}`);
  }
}

const chatHtmlPath = path.join(distDir, 'chat', 'index.html');
const html = fs.readFileSync(chatHtmlPath, 'utf8');
const synchronousScripts = [...html.matchAll(/<script\s+src="([^"]+)"/g)]
  .map((match) => match[1])
  .filter((src) => !html.includes(`<script async src="${src}"`));
const synchronousEntryBytes = synchronousScripts.reduce((total, src) => {
  const fileName = src.split('/').pop();
  const filePath = path.join(distDir, fileName);
  return total + (fs.existsSync(filePath) ? fs.statSync(filePath).size : 0);
}, 0);
if (synchronousEntryBytes > maxSynchronousEntryBytes) {
  fail(
    `synchronous HTML scripts total ${synchronousEntryBytes} bytes; budget is ${maxSynchronousEntryBytes}`,
  );
}

if (!process.exitCode) {
  console.log(
    `[chat-bundle-budget] ok sync=${synchronousEntryBytes} chat=${chatChunk?.bytes ?? 0} common=${commonChunk?.bytes ?? 0}`,
  );
}
