#!/usr/bin/env bash
# Runs every messaging probe against PostgreSQL 17, NATS 2.11 (JetStream) and RabbitMQ 4 in Docker and writes output.txt
# next to this script.  usage: all.sh <work-dir>   (needs a running Docker daemon: dockerd &)
# ALL_FROM=<n> resumes at section n (ALL_TO=<m> stops after section m): keeps the work dir (later sections reuse the builds of 1 and 4) and appends to output.txt.
# OLD_TAROL=<checkout of CLorat/Tarol@6a62f81> enables section 10 (default: ../../../../../Tarol next to this repository).
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; from="${ALL_FROM:-1}"; to="${ALL_TO:-99}"
old="${OLD_TAROL:-$(cd "$here/../../../../.." && pwd)/Tarol}"
if [ "$from" -le 1 ]; then rm -rf "$w"; : > "$out"; fi; mkdir -p "$w"
at() { [ "$from" -le "$1" ] && [ "$1" -le "$to" ]; }
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-420; }
median() { sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }
up() {   # (re)start the containers; a session restart stops Docker
  docker rm -f tarol-probe-pg tarol-probe-nats tarol-probe-nats-auth tarol-probe-rmq >/dev/null 2>&1
  docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
  docker run -d --name tarol-probe-nats -p 54222:4222 nats:2.11 -js >/dev/null
  docker run -d --name tarol-probe-nats-auth -p 54223:4222 -v "$here/Brokers/nats.conf:/etc/nats/nats.conf:ro" nats:2.11 -c /etc/nats/nats.conf >/dev/null
  docker run -d --name tarol-probe-rmq -p 55672:5672 rabbitmq:4-management >/dev/null
  until docker exec tarol-probe-pg pg_isready -q 2>/dev/null; do sleep 1; done
  for i in $(seq 1 90); do   # RabbitMQ sometimes fails its first boot (Erlang prelaunch): start it again
    docker exec tarol-probe-rmq rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1 && break
    if [ "$(docker inspect -f '{{.State.Running}}' tarol-probe-rmq)" != true ]; then
      docker rm -f tarol-probe-rmq >/dev/null; docker run -d --name tarol-probe-rmq -p 55672:5672 rabbitmq:4-management >/dev/null; fi
    sleep 2; done
  "$here/Brokers/rabbit-users.sh" tarol-probe-rmq; sleep 2
}
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECK|xception|^\|' | clean; }
app="$w/a/src/App"; native="$w/c/out/Probe.Messaging.App"
{
up
if at 1; then
echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(docker exec tarol-probe-pg postgres --version), NATS $(docker exec tarol-probe-nats nats-server --version 2>/dev/null || docker inspect -f '{{.Config.Image}}' tarol-probe-nats), RabbitMQ $(docker exec tarol-probe-rmq rabbitmqctl version 2>/dev/null)"
echo; echo "## 1. JIT: consumers, publications, context, keys, order — per transport (both systems on the Npgsql store, in-memory for 'memory')"
"$here/run.sh" "$w/a" App build > "$w/a.log" 2>&1; grep -E ' error ' "$w/a.log" | clean | sort -u
for k in memory postgres postgres+notify nats rabbit; do (cd "$app" && timeout 600 dotnet run --no-build -- selftest $k 2>&1) | show; done
fi
if at 2; then
echo; echo "## 2. JIT: who may publish under a contract prefix (NATS users, RabbitMQ users, PostgreSQL roles + row-level security)"
for k in nats rabbit postgres; do (cd "$app" && timeout 120 dotnet run --no-build -- security $k 2>&1) | show; done
fi
if at 3; then
echo; echo "## 3. JIT: candidate T-D, the old backplane shape (copied from CLorat/Tarol@6a62f81)"
(cd "$app" && timeout 120 dotnet run --no-build -- legacy 2>&1) | show
fi
if at 4; then
echo; echo "## 4. Native AOT publish (all transports in one app): warnings, size, the same checks natively"
"$here/run.sh" "$w/c" App publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
grep -E 'warning|error' "$w/c.log" | clean | sort -u
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/c.log")"; echo "native binary: $(stat -c %s "$native") bytes"
for k in memory postgres postgres+notify nats rabbit; do PROBE_RUNTIME="Native AOT" timeout 600 "$native" selftest $k 2>&1 | grep -E '^# |FAIL|ALL|CHECK|xception' | clean; done
for k in nats rabbit postgres; do PROBE_RUNTIME="Native AOT" timeout 120 "$native" security $k 2>&1 | grep -E '^# |FAIL|ALL|CHECK' | clean; done
PROBE_RUNTIME="Native AOT" "$native" legacy 2>&1 | grep -E '^# |FAIL|ALL|CHECK' | clean
fi
if at 5; then
echo; echo "## 5. Native AOT: one transport per binary (what a broker client adds)"
for t in postgres nats rabbit; do
  "$here/run.sh" "$w/s-$t" Size publish -c Release -r linux-x64 -p:ProbeTransport=$t -o "$w/s-$t/out" > "$w/s-$t.log" 2>&1
  echo "$t: IL warnings $(grep -cE 'IL[23][0-9]{3}' "$w/s-$t.log"), $(stat -c %s "$w/s-$t/out/Probe.Messaging.Size") bytes; $("$w/s-$t/out/Probe.Messaging.Size" 2>&1 | tail -1)"
done
fi
if at 6; then
echo; echo "## 6. Startup: process start → transport connected, subscription declared, first message received (median of 5)"
"$here/run.sh" "$w/e" App build -c Release > "$w/e.log" 2>&1
dll="$w/e/src/App/bin/Release/net11.0/Probe.Messaging.App.dll"
echo "| transport | JIT (Release) | Native AOT |"; echo "|---|---:|---:|"
for k in postgres nats rabbit; do
  j=$(for i in 1 2 3 4 5; do dotnet "$dll" startup $k 2>/dev/null | grep -oE '[0-9]+ ms' | grep -oE '[0-9]+'; done | median)
  n=$(for i in 1 2 3 4 5; do "$native" startup $k 2>/dev/null | grep -oE '[0-9]+ ms' | grep -oE '[0-9]+'; done | median)
  echo "| $k | $j ms | $n ms |"
done
fi
if at 7; then
echo; echo "## 7. Latency and throughput: commit of Auction.Settled → published → IssueInvoice committed in billing (JIT, Release)"
dotnet "$w/e/src/App/bin/Release/net11.0/Probe.Messaging.App.dll" bench postgres,postgres+notify,nats,rabbit 10 50 2000 2>&1 | show
echo "### the same, Native AOT"
PROBE_RUNTIME="Native AOT" "$native" bench postgres,postgres+notify,nats,rabbit 10 50 2000 2>&1 | show
fi
if at 8; then
echo; echo "## 8. Aspire AppHost 13.6.5: PostgreSQL (artflow, billing) + NATS (JetStream) + api + worker + billing (JIT projects)"
docker rm -f tarol-probe-nats tarol-probe-nats-auth tarol-probe-rmq >/dev/null 2>&1   # the AppHost's own NATS; free CPU
"$here/run.sh" "$w/f" Aspire build > "$w/f.log" 2>&1; grep -E ' error|Build succeeded' "$w/f.log" | clean | sort -u
"$here/Aspire/apphost.sh" "$(dirname "$(find "$w/f/src/Aspire/bin" -name Probe.Messaging.AppHost.dll | head -1)")" 5 2>&1 | clean
fi
if at 9; then
echo; echo "## 9. Aspire AppHost: the same with the Native AOT app as all three resources (AddExecutable)"
PROBE_APP_NATIVE="$native" "$here/Aspire/apphost.sh" "$(dirname "$(find "$w/f/src/Aspire/bin" -name Probe.Messaging.AppHost.dll | head -1)")" 5 2>&1 | clean
fi
if at 10 && [ -d "$old/src/Messaging" ]; then
echo; echo "## 10. The old Messaging tests on this SDK (CLorat/Tarol@$(git -C "$old" rev-parse --short HEAD))"
rm -rf "$w/old" && mkdir -p "$w/old"
for p in "$old"/tests/Messaging/*/ "$old"/src/Messaging/Messaging.Backplane.InMemory.UnitTests/; do
  n=$(basename "$p"); dotnet build "$p" -o "$w/old/$n" > "$w/old/$n.log" 2>&1
  exe=$(ls "$w/old/$n" | grep -E "^Tarol\..*Tests$" | head -1)
  echo "$n: build $(grep -cE ' error ' "$w/old/$n.log") errors, $(grep -oE 'warning NU[0-9]+' "$w/old/$n.log" | sort -u | tr '\n' ' ')| $(cd "$w/old/$n" && timeout 300 "./$exe" 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | grep -oE 'Total: [0-9]+(, Errors: [0-9]+, Failed: [0-9]+)?' | tail -1)"
done
(cd "$w/old/Messaging.IntegrationTests" && ./Tarol.Messaging.IntegrationTests 2>&1) | sed 's/\x1b\[[0-9;]*m//g' | grep -oE "System\.[A-Za-z.]+Exception : [^.]*\.|at Microsoft[^ ]+(WriteArguments|SendHubMessage)" | sort | uniq -c
echo "### MessageBackplaneRunnerTests, 6 runs (the flaky test)"
for i in 1 2 3 4 5 6; do (cd "$w/old/Messaging.Backplane.UnitTests" && ./Tarol.Messaging.Backplane.UnitTests 2>&1) | sed 's/\x1b\[[0-9;]*m//g' | grep -oE 'Failed: [0-9]+' | tail -1; done | sort | uniq -c
echo "### F1 fixed in a copy (AddJsonProtocol with a source-generated context for string and byte[], client and server): does F2 appear?"
rm -rf "$w/oldfix" && cp -r "$old" "$w/oldfix"
python3 - "$w/oldfix/tests/Messaging/Messaging.IntegrationTests/Infrastructure/SignalRInMemoryTestHost.cs" <<'PY'
import sys; p = sys.argv[1]; s = open(p).read()
s = s.replace("services.AddSignalR();", "services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJson.Default));", 1)
s = s.replace(".WithAutomaticReconnect()", ".WithAutomaticReconnect()\n            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJson.Default))", 1)
s += "\n[System.Text.Json.Serialization.JsonSerializable(typeof(string))]\n[System.Text.Json.Serialization.JsonSerializable(typeof(byte[]))]\ninternal sealed partial class HubJson : System.Text.Json.Serialization.JsonSerializerContext;\n"
open(p, "w").write(s)
PY
dotnet build "$w/oldfix/tests/Messaging/Messaging.IntegrationTests" -o "$w/oldfix/out" > "$w/oldfix.log" 2>&1
(cd "$w/oldfix/out" && timeout 300 ./Tarol.Messaging.IntegrationTests 2>&1) | sed 's/\x1b\[[0-9;]*m//g' | grep -oE 'Total: [0-9]+, Errors: [0-9]+, Failed: [0-9]+' | tail -1
fi
if at 11; then
echo; echo "## 11. UPDATE … RETURNING order: the claim of a batch (Brokers/claim-order.sql), and its plan"
docker cp "$here/Brokers/claim-order.sql" tarol-probe-pg:/claim-order.sql
docker exec tarol-probe-pg psql -U postgres -q -f /claim-order.sql 2>&1 | grep -vE "NOTICE|DETAIL|drop cascades"
fi
docker rm -f tarol-probe-pg tarol-probe-nats tarol-probe-nats-auth tarol-probe-rmq >/dev/null 2>&1
} >> "$out" 2>&1
echo "wrote $out"
