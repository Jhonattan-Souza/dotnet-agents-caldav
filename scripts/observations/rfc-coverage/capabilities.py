#!/usr/bin/env python3
"""Observe native RFC reports before making runtime-specific support claims."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import xml.etree.ElementTree as ET

from infra import request


def probe(root):
    state = json.loads((root / 'infra-private.json').read_text())
    path = state['home_path'] + 'events/'
    records = []
    def call(name, body):
        start = time.perf_counter_ns()
        status, data, headers = request(state, 'REPORT', path, body, {'Depth': '1'})
        records.append(dict(operation=name, status=status,
                            elapsed_ms=(time.perf_counter_ns()-start)/1e6,
                            response_bytes=len(data), response_sha256=hashlib.sha256(data).hexdigest(),
                            content_type=headers.get('Content-Type', '')))
        return status, data
    propfind = '''<D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:prop>
<D:resourcetype/><D:supported-report-set/><D:sync-token/>
<D:current-user-privilege-set/><C:supported-calendar-component-set/>
</D:prop></D:propfind>'''
    status, data, _ = request(state, 'PROPFIND', path, propfind, {'Depth': '0'})
    assert status == 207
    document = ET.fromstring(data)
    reports = sorted({child.tag for report in document.findall('.//{DAV:}report') for child in report})
    busy_status, busy_data = call('free-busy-query', '''<C:free-busy-query xmlns:C="urn:ietf:params:xml:ns:caldav">
<C:time-range start="20260701T000000Z" end="20261231T000000Z"/></C:free-busy-query>''')
    if busy_status == 200:
        assert b'BEGIN:VFREEBUSY' in busy_data
    def sync(token):
        element = ET.Element('{DAV:}sync-collection')
        ET.SubElement(element, '{DAV:}sync-token').text = token
        ET.SubElement(element, '{DAV:}sync-level').text = '1'
        prop = ET.SubElement(element, '{DAV:}prop')
        ET.SubElement(prop, '{DAV:}getetag')
        return ET.tostring(element, encoding='utf-8')
    sync_status, sync_data = call('sync-initial', sync(''))
    if sync_status == 207:
        initial = ET.fromstring(sync_data)
        token = initial.findtext('{DAV:}sync-token')
        assert token
        records[-1]['responses'] = len(initial.findall('{DAV:}response'))
        status, data = call('sync-no-change', sync(token))
        assert status == 207
        unchanged = ET.fromstring(data)
        assert unchanged.findtext('{DAV:}sync-token')
        records[-1]['responses'] = len(unchanged.findall('{DAV:}response'))
        assert records[-1]['responses'] == 0
    public = dict(server=state.get('server', 'radicale'),
                  advertised_reports=reports, observations=records)
    (root / 'native-capabilities.json').write_text(json.dumps(public, indent=2))
    print(json.dumps(public, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    probe(parser.parse_args().root)
