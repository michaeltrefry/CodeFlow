# syntax=docker/dockerfile:1.7
FROM node:24-bookworm-slim AS build
WORKDIR /src

COPY codeflow-ui/package.json codeflow-ui/package-lock.json ./
RUN npm ci

COPY codeflow-ui/ ./
RUN npx ng build --configuration production

FROM nginx:1.27-alpine AS runtime
COPY codeflow-ui/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/codeflow-ui/browser /usr/share/nginx/html
COPY codeflow-ui/docker-entrypoint.sh /docker-entrypoint.d/00-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/00-runtime-config.sh

EXPOSE 80
