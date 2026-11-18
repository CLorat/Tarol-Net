#!/usr/bin/env bash
# Runs every search probe and appends to output.txt next to this script. Resumable: each section writes <work>/<n>.ok, and a
# rerun skips the sections that finished (a session restart kills Docker and background jobs). FRESH=1 starts over.
# usage: setsid nohup docs/migration/probes/search/all.sh <work-dir> > /tmp/search-all.log 2>&1 &
# Needs Docker (dockerd &). Containers: postgres:17, meilisearch v1.13, typesense 28.0, opensearch 2.19.1 (mirror.gcr.io).
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ "${FRESH:-0}" = 1 ]; then rm -rf "$w"; : > "$out"; fi; mkdir -p "$w"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-600; }
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECKS|xception|^\|' | clean; }
img=mirror.gcr.io
start() {   # start (or create) one container and wait until it answers
  docker unpause "$(name "$1")" >/dev/null 2>&1
  case "$1" in
    pg) docker start tarol-search-pg >/dev/null 2>&1 || docker run -d --name tarol-search-pg -e POSTGRES_PASSWORD=probe -p 55433:5432 --shm-size=1g $img/postgres:17 -c max_connections=200 -c shared_buffers=1GB >/dev/null
        until docker exec tarol-search-pg pg_isready -q 2>/dev/null; do sleep 1; done; sleep 1 ;;
    meili) docker start tarol-search-meili >/dev/null 2>&1 || docker run -d --name tarol-search-meili -p 57700:7700 -e MEILI_ENV=development -e MEILI_NO_ANALYTICS=true $img/getmeili/meilisearch:v1.13 >/dev/null
        until curl -sf localhost:57700/health >/dev/null; do sleep 1; done ;;
    typesense) docker start tarol-search-ts >/dev/null 2>&1 || docker run -d --name tarol-search-ts -p 58108:8108 $img/typesense/typesense:28.0 --data-dir /tmp --api-key=probe-local-only >/dev/null
        until curl -sf localhost:58108/health >/dev/null; do sleep 1; done ;;
    opensearch) docker start tarol-search-os >/dev/null 2>&1 || docker run -d --name tarol-search-os -p 59200:9200 -e discovery.type=single-node -e DISABLE_SECURITY_PLUGIN=true -e DISABLE_INSTALL_DEMO_CONFIG=true \
          -e cluster.routing.allocation.disk.threshold_enabled=false -e "OPENSEARCH_JAVA_OPTS=-Xms2g -Xmx2g" $img/opensearchproject/opensearch:2.19.1 >/dev/null
        until curl -sf localhost:59200 >/dev/null; do sleep 2; done ;;
  esac
}
name() { case "$1" in pg) echo tarol-search-pg;; meili) echo tarol-search-meili;; typesense) echo tarol-search-ts;; opensearch) echo tarol-search-os;; esac; }
only() {   # run one engine alone: unpause/start it, pause the others
  local e
  for e in pg meili typesense opensearch; do
    if [ "$e" = "$1" ] || [ "$e" = "${2:-}" ]; then docker unpause "$(name $e)" >/dev/null 2>&1; start $e; else docker pause "$(name $e)" >/dev/null 2>&1; fi
  done
}
mem() { docker stats --no-stream --format '{{.Name}} {{.MemUsage}} (CPU {{.CPUPerc}})' "$(name $1)" | sed 's/^/INFO container memory: /'; }
disk() { docker exec "$(name $1)" sh -c "du -sb $2 2>/dev/null | cut -f1" | awk -v e="$1" '{printf "INFO %s data directory on disk: %.0f MB\n", e, $1/1e6}'; }
app="$w/a/src/Search"; native="$w/c/out/Probe.Search"
failed=0
J() { (cd "$app" && timeout 7200 dotnet run --no-build -- "$@" 2>&1) > "$w/last.log"; grep -q "Unhandled exception" "$w/last.log" && failed=1; show < "$w/last.log"; }
N() { (PROBE_RUNTIME="Native AOT" timeout 3600 "$native" "$@" 2>&1) | show; }
sec() { [ -f "$w/$1.ok" ] && return 1; echo; echo "## $1. $2"; return 0; }
ok() { if [ "$failed" = 0 ]; then touch "$w/$1.ok"; else echo "SECTION $1 NOT MARKED DONE: a probe crashed (rerun resumes here)"; failed=0; fi; }
{
[ -f "$w/1.ok" ] && "$here/run.sh" "$w/a" Search build > "$w/a.log" 2>&1   # a resumed run builds today's probe sources
if sec 1 "Environment, build, licences"; then
  echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(free -g | awk '/Mem/{print $2}') GB, Docker $(docker version -f '{{.Server.Version}}')"
  "$here/run.sh" "$w/a" Search build > "$w/a.log" 2>&1; grep -E ' error |Build succeeded' "$w/a.log" | clean | sort -u
  only pg; echo "INFO $(docker exec tarol-search-pg postgres --version)"
  echo "INFO meilisearch $(docker run --rm $img/getmeili/meilisearch:v1.13 meilisearch --version 2>/dev/null)"
  echo "INFO typesense image $img/typesense/typesense:28.0, opensearch $img/opensearchproject/opensearch:2.19.1"
  echo "INFO licence of the PostgreSQL server (image): $(docker exec tarol-search-pg sh -c 'head -c 300 /usr/share/doc/postgresql-17/copyright 2>/dev/null || ls /usr/share/doc | grep -i postgres | head -3' | tr '\n' ' ')"
  for l in "meilisearch v1.13.3|https://raw.githubusercontent.com/meilisearch/meilisearch/v1.13.3/LICENSE" "meilisearch main|https://raw.githubusercontent.com/meilisearch/meilisearch/main/LICENSE" \
           "meilisearch main, enterprise edition|https://raw.githubusercontent.com/meilisearch/meilisearch/main/LICENSE-EE" \
           "typesense v28|https://raw.githubusercontent.com/typesense/typesense/v28.0/LICENSE.txt" "opensearch 2.19.1|https://raw.githubusercontent.com/opensearch-project/OpenSearch/2.19.1/LICENSE.txt" \
           "elasticsearch main|https://raw.githubusercontent.com/elastic/elasticsearch/main/LICENSE.txt" "pgvector|https://raw.githubusercontent.com/pgvector/pgvector/master/LICENSE" \
           "paradedb pg_search|https://raw.githubusercontent.com/paradedb/paradedb/main/LICENSE"; do
    n="${l%%|*}"; u="${l#*|}"; code=$(curl -s -o "$w/lic.txt" -w '%{http_code}' "$u")
    echo "INFO licence $n: HTTP $code: $(head -c 2000 "$w/lic.txt" | tr -s ' \n' ' ' | grep -oiE 'MIT License|GNU AFFERO GENERAL PUBLIC LICENSE|GNU GENERAL PUBLIC LICENSE|Apache License|Business Source License|Elastic License|Server Side Public License|PostgreSQL License|Permission to use, copy, modify, and distribute|Version [0-9.]+|Copyright[^.]{0,60}' | head -4 | tr '\n' ';')"
  done
  docker run --rm --entrypoint sh $img/opensearchproject/opensearch:2.19.1 -c 'head -3 /usr/share/opensearch/LICENSE.txt' 2>/dev/null | tr -s ' \n' ' ' | sed 's/^/INFO licence file in the opensearch image: /'; echo
  ok 1
fi
if sec 2 "Functional checks: the contract every candidate passes, languages, the feeder prototype (JIT)"; then
  only pg; J selftest pg; J feeder
  for e in meili typesense opensearch; do only $e; J selftest $e; done
  ok 2
fi
for e in pg meili typesense opensearch; do
  n=$((3 + $(echo "pg meili typesense opensearch" | tr ' ' '\n' | grep -n "^$e$" | cut -d: -f1) - 1))
  if sec $n "Bench $e alone (others paused): load 10k -> 100k -> 1M, sizes, queries (round 2 of 2, page of 20), feeder upserts, rebuild; then write -> searchable"; then
    only $e; J bench $e; mem $e
    case $e in meili) disk meili /meili_data;; typesense) disk typesense /tmp;; opensearch) disk opensearch /usr/share/opensearch/data;; pg) disk pg /var/lib/postgresql/data;; esac
    echo; J lag $e
    ok $n
  fi
done
if sec 7 "Feeder: rebuild from the feed through Load<T> (20,000 artworks = 60,000 documents), JIT"; then
  only pg; J feeder bench 20000; ok 7
fi
if sec 8 "Native AOT: publish (IL warnings), the contract natively against every engine, the feeder natively"; then
  "$here/run.sh" "$w/c" Search publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
  grep -E 'warning|error' "$w/c.log" | clean | sort -u | head -20
  echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/c.log")"; echo "native binary: $(stat -c %s "$native") bytes"
  only pg; N selftest pg; N feeder
  for e in meili typesense opensearch; do only $e; N selftest $e; done
  ok 8
fi
if sec 9 "The extensions as a non-superuser that owns its database (managed PostgreSQL), in a fresh postgres:17"; then
  docker rm -f tarol-ext-check >/dev/null 2>&1
  docker run -d --name tarol-ext-check -e POSTGRES_PASSWORD=probe $img/postgres:17 >/dev/null
  until docker exec tarol-ext-check pg_isready -q 2>/dev/null; do sleep 1; done; sleep 2
  docker exec tarol-ext-check psql -U postgres -q -c "CREATE ROLE app LOGIN PASSWORD 'x'" -c "CREATE DATABASE appdb OWNER app"
  docker exec -e PGPASSWORD=x tarol-ext-check psql -h localhost -U app -d appdb -At -c "SELECT 'INFO superuser: ' || rolsuper FROM pg_roles WHERE rolname = current_user" \
    -c "CREATE EXTENSION pg_trgm" -c "CREATE EXTENSION btree_gin" -c "CREATE EXTENSION unaccent" -c "CREATE EXTENSION vector" \
    -c "SELECT 'INFO created as app: ' || string_agg(extname || ' ' || extversion, ', ' ORDER BY extname) FROM pg_extension" 2>&1 | sed 's/^ERROR/INFO ERROR/; s/^CREATE EXTENSION/INFO CREATE EXTENSION ok/'
  docker rm -f tarol-ext-check >/dev/null; ok 9
fi
if sec 10 "Meilisearch write -> searchable again, searching the token as a phrase (section 4's query was typo-tolerant and found the previous sample)"; then
  only meili; J lag meili; ok 10
fi
if sec 11 "PostgreSQL at 1M (the table section 3 left): bounded ranking (rank the first 10,000 / 1,000 matches) per tenant index, and the trigram index led by the tenant"; then
  only pg; J bounded; ok 11
fi
echo; echo "## done $(date -u +%H:%M)"
} 2>&1 | tee -a "$out"
