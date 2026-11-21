import { createHash, randomUUID } from 'node:crypto';
import { readFile, rename, unlink, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { busClient, chatClient, deliverToChat } from './pudding-bus-client.mjs';
import { sessionId } from './sessions.mjs';

const [command, configFile, ...args] = process.argv.slice(2);
const stateDirectory = join(process.env.LOCALAPPDATA || join(homedir(), '.local', 'state'), 'CodexChatHttp');
const controller = new AbortController();

async function readJson(path) {
  const contents = await readFile(path, 'utf8');
  try { return JSON.parse(contents.replace(/^\uFEFF/, '')); }
  catch { throw new Error('配置文件或接入记录不是有效的 JSON。'); }
}

async function writeCheckpoint(path, checkpoint) {
  const temporary = path + '.' + randomUUID() + '.tmp';
  try {
    await writeFile(temporary, JSON.stringify(checkpoint), { flag: 'wx', mode: 0o600 });
    await rename(temporary, path);
  } finally {
    await unlink(temporary).catch(() => {});
  }
}

function bindingMatches(value, config) {
  return value?.workspaceId === config.workspaceId && value?.agentId === config.agentId;
}

async function readCheckpoint(path, config) {
  try {
    const state = await readJson(path);
    if (!bindingMatches(state, config) || state.sessionId !== config.sessionId ||
        !Number.isSafeInteger(state.cursor) || state.cursor < 0) {
      throw new Error('接入记录与配置的 Agent 或 Codex 会话不一致。');
    }
    return state;
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    return { workspaceId: config.workspaceId, agentId: config.agentId, sessionId: config.sessionId, cursor: 0 };
  }
}

async function run(config, checkpointFile) {
  const signal = controller.signal;
  const bus = busClient(config, signal);
  const connectionPath = join(stateDirectory, 'connection-' + config.chatPort + '.json');
  const chat = async () => {
    const connection = await readJson(connectionPath);
    if (connection.baseUrl !== 'http://127.0.0.1:' + config.chatPort)
      throw new Error('Chat HTTP 连接文件与配置的本机端口不一致。');
    return chatClient(connection, config.sessionId, signal);
  };
  await (await chat()).check();
  let state = await readCheckpoint(checkpointFile, config);
  let ownedLease = null;
  try {
    while (!signal.aborted) {
      let pause = 750;
      try {
        if (!ownedLease || Date.parse(ownedLease.expiresAtUtc) - Date.now() < 50000) {
          ownedLease = ownedLease ? await bus.renew(ownedLease.leaseId) : await bus.register();
          if (!bindingMatches(ownedLease, config)) {
            const error = new Error('总线返回的工作区或 Agent ID 与配置不一致。');
            error.fatal = true;
            throw error;
          }
          state = { ...state, leaseId: ownedLease.leaseId, expiresAtUtc: ownedLease.expiresAtUtc };
          await writeCheckpoint(checkpointFile, state);
        }
        const page = await bus.inbox(ownedLease.leaseId, state.cursor);
        // A single ordered delivery at a time keeps the cursor behind any work with an uncertain result.
        if (page.items.length) {
          const item = page.items[0];
          if (!Number.isSafeInteger(item.sequence) || item.sequence <= state.cursor) throw new Error('无效的收件箱游标。');
          let outcome;
          try { outcome = await deliverToChat(await chat(), item); }
          catch (error) {
            // Chat HTTP may have restarted and rotated its local credential.
            if (error.status === 401) throw new Error('Chat HTTP 连接正在更新。');
            throw error;
          }
          if (outcome) {
            if (outcome.result) await bus.settle(ownedLease.leaseId, item.messageId, outcome.result);
            state = { ...state, cursor: item.sequence };
            await writeCheckpoint(checkpointFile, state);
          }
        }
      } catch (error) {
        if (signal.aborted) break;
        if (error.fatal || error.status === 401 || error.status === 403) throw error;
        if (error.code === 'agent.adapter_lease_rejected') ownedLease = null;
        if (error.code !== 'agent.adapter_busy') console.error('总线轮询暂缓。', error.status ?? error.code ?? error.name);
        pause = 3000;
      }
      await delay(pause, null, { signal }).catch(error => { if (!signal.aborted) throw error; });
    }
  } finally {
    if (ownedLease) await busClient(config).disconnect(ownedLease.leaseId).catch(() => {});
  }
}

async function main() {
  if (!configFile || !['run', 'status', 'agents', 'send', 'delegate', 'result'].includes(command)) {
    throw new Error('用法：node pudding-bus.mjs <run|status|agents|send|delegate|result> <配置文件> [参数]');
  }
  const config = await readJson(resolve(configFile));
  config.sessionId = sessionId(config.sessionId);
  config.chatPort ??= 47931;
  if (!Number.isInteger(config.chatPort) || config.chatPort < 1 || config.chatPort > 65535 ||
      typeof config.agentId !== 'string' || !config.agentId || typeof config.workspaceId !== 'string' || !config.workspaceId) {
    throw new Error('配置需要工作区、Agent ID、Codex 会话 ID 和有效本机端口。');
  }
  const key = createHash('sha256').update(JSON.stringify([new URL(config.baseUrl).origin, config.workspaceId, config.agentId])).digest('hex');
  const checkpointFile = join(stateDirectory, 'pudding-bus-' + key + '.json');
  if (command === 'run') return run(config, checkpointFile);

  const state = await readCheckpoint(checkpointFile, config);
  if (command === 'status') {
    console.log(JSON.stringify({ agentId: state.agentId, cursor: state.cursor, expiresAtUtc: state.expiresAtUtc }));
    return;
  }
  if (!state.leaseId) throw new Error('请先启动总线订阅。');
  const bus = busClient(config);
  if (command === 'agents') {
    let after;
    do {
      const page = await bus.directory(state.leaseId, after);
      console.log(JSON.stringify(page.items));
      after = page.nextCursor;
    } while (after);
    return;
  }
  if (command === 'result' && args.length === 1) {
    console.log(JSON.stringify(await bus.receipt(state.leaseId, args[0])));
    return;
  }
  if ((command === 'send' || command === 'delegate') && args.length === 3) {
    const [targetAgentId, operationId, textFile] = args;
    const text = (await readFile(resolve(textFile), 'utf8')).replace(/^\uFEFF/, '');
    if (!text.trim() || text.length > 16000) throw new Error('文本必须为 1 到 16000 个字符。');
    console.log(JSON.stringify(await bus[command](state.leaseId, sessionId(operationId), targetAgentId, text)));
    return;
  }
  throw new Error('send/delegate 需要目标 Agent ID、操作 UUID、UTF-8 文本文件。result 需要 messageId。');
}

process.once('SIGINT', () => controller.abort());
process.once('SIGTERM', () => controller.abort());
main().catch(error => {
  if (controller.signal.aborted) return;
  console.error(error.status ? 'HTTP ' + error.status + (error.code ? ' ' + error.code : '') : error.message);
  process.exitCode = 1;
});
