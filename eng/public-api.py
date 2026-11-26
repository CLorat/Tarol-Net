#!/usr/bin/env python3
"""Regenerates the public API files (PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt) from the code.

The PublicApiAnalyzers report every public symbol missing from the files (RS0016) and every line naming a symbol that is gone
(RS0017). Their SARIF log carries the exact line the analyzer expects ("APIName"), so this script builds the runtime packages
once with a SARIF log per project, adds the missing lines to PublicAPI.Unshipped.txt and removes the stale ones, then sorts.
Nothing is written by hand, and generated members (System.Text.Json contexts) are covered like any other symbol.

Usage: eng/public-api.py update          add new API to Unshipped, drop removed API from both files
       eng/public-api.py ship            move every Unshipped line to Shipped (the release step)
       eng/public-api.py check-shipped   fail if any PublicAPI.Unshipped.txt has a line (a release must ship it first)
"""
import glob, json, os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HEADER = "#nullable enable"


def projects():
    for csproj in sorted(glob.glob(os.path.join(ROOT, "src", "*", "*", "*.csproj"))):
        if not csproj.endswith((".Generators.csproj", ".CodeFixes.csproj")):
            yield os.path.splitext(os.path.basename(csproj))[0], os.path.dirname(csproj)


def read(path):
    if not os.path.exists(path):
        return []
    return [l for l in open(path, encoding="utf-8").read().splitlines() if l.strip() and l.strip() != HEADER]


def write(path, lines):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n" + "".join(l + "\n" for l in sorted(set(lines), key=lambda l: (l.lstrip("~"), l))))


def update():
    logs = os.path.join(ROOT, "artifacts", "public-api")
    shutil.rmtree(logs, ignore_errors=True)
    os.makedirs(logs)
    solution = os.path.join(logs, "src.slnx")
    with open(solution, "w", encoding="utf-8") as f:
        f.write("<Solution>\n" + "".join(f'  <Project Path="{os.path.join(d, n + ".csproj")}" />\n' for n, d in projects())
                + "</Solution>\n")
    subprocess.run(["dotnet", "build", solution, "--no-incremental", "-nologo", "-v", "q", "-clp:NoSummary",
                    f"-p:TarolPublicApiLog={logs}", "-p:WarningsNotAsErrors=RS0016%3BRS0017"], check=True, cwd=ROOT)
    changed = 0
    for name, directory in projects():
        sarif = os.path.join(logs, name + ".sarif")
        if not os.path.exists(sarif):
            sys.exit(f"public-api: no SARIF log for {name}")
        add, remove = set(), set()
        for run in json.load(open(sarif, encoding="utf-8-sig"))["runs"]:
            for result in run.get("results", []):
                props = result.get("properties", {}).get("customProperties", {})
                if result["ruleId"] == "RS0016":
                    add.add(props["APIName"])
                    remove.update(s for s in props.get("ApiNamesOfSiblingsToRemove", "").split(";;") if s)
                elif result["ruleId"] == "RS0017":
                    remove.add(props["APIName"])
        shipped_path, unshipped_path = (os.path.join(directory, f) for f in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"))
        shipped, unshipped = read(shipped_path), read(unshipped_path)
        new_shipped = [l for l in shipped if l not in remove]
        new_unshipped = [l for l in unshipped if l not in remove] + sorted(add - set(shipped))
        if new_shipped != shipped or sorted(set(new_unshipped)) != sorted(unshipped) or not os.path.exists(unshipped_path):
            changed += 1
        write(shipped_path, new_shipped)
        write(unshipped_path, new_unshipped)
        print(f"{name}: +{len(add)} -{len(remove)} (shipped {len(new_shipped)}, unshipped {len(set(new_unshipped))})")
    print(f"public-api: {changed} project(s) changed")


def ship():
    for name, directory in projects():
        shipped_path, unshipped_path = (os.path.join(directory, f) for f in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"))
        unshipped = read(unshipped_path)
        write(shipped_path, read(shipped_path) + unshipped)
        write(unshipped_path, [])
        print(f"{name}: shipped {len(unshipped)}")


def check_shipped():
    pending = [(n, len(read(os.path.join(d, "PublicAPI.Unshipped.txt")))) for n, d in projects()]
    pending = [(n, c) for n, c in pending if c]
    for n, c in pending:
        print(f"ERROR {n}: {c} unshipped public API line(s); run eng/public-api.py ship in the release PR", file=sys.stderr)
    sys.exit(1 if pending else 0)


if __name__ == "__main__":
    {"update": update, "ship": ship, "check-shipped": check_shipped}.get(sys.argv[1] if len(sys.argv) > 1 else "", lambda: sys.exit(__doc__))()
