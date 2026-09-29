"""Read-only staging gate. Stdlib only; raw HTTP/SSH output never becomes an artifact."""
import argparse
import hashlib
from html.parser import HTMLParser
import json
import os
import pathlib
import re
import subprocess
import tempfile
import time
import urllib.request

BASE = 'https://staging.hockeyplanner.ru'
SHA = re.compile(r'^[0-9a-f]{7,40}$')
MIGRATION = re.compile(r'^\d{14}_[A-Za-z0-9_]+$')
REQUIRED = {
    '20260125121252_InitialCreate', '20260125125623_Attendance_RenameFieldToUser',
    '20260125133940_Line_RenameFieldToPlayers', '20260928151908_AddNotificationJobs',
    '20260928152829_AddNotificationLogicalIdentity', '20260928154002_AddDurableEmailAndLeagueNotificationWork',
}
WRAPPERS = {name: f'sudo /usr/local/bin/hp-staging-{name}' for name in ('status', 'migrations', 'queue', 'logs')}


class Failure(Exception):
    def __init__(self, category, code):
        super().__init__(code)
        self.category, self.code = category, code


def require(ok, code, category='ENVIRONMENT STATE FAILURE'):
    if not ok:
        raise Failure(category, code)


def document(value):
    require(isinstance(value, dict) and value.get('schemaVersion') == 1, 'wrapper_contract', 'TEST INFRA FAILURE')
    return value


def check_health(status, body):
    require(status == 200 and body.strip() == b'Healthy', 'health', 'POST-DEPLOY HEALTH FAILURE')


def check_version(status, value, expected=None):
    require(status == 200 and isinstance(value, dict), 'version_json', 'POST-DEPLOY HEALTH FAILURE')
    commit = value.get('commit')
    require(value.get('environment') == 'Staging' and isinstance(commit, str) and bool(SHA.fullmatch(commit)),
            'version_environment_or_commit', 'POST-DEPLOY HEALTH FAILURE')
    require(expected is None or expected.startswith(commit), 'backend_sha', 'POST-DEPLOY HEALTH FAILURE')
    return commit


def check_frontend(value, expected=None):
    require(isinstance(value, dict), 'frontend_metadata', 'POST-DEPLOY HEALTH FAILURE')
    commit, asset = value.get('commit'), value.get('mainJs')
    require(isinstance(commit, str) and bool(re.fullmatch('[0-9a-f]{40}', commit))
            and value.get('environment') == 'Staging', 'frontend_identity', 'POST-DEPLOY HEALTH FAILURE')
    require(expected is None or commit == expected, 'frontend_sha', 'POST-DEPLOY HEALTH FAILURE')
    require(isinstance(asset, dict) and isinstance(asset.get('path'), str)
            and bool(re.fullmatch(r'/static/js/main\.[a-zA-Z0-9_-]+\.js', asset['path']))
            and isinstance(asset.get('sha256'), str) and bool(re.fullmatch('[0-9a-f]{64}', asset['sha256'])),
            'frontend_asset_metadata', 'POST-DEPLOY HEALTH FAILURE')
    return {'commit': commit, 'mainJs': {'path': asset['path'], 'sha256': asset['sha256']}}


def check_index(body, asset):
    class Scripts(HTMLParser):
        def __init__(self):
            super().__init__()
            self.sources = []
        def handle_starttag(self, tag, attrs):
            if tag == 'script':
                self.sources.append(dict(attrs).get('src'))
    parser = Scripts()
    try:
        parser.feed(body.decode('utf-8'))
    except (ValueError, UnicodeError):
        raise Failure('POST-DEPLOY HEALTH FAILURE', 'frontend_html') from None
    require(asset in parser.sources, 'frontend_index_bundle', 'POST-DEPLOY HEALTH FAILURE')


def check_status(value):
    value = document(value)
    result = {}
    for name in ('backend', 'postgres'):
        item = value.get(name, {})
        require(isinstance(item, dict) and item.get('state') == 'running' and item.get('health') == 'healthy'
                and item.get('restarting') is False and type(item.get('restartCount')) is int
                and item['restartCount'] == 0, 'container_' + name)
        result[name] = {'state': 'running', 'health': 'healthy', 'restarting': False, 'restartCount': 0}
    return result


def check_migrations(value, expected):
    values = document(value).get('migrations')
    require(isinstance(values, list) and all(isinstance(x, str) and MIGRATION.fullmatch(x) for x in values),
            'migration_contract', 'TEST INFRA FAILURE')
    require(len(values) == len(set(values)) and set(values) == set(expected) | REQUIRED, 'migration_mismatch')
    return {'count': len(values), 'latest': max(values), 'matches': True}


def check_queue(value):
    value = document(value)
    counts = ('total_jobs', 'pending', 'processing_claimed', 'completed', 'retrying', 'terminal_failed', 'max_attempt_count')
    result = {}
    for key in counts:
        require(type(value.get(key)) is int and value[key] >= 0, 'queue_contract', 'TEST INFRA FAILURE')
        result[key] = value[key]
    require(result['total_jobs'] == sum(result[k] for k in ('pending', 'processing_claimed', 'completed', 'terminal_failed'))
            and result['retrying'] <= result['pending'], 'queue_counts', 'TEST INFRA FAILURE')
    for key, count in (('oldest_pending_age_seconds', 'pending'), ('oldest_processing_age_seconds', 'processing_claimed')):
        age = value.get(key)
        require((result[count] == 0 and age is None) or
                (result[count] > 0 and type(age) in (int, float) and 0 <= age < float('inf')),
                'queue_age_contract', 'TEST INFRA FAILURE')
        result[key] = age
    require(result['terminal_failed'] == 0, 'queue_terminal_failed')
    require((result['oldest_processing_age_seconds'] or 0) <= 300, 'queue_stuck_processing')
    require(result['processing_claimed'] == 0, 'queue_processing_busy')
    require((result['oldest_pending_age_seconds'] or 0) <= 900, 'queue_pending_age')
    require(result['retrying'] <= 25, 'queue_excessive_retries')
    result['warning'] = 'transient_retry' if result['retrying'] else None
    return result


def summarize_logs(raw):
    # Count patterns only. Never emit raw log lines, exception messages, URLs or user IDs.
    return {key: sum(bool(re.search(pattern, line, re.I)) for line in raw.splitlines()) for key, pattern in {
        'exception_lines': r'exception|\bfail:', 'migration_error_lines': r'(migration.*(fail|error))|(42P01)',
        'notification_error_lines': r'notification.*(exception|fail|error)',
    }.items()}


def decode(raw):
    try:
        return json.loads(raw)
    except (ValueError, UnicodeError):
        raise Failure('POST-DEPLOY HEALTH FAILURE', 'malformed_json') from None


def expected_migrations(repo, revision):
    require(bool(SHA.fullmatch(revision)), 'unsafe_revision', 'TEST INFRA FAILURE')
    try:
        # The counterpart can deploy a commit pushed after this job's checkout.
        # Fetch only the fixed develop ref; never turn HTTP metadata into a fetch URL/ref.
        resolve = ['git', '-C', str(repo), 'rev-parse', '--verify', '--end-of-options', revision + '^{commit}']
        resolved = subprocess.run(resolve, capture_output=True, text=True, timeout=10)
        if resolved.returncode:
            subprocess.run(['git', '-C', str(repo), 'fetch', '--no-tags', 'origin', 'develop'],
                           capture_output=True, check=True, timeout=20)
            resolved = subprocess.run(resolve, capture_output=True, text=True, check=True, timeout=10)
        names = subprocess.run(['git', '-C', str(repo), 'ls-tree', '-r', '--name-only', resolved.stdout.strip(), '--',
                                'HockeyPlanner.Backend.Infrastructure/Data/Migrations'], capture_output=True,
                               text=True, check=True, timeout=10).stdout.splitlines()
    except (OSError, subprocess.SubprocessError):
        raise Failure('POST-DEPLOY HEALTH FAILURE', 'backend_revision_unavailable') from None
    ids = {pathlib.Path(name).stem for name in names if MIGRATION.fullmatch(pathlib.Path(name).stem)}
    require(bool(ids), 'empty_migration_list', 'TEST INFRA FAILURE')
    return ids | REQUIRED


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def http(path):
    try:
        request = urllib.request.Request(BASE + path, headers={'Cache-Control': 'no-cache'})
        with urllib.request.build_opener(NoRedirect).open(request, timeout=8) as response:
            data = response.read(8 * 1024 * 1024 + 1)
            require(len(data) <= 8 * 1024 * 1024, 'response_size', 'POST-DEPLOY HEALTH FAILURE')
            return response.status, data
    except Failure:
        raise
    except Exception:
        raise Failure('POST-DEPLOY HEALTH FAILURE', 'http_unavailable') from None


class Diagnostics:
    def __enter__(self):
        host = os.environ.get('STAGING_DIAGNOSTIC_HOST', '')
        require(bool(re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}', host)), 'ssh_host', 'TEST INFRA FAILURE')
        key, hosts = os.environ.get('STAGING_DIAGNOSTIC_KEY'), os.environ.get('STAGING_KNOWN_HOSTS')
        require(bool(key and hosts), 'ssh_secrets_missing', 'TEST INFRA FAILURE')
        self.temp = tempfile.TemporaryDirectory(prefix='hp-staging-ssh-')
        root = pathlib.Path(self.temp.name)
        for name, value in (('key', key), ('known_hosts', hosts)):
            file = root / name
            file.write_text(value + '\n', encoding='utf-8')
            file.chmod(0o600)
        self.args = ['ssh', '-F', os.devnull, '-i', str(root / 'key'), '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
                     '-o', 'StrictHostKeyChecking=yes', '-o', 'UserKnownHostsFile=' + str(root / 'known_hosts'),
                     '-o', 'ConnectTimeout=8', '-o', 'ServerAliveInterval=5', '-o', 'ServerAliveCountMax=2', 'codex@' + host]
        return self

    def __exit__(self, *args):
        self.temp.cleanup()

    def read(self, name):
        require(name in WRAPPERS, 'wrapper_not_allowed', 'TEST INFRA FAILURE')
        try:
            result = subprocess.run(self.args + [WRAPPERS[name]], capture_output=True, text=True, timeout=25, check=True)
            require(len(result.stdout) <= 2_000_000, 'wrapper_size', 'TEST INFRA FAILURE')
            return result.stdout
        except (OSError, subprocess.SubprocessError):
            raise Failure('TEST INFRA FAILURE', 'wrapper_unavailable_' + name) from None


def sample(kind, expected, repo, diagnostics, get=http):
    check_health(*get('/api/health'))
    status, body = get('/api/version')
    backend = check_version(status, decode(body), expected if kind == 'backend' else None)
    frontend = None
    if kind == 'frontend':
        status, body = get('/build-meta.json?smoke=' + str(time.time_ns()))
        require(status == 200, 'frontend_metadata_http', 'POST-DEPLOY HEALTH FAILURE')
        frontend = check_frontend(decode(body), expected)
        status, index = get('/login?smoke=' + str(time.time_ns()))
        require(status == 200, 'frontend_index_http', 'POST-DEPLOY HEALTH FAILURE')
        check_index(index, frontend['mainJs']['path'])
        status, asset = get(frontend['mainJs']['path'])
        require(status == 200 and hashlib.sha256(asset).hexdigest() == frontend['mainJs']['sha256'],
                'frontend_bundle_hash', 'POST-DEPLOY HEALTH FAILURE')
    return {'backendCommit': backend, 'frontend': frontend,
            'containers': check_status(decode(diagnostics.read('status'))),
            'migrations': check_migrations(decode(diagnostics.read('migrations')), expected_migrations(repo, backend)),
            'queue': check_queue(decode(diagnostics.read('queue')))}


def verify_until_stable(probe, browser, attempts=30, pause=time.sleep, clock=time.monotonic, budget=600):
    last = Failure('POST-DEPLOY HEALTH FAILURE', 'not_verified')
    deadline = clock() + budget
    for attempt in range(1, attempts + 1):
        if clock() >= deadline:
            break
        try:
            before = probe()
            browser()
            pause(3)
            after = probe()
            require(clock() < deadline, 'verification_timeout', 'POST-DEPLOY HEALTH FAILURE')
            require(before['backendCommit'] == after['backendCommit'] and before['frontend'] == after['frontend'],
                    'deployment_changed_during_smoke', 'POST-DEPLOY HEALTH FAILURE')
            return {'result': 'STAGING VALIDATION PASS', 'attempts': attempt, **after}
        except Failure as error:
            last = error
            print(json.dumps({'attempt': attempt, 'category': error.category, 'code': error.code}), flush=True)
            if error.category == 'TEST INFRA FAILURE':
                raise
            if attempt < attempts and clock() < deadline:
                pause(min(15, attempt * 3, deadline - clock()))
    raise last


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--kind', choices=['backend', 'frontend'], required=True)
    parser.add_argument('--expected', required=True)
    parser.add_argument('--backend-repo', type=pathlib.Path, required=True)
    parser.add_argument('--frontend-repo', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    report = None
    try:
        require(bool(re.fullmatch('[0-9a-f]{40}', args.expected)), 'expected_sha', 'TEST INFRA FAILURE')
        with Diagnostics() as diagnostic:
            def browser():
                # Do not forward SSH secrets to browser or its trace machinery.
                env = {k: v for k, v in os.environ.items() if k in ('PATH', 'HOME', 'USERPROFILE', 'SystemRoot', 'TEMP', 'TMP', 'CI')}
                env.update(HP_STAGING_URL=BASE, HP_SMOKE_OUTPUT=str(args.output.resolve() / 'browser'))
                try:
                    result = subprocess.run(['node', 'scripts/staging/run-browser.cjs'], cwd=args.frontend_repo,
                                            env=env, capture_output=True, timeout=65)
                except (OSError, subprocess.SubprocessError):
                    raise Failure('TEST INFRA FAILURE', 'browser_process') from None
                if result.returncode:
                    marker = args.output / 'browser' / 'result.json'
                    code = 'browser_assertion' if marker.exists() and decode(marker.read_bytes()).get('code') == 'browser_assertion' else 'browser_infra'
                    raise Failure('POST-DEPLOY HEALTH FAILURE' if code == 'browser_assertion' else 'TEST INFRA FAILURE', code)
            try:
                report = verify_until_stable(lambda: sample(args.kind, args.expected, args.backend_repo, diagnostic), browser)
            except Failure as error:
                report = {'result': 'FAIL', 'category': error.category, 'code': error.code}
                try:
                    report['logs'] = summarize_logs(diagnostic.read('logs'))
                except Failure:
                    report['logs'] = {'available': False}
    except Failure as error:
        report = {'result': 'FAIL', 'category': error.category, 'code': error.code}
    except Exception:
        report = {'result': 'FAIL', 'category': 'TEST INFRA FAILURE', 'code': 'unexpected_smoke_failure'}
    (args.output / 'summary.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))
    return 0 if report['result'] == 'STAGING VALIDATION PASS' else 1


if __name__ == '__main__':
    raise SystemExit(main())
