#!/usr/bin/env python3
"""Observe the atomic MOVE preconditions a verified interoperability profile requires.

Required cases are the atomic guarantees Move modules delegate to the server and
the digest-pinned Radicale 3.7.8 reference satisfies: a byte-preserving MOVE
between Calendars, `Overwrite: F` rejection of an occupied destination, and
same-kind and cross-kind UID rejection without a commit. Supplementary cases
are recorded without deciding a profile: a stale `If-Match` (the reference does
not evaluate it on MOVE; Move modules re-read the source revision before
dispatch and reconcile both sides afterwards), a same-Calendar rename, and,
with a Nextcloud attendee manifest, scheduling side effects of MOVE.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import secrets
import subprocess
import xml.etree.ElementTree as ET

from infra import docker, request


CONFLICT = (409, 412)


def todo(uid, summary='Move precondition fixture'):
    return '\r\n'.join(['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//RFC move observation//EN',
        'BEGIN:VTODO', 'UID:'+uid, 'DTSTAMP:20260101T000000Z', 'SUMMARY:'+summary,
        'END:VTODO', 'END:VCALENDAR', ''])


def event(uid):
    return '\r\n'.join(['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//RFC move observation//EN',
        'BEGIN:VEVENT', 'UID:'+uid, 'DTSTAMP:20260101T000000Z', 'DTSTART:20260816T100000Z',
        'DTEND:20260816T110000Z', 'SUMMARY:Move precondition collision', 'END:VEVENT', 'END:VCALENDAR', ''])


def scheduled_event(uid):
    return '\r\n'.join(['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//RFC move observation//EN',
        'BEGIN:VEVENT', 'UID:'+uid, 'DTSTAMP:20260101T000000Z', 'DTSTART:20270816T100000Z',
        'DTEND:20270816T110000Z', 'SUMMARY:Move scheduling observation',
        'ORGANIZER:mailto:organizer@example.invalid',
        'ATTENDEE;PARTSTAT=ACCEPTED:mailto:organizer@example.invalid',
        'ATTENDEE;RSVP=TRUE;PARTSTAT=NEEDS-ACTION:mailto:attendee@example.invalid',
        'END:VEVENT', 'END:VCALENDAR', ''])


def opaque_name(uid):
    digest = hashlib.sha256(uid.encode()).digest()
    return base64.urlsafe_b64encode(digest).decode().rstrip('=') + '.ics'


SABRE = '{http://sabredav.org/ns}'


def dav_errors(data):
    try:
        document = ET.fromstring(data)
    except ET.ParseError:
        return [], None
    if document.tag != '{DAV:}error':
        return [], None
    names = sorted(child.tag.replace('{urn:ietf:params:xml:ns:caldav}', 'CALDAV:')
                   .replace('{DAV:}', 'DAV:') for child in document if not child.tag.startswith(SABRE))
    diagnostic = {key: document.findtext(SABRE + key) for key in ('exception', 'message')
                  if document.findtext(SABRE + key)}
    return names, diagnostic or None


class Probe:
    def __init__(self, state):
        self.state = state
        self.suffix = secrets.token_hex(4)
        self.source = state['home_path'] + 'move-source-' + self.suffix + '/'
        self.destination = state['home_path'] + 'move-destination-' + self.suffix + '/'
        self.cases = []

    def calendar(self, path, kinds):
        comps = ''.join(f'<C:comp name="{kind}"/>' for kind in kinds)
        body = f'''<C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:set><D:prop>
<D:displayname>RFC move {self.suffix}</D:displayname>
<C:supported-calendar-component-set>{comps}</C:supported-calendar-component-set>
</D:prop></D:set></C:mkcalendar>'''
        assert request(self.state, 'MKCALENDAR', path, body)[0] == 201, path

    def put(self, path, data, prior=None):
        headers = {'Content-Type': 'text/calendar'}
        headers.update({'If-Match': prior} if prior else {'If-None-Match': '*'})
        status = request(self.state, 'PUT', path, data, headers)[0]
        assert status in ((200, 204) if prior else (201,)), (path, status)
        return self.get(path)

    def get(self, path):
        status, data, headers = request(self.state, 'GET', path)
        tag = headers.get('ETag') or headers.get('Etag')
        return dict(status=status, etag=tag, body=data if status == 200 else None)

    def move(self, path, destination, tag):
        status, data, headers = request(self.state, 'MOVE', path, None, {
            'If-Match': tag, 'Overwrite': 'F',
            'Destination': self.state['url'] + destination})
        errors, diagnostic = dav_errors(data)
        result = dict(status=status, dav_errors=errors)
        if diagnostic:
            result['server_diagnostic'] = diagnostic
        return result

    def record(self, name, expectation, move, checks, required=True):
        passed = expectation(move) and all(checks.values())
        self.cases.append(dict(case=name, required=required, move=move, checks=checks, passed=passed))
        print(json.dumps(self.cases[-1]), flush=True)

    def success_between_calendars(self):
        uid = 'move-success-' + self.suffix
        source = self.put(self.source + 'success.ics', todo(uid))
        target = self.destination + opaque_name(uid)
        move = self.move(self.source + 'success.ics', target, source['etag'])
        after = self.get(target)
        self.record('success_between_calendars', lambda m: m['status'] in (201, 204), move, dict(
            source_absent=self.get(self.source + 'success.ics')['status'] == 404,
            destination_present=after['status'] == 200,
            destination_strong_entity_tag=bool(after['etag']) and not after['etag'].startswith('W/'),
            destination_bytes_preserved=after['body'] == source['body']))

    def success_same_calendar_rename(self):
        uid = 'move-rename-' + self.suffix
        source = self.put(self.source + 'rename-before.ics', todo(uid))
        move = self.move(self.source + 'rename-before.ics', self.source + 'rename-after.ics', source['etag'])
        self.record('success_same_calendar_rename', lambda m: m['status'] in (201, 204), move, dict(
            source_absent=self.get(self.source + 'rename-before.ics')['status'] == 404,
            destination_present=self.get(self.source + 'rename-after.ics')['status'] == 200), required=False)

    def stale_if_match(self):
        uid = 'move-stale-' + self.suffix
        path = self.source + 'stale.ics'
        first = self.put(path, todo(uid))
        changed = self.put(path, todo(uid, 'Changed after read'), first['etag'])
        target = self.destination + opaque_name(uid)
        move = self.move(path, target, first['etag'])
        current = self.get(path)
        self.record('stale_if_match', lambda m: m['status'] == 412, move, dict(
            entity_tag_changed=changed['etag'] != first['etag'],
            source_unchanged=current['status'] == 200 and current['etag'] == changed['etag'],
            destination_absent=self.get(target)['status'] == 404), required=False)

    def overwrite_false_occupied(self):
        uid = 'move-occupied-' + self.suffix
        source = self.put(self.source + 'occupied.ics', todo(uid))
        target = self.destination + opaque_name(uid)
        occupant = self.put(target, todo('occupant-' + self.suffix))
        move = self.move(self.source + 'occupied.ics', target, source['etag'])
        self.record('overwrite_false_occupied_destination', lambda m: m['status'] == 412, move, dict(
            source_unchanged=self.get(self.source + 'occupied.ics')['etag'] == source['etag'],
            destination_unchanged=self.get(target)['etag'] == occupant['etag']))

    def uid_conflict(self, cross_kind):
        name = 'cross-kind' if cross_kind else 'same-kind'
        uid = 'move-uid-' + name + '-' + self.suffix
        source = self.put(self.source + name + '.ics', todo(uid))
        existing = self.put(self.destination + name + '-existing.ics', event(uid) if cross_kind else todo(uid))
        target = self.destination + opaque_name(uid)
        move = self.move(self.source + name + '.ics', target, source['etag'])
        expected = lambda m: m['status'] in CONFLICT or (
            m['status'] == 403 and 'CALDAV:no-uid-conflict' in m['dav_errors'])
        self.record('uid_conflict_' + name.replace('-', '_'), expected, move, dict(
            source_unchanged=self.get(self.source + name + '.ics')['etag'] == source['etag'],
            existing_unchanged=self.get(self.destination + name + '-existing.ics')['etag'] == existing['etag'],
            destination_absent=self.get(target)['status'] == 404))

    def scheduling(self, attendee):
        uid = 'move-scheduling-' + self.suffix
        source_calendar = self.state['home_path'] + 'move-scheduling-' + self.suffix + '/'
        self.calendar(source_calendar, ['VEVENT'])
        try:
            source = self.put(source_calendar + 'scheduled.ics', scheduled_event(uid))
            delivered = attendee_resources(attendee)
            mail_attempts = imip_attempts(self.state)
            target = self.destination + opaque_name(uid)
            move = self.move(source_calendar + 'scheduled.ics', target, source['etag'])
            self.record('scheduling_side_effects_absent', lambda m: m['status'] in (201, 204), move, dict(
                put_delivered_to_attendee=any(uid.encode() in body for body in delivered.values()),
                attendee_resources_unchanged=attendee_resources(attendee) == delivered,
                no_additional_imip_attempt=imip_attempts(self.state) == mail_attempts,
                destination_bytes_preserved=self.get(target)['body'] == source['body']), required=False)
        finally:
            assert request(self.state, 'DELETE', source_calendar)[0] in (200, 204), source_calendar

    def run(self, attendee=None):
        self.calendar(self.source, ['VTODO'])
        self.calendar(self.destination, ['VEVENT', 'VTODO'])
        try:
            if attendee:
                self.scheduling(attendee)
            self.success_between_calendars()
            self.success_same_calendar_rename()
            self.stale_if_match()
            self.overwrite_false_occupied()
            self.uid_conflict(cross_kind=False)
            self.uid_conflict(cross_kind=True)
        finally:
            for path in (self.source, self.destination):
                assert request(self.state, 'DELETE', path)[0] in (200, 204), path


def attendee_resources(state):
    """Return every attendee Calendar member, including scheduling inbox deliveries."""
    collections = [href for href in members(state, state['home_path']) if href != state['home_path']]
    hrefs = [href for path in collections for href in members(state, path) if href.endswith('.ics')]
    return {href: request(state, 'GET', href)[1] for href in hrefs}


def members(state, path):
    body = '<D:propfind xmlns:D="DAV:"><D:prop><D:getetag/></D:prop></D:propfind>'
    status, data, _ = request(state, 'PROPFIND', path, body, {'Depth': '1'})
    return [x.findtext('{DAV:}href') for x in ET.fromstring(data).findall('{DAV:}response')] if status == 207 else []


def owned_container(state):
    if state.get('parent_fixture'):
        state = json.loads((Path(state['parent_fixture']) / 'infra-private.json').read_text())
    container = state['containers'][0]
    owner = docker('inspect', '--format', '{{index .Config.Labels "caldav.rfc.owner"}}', container)
    if owner != state['run']:
        raise ValueError('The Nextcloud container does not belong to this fixture.')
    return container


def imip_attempts(state):
    log = subprocess.run(['docker', 'exec', '--user', 'www-data', owned_container(state), 'cat', 'data/nextcloud.log'],
                         capture_output=True, text=True, check=True).stdout
    return sum('Sending mail to' in line for line in log.splitlines())


def configure_scheduling(state, attendee):
    if state.get('server') != 'nextcloud' or attendee.get('parent_fixture') is None:
        raise ValueError('Scheduling observation needs a Nextcloud fixture and a fresh-user attendee manifest.')
    container = owned_container(state)
    for user, email in ((state['username'], 'organizer@example.invalid'),
                        (attendee['username'], 'attendee@example.invalid')):
        subprocess.run(['docker', 'exec', '--user', 'www-data', container, 'php', 'occ',
                        'user:setting', user, 'settings', 'email', email], check=True, capture_output=True)


def main(root, output, attendee_root=None):
    state = json.loads((root / 'infra-private.json').read_text())
    attendee = json.loads((attendee_root / 'infra-private.json').read_text()) if attendee_root else None
    if attendee:
        configure_scheduling(state, attendee)
    probe = Probe(state)
    probe.run(attendee)
    infra = json.loads((root / 'infra.json').read_text())
    public = dict(server=state.get('server', 'radicale'),
                  image=infra.get('image') or infra.get('images', {}).get('radicale'),
                  request_headers=['If-Match: <strong entity tag>', 'Overwrite: F', 'Destination: <absolute href>'],
                  cases=probe.cases,
                  profile_preconditions_satisfied=all(case['passed'] for case in probe.cases if case['required']),
                  supplementary={case['case']: case['passed'] for case in probe.cases if not case['required']})
    output.write_text(json.dumps(public, indent=2))
    print(json.dumps(dict(server=public['server'],
                          profile_preconditions_satisfied=public['profile_preconditions_satisfied'],
                          supplementary=public['supplementary'])))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--attendee-root', type=Path,
                        help='Nextcloud fresh-user manifest used to observe scheduling side effects of MOVE.')
    args = parser.parse_args()
    main(args.root.resolve(), args.output.resolve(),
         args.attendee_root.resolve() if args.attendee_root else None)
