#!/usr/bin/env python3
"""Verify Hermes's disposable To-do at Radicale, then finish cleanup via direct MRTR."""
import argparse
import asyncio
import json
from pathlib import Path
import urllib.parse
import xml.etree.ElementTree as ET
from driver import Client,environment
from functional import Functional
from infra import request,verify,expected_counts
from build_manifest import prepared_input


async def run(root,assembly):
    build_input=prepared_input(root,assembly)
    assembly=Path(build_input['assembly'])
    state=json.loads((root/'infra-private.json').read_text())
    uid='hermes-perf-20260905-integration'
    body=f'''<C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:prop>
<D:getetag/><C:calendar-data/></D:prop><C:filter><C:comp-filter name="VCALENDAR"><C:comp-filter name="VTODO">
<C:prop-filter name="UID"><C:text-match collation="i;octet">{uid}</C:text-match></C:prop-filter>
</C:comp-filter></C:comp-filter></C:filter></C:calendar-query>'''
    status,data,_=request(state,'REPORT','/perftest/todos/',body,{'Depth':'1'})
    assert status==207
    matches=[x for x in ET.fromstring(data).findall('{DAV:}response') if uid in ''.join(x.itertext())]
    checks=[dict(completed='STATUS:COMPLETED' in ''.join(x.itertext()),
                 summary_patched='SUMMARY:Hermes patched integration' in ''.join(x.itertext())) for x in matches]
    assert len(checks)==1 and all(checks[0].values()),checks
    (root/'hermes-authoritative.json').write_text(json.dumps(dict(http_status=status,matching_resources=1,checks=checks),indent=2))
    output=root/'hermes-direct-cleanup-final';output.mkdir()
    async with Client(assembly,environment(state,'caldav-perf-hermes-direct-cleanup-final')) as c:
        f=Functional(output,c,state)
        href=urllib.parse.urljoin(state['url'],matches[0].findtext('{DAV:}href'))
        snapshot=await f.read(href)
        assert snapshot['entityRevision']['entityUid']==uid
        await f.call('calendar_resources.delete',dict(revision=snapshot['entityRevision']))
        f.authoritative(href,absent=True)
    (output/'process.json').write_text(json.dumps(dict(c.identity,build_input=build_input),indent=2))
    assert verify(state)==expected_counts(root)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('assembly',type=Path)
    a=p.parse_args();asyncio.run(run(a.root,a.assembly))
