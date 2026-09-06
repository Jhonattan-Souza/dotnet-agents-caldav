#!/usr/bin/env python3
"""Diagnostic EventPipe capture, separate from the latency comparison."""
import argparse
import asyncio
from contextlib import suppress
import json
from pathlib import Path
from driver import Client,environment,query
from build_manifest import prepared_input


def save_record(path,records,record,warmup):
    records.append(dict(record,warmup=warmup))
    path.write_text(json.dumps(records,indent=2))
    if record['outcome']!='success' or record.get('is_error'):
        raise RuntimeError('Profile call failed: '+record['outcome'])


async def stop_collectors(collectors):
    for process in collectors:
        if process.returncode is None:
            with suppress(ProcessLookupError):
                process.terminate()
            try:
                await asyncio.wait_for(process.wait(),5)
            except TimeoutError:
                with suppress(ProcessLookupError):
                    process.kill()
                await process.wait()


async def capture(a,client,cursor,records,samples):
    collectors=[]
    with (a.root/(a.name+'-profile.log')).open('w') as log:
        try:
            collectors.append(await asyncio.create_subprocess_exec(str(a.profilers/'dotnet-trace'),'collect',
                '--process-id',str(client.process.pid),'--profile','dotnet-sampled-thread-time',
                '--duration','00:00:20','--output',str(a.root/(a.name+'.nettrace')),stdout=log,stderr=log))
            collectors.append(await asyncio.create_subprocess_exec(str(a.profilers/'dotnet-counters'),'collect',
                '--process-id',str(client.process.pid),'--counters','System.Runtime','--format','json',
                '--duration','00:00:20','--output',str(a.root/(a.name+'-counters.json')),stdout=log,stderr=log))
            for _ in range(5 if a.mode=='start' else 60):
                arguments=query(a.tool,200) if a.mode=='start' else dict(cursor=cursor,pageSize=200)
                _,record=await client.call(a.tool,arguments)
                save_record(samples,records,record,False)
            for process in collectors:
                if await asyncio.wait_for(process.wait(),25)!=0:
                    raise RuntimeError('Diagnostic collector failed; inspect the profile log')
        finally:
            await stop_collectors(collectors)


async def run(a):
    build_input=prepared_input(a.root,a.assembly,a.build)
    a.assembly=Path(build_input['assembly'])
    suffixes=['-profile-samples.json','-profile-process.json','-profile.log','.nettrace','-counters.json']
    if any((a.root/(a.name+suffix)).exists() for suffix in suffixes):
        raise RuntimeError('Use a new profile name; preserve previous attempts')
    state=json.loads((a.root/'infra-private.json').read_text())
    samples=a.root/(a.name+'-profile-samples.json')
    records=[]
    client=Client(a.assembly,environment(state,'caldav-perf-profile-'+a.name))
    completed=False
    try:
        async with client as c:
            for _ in range(5):
                response,record=await c.call(a.tool,query(a.tool,200))
                save_record(samples,records,record,True)
            cursor=response['result']['structuredContent']['pagination']['nextCursor']
            if a.mode=='continue' and cursor is None:
                raise RuntimeError('Continue profiling requires a paginated result')
            await capture(a,c,cursor,records,samples)
        completed=True
    finally:
        if hasattr(client,'identity'):
            (a.root/(a.name+'-profile-process.json')).write_text(json.dumps(
                dict(client.identity,profile_completed=completed,build_input=build_input),indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('assembly',type=Path);p.add_argument('profilers',type=Path)
    p.add_argument('--name',required=True);p.add_argument('--tool',default='calendar_occurrences.query')
    p.add_argument('--build',choices=['baseline','candidate'],default='candidate')
    p.add_argument('--mode',choices=['start','continue'],default='continue')
    asyncio.run(run(p.parse_args()))
