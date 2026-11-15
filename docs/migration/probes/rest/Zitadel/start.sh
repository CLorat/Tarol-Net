#!/usr/bin/env bash
# Starts PostgreSQL 17 + Zitadel (ghcr.io/zitadel/zitadel, pinned) in Docker on port 8080, waits until ready, provisions it
# (setup.py) and writes <dir>/clients.txt and <dir>/project.txt. usage: start.sh <dir>   stop: docker rm -f zt ztpg
set -euo pipefail
dir="$1"; image=ghcr.io/zitadel/zitadel:v4.20.3
mkdir -p "$dir"; chmod 777 "$dir"; rm -f "$dir/admin.pat"
docker rm -f zt ztpg >/dev/null 2>&1 || true; docker network create zt >/dev/null 2>&1 || true
docker run -d --name ztpg --network zt -e POSTGRES_PASSWORD=probe postgres:17 >/dev/null
until docker exec ztpg pg_isready -q; do sleep 1; done; sleep 2
docker run -d --name zt --network zt -p 8080:8080 -v "$dir":/out \
  -e ZITADEL_DATABASE_POSTGRES_HOST=ztpg -e ZITADEL_DATABASE_POSTGRES_PORT=5432 -e ZITADEL_DATABASE_POSTGRES_DATABASE=zitadel \
  -e ZITADEL_DATABASE_POSTGRES_USER_USERNAME=zitadel -e ZITADEL_DATABASE_POSTGRES_USER_PASSWORD=zitadel -e ZITADEL_DATABASE_POSTGRES_USER_SSL_MODE=disable \
  -e ZITADEL_DATABASE_POSTGRES_ADMIN_USERNAME=postgres -e ZITADEL_DATABASE_POSTGRES_ADMIN_PASSWORD=probe -e ZITADEL_DATABASE_POSTGRES_ADMIN_SSL_MODE=disable \
  -e ZITADEL_EXTERNALSECURE=false -e ZITADEL_EXTERNALDOMAIN=localhost -e ZITADEL_EXTERNALPORT=8080 \
  -e ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_USERNAME=admin-sa -e ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_NAME=admin-sa \
  -e ZITADEL_FIRSTINSTANCE_ORG_MACHINE_PAT_EXPIRATIONDATE=2030-01-01T00:00:00Z -e ZITADEL_FIRSTINSTANCE_PATPATH=/out/admin.pat \
  "$image" start-from-init --masterkey MasterkeyNeedsToHave32Characters --tlsMode disabled >/dev/null
start=$(date +%s)
until [ -s "$dir/admin.pat" ] && curl -sf http://localhost:8080/debug/ready >/dev/null; do sleep 1; done
echo "zitadel $(docker run --rm "$image" --version 2>/dev/null | tail -1 | awk '{print $3}') ready after $(( $(date +%s) - start )) s"
python3 "$(dirname "$0")/setup.py" http://localhost:8080 "$dir/admin.pat" "$dir/project.txt" > /dev/null
echo "provisioned: project artflow, roles seller bidder reader admin dealer service, machine users $(awk '/^client/ {printf "%s ", $2}' "$dir/clients.txt")"
