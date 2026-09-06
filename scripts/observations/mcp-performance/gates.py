#!/usr/bin/env python3
"""Run repository gates and bind their artifacts to the prepared candidate."""
import argparse
import json
from pathlib import Path
import subprocess
from build_manifest import load_builds, source_identity, runtime_files

STEPS=['tool-restore','restore','build','tests','slopwatch']


def verify_gate_identity(directory,candidate):
    proof=json.loads((directory/'gate-source.json').read_text())
    if (not proof['completed'] or proof['source']!=candidate['source']
            or proof['source_after']!=candidate['source']
            or proof['runtime_files_sha256']!=candidate['runtime_files_sha256']
            or proof['steps']!=[dict(name=name,exit_code=0) for name in STEPS]):
        raise RuntimeError('Gate evidence does not certify the prepared candidate source and runtime')


def run(root,name):
    root=root.resolve()
    repository=Path(__file__).resolve().parents[3]
    if root.is_relative_to(repository) or Path(name).name!=name:
        raise ValueError('Use an external prepared root and a simple gate run name')
    candidate=load_builds(root)['candidate']
    source=source_identity(repository)
    if source!=candidate['source']:
        raise RuntimeError('Prepare the current candidate before running its gates')
    directory=root/name
    proof_path=root/(name+'-source.json')
    if directory.exists() or proof_path.exists():
        raise RuntimeError('Use a fresh gate run name; preserve previous attempts')
    directory.mkdir()
    proof=dict(source=source,source_after=None,runtime_files_sha256=None,steps=[],completed=False)
    proof_path.write_text(json.dumps(proof,indent=2))
    commands=[
        ['dotnet','tool','restore'],['dotnet','restore'],['dotnet','build','-c','Release','--no-restore'],
        ['bash','scripts/run-test-suite.sh','--artifacts-dir',str(directory)],
        ['dotnet','tool','run','slopwatch','analyze','--config','.slopwatch/slopwatch.json','--fail-on','warning']]
    assembly=repository/'src/DotnetAgents.CalDav.Mcp/bin/Release/net10.0/DotnetAgents.CalDav.Mcp.dll'
    try:
        for step,command in zip(STEPS,commands):
            with (root/(name+'-'+step+'.log')).open('w') as log:
                result=subprocess.run(command,cwd=repository,stdout=log,stderr=log)
            proof['steps'].append(dict(name=step,exit_code=result.returncode))
            proof_path.write_text(json.dumps(proof,indent=2))
            if result.returncode:
                raise RuntimeError('Gate failed: '+step)
            if step=='build':
                proof['runtime_files_sha256']=runtime_files(assembly)
                if proof['runtime_files_sha256']!=candidate['runtime_files_sha256']:
                    raise RuntimeError('Gate build differs from the prepared candidate runtime')
        proof['source_after']=source_identity(repository)
        if proof['source_after']!=source or runtime_files(assembly)!=candidate['runtime_files_sha256']:
            raise RuntimeError('Candidate changed during the gate run')
        proof['completed']=True
    finally:
        # The suite requires an empty artifact directory at entry. Write its
        # portable source proof only after the runner returns or fails.
        for path in [proof_path,directory/'gate-source.json']:
            path.write_text(json.dumps(proof,indent=2))
    print(json.dumps(dict(gates=str(directory),source=source['sha'],completed=True)))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root',type=Path)
    parser.add_argument('--name',default='gates-final')
    args=parser.parse_args()
    run(args.root,args.name)
