#!/bin/sh
# Writes the SPA's runtime configuration from the container environment at startup, so one built image serves any
# environment. An empty SQLFLOW_API_BASE_URL means same-origin (the ingress path-split layout); the SPA falls back
# to window.location.origin when apiBaseUrl is blank.
set -e

API_BASE_URL="${SQLFLOW_API_BASE_URL:-}"
printf '{\n  "apiBaseUrl": "%s"\n}\n' "$API_BASE_URL" > /usr/share/nginx/html/config.json
echo "sqlflow-gui: config.json written (apiBaseUrl='${API_BASE_URL}')"
