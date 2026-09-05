# Observação de performance por MCP stdio

Harness local para comparar o SHA inicial com o checkout candidato. Usa somente
Python stdlib para protocolo, fixtures e medidas. Hermes usa o Python da sua
instalação, com PyYAML e python-dotenv existentes. Não há dependência nova no
produto, AppHost, endpoint HTTP do MCP ou gate de tempo de máquina.

O protocolo moderno `2026-07-28` negocia via `server/discover`; `initialize` é
legado. O driver verifica `supportedVersions`, capabilities, catálogo e o
assembly mapeado pelo processo Linux. `_meta` acompanha cada request. Todos os
Continues conservam o processo original. MRTR conserva `requestState` e envia
`inputResponses` na segunda chamada; confirmação nunca entra em `arguments`.

## Preparação

Execute a partir da raiz do repositório. Use diretório externo novo. O baseline
fica em clone Git compartilhado local, sem alterar branches ou configurações
existentes. A execução de 2026-09-05 preservou o Release original diretamente
antes de editar; o script abaixo permite repetir com o mesmo SHA de origem.

```bash
bash scripts/observations/mcp-performance/prepare.sh /tmp/caldav-perf-new 37cdba56f3c957ee8f241eadf1e55b883e950458
python3 scripts/observations/mcp-performance/infra.py up /tmp/caldav-perf-new
python3 scripts/observations/mcp-performance/infra.py seed /tmp/caldav-perf-new
python3 scripts/observations/mcp-performance/infra.py verify /tmp/caldav-perf-new
```

`infra.py` reutiliza imagem, autenticação e storage do `RadicaleFixture`, com
portas dinâmicas em loopback. Radicale 3.7.8 e Dashboard 13.4.2 mantêm os digests
do checkout. Ambos recebem limites de 4 CPUs/2 GiB. Dashboard conserva login por
token e autenticação por chave na API de exportação. O manifesto privado tem
modo 0600. Não copie esse manifesto, config do Hermes, logs privados ou chaves
ao Git.

Seed 20260905: 600 Events e 600 To-dos distribuídos em 180 dias desde
2026-07-01; 60 recorrentes por tipo, 30 Events date-only e 12 cancelados;
390 To-dos abertos, 180 completos e 30 cancelados. Arquivo vazio desde o início.
Janela fixa: 2026-07-01 a 2026-12-31 UTC; contexto America/Sao_Paulo; CalDAV usa
URL raiz, sem encurtar discovery pela URL do principal. PROPFIND Depth:1 com
ETags verifica os recursos persistidos. `corpus.json` registra contagens e hash
das entradas determinísticas. Seeding e limpeza ficam fora da medida.

## Matriz funcional e clientes

```bash
python3 scripts/observations/mcp-performance/functional.py /tmp/caldav-perf-new /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
python3 scripts/observations/mcp-performance/edges.py /tmp/caldav-perf-new /tmp/caldav-perf-new/baseline/DotnetAgents.CalDav.Mcp.dll /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
```

`functional.py` percorre o catálogo vivo completo com exact habilitado somente
no filho correspondente. Inclui queries limitadas e não limitadas quando
permitidas, Start/Continue 1/5/200, recursos, criação/patch/conclusão, cinco
mutações de recorrência, Move vazio/populado, exact create/replace/move e deletes
de recurso/calendário por MRTR. Releitura HTTP verifica mutações e ausência.
`edges.py` cobre leituras de controle, OTLP ligado/desligado/indisponível, EOF,
escalas e falhas esperadas de limites. Uma falha esperada não conta como operação
útil. O store só retém snapshots que precisam de continuação; corpus vazio não
serve para testar sua saturação.

Use a versão e o Python da instalação Hermes que `hermes --version` identifica.
A prova implementada aceita o perfil OpenRouter já configurado. Ela lê somente
modelo, reasoning e a credencial desse provedor, grava configuração isolada em
`hermes-isolated` e habilita somente o MCP `perf`. Não muda o perfil original.
Exemplo da máquina desta execução:

```bash
/home/jhow/.hermes/hermes-agent/venv/bin/python scripts/observations/mcp-performance/hermes.py /tmp/caldav-perf-new /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
python3 scripts/observations/mcp-performance/verify_hermes.py /tmp/caldav-perf-new /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
```

O proxy preserva cada byte MCP e registra testemunhas sanitizadas. Não implementa
MRTR pelo Hermes. O segundo comando verifica COMPLETED no Radicale e completa
a limpeza via cliente direto quando o Hermes parou em input_required. Ele exige
que a fixture ainda exista; se uma versão futura do Hermes completar o delete,
adapte a verificação à ausência autoritativa e conserve sua evidência de wire.
Inferência não entra na latência atribuída ao MCP. Guarde `hermes-usage.json`
como custo separado, sem alegar tracing da inferência.

## Comparação

Não rode builds, testes, profiling ou outro benchmark em paralelo. Restaure
600/600/0 antes de iniciar. Os comandos abaixo alternam baseline/candidato
entre blocos/coortes e recusam sobrescrever amostras de um nome existente.

```bash
python3 scripts/observations/mcp-performance/benchmark.py /tmp/caldav-perf-new /tmp/caldav-perf-new/baseline/DotnetAgents.CalDav.Mcp.dll /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll --name continue-serial
python3 scripts/observations/mcp-performance/benchmark.py /tmp/caldav-perf-new /tmp/caldav-perf-new/baseline/DotnetAgents.CalDav.Mcp.dll /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll --name start-serial --mode start --samples 30
```

Continue: três blocos de 100 por família e tamanho, após 20 aquecimentos para
estabilizar o JIT observado. Start: três blocos de 30, coortes de cinco
aquecimentos/cinco medidos; reiniciar o filho entre coortes evita exceder 16
snapshots/128 MiB/10 minutos. A redução de amostras de Start foi definida antes
da comparação pelo custo da aquisição integral e da retenção, e reduz a precisão
do p95. Nenhum limite foi alterado. Startup/discovery MCP e shutdown ficam nos
arquivos `*-processes.jsonl`, separados das chamadas aquecidas.

Para concorrência, repita Continue com `--sizes 200 --concurrency 2` e `4`, cada
qual com `--name` próprio. `--topology single_session` envia chamadas simultâneas
ao mesmo processo; `--topology processes` usa um filho por chamada concorrente,
com snapshot próprio. Para custo limitado nos controles entre processos, use
`--samples 32` (divisível por quatro). Nunca compare throughput de uma topologia
como se fosse outra. Os aquecimentos concorrentes usam 20 rodadas na carga escolhida.

`--compare-otlp` exige o mesmo assembly nos dois argumentos: a etiqueta baseline
usa OTLP e a candidata o desabilita, alternando a ordem entre blocos. Use
`--sizes 200 --samples 30 --name otlp-overhead`. `--no-otlp` desabilita ambos.
p50/p95 usam nearest rank, `ceil(p*N)`; p99 é
apenas descritivo. Os JSONL retêm aquecimentos, medidas, falhas, CPU do processo,
RSS/HWM, bytes stdio e hashes de itens. CPU de `/proc` tem resolução de 10 ms
nesta máquina. `*-batches.jsonl` fornece tempo de lote e throughput efetivo;
o inverso da latência média só representa capacidade serial de serviço.

## Profiling e exportação

```bash
dotnet tool install dotnet-counters --tool-path /tmp/caldav-perf-new/profilers
dotnet tool install dotnet-trace --tool-path /tmp/caldav-perf-new/profilers
python3 scripts/observations/mcp-performance/profile.py /tmp/caldav-perf-new /tmp/caldav-perf-new/baseline/DotnetAgents.CalDav.Mcp.dll /tmp/caldav-perf-new/profilers --name baseline-profile
python3 scripts/observations/mcp-performance/telemetry.py /tmp/caldav-perf-new complete --zip
```

Consulte `dotnet-trace list-profiles`, `collect --help`, `dotnet-counters collect
--help`, `aspire export --help` e `aspire otel spans --help` na versão instalada.
EventPipe `dotnet-sampled-thread-time` inclui esperas; seus percentuais não são
percentuais de CPU. Runtime counters são coleta externa, não métricas já
exportadas pelo produto. Para a comparação síncrona de alocação, copie
`schema-observation/` ao diretório externo, compile e passe diretório do assembly,
uma página estruturada real de MCP salva localmente e nome da ferramenta. O
programa usa reflexão para chamar o guard original em cada assembly sobre o
mesmo JSON, após 20 aquecimentos e com 100 amostras. Não mede transporte.

O exportador usa a API suportada do Dashboard e exige
`returnedCount == totalCount`; não aceita truncamento silencioso. O Dashboard
mantém 50.000 traces e 100.000 logs; exporte por bloco antes da retenção encher.
O ZIP do Aspire conserva formato importável. As exportações completas ficam
fora do Git. Relacione calls a spans por serviço, ferramenta e intervalo UTC;
não invente propagação de trace pelo modelo. Contagens HTTP vêm dos spans de
tentativas; bytes HTTP não estão na allowlist atual. Bytes registrados pelo
driver são JSON-RPC stdio, não corpos CalDAV.

## Gates e limpeza

Depois de qualquer mudança de produto, execute os restores, build Release,
`run-test-suite.sh` com diretório externo vazio e Slopwatch na ordem do AGENTS.md.
Não edite o checkout enquanto a suíte observa seu estado. Gate de pacote usa
metadata versionada temporária com `scripts/prepare-release-metadata.sh` e
`scripts/verify-release-package.sh`, sem modificar `.mcp/server.json` de origem.
`package.sh <diretório-externo>` reproduz esse gate num clone local com o patch de
produto, gerando somente o pacote de teste `0.0.0-perf.20260905`, sem publicação.

Antes de remover infraestrutura, exporte e verifique 600/600/0. `infra.py down`
confere a label de propriedade antes de remover seus containers e volumes
anônimos, e grava `cleanup.json`. Depois remova somente `hermes-isolated`, clone
e ferramentas temporários criados nesta execução. Retenha amostras, hashes e
exportações sanitizadas pelo tempo desejado. Preserve caches e containers alheios.

```bash
python3 scripts/observations/mcp-performance/infra.py verify /tmp/caldav-perf-new
python3 scripts/observations/mcp-performance/infra.py down /tmp/caldav-perf-new
```
