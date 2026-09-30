"""HP-77: deterministic metadata only; never echo subprocess output or exceptions.

Kept identical in both repos. No dependency upgrades and no AI/network API keys.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


class InvalidScan(ValueError):
    pass


def require(condition):
    if not condition:
        raise InvalidScan("Invalid or incomplete scanner data")


def severity(value):
    require(isinstance(value, str))
    value = value.lower()
    value = {"moderate": "medium", "informational": "info"}.get(value, value)
    require(value in ("critical", "high", "medium", "low", "info"))
    return value


def identifier(value):
    require(isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9@/_.+\-]{1,200}", value))
    return value


def advisory(value):
    require(isinstance(value, str) and re.fullmatch(
        r"https://github.com/advisories/GHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}", value))
    return value


def finding(repo, source, category, level, component, rule, version="", scope="", details=""):
    level = severity(level)
    identity = [repo, source, category, component, rule, version, scope, details, level]
    return {"schemaVersion": 1, "repo": repo, "source": source, "category": category,
            "severity": level, "fingerprint": hashlib.sha256(
                json.dumps(identity, separators=(",", ":")).encode()).hexdigest(),
            "title": {"dependency": "Known dependency advisory", "quality": "Stale Browserslist data",
                      "secret": "Potential secret: inspect restricted GitHub alert"}[category],
            "package": component, "rule": rule, "version": version, "scope": scope,
            "reference": rule if rule.startswith("https://github.com/advisories/") else "",
            "status": "NEW", "firstSeen": None}


def nuget_findings(data, repo):
    require(isinstance(data, dict) and data.get("version") == 1 and not data.get("errors"))
    require("--vulnerable" in data.get("parameters", "") and "--include-transitive" in data.get("parameters", ""))
    require(isinstance(data.get("projects"), list) and data["projects"])
    # NuGet can report restore/feed errors in logs while still returning JSON.
    require(not any(str(x.get("level", "")).lower() in ("error", "warning") for x in data.get("logs", [])))
    result = []
    for project in data["projects"]:
        require(not project.get("errors"))
        scope = identifier(project["path"].replace("\\", "/").rsplit("/", 1)[-1])
        for framework in project.get("frameworks", []):
            tfm = identifier(framework["framework"])
            for group in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(group, []):
                    require(isinstance(package.get("vulnerabilities"), list) and package["vulnerabilities"])
                    for item in package["vulnerabilities"]:
                        result.append(finding(repo, "nuget", "dependency", item["severity"],
                            identifier(package["id"]), advisory(item["advisoryurl"]),
                            identifier(package["resolvedVersion"]), scope + ":" + tfm))
    return dedup(result)


def npm_findings(data, lock, repo):
    require(isinstance(data, dict) and data.get("auditReportVersion") == 2 and "error" not in data)
    vulns = data.get("vulnerabilities")
    require(isinstance(vulns, dict) and isinstance(lock.get("packages"), dict))
    require(isinstance(data.get("metadata", {}).get("vulnerabilities"), dict))
    require(data["metadata"]["vulnerabilities"].get("total") == len(vulns))

    def causes(name, visited):
        require(name in vulns)
        if name in visited:
            return []  # npm can include dependency cycles; caller requires a concrete advisory.
        via = vulns[name]["via"]
        require(isinstance(via, list) and via)
        result = []
        for item in via:
            if isinstance(item, str):
                result.extend(causes(item, visited | {name}))
            else:
                require(isinstance(item, dict))
                result.append(item)
        return result

    result = []
    for name, entry in vulns.items():
        identifier(name)
        level = severity(entry["severity"])
        require(isinstance(entry.get("nodes"), list) and entry["nodes"])
        versions = set()
        for node in entry["nodes"]:
            require(node in lock["packages"])
            versions.add(identifier(lock["packages"][node]["version"]))
        items = causes(name, set())
        require(items)
        for item in items:
            ref = advisory(item["url"])
            require(isinstance(item["range"], str))
            # Hash advisory range/own severity too, without copying arbitrary scanner prose.
            detail = json.dumps([item["range"], severity(item["severity"])], separators=(",", ":"))
            for version in sorted(versions):
                result.append(finding(repo, "npm", "dependency", level, name, ref, version, details=detail))
    return dedup(result)


def browserslist_finding(output, lock, repo):
    if re.search(r"Browserslist: (?:browsers data \(caniuse-lite\) is .+ old|caniuse-lite is outdated)", output):
        version = identifier(lock["packages"]["node_modules/caniuse-lite"]["version"])
        return [finding(repo, "browserslist", "quality", "info", "caniuse-lite", "stale-data", version)]
    return []


def secret_metadata(alert, repo):
    """Allowlist for operator-side GitHub alert export. Never copy value/prose/path.

    First-party scanning is enabled; CI deliberately never fetches secret alerts.
    The alert number provides dedup without hashing even a redacted secret value.
    """
    number = alert.get("number")
    require(type(number) is int and number > 0)
    return finding(repo, "github-secret-scanning", "secret", "high", "",
                   "secret-scanning", scope="alert-" + str(number))


def dedup(items):
    return sorted({item["fingerprint"]: item for item in items}.values(), key=lambda x: x["fingerprint"])


def apply_baseline(items, baseline):
    require(baseline.get("schemaVersion") == 1 and isinstance(baseline.get("findings"), list))
    known = {}
    for item in baseline["findings"]:
        require(item["category"] in ("dependency", "quality"))  # Secrets cannot be accepted.
        require(re.fullmatch(r"[0-9a-f]{64}", item["fingerprint"]) is not None)
        require(item["fingerprint"] not in known and item.get("rationale") and item.get("status") == "accepted-existing")
        datetime.date.fromisoformat(item["firstSeen"])
        known[item["fingerprint"]] = item
    for item in items:
        if item["fingerprint"] in known:
            old = known[item["fingerprint"]]
            require(item["category"] == old["category"] and item["category"] != "secret")
            item.update(status="KNOWN/BASELINED", firstSeen=old["firstSeen"])
    return items


def blocking(items):
    return any(x["category"] == "secret" or (x["status"] == "NEW" and x["severity"] in ("critical", "high")) for x in items)


def command(args, allowed=(0,)):
    executable = shutil.which(args[0])
    require(executable is not None)
    result = subprocess.run([executable, *args[1:]], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=900)
    require(result.returncode in allowed)
    return result


def scan(kind, repo):
    if kind == "backend":
        # Restore output is captured; the scan below makes vulnerabilities visible.
        command(["dotnet", "restore", "HockeyPlanner.Backend.sln", "--force-evaluate", "-p:NuGetAudit=true", "-p:NuGetAuditMode=all"])
        raw = command(["dotnet", "package", "list", "--project", "HockeyPlanner.Backend.sln",
                       "--no-restore", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"])
        return nuget_findings(json.loads(raw.stdout), repo)
    lock = json.loads(Path("package-lock.json").read_text(encoding="utf-8"))
    raw = command(["npm", "audit", "--package-lock-only", "--json", "--ignore-scripts"], allowed=(0, 1))
    result = npm_findings(json.loads(raw.stdout), lock, repo)
    # Uses the lock-installed version and real Browserslist warning logic, without a build.
    check = command(["node", "-e", "require('browserslist')()"])
    result.extend(browserslist_finding(check.stderr + check.stdout, lock, repo))
    return dedup(result)


def summary(report):
    rows = ["# HP-77 deterministic dependency / quality audit", "",
            "Scan status: " + report["status"], "", "| State | Severity | Component | Rule/advisory | Fingerprint |",
            "| --- | --- | --- | --- | --- |"]
    for item in report["findings"]:
        rows.append("| " + " | ".join([item["status"], item["severity"],
            item["package"] + " " + item["version"], item["rule"], item["fingerprint"]]) + " |")
    rows.extend(["", "Native CodeQL and GitHub secret alerts remain in the Security tab; AI findings are separate.",
                 "No raw scanner logs or secret values are published.", ""])
    return "\n".join(rows)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("backend", "frontend"), required=True)
    parser.add_argument("--baseline", default=".security/baseline.json")
    parser.add_argument("--output", default="security-audit")
    args = parser.parse_args()
    repo = "tracktorist237/" + ("HockeyPlanner.Backend" if args.kind == "backend" else "hockey-planner")
    report = {"schemaVersion": 1, "repo": repo, "status": "SCAN_ERROR", "findings": []}
    code = 2
    try:
        baseline = json.loads(Path(args.baseline).read_text(encoding="utf-8"))
        report["findings"] = apply_baseline(scan(args.kind, repo), baseline)
        code = 1 if blocking(report["findings"]) else 0
        report["status"] = "NEW_HIGH_OR_CRITICAL" if code else "COMPLETE"
    except (ValueError, KeyError, TypeError, AttributeError, OSError, subprocess.SubprocessError):
        # Malformed data, missing tools and feed/network failures fail closed.
        # Exception messages may contain scanner data; never publish them.
        pass
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    (output / "findings.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    rendered = summary(report)
    (output / "summary.md").write_text(rendered, encoding="utf-8")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write(rendered)
    print("HP-77 audit: " + report["status"] + "; findings=" + str(len(report["findings"])))
    return code


if __name__ == "__main__":
    sys.exit(main())
