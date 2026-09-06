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
Falha ou cancelamento durante a negociação fecha stdin, aguarda o filho e o mata
se exceder o encerramento de cinco segundos. As tarefas de leitura são encerradas
antes de propagar a falha original.

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
O arquivo de credenciais fica sob `radicale-private/`, criado com modo 0700.
Somente o diretório interno é montado no container, com os arquivos legíveis pelo
usuário não-root da imagem. O diretório privado do host bloqueia acesso de outros
usuários locais, inclusive quando o umask é 022.

`prepare.sh` grava `baseline-build.json` e `candidate-build.json` com os SHAs
reais, estado de alterações locais, hash do patch e dos arquivos novos, antes do
build. Confere que as fontes não mudaram durante a compilação e associa os hashes
dos assemblies copiados. `aggregate.py` exige esses manifestos; não infere a
identidade pelo Git no momento da análise. Os manifestos sobrevivem à limpeza dos
clones. Execuções históricas anteriores a esse registro mantêm suas evidências
originais, sem reconstruir retroativamente a identidade a partir do checkout atual.
Cada manifesto de benchmark associa os argumentos absolutos aos hashes e à fonte
do build correspondente. Inverter baseline/candidata é erro antes da medição.
No controle `--compare-otlp`, ambos apontam ao mesmo build e conservam essa
identidade na agregação. Cada processo precisa corresponder ao seu argumento,
inclusive ao hash de Core; pertencer ao conjunto dos dois builds não basta.
No controle OTLP, o conjunto de dependências também deve ser idêntico entre os
argumentos; igualdade somente de MCP e Core não basta.
Os manifestos verificam também os assets declarados em `.deps.json` e seus hashes,
incluindo dependências, assets por RID, deps, runtimeconfig e configurações locais.
Cópias auxiliares de publicação fora desse grafo não integram a assinatura.
O driver registra esse conjunto para cada processo. Mantenha
esses diretórios imutáveis; trocar uma dependência como JsonSchema.Net invalida a
identidade mesmo quando MCP e Core não mudaram.

Seed 20260905: 600 Events e 600 To-dos distribuídos em 180 dias desde
2026-07-01; 60 recorrentes por tipo, 30 Events date-only e 12 cancelados;
390 To-dos abertos, 180 completos e 30 cancelados. Arquivo vazio desde o início.
Janela fixa: 2026-07-01 a 2026-12-31 UTC; contexto America/Sao_Paulo; CalDAV usa
URL raiz, sem encurtar discovery pela URL do principal. PROPFIND Depth:1 com
ETags verifica os recursos persistidos. Cada PUT do seed precisa retornar um ETag
forte, salvo no manifesto privado. `verify` compara caminhos e ETags completos:
editar ou substituir um recurso mantendo a contagem também falha. `corpus.json`
registra contagens e hash das entradas determinísticas. Seeding e limpeza ficam
fora da medida.
`seed --count N` permite corpus personalizados. As expectativas de restauração e
limpeza vêm de `corpus.json`. As matrizes funcional e de limites exigem ao menos
dois To-dos semeados; o padrão de 600 mantém a matriz completa de paginação. Em
corpus menores, a matriz funcional continua apenas páginas que tenham cursor.

## Matriz funcional e clientes

```bash
python3 scripts/observations/mcp-performance/functional.py /tmp/caldav-perf-new /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
python3 scripts/observations/mcp-performance/edges.py /tmp/caldav-perf-new /tmp/caldav-perf-new/baseline/DotnetAgents.CalDav.Mcp.dll /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
```

`functional.py` percorre o catálogo vivo completo com exact habilitado somente
no filho correspondente. Antes de acessar as fixtures, ele exige que o assembly
e todas as dependências correspondam a `candidate-build.json`. Inclui queries limitadas e não limitadas quando
permitidas, Start/Continue 1/5/200, recursos, criação/patch/conclusão, cinco
mutações de recorrência, Move vazio/populado, exact create/replace/move e deletes
de recurso/calendário por MRTR. Releitura HTTP verifica mutações e ausência.
Para a fixture UTC de recorrência, a releitura confere UID, regra e horários do
master, RDATE/EXDATE e o RECURRENCE-ID/STATUS do override solicitado após cada
uma das cinco mutações. Operações sem efeito ou em outra instância falham.
`edges.py` cobre leituras de controle, OTLP ligado/desligado/indisponível, EOF,
escalas e falhas esperadas de limites. Uma falha esperada não conta como operação
útil. O store só retém snapshots que precisam de continuação; corpus vazio não
serve para testar sua saturação.
As verificações do harness usam condicionais explícitas, preservadas com
`python -O` e `PYTHONOPTIMIZE`. EOF exige saída zero e ambos os streams vazios;
a agregação rejeita contagens incompletas de traces antes de produzir resultados.

Use a versão e o Python da instalação Hermes que `hermes --version` identifica.
A prova implementada aceita o perfil OpenRouter já configurado. Ela lê somente
modelo, reasoning e a credencial desse provedor, grava configuração isolada em
`hermes-isolated` e habilita somente o MCP `perf`. Não muda o perfil original.
Exemplo da máquina desta execução:

```bash
/home/jhow/.hermes/hermes-agent/venv/bin/python scripts/observations/mcp-performance/hermes.py /tmp/caldav-perf-new /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
python3 scripts/observations/mcp-performance/verify_hermes.py /tmp/caldav-perf-new /tmp/caldav-perf-new/candidate/DotnetAgents.CalDav.Mcp.dll
```

O proxy preserva cada byte MCP e registra testemunhas sanitizadas. Wrapper e proxy
validam a candidata preparada antes de iniciar o cliente/servidor; o proxy registra
a fonte e os hashes de todas as dependências. A verificação direta usa a mesma checagem. Ambos
recusam um wire log existente antes de iniciar o cliente/servidor. Use um diretório
de evidência novo para outra tentativa. O proxy não implementa MRTR pelo Hermes.
O segundo comando verifica COMPLETED no Radicale e completa
a limpeza via cliente direto quando o Hermes parou em input_required. Ele exige
que a fixture ainda exista; se uma versão futura do Hermes completar o delete,
adapte a verificação à ausência autoritativa e conserve sua evidência de wire.
Inferência não entra na latência atribuída ao MCP. Guarde `hermes-usage.json`
como custo separado, sem alegar tracing da inferência.

## Comparação

Não rode builds, testes, profiling ou outro benchmark em paralelo. Restaure
600/600/0 antes de iniciar. Os comandos abaixo alternam baseline/candidato
entre blocos/coortes e recusam sobrescrever amostras de um nome existente.
Os hashes dos itens, incluindo sua ordem, são comparados entre baseline e candidata
por ferramenta/tamanho/bloco, em ambas as topologias. Diferenças interrompem a
execução depois de preservar a amostra divergente. Falhas e timeouts também ficam
no JSONL antes da interrupção, sem contar como throughput útil.

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
Contagens de blocos, amostras e coortes devem ser positivas. Start exige que
`--samples` seja divisível por `--cohort-samples` e usa somente coortes seriais
na topologia `single_session`; argumentos incompatíveis falham antes da execução.
Cada coorte aceita no máximo 11 medidas, reservando cinco dos 16 snapshots para
warmup. O limite de bytes pode exigir coortes menores; mantenha o padrão de cinco
para o corpus documentado, como na comparação original.

Para concorrência, repita Continue com `--sizes 200 --concurrency 2` e `4`, cada
qual com `--name` próprio. `--topology single_session` envia chamadas simultâneas
ao mesmo processo; `--topology processes` usa um filho por chamada concorrente,
com snapshot próprio. Para custo limitado nos controles entre processos, use
`--samples 32` (divisível por quatro). Nunca compare throughput de uma topologia
como se fosse outra. Os aquecimentos concorrentes usam 20 rodadas na carga escolhida.

`--compare-otlp` exige o mesmo assembly nos dois argumentos: a etiqueta baseline
usa OTLP e a candidata o desabilita, alternando a ordem entre blocos. Use
`--sizes 200 --samples 30 --name otlp-overhead`. `--no-otlp` desabilita ambos.
As duas flags são mutuamente exclusivas.
p50/p95 usam nearest rank, `ceil(p*N)`; p99 é
apenas descritivo. Os JSONL retêm aquecimentos, medidas, falhas, CPU do processo,
RSS/HWM, bytes stdio e hashes de itens. CPU de `/proc` tem resolução de 10 ms
nesta máquina. `*-batches.jsonl` fornece tempo de lote e throughput efetivo;
o inverso da latência média só representa capacidade serial de serviço.
Em `single_session` com concorrência maior que um, CPU por chamada e sua média
ficam `null`: os intervalos simultâneos incluem CPU das outras chamadas. A medição
por delta do processo é usada somente sem chamadas sobrepostas no mesmo filho.

## Profiling e exportação

```bash
dotnet tool install dotnet-counters --tool-path /tmp/caldav-perf-new/profilers
dotnet tool install dotnet-trace --tool-path /tmp/caldav-perf-new/profilers
python3 scripts/observations/mcp-performance/profile.py /tmp/caldav-perf-new /tmp/caldav-perf-new/baseline/DotnetAgents.CalDav.Mcp.dll /tmp/caldav-perf-new/profilers --build baseline --name baseline-profile
python3 scripts/observations/mcp-performance/telemetry.py /tmp/caldav-perf-new complete --zip
```

Consulte `dotnet-trace list-profiles`, `collect --help`, `dotnet-counters collect
--help`, `aspire export --help` e `aspire otel spans --help` na versão instalada.
Piloto e profiling validam a candidata por padrão; `--build baseline` seleciona
o manifesto da baseline, e essa identidade fica registrada com o processo.
EventPipe `dotnet-sampled-thread-time` inclui esperas; seus percentuais não são
percentuais de CPU. Runtime counters são coleta externa, não métricas já
exportadas pelo produto. Para a comparação síncrona de alocação, copie
`schema-observation/` ao diretório externo, compile e passe diretório do assembly,
uma página estruturada real de MCP salva localmente, nome da ferramenta e o
manifesto `baseline-build.json` ou `candidate-build.json`, nessa ordem. O nome da
ferramenta precisa corresponder à página capturada. O
programa usa reflexão para chamar o guard original em cada assembly sobre o
mesmo JSON, após 20 aquecimentos e com 100 amostras. Não mede transporte.
A agregação exige o hash de assembly correspondente ao build de cada observação,
além de ferramenta, payload e versão do runtime .NET idênticos entre baseline e candidata.
O observador também registra os hashes dos arquivos de runtime e rejeita mudanças
durante a coleta; esses hashes devem corresponder ao manifesto do build.

`profile.py --mode start` usa cinco warmups e cinco Starts capturados por processo,
abaixo da coorte que esgotou os bytes do store no piloto. Cada chamada é registrada
e verificada, inclusive nos warmups; `busy`, timeout ou outro erro interrompe o
perfil. Os collectors são encerrados em caso de falha, e o registro do processo
fica marcado como incompleto. Use um nome novo para cada perfil.

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
`package.sh <diretório-externo>` reproduz esse gate num clone local com o diff
completo contra HEAD, incluindo alterações staged e arquivos de build da raiz,
mais arquivos novos não ignorados. Um checkout limpo usa diretamente seu commit.
O gate exige `candidate-build.json` desse diretório: verifica a fonte atual antes
do clone e a fonte copiada antes e depois da validação do pacote. Mudanças após
`prepare.sh` exigem preparar outra candidata.
Gera somente o pacote de teste `0.0.0-perf.20260905`, sem publicação. A prova Hermes
propaga o código de saída do cliente depois de emitir seu resumo diagnóstico.
O proxy também propaga o código de saída do MCP e termina de encaminhar stderr
antes de registrar o encerramento.
Sua configuração sanitizada retém somente provedor e nome do modelo, com permissão
0600; credenciais, headers e opções arbitrárias do perfil ficam fora dessa evidência.

Para associar os gates à comparação preparada, use o wrapper sem mudar o estado
Git entre `prepare.sh` e a execução abaixo:

```bash
python3 scripts/observations/mcp-performance/gates.py /tmp/caldav-perf-new --name gates-final
```

Ele executa os cinco comandos exigidos pelo repositório, na mesma ordem, e grava
`gate-source.json` com a identidade inicial/final da fonte e do runtime. A marca
de conclusão só aparece após todos os comandos passarem. O arquivo é colocado
no diretório de artefatos depois que o runner termina, preservando a exigência de
diretório inicialmente vazio. Use outro nome para repetir uma execução falha.
Diretórios históricos sem essa prova não recebem identidade retroativamente.

Execute `python3 scripts/observations/mcp-performance/test_harness.py` para as
regressões do harness. Elas usam repositórios temporários e executáveis simulados
para verificar identidade, contagens e tratamento de falhas; não substituem a
matriz MCP real nem o gate de pacote.

Depois dos ensaios de alocação (salvos como `schema-baseline.json` e
`schema-candidate.json`), dos gates e da exportação `complete` acima, agregue as
execuções escolhidas explicitamente. `--gates` aponta ao diretório de evidências
da suíte dentro da raiz da observação (ou a um caminho absoluto):

A agregação executa `verify-test-artifacts.sh` na fase `complete`, incluindo o
manifesto das variantes estrita e de fuso alternativo, e `verify-coverage.sh`
com os limiares 90%/85%. Diretórios parciais, contadores malsucedidos e cobertura
insuficiente são rejeitados antes da emissão do resultado.
Ela também exige que `gate-source.json` certifique exatamente a fonte e o runtime
da candidata preparada; gates verdes de outro checkout são recusados.

```bash
python3 scripts/observations/mcp-performance/aggregate.py /tmp/caldav-perf-new /tmp/caldav-perf-new/results.json --gates gates-final --runs continue-serial start-serial --traces /tmp/caldav-perf-new/complete-traces.json
```

`--traces` aceita um ou mais arquivos `*-traces.json` produzidos por `telemetry.py`;
não depende de um nome `final-traces.json` implícito. Exports sobrepostos são
deduplicados por trace ID somente quando seus dados coincidem. Versões diferentes
do mesmo trace são rejeitadas; selecione a exportação completa posterior ao
encerramento da operação. A contagem de matches ainda precisa cobrir todas as
chamadas medidas com OTLP.

Antes de remover infraestrutura, exporte e verifique 600/600/0. `infra.py down`
confere a label de propriedade antes de remover seus containers e volumes
anônimos, e grava `cleanup.json`. Depois remova somente `hermes-isolated`, clone
e ferramentas temporários criados nesta execução. Retenha amostras, hashes e
exportações sanitizadas pelo tempo desejado. Preserve caches e containers alheios.

```bash
python3 scripts/observations/mcp-performance/infra.py verify /tmp/caldav-perf-new
python3 scripts/observations/mcp-performance/infra.py down /tmp/caldav-perf-new
```
