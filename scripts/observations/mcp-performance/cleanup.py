#!/usr/bin/env python3
"""Remove this run's infrastructure/configuration, retaining measurement artifacts."""
import argparse
import json
from pathlib import Path
import shutil
from infra import down,verify


def cleanup(root):
    state=json.loads((root/'infra-private.json').read_text())
    counts=verify(state)
    if counts!=dict(events=600,todos=600,archive=0):
        raise RuntimeError(f'Unexpected final corpus: {counts}; inspect owned fixtures before cleanup')
    down(root)
    removed=[]
    for name in ['hermes-isolated','profilers','baseline-checkout','package-checkout','package-metadata']:
        path=root/name
        if path.exists():
            shutil.rmtree(path);removed.append(name)
    private_log=root/'hermes-run-private.log'
    if private_log.exists():
        private_log.unlink();removed.append(private_log.name)
    path=root/'cleanup.json';result=json.loads(path.read_text())
    result.update(final_corpus_before_container_removal=counts,temporary_directories_removed=removed,
                  retained='assemblies, profiles, raw measurement artifacts and sanitized evidence')
    path.write_text(json.dumps(result,indent=2))
    print(json.dumps(result))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('root',type=Path)
    cleanup(p.parse_args().root)
