#!/usr/bin/env python3
"""Join each measured MCP call to its Aspire operation trace and report distributions."""
import argparse
import collections
import json
import math
from pathlib import Path
import statistics


def distribution(values):
    values = sorted(values)
    return dict(count=len(values), min=min(values), p50=statistics.median(values),
                p95=values[max(0, math.ceil(len(values)*0.95)-1)], max=max(values))


def summarize(directory, traces_path):
    calls = [r for r in json.loads((directory / 'functional.json').read_text()) if 'tool' in r]
    traces = json.loads(traces_path.read_text())
    used = set()
    groups = collections.defaultdict(list)
    for call in calls:
        start = call['timestamp_ns']
        end = start + call['elapsed_ms']*1e6
        matches = [t for t in traces if t['attributes'].get('caldav.tool.name') == call['tool']
                   and start <= int(t['start_ns']) <= end]
        if len(matches) != 1:
            raise ValueError(f"Expected exactly one Aspire trace for {call['tool']}; found {len(matches)}")
        trace = matches[0]
        if 'http_observed_requests' in call and call['http_observed_requests'] != sum(trace['http'].values()):
            raise ValueError('HTTP observer and Aspire disagree on attempt count')
        if trace['trace_id'] in used:
            raise ValueError('An Aspire operation trace matched two calls')
        used.add(trace['trace_id'])
        groups[(call['tool'], call.get('phase', 'scenario'), call.get('continuation_round', 0))].append((call, trace))
    result = []
    for (tool, phase, continuation), group in sorted(groups.items()):
        row=dict(tool=tool, phase=phase, continuation_round=continuation,
            wall_ms=distribution([c['elapsed_ms'] for c, _ in group]),
            cpu_ms=distribution([c['cpu_ms'] for c, _ in group]),
            response_bytes=distribution([c['response_bytes'] for c, _ in group]),
            http_requests=distribution([sum(t['http'].values()) for _, t in group]),
            http_methods=dict(sum((collections.Counter(t['http']) for _, t in group),collections.Counter())),
            operation_ms=distribution([t['operation_ms'] for _, t in group]),
            max_rss_bytes=max(c['rss_bytes'] for c, _ in group),
            outcomes=dict(collections.Counter(c['outcome'] for c, _ in group)),
            retries=sum(t['retries'] for _, t in group))
        if all('http_request_body_bytes' in c for c,_ in group):
            row['http_request_body_bytes']=distribution([c['http_request_body_bytes'] for c,_ in group])
            row['http_response_body_bytes']=distribution([c['http_response_body_bytes'] for c,_ in group])
        result.append(row)
    output = dict(calls=len(calls), matched_operation_traces=len(used), operations=result,
                  timing_scope='MCP wire round trip; excludes fixture setup and model inference')
    (directory / 'measurements.json').write_text(json.dumps(output, indent=2))
    print(json.dumps(dict(calls=len(calls), matched_operation_traces=len(used), groups=len(result))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('traces', type=Path)
    args = parser.parse_args()
    summarize(args.directory, args.traces)
