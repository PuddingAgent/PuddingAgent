import fs from 'fs';
import path from 'path';

// ── P6 / D11 定向测试：深链入口归一 ───────────────────────────────────
// 命题：`public/scripts/root-redirect.js` 是**静态经典脚本**（无 import，由 <head> 同步加载），
// 因此只能「读源码文本 + 注入假 window 执行」来断言其行为。
// 本文件的用例**全部可失败**：把判断退回只处理 `pathname === '/'`（M1）⇒ I1/I5/I6 红；
// 删掉「基座内早退」守卫（M2）⇒ I3/I4 红（会自噬成 /admin/admin 或死循环重定向）。

const SCRIPT_PATH = path.join(__dirname, '..', 'public', 'scripts', 'root-redirect.js');
const SCRIPT_SOURCE = fs.readFileSync(SCRIPT_PATH, 'utf8');

interface FakeWindow {
  location: {
    pathname: string;
    search: string;
    hash: string;
    replace: (url: string) => void;
  };
}

/** 以给定 location 运行脚本，返回 `replace()` 的实参列表（未调用 ⇒ 空数组）。 */
function runRedirect(pathname: string, search = '', hash = ''): string[] {
  const calls: string[] = [];
  const fakeWindow: FakeWindow = {
    location: { pathname, search, hash, replace: (url: string) => void calls.push(url) },
  };
  // eslint-disable-next-line no-new-func
  new Function('window', SCRIPT_SOURCE)(fakeWindow);
  return calls;
}

describe('root-redirect：基座外深链必须回到 /admin/ 基座内（D11）', () => {
  it('I1 · `/index-status` ⇒ 重定向到 `/admin/index-status`（本命用例）', () => {
    expect(runRedirect('/index-status')).toEqual(['/admin/index-status']);
  });

  it('I6 · 任意深链同样进基座；`/adminfoo` 不得被误判为已在基座内', () => {
    expect(runRedirect('/any/deep/path')).toEqual(['/admin/any/deep/path']);
    expect(runRedirect('/adminfoo')).toEqual(['/admin/adminfoo']);
  });

  it('I2 · `/` 的既有行为不得回退：仍应重定向到 `/admin/`', () => {
    expect(runRedirect('/')).toEqual(['/admin/']);
  });

  it('I3 · 已在基座内一律不重定向（防重定向循环）', () => {
    expect(runRedirect('/admin/')).toEqual([]);
    expect(runRedirect('/admin/index-status')).toEqual([]);
    expect(runRedirect('/admin/some/deep/route')).toEqual([]);
  });

  it('I4 · 无尾斜杠的 `/admin` 不得被改写成 `/admin/admin`', () => {
    expect(runRedirect('/admin')).toEqual([]);
  });

  it('I5 · search 与 hash 必须原样保留', () => {
    expect(runRedirect('/x', '?a=1', '#h')).toEqual(['/admin/x?a=1#h']);
    expect(runRedirect('/', '?a=1', '#h')).toEqual(['/admin/?a=1#h']);
    expect(runRedirect('/index-status', '?tab=codeIndex')).toEqual([
      '/admin/index-status?tab=codeIndex',
    ]);
  });

  it('I7 · 不产出双斜杠或双基座（前导斜杠归一）', () => {
    for (const input of ['/index-status', '//index-status', '///index-status']) {
      const [target] = runRedirect(input);
      expect(target.startsWith('/admin/')).toBe(true);
      expect(target.slice('/admin/'.length).startsWith('/')).toBe(false);
      expect(target.startsWith('/admin/admin/')).toBe(false);
    }
  });
});
