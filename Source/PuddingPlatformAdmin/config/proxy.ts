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
  // dev 环境代理 → 后端 API（默认 localhost:5000，可通过 PROXY_TARGET 环境变量覆盖）
  dev: {
    '/api/': {
      target: process.env.PROXY_TARGET || 'http://localhost:5000',
      changeOrigin: true,
      secure: false,
    },
    '/ingress/': {
      target: process.env.PROXY_TARGET || 'http://localhost:5000',
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
