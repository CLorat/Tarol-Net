#!/usr/bin/env bash
# EXPLAIN ANALYZE of the claim on three backlog shapes; writes output.txt next to this script.
# usage: claims.sh   (needs Docker; starts and removes its own postgres:17 container)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; out="$here/output.txt"
docker rm -f tarol-claim-pg >/dev/null 2>&1
docker run -d --name tarol-claim-pg -e POSTGRES_PASSWORD=probe postgres:17 >/dev/null
until docker exec tarol-claim-pg pg_isready -q; do sleep 1; done; sleep 2
{
echo "# $(docker exec tarol-claim-pg postgres --version), $(nproc) cores, $(date -u +%Y-%m-%d)"
for args in "0 2000 1" "0 16 125" "0 16 6250" "100000 2000 1" "100000 16 125"; do
  set -- $args; echo; echo "## $1 future timeout rows (one stream each) + $2 streams × $3 due rows"
  docker exec -i tarol-claim-pg psql -U postgres -q -v timeouts=$1 -v streams=$2 -v per=$3 < "$here/plans.sql" 2>&1 \
    | grep -E "^-- |Index Scan|Seq Scan|Execution Time|Rows Removed" | grep -v "outbox_stream on outbox p" | sed 's/^ *//'
done
} > "$out"
docker rm -f tarol-claim-pg >/dev/null 2>&1; echo "written $out"
