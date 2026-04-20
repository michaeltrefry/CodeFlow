# syntax=docker/dockerfile:1.7
FROM node:22-alpine AS build
WORKDIR /src

COPY codeflow-ui/package.json codeflow-ui/package-lock.json ./
RUN npm ci

COPY codeflow-ui/ ./
RUN npx ng build --configuration production

FROM nginx:1.27-alpine AS runtime
COPY codeflow-ui/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/codeflow-ui/browser /usr/share/nginx/html

EXPOSE 80
