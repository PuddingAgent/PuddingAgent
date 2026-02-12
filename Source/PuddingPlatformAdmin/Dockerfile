# ── 运行阶段（静态文件托管）─────────────────────────────────────
# 前端由 build-and-up.ps1 在宿主机用 pnpm 完成编译，Dockerfile 直接
# COPY 本地 dist/ 产物，避免 Docker 内拉取 node:20-alpine 失败。
FROM nginx:alpine AS runtime
WORKDIR /usr/share/nginx/html

# 删除 nginx 默认页，拷贝构建产物
RUN rm -rf ./*
COPY dist .

# SPA 路由回退：所有非文件路径返回 index.html
COPY docker/nginx-spa.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
