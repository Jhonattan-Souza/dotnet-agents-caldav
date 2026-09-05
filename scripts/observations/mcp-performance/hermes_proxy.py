#!/usr/bin/env python3
"""Transparent stdio witness for Hermes; never changes MCP bytes or confirmations."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import threading
import time
from functional import sanitize

assembly=Path(sys.argv[1]).resolve()
evidence=Path(sys.argv[2])
evidence.touch(exist_ok=False)
child=subprocess.Popen([str(Path(shutil.which('dotnet')).resolve()),str(assembly)],
                       stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
pending={}
lock=threading.Lock()


def record(value):
    with lock, evidence.open('a') as out:
        out.write(json.dumps(dict(timestamp_ns=time.time_ns(),pid=child.pid,**value))+'\n')


record(dict(event='launch',assembly=str(assembly),sha256=hashlib.sha256(assembly.read_bytes()).hexdigest(),
            core_sha256=hashlib.sha256(assembly.with_name('DotnetAgents.CalDav.Core.dll').read_bytes()).hexdigest()))


def incoming():
    for line in sys.stdin.buffer:
        value=json.loads(line)
        if 'id' in value:
            params=value.get('params',{})
            pending[value['id']]=dict(method=value.get('method'),tool=params.get('name'),started=time.time_ns(),
                                      continuing='requestState' in params,
                                      protocol=params.get('_meta',{}).get('io.modelcontextprotocol/protocolVersion'),
                                      cursor_call='cursor' in params.get('arguments',{}))
            if value.get('method')=='initialize':
                record(dict(event='initialize_request',protocol=params.get('protocolVersion'),
                            capabilities=params.get('capabilities')))
        child.stdin.write(line);child.stdin.flush()
    child.stdin.close()


def errors():
    data=child.stderr.read()
    record(dict(event='stderr',bytes=len(data)))
    if data:
        sys.stderr.buffer.write(data);sys.stderr.buffer.flush()


def terminate(*_):
    child.terminate()


signal.signal(signal.SIGTERM,terminate)
threading.Thread(target=incoming,daemon=True).start()
threading.Thread(target=errors,daemon=True).start()
for line in child.stdout:
    value=json.loads(line)
    request=pending.pop(value.get('id'),None)
    if request:
        started=request.pop('started')
        result=value.get('result',{})
        record(dict(event='response',**request,elapsed_ms=(time.time_ns()-started)/1e6,
                    structured=sanitize(result),rpc_error='error' in value,
                    catalog_names=[tool['name'] for tool in result.get('tools',[])],
                    command=Path(f'/proc/{child.pid}/cmdline').read_bytes().decode().split('\0')[:-1],
                    assembly_mapped=str(assembly) in Path(f'/proc/{child.pid}/maps').read_text()))
    sys.stdout.buffer.write(line);sys.stdout.buffer.flush()
child.wait()
record(dict(event='exit',code=child.returncode))
