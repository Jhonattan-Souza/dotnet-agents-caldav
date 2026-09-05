#!/usr/bin/env python3
"""Diagnostic EventPipe capture, separate from the latency comparison."""
import argparse
import asyncio
import json
from pathlib import Path
from driver import Client,environment,query


async def run(a):
    state=json.loads((a.root/'infra-private.json').read_text())
    records=[]
    async with Client(a.assembly,environment(state,'caldav-perf-profile-'+a.name)) as c:
        for _ in range(5):
            response,record=await c.call(a.tool,query(a.tool,200))
            assert record['outcome']=='success'
        cursor=response['result']['structuredContent']['pagination']['nextCursor']
        with (a.root/(a.name+'-profile.log')).open('w') as log:
            tracer=await asyncio.create_subprocess_exec(str(a.profilers/'dotnet-trace'),'collect',
                '--process-id',str(c.process.pid),'--profile','dotnet-sampled-thread-time',
                '--duration','00:00:20','--output',str(a.root/(a.name+'.nettrace')),stdout=log,stderr=log)
            counters=await asyncio.create_subprocess_exec(str(a.profilers/'dotnet-counters'),'collect',
                '--process-id',str(c.process.pid),'--counters','System.Runtime','--format','json',
                '--duration','00:00:20','--output',str(a.root/(a.name+'-counters.json')),stdout=log,stderr=log)
            for _ in range(10 if a.mode=='start' else 60):
                args=query(a.tool,200) if a.mode=='start' else dict(cursor=cursor,pageSize=200)
                _,record=await c.call(a.tool,args);records.append(record)
            assert await tracer.wait()==0
            assert await counters.wait()==0
    (a.root/(a.name+'-profile-samples.json')).write_text(json.dumps(records,indent=2))
    (a.root/(a.name+'-profile-process.json')).write_text(json.dumps(c.identity,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('assembly',type=Path);p.add_argument('profilers',type=Path)
    p.add_argument('--name',required=True);p.add_argument('--tool',default='calendar_occurrences.query')
    p.add_argument('--mode',choices=['start','continue'],default='continue')
    asyncio.run(run(p.parse_args()))
