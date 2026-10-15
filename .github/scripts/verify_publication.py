"""Verify public NuGet packages and the installed CLI before declaring a release."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PENDING_EXIT = 10


class Pending(Exception):
    """NuGet has not exposed a package yet, or a read failed transiently."""


class InvalidPublication(Exception):
    """Published contents do not match the requested release."""


def package_ids(stage):
    project = ET.parse(ROOT / 'src/DotCov.Tool/DotCov.Tool.csproj')
    rids = project.findtext('.//ToolPackageRuntimeIdentifiers').split(';')
    ids = ['DotCov', 'DotCov.Fallout', *(f'DotCov.Tool.{rid}' for rid in rids)]
    return ids if stage == 'subpackages' else [*ids, 'DotCov.Tool']


def fetch(url, deadline):
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise Pending('availability window expired')
    separator = '&' if '?' in url else '?'
    request = urllib.request.Request(url + separator + f'fresh={time.time_ns()}',
                                     headers={'Cache-Control': 'no-cache', 'Pragma': 'no-cache'})
    try:
        with urllib.request.urlopen(request, timeout=min(5, remaining)) as response:
            chunks = []
            size = 0
            while True:
                if time.monotonic() >= deadline:
                    raise Pending('download exceeded the availability window')
                chunk = response.read1(65536)
                if not chunk:
                    break
                chunks.append(chunk)
                size += len(chunk)
                if size > 100_000_000:
                    raise InvalidPublication('response exceeds 100 MB')
            body = b''.join(chunks)
            if response.headers.get('Content-Encoding') == 'gzip':
                import gzip
                body = gzip.decompress(body)
            return body
    except urllib.error.HTTPError as error:
        if error.code in (404, 408, 429, 500, 502, 503, 504):
            raise Pending(f'HTTP {error.code}: {url}') from error
        raise InvalidPublication(f'HTTP {error.code}: {url}') from error
    except (urllib.error.URLError, TimeoutError) as error:
        raise Pending(f'{type(error).__name__}: {url}') from error


def read_json(url, deadline):
    return json.loads(fetch(url, deadline))


def endpoints(source, deadline):
    resources = read_json(source, deadline)['resources']
    flat = next(item['@id'] for item in resources if item['@type'] == 'PackageBaseAddress/3.0.0')
    registration = next(item['@id'] for item in resources if item['@type'] == 'RegistrationsBaseUrl/3.6.0')
    return flat.rstrip('/') + '/', registration.rstrip('/') + '/'


def registered(base, package, version, deadline):
    expected = f'/{package}/{version}/{package}.{version}.nupkg'
    # Read the version entries used by NuGet's metadata client. A separately
    # cached leaf can be current while this index is stale, or the reverse.
    index = read_json(f'{base}{package}/index.json', deadline)
    for page in index.get('items', []):
        items = page.get('items')
        if items is None:
            items = read_json(page['@id'], deadline).get('items', [])
        for entry in items:
            catalog = entry.get('catalogEntry', {})
            if (isinstance(catalog, dict) and catalog.get('version') == version
                    and catalog.get('listed') is not False
                    and entry.get('packageContent', '').endswith(expected)):
                return True
    return False


def package_metadata(path, package, version, commit):
    with zipfile.ZipFile(path) as archive:
        names = [name for name in archive.namelist() if name.endswith('.nuspec')]
        if len(names) != 1:
            raise InvalidPublication(f'{package}: expected one nuspec')
        metadata = ET.fromstring(archive.read(names[0])).find('{*}metadata')
    identity = metadata.findtext('{*}id')
    actual_version = metadata.findtext('{*}version')
    repository = metadata.find('{*}repository')
    actual_commit = repository.get('commit') if repository is not None else None
    if identity.lower() != package.lower() or actual_version != version or actual_commit != commit:
        raise InvalidPublication(f'{package}: expected {version} from {commit}; got {identity} {actual_version} from {actual_commit}')
    return {'id': package, 'version': actual_version, 'sourceCommit': actual_commit,
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'file': str(path)}


def inspect_package(package, version, commit, urls, output, deadline):
    key = package.lower()
    flat, registration = urls
    try:
        if version not in read_json(f'{flat}{key}/index.json', deadline)['versions']:
            raise Pending('version absent from package index')
        if not registered(registration, key, version, deadline):
            raise Pending('version absent from registration')
        path = output / f'{key}.{version}.nupkg'
        # Always read the public package. A local build or previously cached file
        # must not satisfy this check when its public download is unavailable.
        path.write_bytes(fetch(f'{flat}{key}/{version}/{path.name}', deadline))
        return {'ready': True, **package_metadata(path, package, version, commit)}
    except Pending as error:
        return {'id': package, 'ready': False, 'reason': str(error)}


def wait(args):
    args.output.mkdir(parents=True, exist_ok=True)
    # Reserve enough time for the final bounded socket read and worker shutdown.
    deadline = time.monotonic() + args.timeout - min(5, args.timeout / 2)
    packages = package_ids(args.stage)
    verified = {}
    state = {'version': args.version, 'sourceCommit': args.commit, 'stage': args.stage, 'status': 'pending'}
    while True:
        try:
            urls = endpoints(args.source, deadline)
            state.pop('reason', None)
            pending = [package for package in packages if package not in verified]
            with ThreadPoolExecutor(max_workers=7) as pool:
                checks = list(pool.map(lambda package: inspect_package(package, args.version, args.commit, urls, args.output, deadline), pending))
            for check in checks:
                if check['ready']:
                    verified[check['id']] = check
            state['packages'] = [verified.get(package, next((item for item in checks if item['id'] == package), {'id': package, 'ready': False})) for package in packages]
            state['status'] = 'verified' if len(verified) == len(packages) else 'pending'
        except Pending as error:
            state['reason'] = str(error)
        except (InvalidPublication, ValueError, KeyError, TypeError, AttributeError, StopIteration, ET.ParseError, zipfile.BadZipFile) as error:
            state.update(status='failed', reason=str(error))
        (args.output / 'verification.json').write_text(json.dumps(state, indent=2) + '\n')
        with (args.output / 'attempts.jsonl').open('a') as history:
            history.write(json.dumps({'time': time.time(), **state}) + '\n')
        print(json.dumps({'status': state['status'], 'verified': list(verified),
                          'pending': [item for item in state.get('packages', []) if not item['ready']],
                          'reason': state.get('reason')}), flush=True)
        if state['status'] == 'verified':
            return 0
        if state['status'] == 'failed':
            return 1
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            print('NuGet availability window ended with packages pending.', file=sys.stderr)
            return PENDING_EXIT
        time.sleep(min(args.poll, remaining))


def smoke(args):
    args.output.mkdir(parents=True, exist_ok=True)
    package = args.output / f'dotcov.tool.{args.runtime}.{args.version}.nupkg'
    package_metadata(package, f'DotCov.Tool.{args.runtime}', args.version, args.commit)
    with zipfile.ZipFile(package) as archive:
        expected = hashlib.sha256(archive.read(f'tools/any/{args.runtime}/DotCov.Tool')).hexdigest()
    actual = hashlib.sha256(args.tool.resolve().read_bytes()).hexdigest()
    if actual != expected:
        raise InvalidPublication('installed native binary differs from the verified public package')
    fixture = ROOT / 'tests/DotCov.Tests/Fixtures/microsoft-same-line-owners.cobertura.xml'
    cases = [('version', ['--version'], 0),
             ('report', ['report', str(fixture), '--format', 'json'], 0),
             ('gate-fail', ['check', str(fixture), '--min-branch', '75', '--format', 'json'], 1),
             ('gate-pass', ['check', str(fixture), '--min-branch', '60', '--format', 'json'], 0)]
    commands = []
    for name, options, expected_exit in cases:
        result = subprocess.run([str(args.tool), *options], text=True, capture_output=True, timeout=30)
        (args.output / f'smoke-{name}.stdout').write_text(result.stdout)
        (args.output / f'smoke-{name}.stderr').write_text(result.stderr)
        commands.append({'name': name, 'expectedExit': expected_exit, 'actualExit': result.returncode})
        if result.returncode != expected_exit:
            raise InvalidPublication(f'{name}: expected exit {expected_exit}, got {result.returncode}: {result.stderr}')
        if name == 'version':
            if not re.fullmatch(r'dotcov ' + re.escape(args.version) + r'(?:\+\S+)?', result.stdout.strip()):
                raise InvalidPublication(f'installed version differs: {result.stdout.strip()}')
        else:
            data = json.loads(result.stdout)
            if data['warnings'] or (data['summary']['coveredBranches'], data['summary']['totalBranches']) != (4, 6):
                raise InvalidPublication(f'{name}: incorrect branch counts or warnings')
            if name.startswith('gate-') and data['gate']['outcome'] != ('fail' if expected_exit == 1 else 'pass'):
                raise InvalidPublication(f'{name}: incorrect gate outcome')
    (args.output / 'installed-tool.json').write_text(json.dumps({'version': args.version, 'sourceCommit': args.commit,
        'runtime': args.runtime, 'binarySha256': actual, 'commands': commands}, indent=2) + '\n')
    print('Verified the installed public native binary, version, branch counts and both gate outcomes.')
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('wait', 'smoke'):
        command = commands.add_parser(name)
        command.add_argument('--version', required=True)
        command.add_argument('--commit', required=True)
        command.add_argument('--output', required=True, type=Path)
    waiter = commands.choices['wait']
    waiter.add_argument('--stage', choices=('subpackages', 'all'), required=True)
    waiter.add_argument('--source', default='https://api.nuget.org/v3/index.json')
    waiter.add_argument('--timeout', type=float, default=240)
    waiter.add_argument('--poll', type=float, default=15)
    installed = commands.choices['smoke']
    installed.add_argument('--tool', required=True, type=Path)
    installed.add_argument('--runtime', required=True, choices=('linux-x64', 'linux-arm64', 'osx-arm64'))
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version) or not re.fullmatch(r'[0-9a-f]{40}', args.commit):
        parser.error('version must be x.y.z and commit must be a full Git SHA')
    if args.command == 'wait' and (not math.isfinite(args.timeout) or not math.isfinite(args.poll) or args.timeout <= 0 or args.timeout > 240 or args.poll <= 0):
        parser.error('availability windows must be positive and at most 240 seconds')
    try:
        return wait(args) if args.command == 'wait' else smoke(args)
    except (InvalidPublication, ValueError, KeyError, ET.ParseError, zipfile.BadZipFile, OSError, subprocess.TimeoutExpired) as error:
        print(f'Publication verification failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
