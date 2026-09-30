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
    require(type(data) is dict and type(data.get("version")) is int and data["version"] == 1)
    require(type(data.get("parameters")) is str)
    flags = data["parameters"].split()
    require("--vulnerable" in flags and "--include-transitive" in flags)
    require(not any(x in flags for x in ("--outdated", "--deprecated")))
    require(type(data.get("sources")) is list and data["sources"] and
            all(type(x) is str and x.strip() for x in data["sources"]))

    def complete(obj):
        require(type(obj) is dict)
        # NuGet JSON v1 reports failed/incomplete audits in `problems`, even
        # with exit 0. Never interpret their omitted findings as a clean scan.
        problems = obj.get("problems", [])
        require(type(problems) is list)
        for problem in problems:
            require(type(problem) is dict and type(problem.get("level")) is str and
                    type(problem.get("text")) is str)
        require(not problems)  # Unknown diagnostic levels also fail closed.
        # Unexpected old/foreign diagnostic formats must not look like success.
        require("errors" not in obj and "logs" not in obj)

    complete(data)
    require(type(data.get("projects")) is list and data["projects"])
    result = []
    seen = set()
    for project in data["projects"]:
        complete(project)
        require(type(project.get("path")) is str and project["path"] not in seen)
        seen.add(project["path"])
        scope = identifier(project["path"].replace("\\", "/").rsplit("/", 1)[-1])
        # The real CLI omits frameworks for projects with no vulnerable packages.
        require(type(project.get("frameworks", [])) is list)
        for framework in project.get("frameworks", []):
            complete(framework)
            tfm = identifier(framework["framework"])
            for group in ("topLevelPackages", "transitivePackages"):
                require(type(framework.get(group, [])) is list)
                for package in framework.get(group, []):
                    complete(package)
                    require(type(package.get("vulnerabilities")) is list and package["vulnerabilities"])
                    for item in package["vulnerabilities"]:
                        require(type(item) is dict)
                        result.append(finding(repo, "nuget", "dependency", item["severity"],
                            identifier(package["id"]), advisory(item["advisoryurl"]),
                            identifier(package["resolvedVersion"]), scope + ":" + tfm))
    return dedup(result)


def npm_findings(data, lock, repo):
    require(type(data) is dict and type(data.get("auditReportVersion")) is int and
            data["auditReportVersion"] == 2 and "error" not in data)
    vulns = data.get("vulnerabilities")
    require(type(vulns) is dict and type(lock) is dict and type(lock.get("packages")) is dict)
    require(type(data.get("metadata")) is dict)
    counts = data["metadata"].get("vulnerabilities")
    levels = ("info", "low", "moderate", "high", "critical")
    require(type(counts) is dict and set(counts) == {*levels, "total"})
    require(all(type(x) is int and x >= 0 for x in counts.values()))
    require(counts["total"] == sum(counts[x] for x in levels) == len(vulns))
    observed = dict.fromkeys(levels, 0)
    candidates, pairs = [], []
    for name, entry in vulns.items():
        identifier(name)
        require(type(entry) is dict and entry.get("name") == name and entry.get("severity") in levels)
        observed[entry["severity"]] += 1
        require(type(entry.get("nodes")) is list and entry["nodes"])
        require(type(entry.get("via")) is list and entry["via"])
        versions = set()
        for node in entry["nodes"]:
            require(type(node) is str and node in lock["packages"])
            installed = lock["packages"][node]
            require(type(installed) is dict)
            # Reject a node accidentally pointing to a different component.
            require(node.endswith("node_modules/" + name))
            versions.add(identifier(installed["version"]))
        for item in entry["via"]:
            if type(item) is str:
                require(item in vulns)
                continue
            require(type(item) is dict and item.get("name") == name and item.get("dependency") == name)
            ref, level = advisory(item["url"]), severity(item["severity"])
            require(type(item.get("range")) is str and item["range"].strip())
            for version in sorted(versions):
                pairs.append([version, item["range"]])
                candidates.append((name, ref, level, version, item["range"]))
    require(observed == {x: counts[x] for x in levels})

    # Resolve concrete component/version identities BEFORE fingerprinting.
    matches = semver_matches(pairs) if pairs else []
    result, affected = [], set()
    for (name, ref, level, version, affected_range), matches_range in zip(candidates, matches):
        if matches_range:
            detail = json.dumps([affected_range, level], separators=(",", ":"))
            result.append(finding(repo, "npm", "dependency", level, name, ref, version, details=detail))
            affected.add(name)

    def causes(name, visited):
        if name in visited:
            return False
        return name in affected or any(causes(item, visited | {name})
            for item in vulns[name]["via"] if type(item) is str)

    # Meta-vulnerable wrappers point at the concrete findings. They do not
    # inherit another package's advisory range, severity or version identity.
    require(all(causes(name, set()) for name in vulns))
    return dedup(result)


def semver_matches(pairs):
    raw = command(["node", str(Path(__file__).with_name("npm_semver.cjs"))],
                  input_data=json.dumps(pairs))
    result = json.loads(raw.stdout)
    require(type(result) is list and len(result) == len(pairs) and all(type(x) is bool for x in result))
    return result


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


def command(args, allowed=(0,), input_data=None):
    executable = shutil.which(args[0])
    require(executable is not None)
    result = subprocess.run([executable, *args[1:]], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=900, input=input_data)
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
