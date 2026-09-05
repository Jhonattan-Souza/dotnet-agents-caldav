#!/usr/bin/env python3
"""Export Aspire's supported telemetry API and reject truncated captures."""
import argparse
import collections
import json
from pathlib import Path
import subprocess
import urllib.parse
import urllib.request


def fetch(state, endpoint, **params):
    url = state['dashboard'] + '/api/telemetry/' + endpoint + '?' + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={'x-api-key':state['api_key']})
    with urllib.request.urlopen(req, timeout=60) as response:
        return json.load(response)


def spans(data):
    for resource in data['data']['resourceSpans']:
        resource_tags=attributes(resource['resource'])
        for scope in resource['scopeSpans']:
            for span in scope['spans']:
                yield dict(span, resource_service=resource_tags.get('service.name'),
                           resource_instance=resource_tags.get('service.instance.id'))


def attributes(span):
    return {a['key']:next(iter(a['value'].values())) for a in span.get('attributes',[])}


def capture(state, resource):
    if resource:
        matches = [r for r in fetch(state,'resources') if resource in (r['name'],r['displayName'])]
        if not matches:
            raise RuntimeError('Requested telemetry resource is absent')
        selectors = [r['displayName'] for r in matches]
    else:
        selectors = [None]
    captures = []
    for selector in selectors:
        params = dict(limit=1000000)
        if selector:
            params['resource'] = selector
        data = fetch(state,'spans',**params)
        if data['totalCount'] != data['returnedCount']:
            raise RuntimeError(f"Truncated export: {data['returnedCount']} of {data['totalCount']}")
        captures.append(data)
    merged = dict(data={'resourceSpans':[r for c in captures for r in c['data']['resourceSpans']]},
                  totalCount=sum(c['totalCount'] for c in captures),
                  returnedCount=sum(c['returnedCount'] for c in captures))
    identities = [(s['traceId'],s['spanId']) for s in spans(merged)]
    if len(identities) != len(set(identities)) or len(identities) != merged['returnedCount']:
        raise RuntimeError('Telemetry export contains duplicate spans or inconsistent counts')
    return merged


def export(root, name, resource=None):
    state = json.loads((root/'infra-private.json').read_text())
    data = capture(state,resource)
    (root/(name+'-spans.json')).write_text(json.dumps(data))
    traces = collections.defaultdict(list)
    for span in spans(data):
        traces[span['traceId']].append(span)
    summaries = []
    for trace, group in traces.items():
        operations = [s for s in group if s['name']=='caldav.operation']
        if not operations:
            continue
        operation = operations[0]
        duration = lambda s:(int(s['endTimeUnixNano'])-int(s['startTimeUnixNano']))/1e6
        phases = collections.defaultdict(float)
        http = collections.Counter()
        statuses = collections.Counter()
        retries = 0
        error_spans = 0
        for span in group:
            tags = attributes(span)
            if 'http.request.method' in tags:
                http[tags['http.request.method']] += 1
                statuses[str(tags.get('http.response.status_code','unavailable'))] += 1
                retries += int(tags.get('http.request.resend_count',0)) > 0
            error_spans += span.get('status',{}).get('code') in (2,'STATUS_CODE_ERROR')
            if span['name'].startswith(('caldav.phase', 'caldav.query.phase')):
                phases[span['name']] += duration(span)
        summaries.append(dict(trace_id=trace, start_ns=operation['startTimeUnixNano'],
            service=operation['resource_service'], instance=operation['resource_instance'],
            operation_ms=duration(operation), outer_ms=max(map(duration,group)),
            http=dict(http), http_statuses=dict(statuses), retries=retries, error_spans=error_spans,
            phases=dict(phases), attributes=attributes(operation)))
    (root/(name+'-traces.json')).write_text(json.dumps(summaries,indent=2))
    print(json.dumps(dict(export=name, spans=data['returnedCount'], traces=len(summaries))))
    return summaries


def cli_export(root, name):
    state = json.loads((root/'infra-private.json').read_text())
    command = ['aspire','export','--dashboard-url',state['dashboard'],'--api-key',state['api_key'],
               '--output',str(root/(name+'.zip')),'--non-interactive']
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    text = result.stdout.decode().replace(state['api_key'],'[redacted]')
    (root/(name+'-export.log')).write_text(text)
    if result.returncode:
        raise RuntimeError('Aspire CLI export failed; inspect sanitized log')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root',type=Path)
    parser.add_argument('name')
    parser.add_argument('--resource')
    parser.add_argument('--zip',action='store_true')
    args = parser.parse_args()
    export(args.root,args.name,args.resource)
    if args.zip:
        cli_export(args.root,args.name)
