#!/usr/bin/env bash
# Copies the probes to a work directory, renames *.csproj.txt, and builds/runs one of them.
# usage: run.sh <work-dir> <Project> <dotnet verb> [args...]   (TarolRoot = this repository)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; root="$(cd "$here/../../../.." && pwd)/"
work="$1"; shift; project="$1"; shift
mkdir -p "$work/src"; cp -r "$here/." "$work/src/"
find "$work/src" -name '*.csproj.txt' -exec sh -c 'cp "$1" "${1%.txt}"' _ {} \;
cd "$work/src/$project"; verb="$1"; shift; dotnet "$verb" -p:TarolRoot="$root" "$@"
