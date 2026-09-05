#!/usr/bin/env python3
"""Join measured windows to exported server traces; emit compact, sanitized evidence."""
import argparse
import collections
import json
import math
from pathlib import Path
import statistics
import xml.etree.ElementTree as ET
from build_manifest import load_builds


def percentile(values,p):
    ordered=sorted(values)
    return ordered[math.ceil(p*len(ordered))-1]


def lines(path):
    return [json.loads(line) for line in path.read_text().splitlines()]


def join_run(root,name,traces):
    summaries=json.loads((root/(name+'-summary.json')).read_text())
    samples=[r for r in lines(root/(name+'-samples.jsonl')) if not r['warmup']]
    batch_path=root/(name+'-batches.jsonl')
    batches=lines(batch_path) if batch_path.exists() else []
    for summary in summaries:
        rows=[r for r in samples if r['label']==summary['label'] and r['tool']==summary['tool']
              and r['size']==summary['size']]
        cohorts=collections.defaultdict(list)
        for row in rows:
            cohorts[(row['block'],row['cohort'])].append(row)
        matches={}
        for cohort in cohorts.values():
            if not cohort[0]['otlp']:
                continue
            low=min(r['timestamp_ns'] for r in cohort)
            high=max(r['timestamp_ns']+r['elapsed_ms']*1e6 for r in cohort)
            for trace in traces:
                if (trace['service']==cohort[0]['service']
                    and trace['attributes'].get('caldav.tool.name')==summary['tool']
                    and low<=int(trace['start_ns'])<=high):
                    matches[trace['trace_id']]=trace
        expected=len(rows) if rows[0]['otlp'] else 0
        assert len(matches)==expected,(name,summary['tool'],summary['size'],summary['label'],len(matches),expected)
        summary.update(run=name,topology=rows[0]['topology'],otlp=rows[0]['otlp'],
            trace_matches=len(matches),mean_stdio_response_bytes=statistics.mean(r['response_bytes'] for r in rows),
            distinct_item_hashes=sorted({r['item_sha256'] for r in rows}))
        matching_batches=[b for b in batches if b['label']==summary['label'] and b['tool']==summary['tool']
                          and b['size']==summary['size']]
        if matching_batches:
            summary['driver_ops_per_second']=1000*sum(b['successful_ops'] for b in matching_batches)/sum(
                b['elapsed_ms'] for b in matching_batches)
        if not matches:
            continue
        group=list(matches.values())
        http=collections.Counter();statuses=collections.Counter();phases=collections.defaultdict(list)
        for trace in group:
            http.update(trace['http']);statuses.update(trace['http_statuses'])
            for phase,value in trace['phases'].items():phases[phase].append(value)
        if summary['mode']=='continue':
            assert not http,(name,summary['tool'],'Continue performed HTTP')
        summary.update(server_mcp_p50_ms=percentile([t['outer_ms'] for t in group],.5),
            server_mcp_p95_ms=percentile([t['outer_ms'] for t in group],.95),
            operation_p50_ms=percentile([t['operation_ms'] for t in group],.5),
            operation_p95_ms=percentile([t['operation_ms'] for t in group],.95),
            http_attempt_totals=dict(http),http_status_totals=dict(statuses),
            retries=sum(t['retries'] for t in group),error_spans=sum(t['error_spans'] for t in group),
            mean_phase_ms={k:statistics.mean(v) for k,v in phases.items()},
            representative_trace_ids=[t['trace_id'] for t in sorted(group,key=lambda t:t['outer_ms'])[::max(1,len(group)//3)][:3]])
    processes=lines(root/(name+'-processes.jsonl'))
    for p in processes:
        assert p['assembly_mapped'] and p['exit_code']==0 and p['stderr_bytes']==0
    startup=[]
    for label in ['baseline','candidate']:
        group=[p for p in processes if p['label']==label]
        startup.append(dict(run=name,label=label,processes=len(group),
            startup_p50_ms=percentile([p['startup_ms'] for p in group],.5),
            startup_p95_ms=percentile([p['startup_ms'] for p in group],.95),
            shutdown_max_ms=max(p['shutdown_ms'] for p in group),
            assembly_sha256=sorted({p['sha256'] for p in group}),
            core_assembly_sha256=sorted({p['core_sha256'] for p in group}),
            negotiated_versions=sorted({v for p in group for v in p['discovery']['result']['supportedVersions']})))
    return summaries,startup


def main(a):
    builds=load_builds(a.root)
    traces=json.loads((a.root/'final-traces.json').read_text())
    results=[];startup=[]
    for name in a.runs:
        r,s=join_run(a.root,name,traces);results+=r;startup+=s
    allocation=[]
    for label in ['baseline','candidate']:
        source=json.loads((a.root/('schema-'+label+'.json')).read_text());samples=source['samples']
        allocation.append(dict(label=label,n=len(samples),payload_sha256=source['payloadSha256'],
            median_ms=statistics.median(s['elapsedMilliseconds'] for s in samples),
            median_allocated_bytes=statistics.median(s['allocatedBytes'] for s in samples),
            gc_collections={g:sum(s[g] for s in samples) for g in ['gen0','gen1','gen2']}))
    gates={}
    for path in sorted((a.root/a.gates).glob('*.trx')):
        gates[path.name]=ET.parse(path).getroot().find('.//{*}Counters').attrib
    coverage=ET.parse(a.root/a.gates/'coverage-report/Cobertura.xml').getroot().attrib
    for process in startup:
        for field,name in [('assembly_sha256','DotnetAgents.CalDav.Mcp.dll'),
                           ('core_assembly_sha256','DotnetAgents.CalDav.Core.dll')]:
            known={build['assembly_sha256'][name] for build in builds.values()}
            if not set(process[field]).issubset(known):
                raise RuntimeError('Measured process does not match either prepared build')
    result=dict(baseline_sha=builds['baseline']['source']['sha'],
        candidate_sha=builds['candidate']['source']['sha'],
        candidate_is_working_tree=builds['candidate']['source']['dirty'],builds=builds,
        percentile_estimator='nearest rank ceil(p*n)',p99_is_descriptive_only=True,
        full_evidence_directory=str(a.root),corpus=json.loads((a.root/'corpus.json').read_text()),
        measurements=results,processes=startup,schema_guard=allocation,gates=gates,
        coverage={k:coverage[k] for k in ['line-rate','branch-rate','lines-covered','lines-valid','branches-covered','branches-valid']})
    a.output.write_text(json.dumps(result,indent=2))
    print(f'Joined {sum(r["trace_matches"] for r in results)} measured operations without truncation')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('output',type=Path)
    p.add_argument('--gates',default='gates-final')
    p.add_argument('--runs',nargs='+',required=True)
    main(p.parse_args())
