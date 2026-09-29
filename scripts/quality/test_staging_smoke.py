import importlib.util
import json
import pathlib
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('smoke', ROOT / 'scripts/staging/smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
SHA = 'a' * 40
HEALTH = {'status': 'Healthy', 'timestamp': '2026-09-29T19:54:03.6655337Z', 'environment': 'Staging'}


def queue(**changes):
    return dict(schemaVersion=1, total_jobs=0, pending=0, processing_claimed=0,
                completed=0, retrying=0, terminal_failed=0, max_attempt_count=0,
                oldest_pending_age_seconds=None, oldest_processing_age_seconds=None, **changes)


class SmokeTests(unittest.TestCase):
    def failure(self, code, fn, *args):
        with self.assertRaises(smoke.Failure) as result:
            fn(*args)
        self.assertEqual(result.exception.code, code)

    def test_wrong_sha_environment_and_malformed_json_fail(self):
        self.failure('backend_sha', smoke.check_version, 200, {'environment': 'Staging', 'commit': SHA}, 'b' * 40)
        self.failure('version_environment_or_commit', smoke.check_version, 200, {'environment': 'Production', 'commit': SHA})
        self.failure('malformed_json', smoke.decode, b'<html>SECRET</html>')
        self.assertEqual(smoke.check_version(200, {'environment': 'Staging', 'commit': SHA}, SHA), SHA)

    def test_health_json_matches_real_endpoint_without_requiring_timestamp(self):
        smoke.check_health(200, smoke.decode(json.dumps(HEALTH).encode()))
        smoke.check_health(200, {'status': 'Healthy', 'environment': 'Staging'})

    def test_sample_rejects_invalid_health_before_any_other_checks(self):
        cases = [
            (200, json.dumps({**HEALTH, 'status': 'Unhealthy'}).encode(), 'health_status_or_environment'),
            (200, json.dumps({**HEALTH, 'environment': 'Production'}).encode(), 'health_status_or_environment'),
            (200, b'{"status":"Healthy"}', 'health_status_or_environment'),
            (200, b'null', 'health_json'),
            (200, b'[]', 'health_json'),
            (200, b'"Healthy"', 'health_json'),
            (200, b'Healthy', 'malformed_json'),
            (200, b'{"status":', 'malformed_json'),
            (503, json.dumps(HEALTH).encode(), 'health_http_status'),
        ]
        for status, body, code in cases:
            with self.subTest(status=status, body=body):
                paths = []
                def get(path):
                    paths.append(path)
                    return status, body
                with self.assertRaises(smoke.Failure) as result:
                    smoke.sample('backend', SHA, ROOT, None, get)
                self.assertEqual(result.exception.code, code)
                self.assertEqual(result.exception.category, 'POST-DEPLOY HEALTH FAILURE')
                self.assertEqual(paths, ['/api/health'])

    def test_migrations_exact_set_including_historical_baseline(self):
        expected = smoke.REQUIRED | {'20261001000000_Example'}
        self.assertEqual(smoke.check_migrations({'schemaVersion': 1, 'migrations': sorted(expected)}, expected)['count'], 7)
        for values in (sorted(smoke.REQUIRED), sorted(expected) + [sorted(expected)[0]], sorted(expected) + ['20271001000000_Future']):
            self.failure('migration_mismatch', smoke.check_migrations, {'schemaVersion': 1, 'migrations': values}, expected)

    def test_queue_thresholds_and_transient_retry_warning(self):
        self.assertIsNone(smoke.check_queue(queue())['warning'])
        q = queue()
        q.update(total_jobs=1, pending=1, retrying=1, max_attempt_count=2, oldest_pending_age_seconds=20)
        self.assertEqual(smoke.check_queue(q)['warning'], 'transient_retry')
        q['oldest_pending_age_seconds'] = 901
        self.failure('queue_pending_age', smoke.check_queue, q)
        q.update(total_jobs=26, pending=26, retrying=26, oldest_pending_age_seconds=10)
        self.failure('queue_excessive_retries', smoke.check_queue, q)
        q = queue()
        q.update(total_jobs=1, terminal_failed=1)
        self.failure('queue_terminal_failed', smoke.check_queue, q)
        q = queue()
        q.update(total_jobs=1, processing_claimed=1, oldest_processing_age_seconds=20)
        self.failure('queue_processing_busy', smoke.check_queue, q)
        q['oldest_processing_age_seconds'] = 301
        self.failure('queue_stuck_processing', smoke.check_queue, q)

    def test_queue_malformed_counts_and_ages_fail_closed(self):
        for key, value in [('pending', True), ('max_attempt_count', -1), ('oldest_pending_age_seconds', 0), ('total_jobs', 1)]:
            q = queue()
            q[key] = value
            with self.assertRaises(smoke.Failure):
                smoke.check_queue(q)

    def test_retry_is_bounded_and_stability_handles_other_repo_deploy(self):
        stable = {'backendCommit': SHA, 'frontend': None}
        responses = iter([stable, {**stable, 'backendCommit': 'b' * 40}, stable, stable])
        calls = []
        result = smoke.verify_until_stable(lambda: next(responses), lambda: calls.append(1), pause=lambda _: None)
        self.assertEqual(result['attempts'], 2)
        self.assertEqual(len(calls), 2)
        def fail():
            calls.append(1)
            raise smoke.Failure('POST-DEPLOY HEALTH FAILURE', 'health')
        calls.clear()
        self.failure('health', smoke.verify_until_stable, fail, lambda: None, 3, lambda _: None)
        self.assertEqual(len(calls), 3)

    def test_infrastructure_is_not_retried_and_deadline_stops(self):
        calls = []
        def fail():
            calls.append(1)
            raise smoke.Failure('TEST INFRA FAILURE', 'wrapper_contract')
        self.failure('wrapper_contract', smoke.verify_until_stable, fail, lambda: None, 30, lambda _: None)
        self.assertEqual(len(calls), 1)
        ticks = iter([0, 601])
        with self.assertRaises(smoke.Failure):
            smoke.verify_until_stable(lambda: self.fail('deadline ignored'), lambda: None, clock=lambda: next(ticks))

    def test_safe_status_projection_and_logs_never_include_payload(self):
        item = dict(state='running', health='healthy', restarting=False, restartCount=0, token='SECRET')
        result = smoke.check_status(dict(schemaVersion=1, backend=item, postgres=item))
        self.assertNotIn('SECRET', json.dumps(result))
        logs = smoke.summarize_logs('Notification exception SECRET\nmigration error PASSWORD')
        self.assertEqual(logs['notification_error_lines'], 1)
        self.assertNotIn('SECRET', json.dumps(logs))
        item['restartCount'] = 1
        self.failure('container_backend', smoke.check_status, dict(schemaVersion=1, backend=item, postgres=item))

    def test_ssh_rejects_injection_and_only_exact_wrappers_allowed(self):
        with patch.dict('os.environ', {'STAGING_DIAGNOSTIC_HOST': 'host;evil'}):
            with self.assertRaises(smoke.Failure):
                with smoke.Diagnostics():
                    self.fail('unsafe host accepted')
        with patch.dict('os.environ', {'STAGING_DIAGNOSTIC_HOST': 'staging.example', 'STAGING_DIAGNOSTIC_KEY': 'TEST', 'STAGING_KNOWN_HOSTS': 'TEST'}):
            with smoke.Diagnostics() as d:
                self.assertIn('StrictHostKeyChecking=yes', d.args)
                self.assertEqual(d.args[-1], 'codex@staging.example')
                self.failure('wrapper_not_allowed', d.read, 'bash')
        self.assertEqual(set(smoke.WRAPPERS), {'status', 'logs', 'migrations', 'queue'})

    def test_observed_backend_revision_is_used_not_frontend_sha(self):
        class Diagnostics:
            def read(self, name):
                item = dict(state='running', health='healthy', restarting=False, restartCount=0)
                return json.dumps({'status': dict(schemaVersion=1, backend=item, postgres=item),
                                   'queue': queue(), 'migrations': dict(schemaVersion=1, migrations=sorted(smoke.REQUIRED))}[name])
        asset = b'console.log("boot")'
        metadata = dict(commit='b' * 40, environment='Staging', mainJs=dict(path='/static/js/main.test.js', sha256=smoke.hashlib.sha256(asset).hexdigest()))
        def get(path):
            if path == '/api/health': return 200, json.dumps(HEALTH).encode()
            if path == '/api/version': return 200, json.dumps(dict(environment='Staging', commit=SHA)).encode()
            if path.startswith('/build-meta.json'): return 200, json.dumps(metadata).encode()
            if path.startswith('/login'): return 200, b'<script src="/static/js/main.test.js"></script>'
            return 200, asset
        with patch.object(smoke, 'expected_migrations', return_value=smoke.REQUIRED) as migrations:
            result = smoke.sample('frontend', 'b' * 40, ROOT, Diagnostics(), get)
            migrations.assert_called_once_with(ROOT, SHA)
            self.assertEqual(result['backendCommit'], SHA)
            self.assertEqual(result['frontend']['commit'], 'b' * 40)

    def test_current_repository_migrations_can_be_resolved(self):
        ids = smoke.expected_migrations(ROOT, '4a4b32ae2491083f76d8bf53c9437f52073ad31e')
        self.assertEqual(len(ids), 43)

    def test_stale_html_cannot_pass_with_new_metadata(self):
        self.failure('frontend_index_bundle', smoke.check_index, b'<script src="/static/js/main.old.js"></script>', '/static/js/main.new.js')
        smoke.check_index(b'<script defer src="/static/js/main.new.js"></script>', '/static/js/main.new.js')

    def test_frontend_metadata_fails_closed(self):
        metadata = dict(commit=SHA, environment='Staging', mainJs=dict(path='/static/js/main.test.js', sha256='a' * 64))
        self.failure('frontend_sha', smoke.check_frontend, metadata, 'b' * 40)
        metadata['mainJs']['path'] = '/api/private'
        self.failure('frontend_asset_metadata', smoke.check_frontend, metadata, SHA)
        metadata['commit'] = 'a' * 7
        self.failure('frontend_identity', smoke.check_frontend, metadata, SHA)

    def test_new_counterpart_commit_refreshes_only_fixed_develop_ref(self):
        from types import SimpleNamespace
        replies = [SimpleNamespace(returncode=1), SimpleNamespace(returncode=0),
                   SimpleNamespace(returncode=0, stdout=SHA + '\n'),
                   SimpleNamespace(stdout='HockeyPlanner.Backend.Infrastructure/Data/Migrations/20260125121252_InitialCreate.cs\n')]
        with patch.object(smoke.subprocess, 'run', side_effect=replies) as run:
            self.assertEqual(smoke.expected_migrations(ROOT, SHA), smoke.REQUIRED)
            self.assertEqual(run.call_args_list[1].args[0][-4:], ['fetch', '--no-tags', 'origin', 'develop'])


if __name__ == '__main__':
    unittest.main()
