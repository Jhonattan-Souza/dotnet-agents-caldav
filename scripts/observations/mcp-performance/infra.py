#!/usr/bin/env python3
"""Owned, disposable Radicale/Aspire infrastructure; no production configuration."""
import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

RADICALE = 'ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80'
DASHBOARD = 'mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b'


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def request(state, method, path, body=None, headers=None):
    auth = base64.b64encode(f"{state['username']}:{state['password']}".encode()).decode()
    h = {'Authorization': 'Basic ' + auth, 'Content-Type': 'application/xml', **(headers or {})}
    req = urllib.request.Request(state['url'] + path, data=body.encode() if isinstance(body, str) else body,
                                 headers=h, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            return response.status, response.read(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers)


def up(root):
    root.mkdir(parents=True, exist_ok=True)
    path = root / 'infra-private.json'
    if path.exists():
        raise RuntimeError('Infrastructure manifest already exists; use its owned resources or clean first.')
    run = 'caldav-perf-' + secrets.token_hex(4)
    state = dict(run=run, username='perftest', password=secrets.token_hex(16),
                 api_key=secrets.token_hex(24), containers=[])
    def save():
        path.write_text(json.dumps(state, indent=2))
        path.chmod(0o600)
    save()
    config = root / 'radicale-config'
    config.mkdir()
    (config / 'config').write_text('''[server]
hosts = 0.0.0.0:5232
[auth]
type = htpasswd
htpasswd_filename = /config/users
htpasswd_encryption = plain
[rights]
type = owner_write
[storage]
filesystem_folder = /var/lib/radicale/collections
[web]
type = internal
[logging]
level = warning
''')
    (config / 'users').write_text(state['username'] + ':' + state['password'])
    for suffix, image, ports, extra in [
        ('radicale', RADICALE, ['127.0.0.1::5232'], ['--env', 'TZ=UTC', '--mount',
         f'type=bind,source={config},target=/config,readonly']),
        ('dashboard', DASHBOARD, ['127.0.0.1::18888', '127.0.0.1::18890'], ['--env',
         'DASHBOARD__API__PRIMARYAPIKEY=' + state['api_key'], '--env',
         'DASHBOARD__TELEMETRYLIMITS__MAXTRACECOUNT=50000', '--env',
         'DASHBOARD__TELEMETRYLIMITS__MAXLOGCOUNT=100000'])]:
        name = run + '-' + suffix
        args = ['run', '-d', '--name', name, '--label', 'caldav.performance.owner=' + run,
                '--cpus', '4', '--memory', '2g']
        for port in ports:
            args += ['-p', port]
        args += extra + [image]
        if suffix == 'radicale':
            args += ['--config', '/config/config', '--hosts', '0.0.0.0:5232,[::]:5232']
        state['containers'].append(name)
        save()
        docker(*args)
    def endpoint(suffix, port):
        return 'http://' + docker('port', run + '-' + suffix, str(port) + '/tcp')
    state.update(url=endpoint('radicale', 5232), dashboard=endpoint('dashboard', 18888),
                 otlp=endpoint('dashboard', 18890))
    save()
    for _ in range(60):
        try:
            if request(state, 'OPTIONS', '/')[0] == 200:
                break
        except OSError:
            pass
        time.sleep(0.5)
    else:
        raise RuntimeError('Radicale readiness timed out')
    print(json.dumps({k: state[k] for k in ['run', 'url', 'dashboard', 'otlp', 'containers']}))


def resource(kind, index):
    date = dt.datetime(2026, 7, 1, 12, tzinfo=dt.timezone.utc) + dt.timedelta(days=index % 180)
    stamp = lambda x: x.strftime('%Y%m%dT%H%M%SZ')
    lines = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//CalDAV performance seed 20260905//EN',
             'BEGIN:' + kind, f'UID:perf-20260905-{kind.lower()}-{index:04d}',
             'DTSTAMP:20260701T000000Z', f'SUMMARY:Performance {kind} {index:04d}',
             'CATEGORIES:PERFORMANCE,DISPOSABLE']
    if kind == 'VEVENT':
        if index % 20 == 1:
            lines += ['DTSTART;VALUE=DATE:' + date.strftime('%Y%m%d'),
                      'DTEND;VALUE=DATE:' + (date + dt.timedelta(days=1)).strftime('%Y%m%d')]
        else:
            lines += ['DTSTART:' + stamp(date), 'DTEND:' + stamp(date + dt.timedelta(hours=1))]
        lines += ['LOCATION:Local fixture']
        if index % 50 == 2:
            lines += ['STATUS:CANCELLED']
    else:
        lines += ['DTSTART:' + stamp(date), 'DUE:' + stamp(date + dt.timedelta(hours=2)),
                  'PRIORITY:' + str(index % 9 + 1)]
        if index % 20 < 6:
            lines += ['STATUS:COMPLETED', 'PERCENT-COMPLETE:100', 'COMPLETED:' + stamp(date)]
        elif index % 20 == 6:
            lines += ['STATUS:CANCELLED']
        else:
            lines += ['STATUS:NEEDS-ACTION']
    if index % 10 == 0:
        lines += ['RRULE:FREQ=WEEKLY;COUNT=' + ('20' if kind == 'VEVENT' else '12')]
    return '\r\n'.join(lines + ['END:' + kind, 'END:VCALENDAR', ''])


def seed(root, count):
    state = json.loads((root / 'infra-private.json').read_text())
    principal = '/' + state['username'] + '/'
    assert request(state, 'MKCOL', principal)[0] in (201, 405)
    digest = hashlib.sha256()
    for name, kind, n in [('events', 'VEVENT', count), ('todos', 'VTODO', count), ('archive', 'VTODO', 0)]:
        path = principal + name + '/'
        body = f'''<D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:set><D:prop>
<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>Performance {name}</D:displayname>
<C:supported-calendar-component-set><C:comp name="{kind}"/></C:supported-calendar-component-set>
</D:prop></D:set></D:mkcol>'''
        assert request(state, 'MKCOL', path, body)[0] == 201
        for i in range(n):
            data = resource(kind, i)
            digest.update(data.encode())
            assert request(state, 'PUT', path + f'{i:04d}.ics', data,
                           {'Content-Type': 'text/calendar', 'If-None-Match': '*'})[0] == 201
        print(f'Seeded {name}: {n}', flush=True)
    counts = verify(state)
    assert counts == {'events': count, 'todos': count, 'archive': 0}, counts
    (root / 'corpus.json').write_text(json.dumps(dict(seed=20260905, resources=counts,
         authored_sha256=digest.hexdigest(), window=['2026-07-01T00:00:00Z','2026-12-31T00:00:00Z'],
         timezone='America/Sao_Paulo'), indent=2))


def verify(state):
    result = {}
    for name in ['events', 'todos', 'archive']:
        body = '<D:propfind xmlns:D="DAV:"><D:prop><D:getetag/></D:prop></D:propfind>'
        status, data, _ = request(state, 'PROPFIND', f"/{state['username']}/{name}/", body, {'Depth':'1'})
        assert status == 207
        result[name] = sum(1 for x in ET.fromstring(data).findall('{DAV:}response')
                           if x.findtext('{DAV:}href', '').endswith('.ics'))
    return result


def down(root):
    path = root / 'infra-private.json'
    state = json.loads(path.read_text())
    for name in reversed(state['containers']):
        if not docker('ps', '-aq', '--filter', 'name=^/' + name + '$'):
            continue
        owner = docker('inspect', '--format', '{{index .Config.Labels "caldav.performance.owner"}}', name)
        assert owner == state['run']
        docker('rm', '-fv', name)
    path.unlink()
    import shutil
    shutil.rmtree(root / 'radicale-config')
    (root / 'cleanup.json').write_text(json.dumps(dict(containers_removed=state['containers'], complete=True)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['up', 'seed', 'verify', 'down'])
    parser.add_argument('root', type=Path)
    parser.add_argument('--count', type=int, default=600)
    args = parser.parse_args()
    if args.action == 'seed':
        seed(args.root, args.count)
    elif args.action == 'verify':
        print(json.dumps(verify(json.loads((args.root / 'infra-private.json').read_text()))))
    else:
        globals()[args.action](args.root)
