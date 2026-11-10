import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, unlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { HttpError } from './sessions.mjs';

// Reserve before dispatch. A crash between Codex acceptance and the receipt
// write leaves an uncertain request, which must never be submitted again.
export function receiptStore(root) {
  return async function once(session, id, payload, dispatch) {
    const dir = join(root, 'receipts', session);
    const file = join(dir, id + '.json');
    const digest = createHash('sha256').update(JSON.stringify(payload)).digest('hex');
    await mkdir(dir, { recursive: true, mode: 0o700 });
    try {
      await writeFile(file, JSON.stringify({ digest }), { flag: 'wx', mode: 0o600 });
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      let previous;
      try { previous = JSON.parse(await readFile(file, 'utf8')); } catch { /* incomplete reservation */ }
      if (previous && previous.digest !== digest) throw new HttpError(409, '该 messageId 已用于不同的请求');
      if (previous?.receipt) return previous.receipt;
      throw new HttpError(409, '该 messageId 已提交，结果待确认，请查询队列和历史');
    }
    const receipt = await dispatch();
    const tmp = file + '.' + randomUUID() + '.tmp';
    try {
      await writeFile(tmp, JSON.stringify({ digest, receipt }), { flag: 'wx', mode: 0o600 });
      await rename(tmp, file);
    } finally { await unlink(tmp).catch(() => {}); }
    return receipt;
  };
}
