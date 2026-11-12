#!/usr/bin/env bash
# Verifies the packed Tarol packages (conventions §5, §6, §8.8):
#   1. every src/<Family>/<Package> that is packable produced <Package>.<version>.nupkg (and nothing else did,
#      in particular no *.Generators package);
#   2. each .nupkg contains lib/net11.0/<Package>.dll, README.md, LICENSE, every skills/<name>/SKILL.md of the project,
#      the bundled generator under analyzers/dotnet/cs for each TarolGenerator="true" reference, and no build/ targets;
#   3. every tests/*/*.PackageTests consumer restores the packages from the local feed (isolated packages
#      folder, so no stale cache), builds, runs, and its project.assets.json lists the skill files.
# Usage: eng/verify-packages.sh [--no-pack]
set -euo pipefail
cd "$(dirname "$0")/.."

feed=artifacts/package/release
if [[ "${1:-}" != "--no-pack" ]]; then
  rm -rf "$feed"
  dotnet pack Tarol.slnx -c Release --nologo -v quiet
fi

python3 -I - "$feed" <<'PY'
import glob, os, re, sys, zipfile
feed = sys.argv[1]
errors = []
packages = {}
for nupkg in glob.glob(os.path.join(feed, "*.nupkg")):
    name = os.path.basename(nupkg)
    m = re.match(r"^(.*?)\.(\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?)\.nupkg$", name)
    if not m:
        errors.append(f"unexpected file name {name}")
        continue
    packages[m.group(1)] = (m.group(2), nupkg)

expected = []
for csproj in sorted(glob.glob("src/*/*/*.csproj")):
    pid = os.path.splitext(os.path.basename(csproj))[0]
    if pid.endswith(".Generators"):
        continue
    expected.append((pid, csproj))

for pid in packages:
    if pid not in dict(expected):
        errors.append(f"unexpected package {pid} (generators must not be packed on their own)")

for pid, csproj in expected:
    if pid not in packages:
        errors.append(f"missing package {pid}")
        continue
    version, nupkg = packages[pid]
    files = set(zipfile.ZipFile(nupkg).namelist())
    project_dir = os.path.dirname(csproj)
    need = {f"lib/net11.0/{pid}.dll", "README.md", "LICENSE"}
    for skill in glob.glob(os.path.join(project_dir, "skills", "*", "SKILL.md")):
        need.add("skills/" + os.path.relpath(skill, os.path.join(project_dir, "skills")).replace(os.sep, "/"))
    text = open(csproj, encoding="utf-8-sig").read()
    for ref in re.findall(r'<ProjectReference\s+Include="([^"]+)"[^>]*TarolGenerator="true"', text, re.S):
        gen = os.path.splitext(os.path.basename(ref.replace("\\", "/")))[0]
        need.add(f"analyzers/dotnet/cs/{gen}.dll")
    for missing in sorted(need - files):
        errors.append(f"{pid} {version}: missing {missing}")
    for bad in sorted(f for f in files if f.startswith("build/") or f.startswith("buildTransitive/")):
        errors.append(f"{pid} {version}: unexpected {bad} (analyzers must not be loaded twice)")
    print(f"{pid} {version}")
    for f in sorted(files):
        if not f.startswith(("_rels/", "package/", "[Content_Types]")) and not f.endswith(".psmdcp"):
            print(f"    {f}")

if errors:
    print("\n".join("ERROR " + e for e in errors), file=sys.stderr)
    sys.exit(1)
PY

# All packages of one build share the MinVer version; read it from any package (none yet: nothing to consume).
first=$(ls "$feed"/*.nupkg 2>/dev/null | head -1 || true)
if [[ -z "$first" ]]; then
  echo "verify-packages: no packages yet, nothing to consume"; echo "verify-packages: OK"; exit 0
fi
version=$(basename "$first" .nupkg | sed -E 's/^[A-Za-z.]+\.([0-9]+\.[0-9]+\.[0-9]+.*)$/\1/')
packages_dir=artifacts/package-tests/packages
rm -rf "$packages_dir"

for consumer in tests/*/*.PackageTests/*.csproj; do
  [[ -e "$consumer" ]] || continue
  echo "== $consumer (Tarol packages $version)"
  dotnet run --project "$consumer" -c Release -p:TarolPackageVersion="$version" -p:RestorePackagesPath="$PWD/$packages_dir"
  assets="artifacts/obj/$(basename "$consumer" .csproj)/project.assets.json"
  echo "   skill files listed in $assets:"
  grep -o '"skills/[^"]*SKILL.md"' "$assets" | sort -u | sed 's/^/     /'
  grep -q '"skills/[^"]*SKILL.md"' "$assets" || { echo "ERROR no skills listed in $assets" >&2; exit 1; }
done
echo "verify-packages: OK"
