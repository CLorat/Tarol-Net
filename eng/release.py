#!/usr/bin/env python3
"""Release checks and notes (docs/architecture/release.md §4, §5).

Usage: eng/release.py check <version> [--dry-run]   fail unless the version ships everything (§4)
       eng/release.py notes <version> [--dry-run]   print the release notes (§5) to stdout

<version> is the package version MinVer took from the tag (1.2.3 or 1.2.3-preview.1). A pre-release uses the sections of its
base version: CHANGELOG "## [1.2.3]", AnalyzerReleases "## Release 1.2.3".
--dry-run (a run without a tag): problems are printed as warnings, and the notes fall back to "## [Unreleased]".
"""
import glob, os, re, sys

VERSION = re.compile(r"^(\d+\.\d+\.\d+)(?:-([0-9A-Za-z.-]+))?$")
FEED = "https://nuget.pkg.github.com/CLorat/index.json"


def _read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def base_of(version):
    m = VERSION.match(version)
    if not m:
        raise ValueError(f"not a version: {version!r}")
    return m.group(1), m.group(2) is not None


def changelog_section(root, heading):
    """The body of '## [heading]' up to the next '## ' heading, stripped; None if there is no such heading."""
    text = _read(os.path.join(root, "CHANGELOG.md"))
    m = re.search(rf"^## \[{re.escape(heading)}\][^\n]*\n(.*?)(?=^## |\Z)", text, re.M | re.S)
    return m.group(1).strip() if m else None


def rule_rows(text):
    return [l for l in text.splitlines() if re.match(r"^[A-Z]+\d+\s*\|", l)]


def unshipped_rules(root):
    found = []
    for path in sorted(glob.glob(os.path.join(root, "src", "*", "*", "AnalyzerReleases.Unshipped.md"))):
        found += [(os.path.relpath(path, root), row.split("|")[0].strip()) for row in rule_rows(_read(path))]
    return found


def unshipped_api(root):
    found = []
    for path in sorted(glob.glob(os.path.join(root, "src", "*", "*", "PublicAPI.Unshipped.txt"))):
        lines = [l for l in _read(path).splitlines() if l.strip() and l.strip() != "#nullable enable"]
        if lines:
            found.append((os.path.relpath(path, root), len(lines)))
    return found


def shipped_rules(root, base):
    """Rows of '## Release <base>' in every AnalyzerReleases.Shipped.md, with the generator's name."""
    rows = []
    for path in sorted(glob.glob(os.path.join(root, "src", "*", "*", "AnalyzerReleases.Shipped.md"))):
        text = _read(path)
        m = re.search(rf"^## Release {re.escape(base)}\s*\n(.*?)(?=^## Release |\Z)", text, re.M | re.S)
        if m:
            generator = os.path.basename(os.path.dirname(path))
            rows += [(generator, [c.strip() for c in row.split("|")]) for row in rule_rows(m.group(1))]
    return rows


def problems(root, version):
    base, _ = base_of(version)
    found = []
    section = changelog_section(root, base)
    if section is None:
        found.append(f"CHANGELOG.md has no '## [{base}]' section")
    elif not section:
        found.append(f"CHANGELOG.md's '## [{base}]' section is empty")
    found += [f"{path}: rule {rule} is not shipped (move it to AnalyzerReleases.Shipped.md under '## Release {base}')"
              for path, rule in unshipped_rules(root)]
    found += [f"{path}: {count} public API line(s) not shipped (eng/public-api.py ship)" for path, count in unshipped_api(root)]
    return found


def notes(root, version, dry_run=False):
    base, prerelease = base_of(version)
    body = changelog_section(root, base)
    if body is None and dry_run:
        body = changelog_section(root, "Unreleased") or ""
    if body is None:
        raise SystemExit(f"release: CHANGELOG.md has no '## [{base}]' section")
    out = [f"# Tarol {version}", ""]
    if prerelease:
        out += [f"Pre-release of {base}.", ""]
    out += [body, ""]
    rows = shipped_rules(root, base)
    if rows:
        out += ["## Diagnostics", "", "| Rule | Category | Severity | Generator |", "|---|---|---|---|"]
        out += [f"| {c[0]} | {c[1]} | {c[2]} | {g} |" for g, c in rows]
        out.append("")
    out += ["## Install", "",
            f"The packages are on GitHub Packages (`{FEED}`); restoring needs a GitHub token with `read:packages`.", "",
            "```xml", f'<PackageReference Include="Tarol" Version="{version}" />', "```", ""]
    return "\n".join(out)


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    dry_run = "--dry-run" in argv
    root = os.environ.get("TAROL_ROOT", os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if len(args) != 2 or args[0] not in ("check", "notes"):
        sys.exit(__doc__)
    command, version = args
    base_of(version)
    if command == "notes":
        sys.stdout.write(notes(root, version, dry_run))
        return 0
    found = problems(root, version)
    for p in found:
        print(("WARNING " if dry_run else "ERROR ") + p, file=sys.stderr)
    print(f"release check {version}: {len(found)} problem(s)" + (" (dry run, not failing)" if dry_run and found else ""))
    return 0 if dry_run or not found else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
