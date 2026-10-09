"""Deterministic staging secret/configuration and actual shell failure regressions.

Only synthetic files and stub commands are used; never contacts Docker or a VPS.
"""
import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import yaml

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("staging_guard", ROOT / "scripts/staging/check_imagesharp_build.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


def script():
    workflow = yaml.safe_load((ROOT / ".github/workflows/deploy-staging-vps.yml").read_text(encoding="utf-8"))
    return workflow["jobs"]["deploy"]["steps"][0]["with"]["script"]


def verify_dockerfile(text):
    assert text.startswith("# syntax=docker/dockerfile:1\n")
    commands = [line for line in text.splitlines() if "dotnet build " in line or "dotnet publish " in line]
    assert len(commands) == 2
    for command in commands:
        assert command.startswith("RUN --mount=type=secret,id=sixlabors_license,required=true dotnet ")
        assert " -c Release " in command
        assert "/p:SixLaborsLicenseFile=/run/secrets/sixlabors_license" in command
    assert "ARG " not in text  # No configuration override can bypass licensed Release.
    assert "SIXLABORS_LICENSE_KEY" not in text and "SixLaborsLicenseKey" not in text
    for line in text.splitlines():
        if "license" in line.lower():
            assert line in commands
    assert "COPY --from=publish /app/publish ." in text
    assert "set -x" not in text and "binlog" not in text.lower()


def verify_script(text):
    assert text.startswith("set -eu\n")
    assert "umask 077" in text
    commands = ['test -f "$LOCK" && test ! -L "$LOCK"', 'exec 9<>"$LOCK"',
                'flock -n 9', 'git merge --ff-only "$EXPECTED_SHA"',
                'test "$(git rev-parse HEAD)" = "$EXPECTED_SHA"',
                'python3 backend-src/scripts/staging/deploy_imagesharp.py --expected-sha "$EXPECTED_SHA" --lock-fd 9']
    assert all(command in text for command in commands)
    positions = [text.index(command) for command in commands]
    assert positions == sorted(positions)
    assert "docker compose" not in text and "docker rm" not in text
    assert "||" not in text and "set -x" not in text
    assert "SIXLABORS_LICENSE_KEY" not in text and "--build-arg" not in text


class StagingImageSharpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "staging"
        (self.root / "backend-src").mkdir(parents=True)
        (self.root / "frontend-src").mkdir()
        (self.root / "frontend-src/VERSION").write_text("0.0.0")
        self.override = self.root / "compose.imagesharp-license.yml"
        self.override.write_text("synthetic override")
        self.license = Path(self.temp.name) / "private/sixlabors.lic"
        self.license.parent.mkdir()
        self.license.write_text("synthetic-private-marker")
        self.config = {"name": "hockeyplanner-staging", "services": {"backend": {"build": {
            "context": str((self.root / "backend-src").resolve()), "dockerfile": guard.DOCKERFILE,
            "secrets": [{"source": "sixlabors_license"}]}}},
            "secrets": {"sixlabors_license": {"file": str(self.license)}}}

    def validate(self, config):
        # Model POSIX metadata on Windows as well; contents are never read by guard.
        original_stat = Path.stat
        def metadata(path, *args, **kwargs):
            result = original_stat(path, *args, **kwargs)
            if path in (self.license, self.license.parent):
                values = {name: getattr(result, name) for name in ("st_mode", "st_uid", "st_size")}
                values.update(st_mode=(stat.S_IFREG | 0o600) if path == self.license else (stat.S_IFDIR | 0o700), st_uid=1234)
                return SimpleNamespace(**values)
            return result
        with patch.object(Path, "stat", metadata), patch.object(guard.os, "geteuid", return_value=1234, create=True):
            guard.validate(config, self.root, self.license)

    def test_dockerfile_and_workflow_require_secret_before_replacement(self):
        verify_dockerfile((ROOT / guard.DOCKERFILE).read_text(encoding="utf-8"))
        verify_script(script())

    def test_dockerfile_and_workflow_bypass_mutations_are_rejected(self):
        dockerfile = (ROOT / guard.DOCKERFILE).read_text(encoding="utf-8")
        for changed in (dockerfile.replace(",required=true", ""), dockerfile.replace("-c Release", "-c Debug"),
                        dockerfile.replace("SixLaborsLicenseFile", "OtherProperty"), dockerfile + "ENV LICENSE=bad\n"):
            with self.assertRaises(AssertionError):
                verify_dockerfile(changed)
        for changed in (script().replace('flock -n 9', ''), script().replace('--lock-fd 9', ''),
                        script().replace('set -eu', 'set -u'), script().replace('umask 077', ''),
                        script() + "\ntrue || true"):
            with self.assertRaises(AssertionError):
                verify_script(changed)

    def test_resolved_compose_accepts_only_staging_build_secret(self):
        self.validate(self.config)
        mutations = [lambda c: c.update(name="production"),
            lambda c: c["services"]["backend"]["build"].update(dockerfile="HockeyPlanner.Backend.WebAPI/Dockerfile"),
            lambda c: c["services"]["backend"]["build"].update(dockerfile_inline="FROM scratch"),
            lambda c: c["services"]["backend"]["build"].update(context=str(self.root)),
            lambda c: c["services"]["backend"]["build"].update(target="base"),
            lambda c: c["services"]["backend"]["build"].update(args={"BUILD_CONFIGURATION": "Debug"}),
            lambda c: c["services"]["backend"]["build"].update(additional_contexts={"private": str(self.license.parent)}),
            lambda c: c["services"]["backend"]["build"].update(secrets=[]),
            lambda c: c["services"]["backend"]["build"]["secrets"][0].update(target="wrong"),
            lambda c: c["secrets"]["sixlabors_license"].update(file="/tmp/wrong"),
            lambda c: c["secrets"]["sixlabors_license"].update(environment="LICENSE"),
            lambda c: c["secrets"]["sixlabors_license"].update(external=True),
            lambda c: c["services"]["backend"].update(secrets=[{"source": "sixlabors_license"}]),
            lambda c: c["services"]["backend"].update(environment={"SixLaborsLicenseKey": "synthetic"}),
            lambda c: c["services"]["backend"].update(volumes=[{"source": str(self.license.parent), "target": "/private"}])]
        for mutate in mutations:
            changed = copy.deepcopy(self.config)
            mutate(changed)
            with self.assertRaises(ValueError):
                self.validate(changed)

    def test_missing_empty_or_insecure_private_file_fails(self):
        with patch.object(guard.stat, "S_IMODE", return_value=0o644):
            with self.assertRaises(ValueError):
                self.validate(self.config)
        original_symlink = Path.is_symlink
        with patch.object(Path, "is_symlink", lambda p: p == self.license or original_symlink(p)):
            with self.assertRaises(ValueError):
                self.validate(self.config)
        self.license.write_text("")
        with self.assertRaises(ValueError):
            self.validate(self.config)
        self.license.unlink()
        with self.assertRaises(ValueError):
            self.validate(self.config)
        self.license.write_text("synthetic")
        with patch.object(guard.os, "geteuid", return_value=-1, create=True):
            with self.assertRaises(ValueError):
                guard.validate(self.config, self.root, self.license)
        self.override.unlink()
        with self.assertRaises(ValueError):
            self.validate(self.config)

    def test_shell_lock_failure_precedes_checkout_update(self):
        shell = shutil.which("bash")
        if os.name == "nt":
            shell = str(Path(shutil.which("git")).parent.parent / "bin/bash.exe")
        lock = self.root / "deploy.lock"
        lock.write_text("synthetic")
        body = script().replace("/opt/hockeyplanner-staging", self.root.as_posix()).replace(
            "/var/lib/hockeyplanner-staging/deploy/deploy.lock", lock.as_posix()).replace("${{ github.sha }}", "synthetic-sha")
        shim = """
stat() { case "$*" in *'%u'*) printf 1234;; *'deploy.lock'*) printf 600;; *) printf 700;; esac; }
id() { printf 1234; }
flock() { return 1; }
git() { printf 'UNSAFE GIT'; }
python3() { printf 'UNSAFE DEPLOY'; }
"""
        result = subprocess.run([shell, "-c", shim + body], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("UNSAFE", result.stdout + result.stderr)

    def test_runtime_aliases_and_dependencies_resolve_canonical_files(self):
        for alias in ("private_config", "other_name", "sixlabors_license"):
            config = copy.deepcopy(self.config)
            config["secrets"][alias] = {"file": str(self.license.parent / ".." / "private" / "sixlabors.lic")}
            config["services"]["backend"]["secrets"] = [{"source": alias}]
            with self.assertRaises(ValueError):
                self.validate(config)
        config["services"]["db"] = {"secrets": [{"source": alias}]}
        config["services"]["backend"].pop("secrets")
        config["services"]["backend"]["depends_on"] = {"db": {"condition": "service_started"}}
        with self.assertRaises(ValueError):
            self.validate(config)

    def test_unrelated_file_secret_passes_and_ambiguous_definition_fails(self):
        unrelated = self.root / "unrelated.secret"
        unrelated.write_text("synthetic unrelated")
        config = copy.deepcopy(self.config)
        config["services"]["backend"]["secrets"] = [{"source": "private_config"}]
        config["secrets"]["private_config"] = {"file": str(unrelated)}
        self.validate(config)
        for definition in ({}, {"environment": "OTHER"}, {"file": str(unrelated), "external": True}, None):
            config["secrets"]["private_config"] = definition
            with self.assertRaises((ValueError, TypeError)):
                self.validate(config)
        del config["secrets"]["private_config"]
        with self.assertRaises(KeyError):
            self.validate(config)

    def test_runtime_symlink_and_hard_link_aliases_fail(self):
        alias = self.root / "alias.secret"
        os.link(self.license, alias)
        config = copy.deepcopy(self.config)
        config["secrets"]["private_config"] = {"file": str(alias)}
        config["services"]["backend"]["secrets"] = ["private_config"]
        with self.assertRaises(ValueError):
            self.validate(config)
        alias.unlink()
        if os.name != "nt":
            alias.symlink_to(self.license)
            with self.assertRaises(ValueError):
                self.validate(config)
            alias.unlink()
        alias.write_text("synthetic alias")
        resolve = Path.resolve
        with patch.object(Path, "resolve", lambda p, *a, **kw: resolve(self.license, *a, **kw) if p == alias else resolve(p, *a, **kw)):
            with self.assertRaises(ValueError):
                self.validate(config)

    def test_runtime_config_resource_cannot_alias_license(self):
        config = copy.deepcopy(self.config)
        config["configs"] = {"private_config": {"file": str(self.license)}}
        config["services"]["backend"]["configs"] = [{"source": "private_config"}]
        with self.assertRaises(ValueError):
            self.validate(config)

    def test_guard_diagnostics_never_echo_untrusted_configuration(self):
        result = subprocess.run([sys.executable, str(ROOT / "scripts/staging/check_imagesharp_build.py")],
            input='{"synthetic-private-marker": "secret"}', capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, "")
        self.assertEqual(result.stderr, "BLOCKED: staging ImageSharp build prerequisites are invalid.\n")


if __name__ == "__main__":
    unittest.main()
