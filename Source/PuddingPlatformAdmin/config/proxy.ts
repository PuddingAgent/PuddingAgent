/**
 * @name 代理的配置
 * @see 在生产环境 代理是无法生效的，所以这里没有生产环境的配置
 * -------------------------------
 * The agent cannot take effect in the production environment
 * so there is no configuration of the production environment
 * For details, please see
 * https://pro.ant.design/docs/deploy
 *
 * @doc https://umijs.org/docs/guides/proxy
 */
export default {
  // dev 环境代理 → PuddingPlatform ASP.NET Core 后端（port 5039）
  dev: {
    '/api/': {
      target: 'http://localhost:5039',
      changeOrigin: true,
      secure: false,
    },
    // dev 环境代理 → PuddingController（port 5000）
    // 模拟 nginx 对 /ingress/ 的 pathRewrite：/ingress/foo → /api/foo
    '/ingress/': {
      target: 'http://localhost:5000',
      changeOrigin: true,
      secure: false,
      pathRewrite: { '^/ingress/': '/api/' },
    },
  },
  /**
   * @name 详细的代理配置
   * @doc https://github.com/chimurai/http-proxy-middleware
   */
  test: {
    // localhost:8000/api/** -> https://preview.pro.ant.design/api/**
    '/api/': {
      target: 'https://proapi.azurewebsites.net',
      changeOrigin: true,
      pathRewrite: { '^': '' },
    },
  },
  pre: {
    '/api/': {
      target: 'your pre url',
      changeOrigin: true,
      pathRewrite: { '^': '' },
    },
  },
};
