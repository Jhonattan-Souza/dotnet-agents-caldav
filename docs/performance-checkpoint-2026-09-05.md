# Checkpoint de performance, 2026-09-05

## Plano e estado

1. Ambiente, corpus e baseline: concluído.
2. Hipóteses, alteração e comparação estatística: concluído.
3. Matriz funcional, Hermes, gates e pacote: concluído; MRTR Hermes bloqueado e documentado.
4. Relatório, evidências e limpeza: concluído.

Checkout inicialmente limpo em `37cdba56f3c957ee8f241eadf1e55b883e950458`.
Objetivo integral: arquivo de anexo `goal-objective.md` desta tarefa.
Evidências completas e builds isolados: `/tmp/caldav-perf-20260905`.
Nenhum container preexistente pertence à tarefa.

Ambiente observado: SDK 10.0.100, runtime 10.0.0, Linux x64
7.2.2-1-cachyos, Ryzen 7 7735HS (8 núcleos/16 threads), 30 GiB RAM,
Docker rootless 29.7.2, Hermes 0.21.0 (`b0ab2e16`), Aspire CLI 13.5.1.
Restore de ferramentas, restore NuGet e build Release concluíram sem erros ou warnings.
Baseline preservado como cópia integral do diretório Release antes de qualquer alteração.
SHA256 MCP: `e1244b5028bd678066f5a5457b807ba16de75fa7b8937d075c48190fb337ee81`.
SHA256 Core: `ffec80c4405ca21a7d36abd71f0b4cc00f225b91975781acfaf3651a108ef5e2`.

## Decisões anteriores à comparação

Percentis por nearest rank (`ceil(p*N)`), com p50/p95 e dispersão por bloco.
P99 será descritivo, sem alegação de robustez com apenas 300 amostras.
Cinco aquecimentos por processo, três blocos de 100 amostras nos cenários
prioritários. Alternância baseline/candidato em coortes dentro dos blocos.
O store permite apenas 16 snapshots por dez minutos. Cada processo executará
cinco Starts de aquecimento e até dez Starts medidos; reinicialização entre
coortes fica fora da latência aquecida e será registrada separadamente.
Continue mantém o processo e o snapshot do Start correspondente.
Não alterar o limite nem introduzir mecanismo privado de limpeza no produto.
O harness usará somente fixtures próprias e relógio/janela/seed fixos.

Hipóteses ainda sem diagnóstico: rodadas multiget sequenciais; parsing/expansão;
validação de output no adaptador; serialização e montagem de páginas.
Os ganhos históricos de discovery, snapshots e Move não pertencem a esta tarefa.

## Checkpoint após piloto

Infraestrutura própria: `caldav-perf-3f20bb0e-radicale` e
`caldav-perf-3f20bb0e-dashboard`, 4 CPUs e 2 GiB por container, portas dinâmicas
em loopback. Manifesto privado no diretório externo de evidências; contém
credenciais descartáveis e chave da API, não copiar ao Git.
Corpus autoritativo: 600/600/0; `corpus.json` contém hash e janela fixa.
Catálogo padrão observado: 19 ferramentas. Assembly carregado confirmado por
`/proc/<pid>/maps`, command line e hashes.

Piloto `pilot.json`, `pilot-spans.json`, `pilot-traces.json`, `pilot.zip`:
Continue 200 de entidades e ocorrências custa 498/707 ms no cliente,
mas só 25/18 ms em `caldav.operation`. Não há HTTP nesses Continues.
Perfil EventPipe `baseline.nettrace` identifica `CalendarOutputSchemaGuard.Validate`
e `JsonSchema.Evaluate` no trabalho restante. `baseline-start.nettrace` cobre Start.
Os percentuais de `dotnet-sampled-thread-time` incluem threads em espera;
não interpretar como percentuais de CPU.

Candidata: `OutputFormat.Flag` em lugar de `List`, pois o guard só consome
`IsValid`. Preserva schemas, validação e erro público. Piloto após JIT: ~102 ms
contra ~370 ms em Continue de ocorrências; não é ainda prova por percentis.
Experimento de multiget em ondas de quatro não melhorou Start (~2,0 s) e foi
removido. Evidência: `multiget-check.json`, builds `flag-candidate` e
`multiget-candidate`; o último é experimento descartado, não produto final.

Amostragem fixada antes da comparação principal: Continue prioritário, três
blocos de 100 por tamanho 1/5/200 e família; 20 aquecimentos de Continue porque
o JIT ainda mudou após cinco no piloto. Start secundário: três blocos de 30,
com cinco aquecimentos e dez medidos por processo. O custo da aquisição integral
e a retenção finita tornam 300 Starts por célula caro; reportar a menor precisão
das 90 observações, sem promover p99 a evidência robusta. A meta de 30% permanece.

## Checkpoint de integração e regressões

37 testes focados do guard passaram. Core candidato idêntico ao inicial,
SHA256 `ffec80c4405ca21a7d36abd71f0b4cc00f225b91975781acfaf3651a108ef5e2`.
MCP candidato: `1885c568a2460993e464c24b358ceb6f4ee8b102f76f3104bfc9e16af8fc1068`.

Primeira suíte: projetos principais e strict-preconditions passaram, cobertura
94,49% linhas / 86,05% branches. Alternate-time-zone não iniciou: Docker rootless
retornou erro de montagem overlay `device or resource busy`. Reter `gates.log`
e `gates/` como tentativa de infraestrutura falha; repetir a suíte em diretório
vazio sem alterar gates. O guard de worktree exige não editar o repositório
enquanto a suíte executa.

`functional-before-discover.json` registra sucesso por todo o catálogo exact
de 23 ferramentas, MRTR, Move vazio/populado e limpeza autoritativa 600/600/0.
O cliente do piloto tentou o handshake legado `initialize` com a revisão moderna;
o servidor o rejeitou, embora as chamadas subsequentes com `_meta` moderno fossem
válidas. Driver corrigido ANTES da comparação principal: `server/discover`,
validação de `supportedVersions` e capabilities, `_meta` em todos os requests.
Repetir matriz com esse handshake e manter as tentativas anteriores identificadas.

Hermes 0.21.0 executou 14 chamadas MCP reais: listagem, três Starts/Continues,
criação/releitura/patch/releitura/conclusão/releitura e review de exclusão.
Cliente tentou initialize 2025-11-25, recebeu erro e recuperou via server/discover.
MRTR continua sem continuação no adaptador: `input_required`, nenhuma chamada
com requestState. Radicale confirmou resumo alterado e COMPLETED. Cliente direto
concluiu exclusão com review/confirm e verificou 404. Evidências `hermes-*` e
`hermes-direct-cleanup/`. O proxy transparente registra assembly/mapeamento,
capabilities, tool name, resultado sanitizado, sessão e timestamps.

`edges.json`: controles de leitura com OTLP on/off/indisponível, EOF limpo,
escalas 1/50/200/600, excesso de 5000 ocorrências e 17º snapshot busy passaram.
Capturas anteriores de tentativa do harness ficam separadas; não contam como
aprovação. `schema-baseline.json` / `schema-candidate.json`: mesmo hash de página,
100 observações após 20 aquecimentos: mediana 245/51 ms e 169/94 MB alocados no
guard. Reflexão e alocação por thread, sem transporte; não são latências MCP.

## Comparação principal em andamento

Continue serial concluído: `continue-serial-summary.json`, 300 amostras por
família/tamanho/revisão, sem erros. p95 200 itens: entidades 261,62 → 56,96 ms;
ocorrências 307,93 → 75,77 ms; To-dos 121,91 → 67,29 ms.
Amostras/blocks completos conservados nos JSONL correspondentes.

O primeiro Start (`start-serial-*`) encontrou falha de admissão no 14º Start de
ocorrências: o limite de bytes do store pode chegar antes do limite de 16 slots.
Não usar essa tentativa como throughput útil nem misturá-la à comparação final.
Nos pilotos de multiget, as duas últimas chamadas por revisão também falharam;
a conclusão de ausência de benefício usa somente as oito medições bem-sucedidas
após aquecimento, não as falhas.
Nova comparação `start-serial-bounded`: cinco aquecimentos/cinco medidas por
processo, seis coortes por bloco e três blocos de 30 por família/revisão.
Nenhum teto do produto mudou; o erro de admissão foi preservado na evidência.

Hermes repetido com a testemunha registrando a versão negociada por request:
2026-07-28 após fallback de initialize para server/discover.
`verify_hermes.py` confirmou persistência e limpou por MRTR direto; o corpus
voltou a 600/600/0 antes de comparar.

Start serial concluído, `start-serial-bounded-summary.json`: 90 medidas por
revisão/família, zero erros. p95 entidades 1600,5 → 1489,2; ocorrências
1679,6 → 1568,9; To-dos 1049,4 → 988,8 ms. Meta 30% NÃO atingida em Start.
Regressão +6,4% de entidades no bloco 0 não se repetiu: blocos 1/2 -7,5%/-15,5%.
Comparações concorrentes estão no processo `remaining-runs.py` (sessão exec 52441):
continue-single_session-2 e -4 (3x100), continue-processes-2 e -4 (3x32),
otlp-overhead (mesma candidata, alternância on/off, 3x30). Não executar build,
gates ou profiling enquanto esse processo mede. Após ele: export final,
repeat suite em diretório vazio, Slopwatch, package.sh, relatório/JSON e limpeza.

## Encerramento

11.232 chamadas medidas sem erros; 10.962 operações com OTLP correlacionadas a
traces e 270 do controle com OTLP desligado. API e ZIP contêm os mesmos 124.729
spans únicos. Auditoria de 633 arquivos não encontrou sentinelas privadas.
Gates finais: 3.592 testes, 94,49% linhas/86,05% branches, Slopwatch zero findings,
Release sem warnings/erros, pacote local e smoke aprovados. A primeira falha de
montagem Docker continua preservada nos artefatos da primeira tentativa.

Relatório e JSON finais: `docs/performance-schema-validation-2026-09-05.*`.
Infraestrutura própria removida; um volume anônimo ausente; credenciais e Hermes
isolado removidos; artefatos de evidência mantidos em `/tmp/caldav-perf-20260905`.
Recursos de outras tarefas e caches compartilhados preservados. Meta 30% atingida
em Continue 200, não em Start (5,8–7,0%). Nenhuma publicação, merge ou commit.
