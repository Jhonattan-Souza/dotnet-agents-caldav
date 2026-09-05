#!/usr/bin/env python3
"""Use the operator's configured Hermes model in an isolated disposable home."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from driver import environment


PROMPT = '''Use only the rfc MCP tools in this persistent session. The server and
all its calendars are disposable local fixtures; the described mutations are
authorized. Do not use terminal, files, external services, or other tools.
Perform these steps in order and describe only calls you actually completed:
1. List calendars.
2. Query calendar entities (event and todo), occurrences, and todos using all
calendars, UTC window 2026-07-01T00:00:00Z through 2026-12-31T00:00:00Z,
page size 1. For each query continue with its returned cursor and page size 5.
Use kind utcDateTime for temporal values.
3. Create a todo in the default calendar with UID hermes-rfc-disposable and
summary Hermes RFC disposable integration.
4. Read the created href, then patch its master summary to Hermes RFC patched
using the returned entityRevision. Read it again and complete it using the
fresh entityRevision. Read again and verify COMPLETED persisted.
5. Attempt to delete it using the fresh revision. If Hermes cannot continue a
server MRTR input_required confirmation, report that exact client limitation
and stop; do not invent arguments or retry the same failing call.
'''


def private_write(path, text):
    path.write_text(text)
    path.chmod(0o600)


def run(root, assembly, source_home, python, executable, prompt_file, output):
    import yaml
    from dotenv import dotenv_values

    state = json.loads((root / 'infra-private.json').read_text())
    output.mkdir(parents=True, exist_ok=False)
    output.chmod(0o700)
    home = output / 'isolated-home'
    home.mkdir(mode=0o700)
    base = yaml.safe_load((source_home / 'config.yaml').read_text())
    model = base['model']
    if not isinstance(model, dict) or model.get('provider') != 'openrouter':
        raise RuntimeError('This observation runner currently supports an already configured OpenRouter provider.')
    config = {
        'model': model,
        'agent': {'reasoning_effort': base.get('agent', {}).get('reasoning_effort', 'medium'),
                  'max_turns': 35},
        'mcp_servers': {'rfc': {
            'command': str(python),
            'args': [str(Path(__file__).with_name('hermes_proxy.py').resolve()),
                     str(assembly.resolve()), str(output / 'wire-sanitized.jsonl')],
            'env': {k: v for k, v in environment(state, 'caldav-rfc-hermes-'+output.name).items()
                    if k.startswith(('CALDAV_', 'OTEL_'))},
            'timeout': 60, 'connect_timeout': 60}},
    }
    private_write(home / 'config.yaml', yaml.safe_dump(config))
    key = os.environ.get('OPENROUTER_API_KEY') or dotenv_values(source_home / '.env').get('OPENROUTER_API_KEY')
    if not key:
        raise RuntimeError('The configured provider credential is unavailable.')
    private_write(home / '.env', 'OPENROUTER_API_KEY=' + key + '\n')
    public = {'model': model, 'agent': config['agent'], 'assembly': str(assembly.resolve()),
              'source_configuration_modified': False, 'isolated_home': str(home)}
    (output / 'configuration-sanitized.json').write_text(json.dumps(public, indent=2))
    prompt = prompt_file.read_text() if prompt_file else PROMPT
    (output / 'prompt.txt').write_text(prompt)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('HERMES_', 'CALDAV_', 'OTEL_'))}
    env['HERMES_HOME'] = str(home)
    log_path = output / 'run-private.log'
    private_write(log_path, '')
    try:
        with log_path.open('w') as log:
            result = subprocess.run(
                [str(executable), '--ignore-rules', '-t', 'rfc', '--usage-file',
                 str(output / 'usage.json'), '-z', prompt], cwd=home, env=env,
                stdout=log, stderr=log, timeout=900)
        summary = {'exit_code': result.returncode,
                   'wire_evidence_exists': (output / 'wire-sanitized.jsonl').exists()}
    finally:
        # Provider credentials are needed only while Hermes runs.
        (home / '.env').unlink(missing_ok=True)
    (output / 'run-summary.json').write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('assembly', type=Path)
    parser.add_argument('--source-home', type=Path, default=Path.home() / '.hermes')
    parser.add_argument('--python', type=Path, default=Path(sys.executable))
    parser.add_argument('--executable', type=Path, default=Path(shutil.which('hermes') or 'hermes'))
    parser.add_argument('--prompt-file', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    run(args.root.resolve(), args.assembly, args.source_home, args.python,
        args.executable, args.prompt_file, args.output.resolve())
