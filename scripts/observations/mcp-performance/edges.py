#!/usr/bin/env python3
"""Read controls, corpus scales, expected limits and telemetry lifecycle probes."""
import argparse
import asyncio
import json
from pathlib import Path
import shutil
from driver import Client, environment, query
from infra import request, resource, verify, expected_counts


def record_scale(remember,record,**metadata):
    remember(dict(record,**metadata,scale=True))
    if record['outcome']!='success' or record.get('is_error'):
        raise RuntimeError('Scale query failed: '+record['outcome'])


async def run(root,baseline,candidate):
    expected=expected_counts(root)
    if expected['todos']<2:
        raise RuntimeError('The limit matrix requires at least two seeded todos; cleanup supports any seed count')
    state=json.loads((root/'infra-private.json').read_text());records=[]
    def remember(value):
        records.append(value)
        with (root/'edges.jsonl').open('a') as stream:
            stream.write(json.dumps(value)+'\n')
    for label,assembly in [('baseline',baseline),('candidate',candidate)]:
        for otlp in [True,False,'unavailable']:
            env=environment(state,f'caldav-perf-controls-{label}-{otlp}',otlp=bool(otlp),exact=True)
            if otlp=='unavailable':
                env['OTEL_EXPORTER_OTLP_ENDPOINT']='http://127.0.0.1:1'
            async with Client(assembly,env) as c:
                for tool in ['calendars.list','calendar_resources.get','calendar_resources.exact_get']:
                    args={} if tool=='calendars.list' else dict(href=state['url']+'/perftest/todos/0001.ics')
                    for i in range(25):
                        _,record=await c.call(tool,args)
                        assert record['outcome']=='success'
                        remember(dict(record,label=label,otlp=otlp,warmup=i<5))
            remember(dict(label=label,otlp=otlp,process=c.identity))
            # No-request EOF must leave both streams empty, including collector failure.
            p=await asyncio.create_subprocess_exec(shutil.which('dotnet'),str(assembly),env=env,
                    stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
            out,err=await asyncio.wait_for(p.communicate(b''),5)
            assert not out and not err and p.returncode==0
            remember(dict(label=label,otlp=otlp,no_request_eof_clean=True))
    path='/perftest/scale/';href=state['url']+path
    body='''<D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:set><D:prop>
<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>Scale fixture</D:displayname>
<C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set>
</D:prop></D:set></D:mkcol>'''
    assert request(state,'MKCOL',path,body)[0]==201
    try:
        previous=0
        for count in [1,50,200]:
            for index in range(previous,count):
                assert request(state,'PUT',path+f'{index:04d}.ics',resource('VEVENT',index),
                               {'Content-Type':'text/calendar','If-None-Match':'*'})[0]==201
            previous=count
            for label,assembly in [('baseline',baseline),('candidate',candidate)]:
                async with Client(assembly,environment(state,'caldav-perf-scale-'+label)) as c:
                    for tool in ['calendar_entities.query','calendar_occurrences.query']:
                        args=query(tool,200);args['scope']=dict(mode='selected',calendar=dict(by='href',href=href))
                        if tool=='calendar_entities.query':args['entityKinds']=['event']
                        _,record=await c.call(tool,args)
                        record_scale(remember,record,label=label,corpus_resources=count)
        # Include the requested seeded collection size without reseeding it.
        async with Client(candidate,environment(state,'caldav-perf-limits')) as c:
            args=query('calendar_entities.query',200)
            args.update(scope=dict(mode='selected',calendar=dict(by='href',href=state['url']+'/perftest/events/')),
                        entityKinds=['event'])
            _,record=await c.call('calendar_entities.query',args)
            record_scale(remember,record,corpus_resources=expected['events'],label='candidate')
        over=resource('VEVENT',10000).replace('RRULE:FREQ=WEEKLY;COUNT=20','RRULE:FREQ=MINUTELY;COUNT=6000')
        assert request(state,'PUT',path+'over-limit.ics',over,{'Content-Type':'text/calendar'})[0]==201
        for label,assembly in [('baseline',baseline),('candidate',candidate)]:
            async with Client(assembly,environment(state,'caldav-perf-limits-'+label)) as c:
                args=query('calendar_occurrences.query',5)
                args['scope']=dict(mode='selected',calendar=dict(by='href',href=href))
                response,record=await c.call('calendar_occurrences.query',args)
                code=response['result']['structuredContent'].get('code')
                assert code=='limit_exhausted',response
                remember(dict(record,label=label,expected_limit=True,code=code))
    finally:
        assert request(state,'DELETE',path)[0] in (200,204)
    # Demonstrate the actual finite store boundary separately from useful throughput.
    for label,assembly in [('baseline',baseline),('candidate',candidate)]:
        async with Client(assembly,environment(state,'caldav-perf-store-'+label)) as c:
            args=query('todos.query',1,bounded=False)
            args['scope']=dict(mode='selected',calendar=dict(by='href',href=state['url']+'/perftest/todos/'))
            for index in range(17):
                response,record=await c.call('todos.query',args)
                code=response['result']['structuredContent'].get('code')
                assert record['outcome']=='success' if index<16 else code=='busy'
                remember(dict(record,label=label,store_index=index,expected_limit=index==16,code=code))
    assert verify(state)==expected
    (root/'edges.json').write_text(json.dumps(records,indent=2))
    print('Read controls, scales, limits, EOF and collector failure passed',flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('baseline',type=Path);p.add_argument('candidate',type=Path)
    a=p.parse_args();asyncio.run(run(a.root,a.baseline,a.candidate))
