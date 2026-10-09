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
    assert "export DOCKER_BUILDKIT=1" in text
    assert text.count("docker compose ") == 1
    assert "--project-name hockeyplanner-staging --project-directory /opt/hockeyplanner-staging" in text
    assert '-f /opt/hockeyplanner-staging/docker-compose.yml \\\n' in text
    assert '-f /opt/hockeyplanner-staging/compose.imagesharp-license.yml "$@"' in text
    commands = ['test -f /opt/hockeyplanner-staging/compose.imagesharp-license.yml',
                'test -r /etc/hockeyplanner-staging/licenses/sixlabors.lic',
                'test -s /etc/hockeyplanner-staging/licenses/sixlabors.lic',
                'COMPOSE_CONFIG="$(compose config --format json)"',
                'printf \'%s\' "$COMPOSE_CONFIG" | python3 backend-src/scripts/staging/check_imagesharp_build.py',
                'unset COMPOSE_CONFIG', 'compose build --no-cache backend',
                'compose up -d --no-build --pull never backend', 'compose ps']
    assert all(command in text for command in commands)
    positions = [text.index(command) for command in commands]
    assert positions == sorted(positions)
    assert "docker rm" not in text and "docker ps" not in text
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
        for changed in (script().replace("--no-cache", ""), script().replace("--no-build", "--build"),
                        script().replace("set -eu", "set -u"), script().replace("compose ps", "docker compose ps"),
                        script().replace("compose build --no-cache backend", "compose build --no-cache backend || true")):
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

    def execute(self, scenario):
        shell = shutil.which("bash")
        if os.name == "nt":
            shell = str(Path(shutil.which("git")).parent.parent / "bin/bash.exe")
        self.assertIsNotNone(shell)
        body = script().replace("/opt/hockeyplanner-staging", self.root.as_posix()).replace(
            "/etc/hockeyplanner-staging/licenses/sixlabors.lic", self.license.as_posix()).replace("${{ github.sha }}", "synthetic-sha")
        # Git/Docker/preflight stubs provide controlled failures and log only public arguments.
        shim = '''
git() {
  case "$1" in status) ;; rev-parse) printf synthetic-sha;; *) return 0;; esac
}
python3() { [ "$SCENARIO" != guard-fails ]; }
docker() {
  printf '%s\\n' "$*" >> "$COMMANDS"
  case "$*" in
    *'config --format json'*) [ "$SCENARIO" != config-fails ] || return 1; printf '{"synthetic":true}';;
    *'build --no-cache backend'*) [ "$SCENARIO" != build-fails ] || return 1;;
  esac
}
'''
        if scenario == "missing-override":
            self.override.unlink()
        elif scenario == "missing-license":
            self.license.unlink()
        elif scenario == "empty-license":
            self.license.write_text("")
        commands = Path(self.temp.name) / "commands.txt"
        env = dict(os.environ, SCENARIO=scenario, COMMANDS=commands.as_posix())
        env.pop("SIXLABORS_LICENSE_KEY", None)
        result = subprocess.run([shell, "-c", shim + body], env=env, capture_output=True, text=True)
        log = commands.read_text() if commands.exists() else ""
        self.assertNotIn("synthetic-private-marker", result.stdout + result.stderr + log)
        self.assertNotIn("{\"synthetic\"", result.stdout + result.stderr)
        return result, log

    def test_actual_shell_failures_never_replace_container(self):
        for scenario in ("missing-override", "missing-license", "empty-license", "config-fails", "guard-fails", "build-fails"):
            # Each scenario gets new synthetic filesystem state.
            self.override.write_text("synthetic override")
            self.license.write_text("synthetic-private-marker")
            result, log = self.execute(scenario)
            self.assertNotEqual(result.returncode, 0, scenario)
            self.assertNotIn("up -d", log, scenario)
            self.assertNotIn(" rm ", log, scenario)
            self.assertNotIn(" stop ", log, scenario)

    def test_actual_shell_success_uses_one_project_and_file_set(self):
        result, log = self.execute("success")
        self.assertEqual(result.returncode, 0, result.stderr)
        prefix = (f"compose --project-name hockeyplanner-staging --project-directory {self.root.as_posix()} "
                  f"-f {self.root.as_posix()}/docker-compose.yml -f {self.override.as_posix()} ")
        self.assertEqual(log.splitlines(), [prefix + suffix for suffix in
            ("config --format json", "build --no-cache backend", "up -d --no-build --pull never backend", "ps")])

    def test_guard_diagnostics_never_echo_untrusted_configuration(self):
        result = subprocess.run([sys.executable, str(ROOT / "scripts/staging/check_imagesharp_build.py")],
            input='{"synthetic-private-marker": "secret"}', capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, "")
        self.assertEqual(result.stderr, "BLOCKED: staging ImageSharp build prerequisites are invalid.\n")


if __name__ == "__main__":
    unittest.main()
