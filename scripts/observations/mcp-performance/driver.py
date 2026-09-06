#!/usr/bin/env python3
"""Persistent JSON-RPC stdio driver. Measurements exclude fixture setup and inference."""
import asyncio
from contextlib import suppress
import hashlib
import json
import os
from pathlib import Path
import shutil
import time
from build_manifest import runtime_files

PROTOCOL = '2026-07-28'
REQUEST_TIMEOUT_SECONDS = 45
SHUTDOWN_TIMEOUT_SECONDS = 5
CAPABILITIES = {'elicitation': {'form': {}}}
WINDOW = {'from': {'kind': 'utcDateTime', 'value': '2026-07-01T00:00:00Z'},
          'to': {'kind': 'utcDateTime', 'value': '2026-12-31T00:00:00Z'}}


class Response(dict):
    """JSON response plus local wire accounting, outside the protocol object."""
    wire_bytes = 0


def environment(state, service, otlp=True, exact=False):
    env = {k:v for k,v in os.environ.items() if not k.startswith(('CALDAV_', 'OTEL_'))}
    env.update(CALDAV_URL=state['url'], CALDAV_USERNAME=state['username'], CALDAV_PASSWORD=state['password'],
               CALDAV_DEFAULT_EVENT_CALENDAR_NAME='Performance events',
               CALDAV_DEFAULT_TODO_CALENDAR_NAME='Performance todos',
               CALDAV_EVALUATION_TIME_ZONE='America/Sao_Paulo', CALDAV_INTEROPERABILITY_PROFILE='radicale-3.7.8',
               OTEL_SERVICE_NAME=service)
    if otlp:
        env.update(OTEL_EXPORTER_OTLP_ENDPOINT=state['otlp'], OTEL_EXPORTER_OTLP_PROTOCOL='http/protobuf')
    if exact:
        env['CALDAV_EXPOSE_EXACT_TOOLS'] = 'true'
    return env


def process_stats(pid):
    stat = Path(f'/proc/{pid}/stat').read_text().split()
    status = Path(f'/proc/{pid}/status').read_text().splitlines()
    mem = {k: int(line.split()[1]) * 1024 for line in status
           for k in ['VmRSS','VmHWM'] if line.startswith(k + ':')}
    return dict(cpu_seconds=(int(stat[13])+int(stat[14]))/os.sysconf('SC_CLK_TCK'), **mem)


class Client:
    def __init__(self, assembly, env):
        self.assembly = Path(assembly).resolve()
        self.env = env
        self.pending = {}
        self.sequence = 0
        self.notifications = 0

    async def __aenter__(self):
        start = time.perf_counter_ns()
        self.process = await asyncio.create_subprocess_exec(str(Path(shutil.which('dotnet')).resolve()),
            str(self.assembly), stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, env=self.env, limit=16*1024*1024)
        self.reader = asyncio.create_task(self.read())
        self.stderr = asyncio.create_task(self.process.stderr.read())
        try:
            return await self.initialize(start)
        except BaseException:
            await asyncio.shield(self.abort_startup())
            raise

    async def abort_startup(self):
        self.process.stdin.close()
        try:
            await asyncio.wait_for(self.process.wait(), SHUTDOWN_TIMEOUT_SECONDS)
        except TimeoutError:
            with suppress(ProcessLookupError):
                self.process.kill()
            await self.process.wait()
        finally:
            for task in [self.reader, self.stderr]:
                if not task.done():
                    task.cancel()
            await asyncio.gather(self.reader, self.stderr, return_exceptions=True)

    async def initialize(self, start):
        self.initialized = await self.request('server/discover', {})
        assert PROTOCOL in self.initialized['result']['supportedVersions'], self.initialized
        assert 'tools' in self.initialized['result']['capabilities']
        self.startup_ms = (time.perf_counter_ns()-start)/1e6
        self.identity = dict(pid=self.process.pid, assembly=str(self.assembly),
            sha256=hashlib.sha256(self.assembly.read_bytes()).hexdigest(),
            core_sha256=hashlib.sha256(self.assembly.with_name('DotnetAgents.CalDav.Core.dll').read_bytes()).hexdigest(),
            runtime_files_sha256=runtime_files(self.assembly),
            command=Path(f'/proc/{self.process.pid}/cmdline').read_bytes().decode().split('\0')[:-1],
            startup_ms=self.startup_ms, discovery=self.initialized)
        # Linux maps verifies the runtime actually loaded this assembly.
        self.identity['assembly_mapped'] = str(self.assembly) in Path(f'/proc/{self.process.pid}/maps').read_text()
        assert self.identity['assembly_mapped']
        return self

    async def read(self):
        try:
            while line := await self.process.stdout.readline():
                message = Response(json.loads(line))
                message.wire_bytes = len(line)
                if 'id' in message and ('result' in message or 'error' in message):
                    future = self.pending.get(message['id'])
                    if future is not None and not future.done():
                        future.set_result(message)
                else:
                    self.notifications += 1
        except Exception as error:
            for future in self.pending.values():
                if not future.done():
                    future.set_exception(error)
        finally:
            for future in self.pending.values():
                if not future.done():
                    future.set_exception(EOFError('MCP stdout closed before its response'))

    async def notify(self, method, params):
        self.process.stdin.write((json.dumps(dict(jsonrpc='2.0',method=method,params=params))+'\n').encode())
        await self.process.stdin.drain()

    async def request(self, method, params):
        params = dict(params)
        params.setdefault('_meta', {
            'io.modelcontextprotocol/protocolVersion': PROTOCOL,
            'io.modelcontextprotocol/clientCapabilities': CAPABILITIES,
            'io.modelcontextprotocol/clientInfo': dict(name='caldav-performance',version='1.0')})
        self.sequence += 1
        sequence = self.sequence
        future = asyncio.get_running_loop().create_future()
        self.pending[sequence] = future
        self.process.stdin.write((json.dumps(dict(jsonrpc='2.0', id=sequence, method=method, params=params))+'\n').encode())
        await self.process.stdin.drain()
        try:
            return await asyncio.wait_for(future, REQUEST_TIMEOUT_SECONDS)
        finally:
            del self.pending[sequence]

    async def call(self, tool, arguments, **continuation):
        params = dict(name=tool, arguments=arguments, _meta={
            'io.modelcontextprotocol/protocolVersion': PROTOCOL,
            'io.modelcontextprotocol/clientCapabilities': CAPABILITIES}, **continuation)
        before = process_stats(self.process.pid)
        timestamp = time.time_ns()
        start = time.perf_counter_ns()
        response = Response()
        client_failure = None
        try:
            response = await self.request('tools/call', params)
        except TimeoutError:
            client_failure = 'client_timeout'
        except (EOFError, ConnectionError):
            client_failure = 'client_transport_error'
        except json.JSONDecodeError:
            client_failure = 'client_protocol_error'
        elapsed = (time.perf_counter_ns()-start)/1e6
        try:
            after = process_stats(self.process.pid)
        except OSError:
            after = before
        result = response.get('result', {})
        structured = result.get('structuredContent', {})
        record = dict(tool=tool, timestamp_ns=timestamp, elapsed_ms=elapsed,
            cpu_ms=(after['cpu_seconds']-before['cpu_seconds'])*1000,
            rss_bytes=after['VmRSS'], peak_rss_bytes=after['VmHWM'],
            outcome=client_failure or structured.get('outcome', structured.get('code', result.get('resultType', 'rpc_error'))),
            is_error=bool(client_failure or result.get('isError') or 'error' in response),
            timed_out=client_failure=='client_timeout',
            item_sha256=hashlib.sha256(json.dumps(structured.get('items',[]),sort_keys=True).encode()).hexdigest(),
            items=len(structured.get('items', [])), response_bytes=response.wire_bytes)
        return response, record

    async def __aexit__(self, *exc):
        start = time.perf_counter_ns()
        self.process.stdin.close()
        try:
            await asyncio.wait_for(self.process.wait(), SHUTDOWN_TIMEOUT_SECONDS)
        except TimeoutError:
            self.process.kill()
            await self.process.wait()
            raise RuntimeError('MCP shutdown exceeded 5 seconds')
        await self.reader
        stderr = await self.stderr
        self.identity.update(shutdown_ms=(time.perf_counter_ns()-start)/1e6,
                             exit_code=self.process.returncode, stderr_bytes=len(stderr))
        assert self.process.returncode == 0 and not stderr, self.identity


def query(tool, size=5, bounded=True):
    args = dict(scope={'mode':'all'}, pageSize=size)
    if tool == 'calendar_entities.query':
        args['entityKinds'] = ['event','todo']
    if tool == 'todos.query':
        args.update(completionStates=['open','completed','cancelled','indeterminate'],
                    projection=['summary','status','due','priority','categories','recurrence'])
    if bounded:
        args.update(WINDOW)
    return args


async def pilot(root, assembly):
    state = json.loads((root/'infra-private.json').read_text())
    records = []
    for tool in ['calendar_entities.query','calendar_occurrences.query','todos.query']:
        async with Client(assembly, environment(state, 'caldav-perf-pilot-'+tool.split('.')[0])) as client:
            catalog = await client.request('tools/list', {})
            (root/'catalog-live.json').write_text(json.dumps(catalog, indent=2))
            for size in [1,5,200]:
                response, record = await client.call(tool, query(tool, size))
                records.append(dict(record, size=size, phase='start'))
                print(json.dumps(records[-1]), flush=True)
                structured = response.get('result',{}).get('structuredContent',{})
                if structured.get('outcome') != 'success':
                    print(json.dumps(response)[:1500], flush=True)
                cursor = structured.get('pagination',{}).get('nextCursor')
                if cursor:
                    _, record = await client.call(tool, dict(cursor=cursor,pageSize=size))
                    records.append(dict(record, size=size, phase='continue'))
                    print(json.dumps(records[-1]), flush=True)
            (root/('pilot-process-'+tool+'.json')).write_text(json.dumps(client.identity,indent=2))
    (root/'pilot.json').write_text(json.dumps(records,indent=2))


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('assembly', type=Path)
    args = parser.parse_args()
    asyncio.run(pilot(args.root,args.assembly))
