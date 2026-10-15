"""HTTP/process regressions for release readiness, not a local-package substitute."""
import argparse
import contextlib
import importlib.util
import io
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading
import time
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/verify_publication.py'
spec = importlib.util.spec_from_file_location('verify_publication', SCRIPT)
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
VERSION = '9.8.7'
COMMIT = '1' * 40


class Feed(ThreadingHTTPServer):
    request_queue_size = 128

    def __init__(self):
        super().__init__(('127.0.0.1', 0), Handler)
        self.packages = {item.lower() for item in release.package_ids('all')}
        self.missing = set()
        self.no_download = set()
        self.stale_leaf = False
        self.stale_index = False
        self.invalid_commit = False
        self.invalid_version = False
        self.invalid_id = False
        self.invalid_zip = False
        self.cache = None
        self.slow_download = False
        self.requests = []
        self.worker = threading.Thread(target=self.serve_forever, kwargs={'poll_interval': 0.01})
        self.worker.start()

    @property
    def base(self):
        return f'http://127.0.0.1:{self.server_port}'

    def stop(self):
        self.shutdown()
        self.server_close()
        self.worker.join()

    def reply(self, path):
        if path == '/v3/index.json':
            return 200, json.dumps({'resources': [
                {'@id': self.base + '/flat/', '@type': 'PackageBaseAddress/3.0.0'},
                {'@id': self.base + '/registration/', '@type': 'RegistrationsBaseUrl/3.6.0'}]}).encode()
        parts = path.strip('/').split('/')
        if len(parts) < 3 or parts[1] not in self.packages or parts[1] in self.missing:
            return 404, b''
        package = parts[1]
        content = f'{self.base}/flat/{package}/{VERSION}/{package}.{VERSION}.nupkg'
        if parts[0] == 'registration':
            if parts[2] != 'index.json':
                return (404, b'') if self.stale_leaf else (200, json.dumps({'listed': True, 'packageContent': content}).encode())
            if self.stale_index:
                return 200, b'{"items":[]}'
            entry = {'catalogEntry': {'version': VERSION, 'listed': True}, 'packageContent': content}
            return 200, json.dumps({'items': [{'items': [entry]}]}).encode()
        if parts[2] == 'index.json':
            return 200, json.dumps({'versions': [VERSION]}).encode()
        if package in self.no_download:
            return 404, b''
        if self.invalid_zip:
            return 200, b'not a package archive'
        identity = 'different-package' if self.invalid_id else package
        version = '9.8.6' if self.invalid_version else VERSION
        commit = '2' * 40 if self.invalid_commit else COMMIT
        result = io.BytesIO()
        with zipfile.ZipFile(result, 'w') as archive:
            archive.writestr(package + '.nuspec', f'<package><metadata><id>{identity}</id><version>{version}</version><repository type="git" commit="{commit}"/></metadata></package>')
        return 200, result.getvalue()


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        feed = self.server
        feed.requests.append(self.path)
        if feed.cache is not None and self.path in feed.cache:
            status, body = feed.cache[self.path]
        else:
            status, body = feed.reply(self.path.split('?')[0])
            if feed.cache is not None:
                feed.cache[self.path] = (status, body)
        self.send_response(status)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        try:
            if feed.slow_download and self.path.split('?')[0].endswith('.nupkg'):
                for byte in body:
                    self.wfile.write(bytes([byte]))
                    self.wfile.flush()
                    time.sleep(0.01)
            else:
                self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass  # The deadline test deliberately closes an expired request.

    def log_message(self, *_):
        pass


class VerifyPublicationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='dotcov-publication-')
        self.output = Path(self.directory.name)
        self.feed = Feed()

    def tearDown(self):
        self.feed.stop()
        self.directory.cleanup()

    def wait(self, stage='all', timeout=2):
        args = argparse.Namespace(version=VERSION, commit=COMMIT, stage=stage,
                                  source=self.feed.base + '/v3/index.json', output=self.output,
                                  timeout=timeout, poll=0.02)
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            status = release.wait(args)
        return status, json.loads((self.output / 'verification.json').read_text())

    def test_every_public_package_matches_version_and_source(self):
        status, evidence = self.wait()
        self.assertEqual(0, status)
        self.assertEqual('verified', evidence['status'])
        self.assertEqual(7, len(evidence['packages']))
        self.assertTrue(all(item['ready'] and item['sourceCommit'] == COMMIT for item in evidence['packages']))

    def test_pointer_does_not_count_as_ready_when_a_runtime_is_absent(self):
        self.feed.missing.add('dotcov.tool.linux-x64')
        status, evidence = self.wait('subpackages', timeout=0.3)
        self.assertEqual(release.PENDING_EXIT, status)
        self.assertEqual('pending', evidence['status'])
        self.assertTrue(any(item['id'] == 'DotCov.Tool.linux-x64' and not item['ready'] for item in evidence['packages']))
        self.assertFalse(any('/dotcov.tool/' in path for path in self.feed.requests))

    def test_an_index_entry_without_a_download_is_pending(self):
        self.feed.no_download.add('dotcov.tool.any')
        status, evidence = self.wait(timeout=0.3)
        self.assertEqual(release.PENDING_EXIT, status)
        self.assertEqual('pending', evidence['status'])

    def test_fresh_reads_recover_when_a_previously_missing_runtime_appears(self):
        self.feed.cache = {}
        self.feed.missing.add('dotcov.tool.linux-arm64')
        timer = threading.Timer(0.15, self.feed.missing.clear)
        timer.start()
        try:
            status, evidence = self.wait()
        finally:
            timer.join()
        self.assertEqual(0, status)
        self.assertEqual('verified', evidence['status'])
        self.assertGreater(len((self.output / 'attempts.jsonl').read_text().splitlines()), 1)

    def test_stale_registration_leaf_uses_the_actual_index_entry(self):
        self.feed.stale_leaf = True
        status, evidence = self.wait()
        self.assertEqual(0, status)
        self.assertEqual('verified', evidence['status'])
        self.assertTrue(any('/registration/' in path and '/index.json' in path for path in self.feed.requests))

    def test_a_fresh_leaf_cannot_replace_a_missing_registration_index_entry(self):
        self.feed.stale_index = True
        status, evidence = self.wait(timeout=0.3)
        self.assertEqual(release.PENDING_EXIT, status)
        self.assertEqual('pending', evidence['status'])

    def test_wrong_source_version_identity_or_corrupt_archive_fails(self):
        for field in ('invalid_commit', 'invalid_version', 'invalid_id', 'invalid_zip'):
            with self.subTest(field=field):
                setattr(self.feed, field, True)
                status, evidence = self.wait()
                self.assertEqual(1, status)
                self.assertEqual('failed', evidence['status'])
                setattr(self.feed, field, False)

    def test_a_slow_continuous_download_cannot_outlive_the_availability_window(self):
        self.feed.slow_download = True
        started = time.monotonic()
        status, evidence = self.wait(timeout=0.4)
        self.assertEqual(release.PENDING_EXIT, status)
        self.assertEqual('pending', evidence['status'])
        self.assertLess(time.monotonic() - started, 0.8)

    def test_a_local_file_cannot_replace_an_unavailable_public_download(self):
        self.assertEqual(0, self.wait()[0])
        self.feed.no_download.add('dotcov.tool.any')
        status, evidence = self.wait(timeout=0.3)
        self.assertEqual(release.PENDING_EXIT, status)
        self.assertEqual('pending', evidence['status'])


if __name__ == '__main__':
    unittest.main()
