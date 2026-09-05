#!/usr/bin/env python3
"""Live assertions for the standard metadata and native report tool surface."""
import secrets
import urllib.parse
import xml.etree.ElementTree as ET

from infra import request


BUSY_WINDOW = dict(from_='2027-01-10T09:00:00Z', to='2027-01-10T15:00:00Z')


def check(suite, label, condition):
    if not condition:
        raise AssertionError(label)
    suite.records.append(dict(assertion=label, passed=True))
    suite.save()


def event(uid, start, end, extra=(), summary='RFC native report fixture'):
    return '\r\n'.join(['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//RFC observation//EN',
        'BEGIN:VEVENT', 'UID:'+uid, 'DTSTAMP:20260101T000000Z', 'DTSTART:'+start,
        'DTEND:'+end, 'SUMMARY:'+summary, *extra, 'END:VEVENT', 'END:VCALENDAR', ''])


def put(suite, href, data, prior=None):
    headers={'Content-Type':'text/calendar', 'If-Match':prior} if prior else {'Content-Type':'text/calendar','If-None-Match':'*'}
    status,_,response=request(suite.state,'PUT',urllib.parse.urlparse(href).path,data,headers)
    check(suite,'fixture_resource_updated' if prior else 'fixture_resource_created',status in ((200,204) if prior else (201,)))
    suite.owned.add(href)
    return response.get('ETag') or response.get('Etag')


def metadata_readback(suite, href, name, description):
    body='<D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:prop><D:displayname/><C:calendar-description/></D:prop></D:propfind>'
    status,data,_=request(suite.state,'PROPFIND',urllib.parse.urlparse(href).path,body,{'Depth':'0'})
    check(suite,'metadata_readback_multistatus',status==207)
    properties={}
    for propstat in ET.fromstring(data).findall('.//{DAV:}propstat'):
        status_text=propstat.findtext('{DAV:}status','')
        for prop in propstat.find('{DAV:}prop'):
            properties[prop.tag]=(int(status_text.split()[1]),prop.text or '')
    for tag,expected in [('{DAV:}displayname',name),('{urn:ietf:params:xml:ns:caldav}calendar-description',description)]:
        actual=properties.get(tag)
        check(suite,'metadata_'+tag.split('}')[-1]+'_readback',actual==(404,'') if expected is None else actual==(200,expected))


async def metadata(suite, href):
    suite.phase='metadata-inspect'
    observed=await suite.call('calendars.inspect',dict(calendarHref=href))
    check(suite,'inspect_exact_identity',observed['calendarHref']==href)
    check(suite,'inspect_known_name',observed['displayName']=='RFC native operations')
    check(suite,'inspect_report_evidence',any(report['localName']=='sync-collection' for report in observed['reports']))
    expected_scheduling='not_advertised' if suite.state.get('server','radicale')=='radicale' else 'advertised'
    check(suite,'inspect_scheduling_evidence',observed['scheduling']['state']==expected_scheduling)
    suite.phase='metadata-set'
    changed=await suite.call('calendars.patch',dict(calendarHref=href,patch={
        'displayName':dict(operation='set',value='RFC native renamed'),
        'description':dict(operation='set',value='RFC native description')}))
    check(suite,'metadata_unconditional_committed',changed['mutationState']=='committed' and changed['concurrency']=='unconditional')
    metadata_readback(suite,href,'RFC native renamed','RFC native description')
    suite.phase='metadata-omit-preserves'
    await suite.call('calendars.patch',dict(calendarHref=href,patch={'description':dict(operation='set',value='RFC replacement description')}))
    metadata_readback(suite,href,'RFC native renamed','RFC replacement description')
    suite.phase='metadata-remove'
    await suite.call('calendars.patch',dict(calendarHref=href,patch={'description':dict(operation='remove')}))
    metadata_readback(suite,href,'RFC native renamed',None)
    expected_removal='committed_but_unverified' if suite.state.get('server','radicale')=='radicale' else 'success'
    removal=await suite.call('calendars.patch',dict(calendarHref=href,patch={'displayName':dict(operation='remove')}),
                             _expected=expected_removal)
    observed=await suite.call('calendars.inspect',dict(calendarHref=href))
    if expected_removal=='success':
        metadata_readback(suite,href,None,None)
    else:
        check(suite,'server_synthesized_displayname_remove_unverified',removal['mutationState']=='committed'
              and bool(observed['displayName']) and observed['displayName']!='RFC native renamed')
    suite.phase='metadata-language'
    language=await suite.call('calendars.patch',dict(calendarHref=href,patch={
        'displayName':dict(operation='set',value='RFC native operations'),
        'description':dict(operation='set',value='Descrição RFC',language='pt-BR')}),
        _expected=('success','committed_but_unverified'))
    observed=await suite.call('calendars.inspect',dict(calendarHref=href))
    check(suite,'metadata_language_failure_truth',language.get('outcome')=='success'
          or language.get('mutationState')=='committed')
    check(suite,'metadata_language_readback_text',observed['description']=='Descrição RFC')
    suite.phase='metadata-validation'
    await suite.call('calendars.patch',dict(calendarHref=href,patch={}),_expected='invalid_input')


async def seed_and_busy(suite, href):
    corpus=[
        ('clipped','20270110T083000Z','20270110T093000Z',()),
        ('busy','20270110T100000Z','20270110T110000Z',()),
        ('tentative','20270110T113000Z','20270110T120000Z',('STATUS:TENTATIVE',)),
        ('transparent','20270110T120000Z','20270110T130000Z',('TRANSP:TRANSPARENT',)),
        ('cancelled','20270110T130000Z','20270110T140000Z',('STATUS:CANCELLED',)),
    ]
    for name,start,end,extra in corpus:
        put(suite,href+name+'.ics',event('rfc-'+name,start,end,extra))
    suite.phase='freebusy-known-intervals'
    expected_outcome='upstream_protocol_error' if suite.state.get('server','radicale')=='radicale' else 'success'
    busy=await suite.call('calendars.free_busy',dict(calendarHref=href,**{'from':BUSY_WINDOW['from_'],'to':BUSY_WINDOW['to']}),
                          _expected=expected_outcome)
    actual=[(period['from'],period['to'],period['busyType']) for period in busy.get('periods',[])]
    expected=[('2027-01-10T09:00:00Z','2027-01-10T09:30:00Z','BUSY'),
              ('2027-01-10T10:00:00Z','2027-01-10T11:00:00Z','BUSY'),
              ('2027-01-10T11:30:00Z','2027-01-10T12:00:00Z','BUSY-TENTATIVE')]
    if expected_outcome=='success':
        check(suite,'native_busy_periods_match_authored_busy_tentative_clipped',actual==expected)
        check(suite,'native_busy_complete_server_authority',busy['complete'] and busy['temporalAuthority']=='server')
    else:
        check(suite,'nonconforming_native_freebusy_has_no_availability','periods' not in busy and 'complete' not in busy)
    suite.phase='freebusy-empty'
    empty=await suite.call('calendars.free_busy',dict(calendarHref=href,**{'from':'2027-01-10T14:00:00Z','to':'2027-01-10T15:00:00Z'}),
                           _expected=expected_outcome)
    check(suite,'native_busy_empty_verified' if expected_outcome=='success' else 'invalid_empty_native_response_rejected',
          empty.get('periods')==[] if expected_outcome=='success' else 'periods' not in empty)
    suite.phase='freebusy-validation'
    await suite.call('calendars.free_busy',dict(calendarHref=href,**{'from':'2027-01-10T15:00:00Z','to':'2027-01-10T09:00:00Z'}),_expected='invalid_input')
    return {href+name+'.ics' for name,_,_,_ in corpus}


async def changes(suite, href, expected_members):
    suite.phase='sync-initial'
    initial=await suite.call('calendar_resources.changes',dict(calendarHref=href,pageSize=100))
    check(suite,'sync_initial_complete',initial['mode']=='initial' and not initial['hasMore'])
    check(suite,'sync_initial_inventory',set(change['href'] for change in initial['changes'] if change['kind']=='changed')==expected_members)
    check(suite,'sync_initial_tags_observational',all(change.get('etag') for change in initial['changes'] if change['kind']=='changed'))
    checkpoint=initial['checkpoint']
    suite.phase='sync-no-change'
    unchanged=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint))
    check(suite,'sync_nochange_incremental',unchanged['mode']=='incremental' and unchanged['changes']==[] and not unchanged['hasMore'])
    checkpoint=unchanged['checkpoint']
    changed_href=href+'delta.ics'
    resource=event('rfc-delta','20270110T143000Z','20270110T144500Z',summary='RFC delta first')
    etag=put(suite,changed_href,resource)
    suite.phase='sync-created'
    created=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint,pageSize=100))
    check(suite,'sync_created_delta_only',[(c['href'],c['kind']) for c in created['changes']]==[(changed_href,'changed')])
    check(suite,'sync_created_etag_matches_server',created['changes'][0]['etag']==etag)
    checkpoint=created['checkpoint']
    etag=put(suite,changed_href,resource.replace('RFC delta first','RFC delta updated'),etag)
    suite.phase='sync-updated'
    updated=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint,pageSize=100))
    check(suite,'sync_updated_delta_only',[(c['href'],c['kind']) for c in updated['changes']]==[(changed_href,'changed')])
    check(suite,'sync_updated_etag_matches_server',updated['changes'][0]['etag']==etag)
    checkpoint=updated['checkpoint']
    status,_,_=request(suite.state,'DELETE',urllib.parse.urlparse(changed_href).path)
    check(suite,'fixture_delta_deleted',status in (200,204))
    suite.owned.discard(changed_href)
    suite.phase='sync-removed'
    removed=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint,pageSize=100))
    check(suite,'sync_removed_delta_only',removed['changes']==[dict(href=changed_href,kind='removed')])
    checkpoint=removed['checkpoint']
    suite.phase='sync-checkpoint-validation'
    invalid=('A' if checkpoint[0]!='A' else 'B')+checkpoint[1:]
    for _ in range(3):
        error=await suite.call('calendar_resources.changes',dict(checkpoint=invalid),_expected='sync_reset_required')
        check(suite,'invalid_checkpoint_no_advancement','checkpoint' not in error)
    await suite.call('calendar_resources.changes',dict(calendarHref=href,checkpoint=checkpoint),_expected='invalid_input')
    resumed=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint))
    check(suite,'valid_checkpoint_survives_invalid_attempts',resumed['changes']==[] and resumed['mode']=='incremental')
    suite.new_checkpoint=await batch_delta(suite,href,resumed['checkpoint'])
    suite.phase='sync-small-page'
    small=await suite.call('calendar_resources.changes',dict(calendarHref=href,pageSize=1),_expected=('success','limit_exhausted'))
    if small.get('outcome')=='success':
        members=set()
        pages=0
        while True:
            pages+=1
            check(suite,'small_page_size_respected',len(small['changes'])<=1 and small['mode']=='initial')
            members.update(c['href'] for c in small['changes'] if c['kind']=='changed')
            if not small['hasMore']:
                break
            check(suite,'small_page_progress_bounded',pages<=20)
            small=await suite.call('calendar_resources.changes',dict(checkpoint=small['checkpoint'],pageSize=1))
        check(suite,'small_page_complete_inventory',members==expected_members)
    else:
        check(suite,'limit_failure_has_no_checkpoint','checkpoint' not in small)


async def batch_delta(suite, href, checkpoint):
    owned=[]
    try:
        for index in range(3):
            target=href+'batch-'+str(index)+'.ics'
            put(suite,target,event('rfc-batch-'+str(index),'20270110T140000Z','20270110T141000Z'))
            owned.append(target)
        suite.phase='sync-batch-small-page'
        small=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint,pageSize=1),
                               _expected=('success','limit_exhausted'))
        if small.get('outcome')=='success':
            check(suite,'limited_delta_does_not_silently_skip_changes',small['hasMore'])
        else:
            check(suite,'limited_delta_failure_has_no_checkpoint','checkpoint' not in small)
        suite.phase='sync-batch-larger-retry'
        complete=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint,pageSize=100))
        check(suite,'larger_retry_retains_all_three_changes',
              set(c['href'] for c in complete['changes'] if c['kind']=='changed')==set(owned)
              and len(complete['changes'])==3 and not complete['hasMore'])
        suite.phase='sync-batch-no-change'
        unchanged=await suite.call('calendar_resources.changes',dict(checkpoint=complete['checkpoint']))
        check(suite,'batch_checkpoint_has_no_missing_delta',unchanged['changes']==[] and not unchanged['hasMore'])
        return unchanged['checkpoint']
    finally:
        for target in owned:
            status,_,_=request(suite.state,'DELETE',urllib.parse.urlparse(target).path)
            check(suite,'batch_fixture_removed',status in (200,204))
            suite.owned.discard(target)


async def scheduling_guard(suite, href):
    suite.phase='scheduling-guard'
    target=href+'participation.ics'
    suite.owned.add(target)
    resource=event('rfc-participation','20270110T143000Z','20270110T144500Z',
                   ('ORGANIZER:mailto:organizer@example.invalid','ATTENDEE:mailto:attendee@example.invalid'))
    expected='success' if suite.state.get('server','radicale')=='radicale' else 'unsupported_capability'
    result=await suite.call('calendar_resources.exact_create',dict(destinationHref=target,utf8Resource=resource),_expected=expected)
    if expected=='success':
        suite.authoritative(target,'ORGANIZER:mailto:organizer@example.invalid')
    else:
        check(suite,'scheduling_write_not_attempted',result['mutationState']=='not_attempted')
        suite.authoritative(target,absent=True)
        suite.owned.discard(target)


async def new_operations(suite, include_metadata=True):
    suite.phase='protocol-fixture'
    href=suite.href(suite.state.get('protocol_calendar_path','native-operations-'+secrets.token_hex(4)+'/'))
    suite.owned.add(href)
    await suite.call('calendars.create',dict(displayName='RFC native operations',entityKinds=['event'],destinationHref=href))
    if include_metadata:
        await metadata(suite,href)
    members=await seed_and_busy(suite,href)
    await changes(suite,href,members)
    await scheduling_guard(suite,href)
    suite.phase='scenario'


async def checkpoint_restart(checkpoint, assembly, env, output):
    import json
    from driver import Client
    from functional import Functional
    output.mkdir()
    isolated=dict(env,OTEL_SERVICE_NAME=env['OTEL_SERVICE_NAME']+'-checkpoint-restart')
    async with Client(assembly,isolated) as client:
        suite=Functional(output,client,{})
        suite.phase='checkpoint-new-process'
        response=await suite.call('calendar_resources.changes',dict(checkpoint=checkpoint),_expected='sync_reset_required')
        check(suite,'checkpoint_rejected_by_new_mcp_process','checkpoint' not in response)
    (output/'functional-process.json').write_text(json.dumps(client.identity,indent=2))


async def run_protocol(root, assembly, output, reports_only, exact_scope=False):
    import json
    from driver import Client, environment
    from functional import Functional, configure_scope
    output.mkdir(parents=True,exist_ok=False)
    state=json.loads((root/'infra-private.json').read_text())
    if exact_scope:
        configure_scope(state)
    service='caldav-rfc-'+state.get('server','radicale')+'-'+output.name
    async with Client(assembly,environment(state,service,exact=True)) as client:
        suite=Functional(output,client,state)
        try:
            await new_operations(suite,include_metadata=not reports_only)
        finally:
            for href in sorted(suite.owned,key=len,reverse=True):
                status,_,_=request(state,'DELETE',urllib.parse.urlparse(href).path)
                assert status in (200,204,404)
    client.identity['service_name']=service
    (output/'functional-process.json').write_text(json.dumps(client.identity,indent=2))
    if getattr(suite,'new_checkpoint',None):
        await checkpoint_restart(suite.new_checkpoint,assembly,environment(state,service),output/'checkpoint-restart')


if __name__=='__main__':
    import argparse
    import asyncio
    from pathlib import Path
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root',type=Path)
    parser.add_argument('assembly',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--reports-only',action='store_true')
    parser.add_argument('--exact-scope',action='store_true')
    args=parser.parse_args()
    asyncio.run(run_protocol(args.root,args.assembly,args.output,args.reports_only,args.exact_scope))
