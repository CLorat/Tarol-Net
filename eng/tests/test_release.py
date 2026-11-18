"""Tests of eng/release.py (run: python3 -m unittest discover -s eng/tests)."""
import importlib.util, os, subprocess, sys, tempfile, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(os.path.dirname(HERE), "release.py")
spec = importlib.util.spec_from_file_location("release", SCRIPT)
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

CHANGELOG = """# Changelog

## [Unreleased]

### Added

* Something new.

## [0.1.0] - 2026-11-20

### Added

* The first release.

### Upgrade notes

* Schema version 3.
"""

SHIPPED = """; Shipped analyzer releases

## Release 0.1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TOPT001 | Tarol.Options | Warning | Diagnostics
"""

UNSHIPPED_EMPTY = "; Unshipped analyzer release\n"
UNSHIPPED_RULE = UNSHIPPED_EMPTY + "\n### New Rules\n\nRule ID | Category | Severity | Notes\n--------|----------|----------|-------\nTOPT002 | Tarol.Options | Warning | Diagnostics\n"


def _write(path, text):
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


class Tree:
    def __init__(self, changelog=CHANGELOG, unshipped=UNSHIPPED_EMPTY, api="#nullable enable\n"):
        self.dir = tempfile.TemporaryDirectory()
        root = self.root = self.dir.name
        gen = os.path.join(root, "src", "Options", "Tarol.Options.Generators")
        pkg = os.path.join(root, "src", "Options", "Tarol.Options")
        os.makedirs(gen)
        os.makedirs(pkg)
        _write(os.path.join(root, "CHANGELOG.md"), changelog)
        _write(os.path.join(gen, "AnalyzerReleases.Shipped.md"), SHIPPED)
        _write(os.path.join(gen, "AnalyzerReleases.Unshipped.md"), unshipped)
        _write(os.path.join(pkg, "PublicAPI.Shipped.txt"), "#nullable enable\nTarol.Options.Option\n")
        _write(os.path.join(pkg, "PublicAPI.Unshipped.txt"), api)

    def __enter__(self):
        return self.root

    def __exit__(self, *exc):
        self.dir.cleanup()


class ReleaseTests(unittest.TestCase):
    def test_a_shipped_release_has_no_problems(self):
        with Tree() as root:
            self.assertEqual(release.problems(root, "0.1.0"), [])

    def test_a_pre_release_uses_its_base_version(self):
        with Tree() as root:
            self.assertEqual(release.problems(root, "0.1.0-preview.1"), [])
            self.assertIn("Pre-release of 0.1.0.", release.notes(root, "0.1.0-preview.1"))

    def test_a_missing_changelog_section_is_a_problem(self):
        with Tree() as root:
            self.assertEqual(release.problems(root, "0.2.0"), ["CHANGELOG.md has no '## [0.2.0]' section"])

    def test_an_empty_changelog_section_is_a_problem(self):
        with Tree(changelog="## [0.1.0]\n\n## [0.0.1]\n\n* old\n") as root:
            self.assertEqual(release.problems(root, "0.1.0"), ["CHANGELOG.md's '## [0.1.0]' section is empty"])

    def test_an_unshipped_rule_is_a_problem(self):
        with Tree(unshipped=UNSHIPPED_RULE) as root:
            [problem] = release.problems(root, "0.1.0")
            self.assertIn("rule TOPT002 is not shipped", problem)

    def test_unshipped_public_api_is_a_problem(self):
        with Tree(api="#nullable enable\nTarol.Options.Option.New() -> void\n") as root:
            [problem] = release.problems(root, "0.1.0")
            self.assertIn("1 public API line(s) not shipped", problem)

    def test_the_notes_hold_the_section_the_diagnostics_and_the_install_line(self):
        with Tree() as root:
            text = release.notes(root, "0.1.0")
        self.assertTrue(text.startswith("# Tarol 0.1.0\n"))
        self.assertIn("* The first release.", text)
        self.assertIn("* Schema version 3.", text)
        self.assertNotIn("Something new", text)  # the Unreleased section is not this release
        self.assertIn("| TOPT001 | Tarol.Options | Warning | Tarol.Options.Generators |", text)
        self.assertIn('<PackageReference Include="Tarol" Version="0.1.0" />', text)

    def test_a_dry_run_falls_back_to_unreleased_and_does_not_fail(self):
        with Tree(unshipped=UNSHIPPED_RULE) as root:
            self.assertIn("Something new", release.notes(root, "0.0.0-alpha.0.62", dry_run=True))
            env = dict(os.environ, TAROL_ROOT=root)
            dry = subprocess.run([sys.executable, SCRIPT, "check", "0.0.0-alpha.0.62", "--dry-run"], env=env, capture_output=True, text=True)
            real = subprocess.run([sys.executable, SCRIPT, "check", "0.1.0"], env=env, capture_output=True, text=True)
        self.assertEqual(dry.returncode, 0)
        self.assertIn("WARNING", dry.stderr)
        self.assertEqual(real.returncode, 1)
        self.assertIn("ERROR", real.stderr)

    def test_not_a_version_is_refused(self):
        with self.assertRaises(ValueError):
            release.base_of("v0.1.0")


if __name__ == "__main__":
    unittest.main()
