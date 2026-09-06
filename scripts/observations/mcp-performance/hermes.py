#!/usr/bin/env python3
import json,os,subprocess,sys
from pathlib import Path
import yaml
from dotenv import dotenv_values
import argparse, shutil
p=argparse.ArgumentParser(description='Run installed Hermes against an isolated checkout MCP configuration (OpenRouter profile).')
p.add_argument('root',type=Path);p.add_argument('assembly',type=Path)
p.add_argument('--source-home',type=Path,default=Path.home()/'.hermes')
a=p.parse_args()
root=a.root.resolve()
if (root/'hermes-wire-sanitized.jsonl').exists():
    p.error('Hermes wire evidence already exists; use a fresh evidence directory')
from build_manifest import prepared_input
build_input=prepared_input(root,a.assembly)
a.assembly=Path(build_input['assembly'])
state=json.loads((root/'infra-private.json').read_text())
repo=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(repo/'scripts/observations/mcp-performance'))
from driver import environment
home=root/'hermes-isolated';home.mkdir(exist_ok=True);home.chmod(0o700)
base=yaml.safe_load((a.source_home/'config.yaml').read_text())
if not (base['model'].get('provider')=='openrouter'):
    raise RuntimeError('This witness uses the already configured OpenRouter provider only')
config={'model':base['model'],'agent':{'reasoning_effort':base.get('agent',{}).get('reasoning_effort','medium'),'max_turns':30},
'mcp_servers':{'perf':{'command':sys.executable,'args':[str(repo/'scripts/observations/mcp-performance/hermes_proxy.py'),str(a.assembly),str(root/'hermes-wire-sanitized.jsonl'),str(root)],
'env':{k:v for k,v in environment(state,'caldav-perf-hermes',exact=False).items() if k.startswith(('CALDAV_','OTEL_'))},'timeout':60,'connect_timeout':60}}}
(home/'config.yaml').write_text(yaml.safe_dump(config));(home/'config.yaml').chmod(0o600)
secrets=dotenv_values(a.source_home/'.env')
key=os.environ.get('OPENROUTER_API_KEY') or secrets.get('OPENROUTER_API_KEY')
if not (key):
    raise RuntimeError('Configured OpenRouter credential unavailable')
(home/'.env').write_text('OPENROUTER_API_KEY='+key+'\n');(home/'.env').chmod(0o600)
model_metadata={key:config['model'][key] for key in ['provider','model','default']
                if isinstance(config['model'].get(key),str)}
with os.fdopen(os.open(root/'hermes-config-sanitized.json',os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600),'w') as stream:
 os.fchmod(stream.fileno(),0o600)
 json.dump(dict(model=model_metadata,agent=config['agent'],hermes_home=str(home),mcp_command=config['mcp_servers']['perf']['command'],mcp_args=config['mcp_servers']['perf']['args']),stream,indent=2)
(root/'hermes-build.json').write_text(json.dumps(build_input,indent=2))
prompt='''Execute uma prova real de integração usando somente as ferramentas MCP perf disponíveis nesta sessão persistente. Todos os calendários são fixtures locais descartáveis e as mutações abaixo estão autorizadas. Faça em sequência: 1) calendars.list. 2) calendar_entities.query com scope all, entityKinds event e todo, janela UTC 2026-07-01T00:00:00Z até 2026-12-31T00:00:00Z e pageSize 1; Continue com o cursor obtido e pageSize 5 na mesma sessão. 3) calendar_occurrences.query com a mesma janela e pageSize 1, depois Continue pageSize 5. 4) todos.query com scope all, a mesma janela e pageSize 1; Continue pageSize 5. Os valores temporais usam kind utcDateTime. 5) todos.create no calendário padrão, entity kind todo, UID hermes-perf-20260905-integration, fields summary Hermes disposable integration. 6) calendar_resources.get no href criado. 7) todos.patch usando o entityRevision recém-lido, target scope master, patch scalars field summary operation set value Hermes patched integration. 8) Releia, execute todos.complete com a revisão fresca e releia para confirmar COMPLETED persistido. 9) Tente calendar_resources.delete usando a revisão fresca. Se houver uma confirmação MRTR que a integração não consiga continuar, registre a limitação e pare sem inventar campos ou repetir tentativas. Resuma apenas chamadas realmente executadas. Não use terminal, arquivos nem ferramentas fora deste MCP.'''
(root/'hermes-prompt.txt').write_text(prompt)
env={k:v for k,v in os.environ.items() if not k.startswith(('HERMES_','CALDAV_','OTEL_'))}
env['HERMES_HOME']=str(home)
with (root/'hermes-run-private.log').open('w') as log:
 result=subprocess.run([shutil.which('hermes'),'--ignore-rules','-t','perf','--usage-file',str(root/'hermes-usage.json'),'-z',prompt],cwd=home,env=env,stdout=log,stderr=log,timeout=600)
print(json.dumps({'exit_code':result.returncode,'wire_evidence_exists':(root/'hermes-wire-sanitized.jsonl').exists()}))
sys.exit(result.returncode)
