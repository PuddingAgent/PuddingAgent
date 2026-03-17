# 本地预编译版 Dockerfile（读取 build-and-up.ps1 输出的 dist/ 目录）
FROM nginx:alpine
COPY dist/ /usr/share/nginx/html/
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
