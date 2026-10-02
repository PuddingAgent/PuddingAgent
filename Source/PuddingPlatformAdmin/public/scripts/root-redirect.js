/**
 * 深链入口归一（D11）—— 宿主把**任意未知路径**都回退到本页 shell，而 Admin SPA 的基座是
 * `/admin/`（见 `config/config.ts` 的 `publicPath`）。基座外的 pathname 永远匹配不上任何路由，
 * 页面就会**永久**停在「正在加载资源」加载壳（实测 `http://127.0.0.1/index-status` 即如此）。
 *
 * 因此：凡不在 `/admin/` 基座内，就带上 search + hash 回到基座内；已在基座内一律不动。
 *
 * ⚠️ 本文件由 `<head>` 以 `<script src>` **同步**加载，不经打包器/babel ⇒ 只能写 ES5，
 *    且必须保持极小（它在首屏前同步执行）。
 */
(function () {
  var ADMIN_BASE = '/admin/';
  var path = window.location.pathname;
  // 已在基座内 ⇒ 绝不重定向（含无尾斜杠的 `/admin`，否则会自噬成 `/admin/admin`）。
  if (path.indexOf(ADMIN_BASE) === 0 || path === '/admin') return;
  // 去掉多余前导斜杠，避免拼出 `/admin//x` 这种双斜杠。
  var relative = path.replace(/^\/+/, '');
  window.location.replace(
    ADMIN_BASE + relative + window.location.search + window.location.hash,
  );
})();
