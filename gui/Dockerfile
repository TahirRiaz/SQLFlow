# The SQLFlow GUI as a container: the built SPA served by unprivileged nginx on port 8080. The API base URL is
# injected at CONTAINER START (not build) by writing /config.json from SQLFLOW_API_BASE_URL, so one image serves
# every environment:
#   SQLFLOW_API_BASE_URL=""                          same-origin (the recommended ingress path-split layout)
#   SQLFLOW_API_BASE_URL="https://api.example.com"   a separate API origin (requires CORS on the control plane)
#
# Build from the gui/ directory:
#   docker build -t sqlflow-gui:latest gui/

FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build
# The entrypoint script must be executable or the base image's entrypoint silently skips it and /config.json
# is never written. The bit is set here, in the root build stage, because COPY --from preserves modes while
# COPY --chmod needs BuildKit, which classic builders (ACR Tasks) do not have. The CR strip keeps the shebang
# valid when the build context comes from a Windows checkout (CRLF would make the kernel look for /bin/sh\r).
RUN sed -i 's/\r$//' docker/40-runtime-config.sh && chmod 755 docker/40-runtime-config.sh

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY --from=build --chown=nginx:nginx /src/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/docker/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh
EXPOSE 8080
