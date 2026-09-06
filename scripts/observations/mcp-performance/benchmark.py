#!/usr/bin/env python3
"""Paired blocks with identical corpus/instrumentation, nearest-rank summaries."""
import argparse
import asyncio
import collections
from contextlib import AsyncExitStack
import hashlib
import json
import math
from pathlib import Path
import time
from driver import Client, environment, query
from telemetry import export
from build_manifest import load_builds, benchmark_inputs

TOOLS = ['calendar_entities.query', 'calendar_occurrences.query', 'todos.query']


def validate_args(args):
    if args.compare_otlp and args.no_otlp:
        raise ValueError('compare-otlp and no-otlp are mutually exclusive')
    for name in ['blocks', 'samples', 'cohort_samples']:
        if getattr(args, name) <= 0:
            raise ValueError(name.replace('_', '-') + ' must be positive')
    if args.mode == 'start':
        if args.cohort_samples > 11:
            raise ValueError('Start allows at most 11 cohort-samples plus five warmups within 16 snapshots')
        if args.samples % args.cohort_samples:
            raise ValueError('Start samples must be divisible by cohort-samples')
        if args.topology != 'single_session' or args.concurrency != 1:
            raise ValueError('Start supports only serial single_session cohorts')


def append(path, value):
    with path.open('a') as stream:
        stream.write(json.dumps(value)+'\n')


def record_sample(args, base, record, warmup, size, references):
    record=dict(record)
    if base['topology']=='single_session' and base['concurrency']>1:
        record['cpu_ms']=None
        record['cpu_measurement_scope']='unavailable: overlapping calls share the process'
    else:
        record['cpu_measurement_scope']='process delta for one nonoverlapping request'
    append(args.root/(args.name+'-samples.jsonl'), dict(base, **record, warmup=warmup, size=size))
    if record['outcome'] != 'success':
        raise RuntimeError(f'{base["label"]} {base["tool"]}: {record["outcome"]}')
    if not warmup:
        key = (base['tool'], size, base['block'])
        digest = (record['item_sha256'], record['items'])
        if key in references and references[key] != digest:
            raise RuntimeError('Result content/order differs between samples or compared builds')
        references[key] = digest


async def cohort(args, label, tool, block, cohort_index, references):
    state = json.loads((args.root/'infra-private.json').read_text())
    service = f'caldav-perf-{args.name}-{label}-{tool.split(".")[0]}'
    otlp = not args.no_otlp and (not args.compare_otlp or label=='baseline')
    env = environment(state, service, otlp=otlp)
    assembly = getattr(args,label)
    async with Client(assembly, env) as client:
        catalog = await client.request('tools/list', {})
        assert set(TOOLS).issubset(t['name'] for t in catalog['result']['tools'])
        base = dict(label=label, tool=tool, block=block, cohort=cohort_index, mode=args.mode,
                    concurrency=args.concurrency, topology='single_session', service=service,
                    otlp=otlp, pid=client.process.pid)
        if args.mode == 'start':
            for i in range(5+args.cohort_samples):
                response, record = await client.call(tool, query(tool, args.page_size))
                record_sample(args,base,record,i<5,args.page_size,references)
        else:
            response, setup = await client.call(tool, query(tool, 1))
            assert setup['outcome']=='success', setup
            cursor = response['result']['structuredContent']['pagination']['nextCursor']
            for size in args.sizes:
                call_args = dict(cursor=cursor,pageSize=size)
                # Output validation tiers up later than five calls in the pilot.
                for _ in range(20):
                    for _, record in await asyncio.gather(*(client.call(tool, call_args)
                            for _ in range(args.concurrency))):
                        record_sample(args,base,record,True,size,references)
                reference = None
                start = time.perf_counter_ns()
                for offset in range(0,args.samples,args.concurrency):
                    replies = await asyncio.gather(*(client.call(tool,call_args)
                        for _ in range(min(args.concurrency,args.samples-offset))))
                    for response,record in replies:
                        record_sample(args,base,record,False,size,references)
                        value = response['result']['structuredContent']
                        digest = hashlib.sha256(json.dumps(value,sort_keys=True).encode()).hexdigest()
                        if reference is None:
                            reference = digest
                        assert reference == digest, 'Replay content/order changed'
                append(args.root/(args.name+'-batches.jsonl'),dict(base,size=size,
                    successful_ops=args.samples, elapsed_ms=(time.perf_counter_ns()-start)/1e6))
    append(args.root/(args.name+'-processes.jsonl'),dict(base,**client.identity))


async def process_cohort(args,label,tool,block,references):
    state=json.loads((args.root/'infra-private.json').read_text())
    service=f'caldav-perf-{args.name}-{label}-{tool.split(".")[0]}'
    clients=[];cursors=[]
    base=dict(label=label,tool=tool,block=block,cohort=0,mode='continue',
              concurrency=args.concurrency,topology='processes',service=service,otlp=not args.no_otlp)
    async with AsyncExitStack() as stack:
        for _ in range(args.concurrency):
            c=await stack.enter_async_context(Client(getattr(args,label),environment(state,service,otlp=not args.no_otlp)))
            response,record=await c.call(tool,query(tool,1))
            assert record['outcome']=='success'
            clients.append(c);cursors.append(response['result']['structuredContent']['pagination']['nextCursor'])
        for size in args.sizes:
            async def call(index):
                response,record=await clients[index].call(tool,dict(cursor=cursors[index],pageSize=size))
                return response,dict(record,pid=clients[index].process.pid)
            for _ in range(20):
                for _,record in await asyncio.gather(*(call(i) for i in range(args.concurrency))):
                    record_sample(args,base,record,True,size,references)
            start=time.perf_counter_ns()
            for offset in range(0,args.samples,args.concurrency):
                for _,record in await asyncio.gather(*(call(i) for i in range(min(args.concurrency,args.samples-offset)))):
                    record_sample(args,base,record,False,size,references)
            append(args.root/(args.name+'-batches.jsonl'),dict(base,size=size,successful_ops=args.samples,
                   elapsed_ms=(time.perf_counter_ns()-start)/1e6))
    for c in clients:
        append(args.root/(args.name+'-processes.jsonl'),dict(base,**c.identity))


async def run(args):
    args.baseline=args.baseline.resolve()
    args.candidate=args.candidate.resolve()
    output=args.root/(args.name+'-samples.jsonl')
    if output.exists():
        raise RuntimeError('Use a new run name; never overwrite previous samples')
    if args.compare_otlp:
        assert args.baseline.read_bytes()==args.candidate.read_bytes()
        assert args.topology=='single_session'
    manifest={k:str(v) if isinstance(v,Path) else v for k,v in vars(args).items()}
    manifest['build_inputs']=benchmark_inputs(load_builds(args.root),args.baseline,args.candidate,args.compare_otlp)
    manifest['harness_sha256']={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                               for p in Path(__file__).parent.glob('*.py')}
    (args.root/(args.name+'-manifest.json')).write_text(json.dumps(manifest,indent=2))
    references={}
    for block in range(args.blocks):
        for tool in args.tools:
            cohorts = args.samples//args.cohort_samples if args.mode=='start' else 1
            for index in range(cohorts):
                labels = ['baseline','candidate'] if (block+index)%2==0 else ['candidate','baseline']
                for label in labels:
                    if args.topology=='processes':
                        await process_cohort(args,label,tool,block,references)
                    else:
                        await cohort(args,label,tool,block,index,references)
                print(json.dumps(dict(run=args.name,block=block,tool=tool,cohort=index,complete=True)),flush=True)
            if not args.no_otlp:
                export(args.root,args.name+f'-b{block}-'+tool.split('.')[0])
    summarize(args.root,args.name)


def summarize(root,name):
    groups = collections.defaultdict(list)
    for line in (root/(name+'-samples.jsonl')).read_text().splitlines():
        row=json.loads(line)
        if not row['warmup']:
            groups[(row['label'],row['tool'],row['mode'],row['size'],row['concurrency'])].append(row)
    result=[]
    for key,rows in groups.items():
        times=sorted(r['elapsed_ms'] for r in rows)
        percentile=lambda p:times[math.ceil(p*len(times))-1]
        block_p95={str(b):sorted(r['elapsed_ms'] for r in rows if r['block']==b)[
            math.ceil(.95*sum(r['block']==b for r in rows))-1] for b in set(r['block'] for r in rows)}
        result.append(dict(label=key[0],tool=key[1],mode=key[2],size=key[3],concurrency=key[4],n=len(rows),
            p50_ms=percentile(.5),p95_ms=percentile(.95),p99_descriptive_ms=percentile(.99),
            min_ms=times[0],max_ms=times[-1],block_p95_ms=block_p95,
            errors=sum(r['is_error'] or r['outcome']!='success' for r in rows),
            timeouts=sum(r.get('timed_out',False) for r in rows),
            mean_cpu_ms=(sum(r['cpu_ms'] for r in rows)/len(rows)
                         if all(r['cpu_ms'] is not None for r in rows) else None),
            cpu_measurement_scope=rows[0]['cpu_measurement_scope'],
            max_rss_bytes=max(r['rss_bytes'] for r in rows),
            serial_service_ops_per_second=len(rows)*1000/sum(times)))
    (root/(name+'-summary.json')).write_text(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path)
    p.add_argument('baseline',type=Path)
    p.add_argument('candidate',type=Path)
    p.add_argument('--name',required=True)
    p.add_argument('--mode',choices=['start','continue'],default='continue')
    p.add_argument('--blocks',type=int,default=3)
    p.add_argument('--samples',type=int,default=100)
    p.add_argument('--cohort-samples',type=int,default=5)
    p.add_argument('--page-size',type=int,default=200)
    p.add_argument('--sizes',type=int,nargs='+',default=[1,5,200])
    p.add_argument('--concurrency',type=int,choices=[1,2,4],default=1)
    p.add_argument('--topology',choices=['single_session','processes'],default='single_session')
    p.add_argument('--tools',nargs='+',default=TOOLS)
    p.add_argument('--no-otlp',action='store_true')
    p.add_argument('--compare-otlp',action='store_true',help='Same assembly: baseline OTLP on, candidate OTLP off')
    args=p.parse_args()
    try:
        validate_args(args)
    except ValueError as error:
        p.error(str(error))
    asyncio.run(run(args))
