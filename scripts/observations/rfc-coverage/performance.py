#!/usr/bin/env python3
"""Cold/warm observations for new tools, with exact HTTP payload counts."""
import argparse
import asyncio
import collections
from contextlib import nullcontext
import datetime as dt
import json
from pathlib import Path
import secrets

from driver import Client, environment
from functional import Functional
from http_observer import HttpObservationProxy
from infra import request
from protocol import event


OPERATIONS = ('calendars.inspect', 'calendars.patch', 'calendars.free_busy', 'calendar_resources.changes')


def seed(state, count):
    path = state['home_path']+'native-performance-'+secrets.token_hex(4)+'/'
    body = '<C:mkcalendar xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:D="DAV:"><D:set><D:prop><D:displayname>RFC performance</D:displayname></D:prop></D:set></C:mkcalendar>'
    assert request(state, 'MKCALENDAR', path, body)[0] == 201
    start = dt.datetime(2027, 1, 1, tzinfo=dt.timezone.utc)
    for index in range(count):
        at = start+dt.timedelta(minutes=index*15)
        data = event('rfc-benchmark-'+str(index),at.strftime('%Y%m%dT%H%M%SZ'),
                     (at+dt.timedelta(minutes=10)).strftime('%Y%m%dT%H%M%SZ'))
        assert request(state, 'PUT', path+str(index)+'.ics', data,
                       {'Content-Type':'text/calendar','If-None-Match':'*'})[0] == 201
    return path


def arguments(tool, href):
    if tool == 'calendars.patch':
        return dict(calendarHref=href,patch={'description':dict(operation='set',value='RFC bounded benchmark')})
    if tool == 'calendars.free_busy':
        return dict(calendarHref=href,**{'from':'2027-01-01T00:00:00Z','to':'2027-02-01T00:00:00Z'})
    if tool == 'calendar_resources.changes':
        return dict(calendarHref=href,pageSize=500)
    return dict(calendarHref=href)


async def measured(suite, proxy, tool, args, expected):
    before = proxy.snapshot() if proxy else None
    result = await suite.call(tool,args,_expected=expected)
    if proxy is None:
        return result
    observed = proxy.since(before)
    suite.records[-1].update(http_observed_requests=len(observed),
        http_request_body_bytes=sum(r['request_body_bytes'] for r in observed),
        http_response_body_bytes=sum(r['response_body_bytes'] for r in observed),
        http_observed_methods=dict(collections.Counter(r['method'] for r in observed)))
    suite.save()
    return result


async def run(root, assembly, output, resource_count, cold_runs, warm_runs, selected_scope, direct=False, operations=None):
    output.mkdir(parents=True,exist_ok=False)
    state = json.loads((root/'infra-private.json').read_text())
    path = seed(state,resource_count)
    records, processes, http_records = [], [], []
    operations = operations or OPERATIONS
    service = 'caldav-rfc-'+state.get('server','radicale')+'-'+output.name
    try:
        with (nullcontext(None) if direct else HttpObservationProxy(state)) as proxy:
            connection = state if direct else proxy.state
            href = connection['url']+path
            for scope in (('discovery','explicit') if selected_scope=='both' else (selected_scope,)):
                for tool in operations:
                    expected = 'upstream_protocol_error' if tool=='calendars.free_busy' and state.get('server','radicale')=='radicale' else 'success'
                    for run_index in range(cold_runs):
                        env = environment(connection,service)
                        if scope=='explicit':
                            env['CALDAV_CALENDAR_HREFS']=href
                        async with Client(assembly,env) as client:
                            suite = Functional(output,client,connection)
                            suite.records = records
                            suite.phase=scope+'-cold'
                            args=arguments(tool,href)
                            result=await measured(suite,proxy,tool,args,expected)
                            suite.phase=scope+'-warm'
                            for _ in range(warm_runs):
                                result=await measured(suite,proxy,tool,args,expected)
                            if tool=='calendar_resources.changes':
                                assert len(result['changes'])==resource_count and not result['hasMore']
                                suite.phase=scope+'-incremental-no-change'
                                for _ in range(warm_runs):
                                    result=await measured(suite,proxy,tool,dict(checkpoint=result['checkpoint'],pageSize=500),'success')
                                    assert result['changes']==[] and not result['hasMore']
                        processes.append(dict(client.identity,scope=scope,tool=tool,cold_run=run_index))
            http_records=proxy.records.copy() if proxy else []
    finally:
        assert request(state,'DELETE',path)[0] in (200,204)
        (output/'processes.json').write_text(json.dumps(processes,indent=2))
        if not direct:
            (output/'http-payload-observations.json').write_text(json.dumps(http_records,indent=2))
    (output/'scenario.json').write_text(json.dumps(dict(resources=resource_count,cold_runs=cold_runs,
        warm_runs_per_process=warm_runs,service_name=service,scope=selected_scope,direct=direct,operations=operations,
        timing_note=('MCP connects directly to the fixture. HTTP attempts come from Aspire; no HTTP payload byte capture.' if direct else
                     'A loopback observer buffers HTTP responses to measure payload bytes; its connection handling adds timing overhead.')+
                    ' Setup and model inference excluded.'),indent=2))
    print(json.dumps(dict(calls=len([r for r in records if 'tool' in r]),processes=len(processes),http_requests=None if direct else len(http_records))))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root',type=Path)
    parser.add_argument('assembly',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--resources',type=int,default=100)
    parser.add_argument('--cold-runs',type=int,default=3)
    parser.add_argument('--warm-runs',type=int,default=5)
    parser.add_argument('--scope',choices=['both','discovery','explicit'],default='both')
    parser.add_argument('--direct',action='store_true',help='Connect without the HTTP payload observer for latency measurements.')
    parser.add_argument('--operation',choices=OPERATIONS,action='append',help='Measure only this operation; repeat to select several.')
    args=parser.parse_args()
    if not 1<=args.resources<=500 or args.cold_runs<1 or args.warm_runs<1:
        parser.error('Use 1-500 resources and positive cold/warm run counts.')
    asyncio.run(run(args.root,args.assembly,args.output,args.resources,args.cold_runs,args.warm_runs,args.scope,args.direct,args.operation))
