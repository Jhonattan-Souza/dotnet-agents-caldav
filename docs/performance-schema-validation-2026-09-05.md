# Performance da validação de schemas, 2026-09-05

A candidata reduziu o p95 de Continue com 200 itens em 78,2% para entidades,
75,4% para ocorrências e 44,8% para To-dos. Preservou validação e resultados,
aumentou throughput e reduziu CPU/RSS nos cenários medidos. A meta de 30% foi
atingida nessas continuações, mas **não em Start**, cujo p95 melhorou 5,8–7,0%.
Não se declara um ganho global do servidor.

## Alteração e evidência causal

O adaptador validava cada resposta com `OutputFormat.List`, construindo diagnósticos
que o guard descartava: ele consumia somente `IsValid` e lançava uma mensagem fixa
em caso de violação. A candidata usa `OutputFormat.Flag` no mesmo schema e na mesma
chamada `Evaluate`. Schemas, catálogo, limites, mensagens, Core, stdio e política
OTLP permanecem iguais. Cinco regressões com páginas de 200 itens comparam a
validade com o modo anterior e verificam erro no último item e em referências
aninhadas. A biblioteca documenta Flag como saída de validade com otimizações de
evaluação ([JsonSchema.Net](https://docs.json-everything.net/schema/basics/)).

O piloto isolou o custo pelo trace: Continue de ocorrências com 200 itens tinha
18,2 ms em `caldav.operation`, mas 699,8 ms no span MCP e 706,6 ms no cliente.
Nenhum HTTP ocorreu. EventPipe encontrou `CalendarOutputSchemaGuard.Validate` e
`JsonSchema.Evaluate` no caminho remanescente. Os percentuais do perfil de tempo
de threads incluem esperas, portanto não representam percentuais de CPU.

No ensaio síncrono do guard sobre a mesma página real, 100 observações após 20
warmups deram mediana de 245,00 → 50,51 ms; alocação por thread de 168.909.200 →
94.049.464 bytes por validação (-44,3%). Houve 216 coleções Gen2 no baseline e zero
na candidata nesse ensaio. Isso mede o guard por reflexão, não latência MCP ou
GC de uma sessão completa. Payload SHA256:
`0928dbadb37cc84a1eb8433de8b0b87cfef25b017fb91f0645341f93490817ac`.

## Ambiente e identidade

- SHA inicial, checkout limpo: `37cdba56f3c957ee8f241eadf1e55b883e950458`.
- Candidata medida: alterações locais sobre esse SHA, antes do commit e da abertura
  do PR. Os hashes abaixo identificam os assemblies usados nas medições.
- Baseline MCP SHA256: `e1244b5028bd678066f5a5457b807ba16de75fa7b8937d075c48190fb337ee81`.
- Candidata MCP SHA256: `1885c568a2460993e464c24b358ceb6f4ee8b102f76f3104bfc9e16af8fc1068`.
- Core nas duas versões SHA256: `ffec80c4405ca21a7d36abd71f0b4cc00f225b91975781acfaf3651a108ef5e2`.
- SDK 10.0.100/runtime 10.0.0; Linux x64 7.2.2-1-cachyos; Ryzen 7 7735HS,
  8 núcleos/16 threads, 30 GiB RAM; Docker rootless 29.7.2.
- Radicale 3.7.8: `ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`.
- Dashboard 13.4.2: `mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b`.
- Cada container: 4 CPUs/2 GiB. Portas somente em loopback, autenticação preservada.
  Raiz CalDAV `http://127.0.0.1:32775`; OTLP HTTP/protobuf porta 32777; Dashboard
  porta 32776. Esses endereços identificam infraestrutura descartável desta execução.
- Aspire CLI 13.5.1; Hermes 0.21.0, revisão `b0ab2e16`, OpenRouter
  `openai/gpt-5.6-luna`, reasoning medium. Profilers 10.0.731102.

O driver executou `/home/jhow/.local/share/mise/installs/dotnet/10.0.100/dotnet`
com a DLL absoluta em `/tmp/caldav-perf-20260905/baseline/` ou `candidate/`.
Cada processo registra comando, hash e presença da DLL em `/proc/<pid>/maps`.
As versões publicadas via dnx não participaram da comparação. A máquina é uma
estação de trabalho compartilhada; cargas externas não foram interrompidas nem
controladas. A alternância e a dispersão por bloco reduzem a confusão com deriva,
mas estes números não são um SLA de produção.

## Método

Seed determinístico 20260905: 600 VEVENTs, 600 VTODOs, arquivo vazio; distribuição
histórica de datas/recorrências/estados; janela 2026-07-01 a 2026-12-31 UTC,
America/Sao_Paulo. PROPFIND Depth:1 com ETags confirmou 600/600/0 antes da comparação.
As contagens voltaram a 600/600/0 após cada passagem funcional. Build,
seeding, mutações de preparação e restauração ficaram fora das chamadas medidas.

A verificação histórica do seed conferiu as contagens 600/600/0; não conservou
um mapa de ETags para detectar edição de um recurso que mantivesse a contagem.
As mutações funcionais usaram fixtures separadas e os hashes das páginas medidas
coincidiram entre builds, mas isso não constitui uma verificação de integridade
de todos os recursos do corpus. O harness corrigido passa a comparar caminhos e
ETags fortes com os retornados na criação do seed; essa garantia não é atribuída
retroativamente à execução histórica.

MCP moderno negocia `2026-07-28` por `server/discover`; o driver verifica versão e
capabilities e descobre tools/list. Os pilotos tentaram initialize legado e
receberam rejeição; suas chamadas posteriores com metadata moderna eram válidas,
mas suas medidas de handshake não entram na comparação. O harness foi corrigido
antes da comparação principal e a matriz funcional foi repetida.

Continue usa três blocos de 100 observações por célula, com 20 warmups por tamanho
e processo. Baseline/candidata alternam ordem por bloco. Cada snapshot permanece
na sessão original. Hashes dos itens e sua ordem coincidiram nas nove células
entre versões e blocos. Percentis: nearest rank, `ceil(p*N)`. P99 nos JSONs é
descritivo; 300 amostras não tornam sua cauda robusta.

Start usa três blocos de 30 observações por célula, com cinco warmups/cinco medidas
por processo. Essa amostragem menor limita precisão do p95 e foi escolhida pelo
custo da aquisição integral. A tentativa inicial com dez medidas por coorte
atingiu o limite de bytes do store no 14º Start de ocorrências. Ela permanece
separada em `start-serial-*`, com sua falha, e não conta como throughput útil.
A comparação final usa `start-serial-bounded-*`. Nenhum limite do produto mudou.

Latência do cliente inclui envio stdio, execução, retorno e decodificação JSON;
exclui escrita do registro de benchmark. Duração no servidor vem dos spans MCP e
de operação. Startup/discovery MCP e shutdown constam dos registros de processos.
Throughput serial da tabela é operações bem-sucedidas/soma das latências medidas;
os JSONL de lotes incluem throughput do driver com suas verificações. CPU de
`/proc` tem resolução de 10 ms; RSS/HWM são observações do processo. Bytes medidos
são bytes JSON-RPC stdio, não bytes HTTP. A allowlist atual não exporta bytes de
corpos CalDAV, e essa ausência não foi preenchida com estimativas.

Correção de interpretação de CPU: nas séries históricas `single_session` com
concorrência 2/4, os deltas do processo foram coletados em intervalos sobrepostos
e incluem trabalho das outras chamadas. Não medem CPU por operação. O JSON
estruturado conserva esses números como `mean_overlapping_process_cpu_delta_ms`
e marca `mean_cpu_ms` como `null` nessas células. Os ganhos de CPU apresentados
nas tabelas seriais abaixo usam chamadas sem sobreposição e continuam aplicáveis.
As amostras brutas permanecem preservadas fora do Git.

## Continue serial: resultados completos

300 medidas por revisão em cada linha. Tempos em ms. Nenhuma chamada resultou em
erro ou timeout. A última coluna é contagem HTTP por chamada.

| Ferramenta | Página | Baseline p50 / p95 | Candidata p50 / p95 | Redução p95 | ops/s B / C | Erros B / C | HTTP B / C |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `calendar_entities.query` | 1 | 3.40 / 4.45 | 2.32 / 3.11 | 30.0% | 289.2 / 419.4 | 0 / 0 | 0 / 0 |
| `calendar_entities.query` | 5 | 10.79 / 12.58 | 7.57 / 8.98 | 28.6% | 92.5 / 130.0 | 0 / 0 | 0 / 0 |
| `calendar_entities.query` | 200 | 183.96 / 261.62 | 51.62 / 56.96 | 78.2% | 5.1 / 19.3 | 0 / 0 | 0 / 0 |
| `calendar_occurrences.query` | 1 | 3.81 / 4.88 | 2.92 / 3.91 | 19.8% | 254.4 / 332.1 | 0 / 0 | 0 / 0 |
| `calendar_occurrences.query` | 5 | 14.82 / 17.51 | 11.07 / 16.54 | 5.6% | 66.6 / 84.6 | 0 / 0 | 0 / 0 |
| `calendar_occurrences.query` | 200 | 276.76 / 307.93 | 70.86 / 75.77 | 75.4% | 3.6 / 14.0 | 0 / 0 | 0 / 0 |
| `todos.query` | 1 | 2.08 / 2.93 | 1.45 / 2.09 | 28.8% | 471.1 / 630.5 | 0 / 0 | 0 / 0 |
| `todos.query` | 5 | 4.57 / 5.85 | 2.95 / 4.14 | 29.3% | 213.2 / 323.5 | 0 / 0 | 0 / 0 |
| `todos.query` | 200 | 62.14 / 121.91 | 25.98 / 67.29 | 44.8% | 14.6 / 25.9 | 0 / 0 | 0 / 0 |

As páginas de 200 itens superaram a meta de redução de 30% do p95 nas três
famílias. Os ganhos menores das páginas pequenas estão expostos, sem agregação
em um percentual global. A meta de Start não foi atingida, conforme a comparação abaixo.

## Start serial: resultados completos

90 medidas por revisão/família, página 200; nenhum erro ou timeout. HTTP abaixo
é a forma normal, igual nas revisões; retries reais permanecem na exportação.

| Ferramenta | Baseline p50 / p95 ms | Candidata p50 / p95 ms | Redução p95 | ops/s B / C | CPU média ms B / C | Máx. RSS MiB B / C | HTTP normal por chamada |
|---|---:|---:|---:|---:|---:|---:|---|
| `calendar_entities.query` | 1408.5 / 1600.5 | 1276.8 / 1489.2 | 7.0% | 0.70 / 0.77 | 726.0 / 649.3 | 344.8 / 289.8 | 5 PROPFIND + 27 REPORT |
| `calendar_occurrences.query` | 1595.6 / 1679.6 | 1405.2 / 1568.9 | 6.6% | 0.62 / 0.71 | 737.2 / 556.8 | 451.2 / 347.1 | 5 PROPFIND + 27 REPORT |
| `todos.query` | 826.6 / 1049.4 | 801.8 / 988.8 | 5.8% | 1.15 / 1.20 | 790.9 / 749.7 | 219.9 / 205.8 | 5 PROPFIND + 14 REPORT |

**A meta de 30% não foi atingida em Start.** Os ganhos de p95 ficaram entre 5,8%
e 7,0%; a menor amostragem limita a precisão dessa cauda. Não se declara ganho
uniforme do servidor nem um percentual global. O custo dominante remanescente é
a aquisição CalDAV: no trace exploratório `16c92af2e4d83a84e4f69dd52495c072`, fetch
levou 1.288 ms, dos quais 893 ms somados nos 24 REPORTs multiget. A mudança do
guard não elimina essas viagens nem a materialização/serialização integral de Start.

Investigação do limiar de regressão: o bloco 0 de entidades teve p95 +6,4% na
candidata; os blocos 1 e 2 tiveram -7,5% e -15,5%. A regressão não se reproduziu,
e o agregado de 90 medidas teve -7,0%. Essa dispersão impede promover o pequeno
ganho de Start a uma promessa de latência; a redução de Continue grande tem
margem muito maior. Os JSONs preservam os p95 de cada bloco.

## Fronteiras de tempo e recursos: Continue 200

B/C = baseline/candidata. O throughput deste quadro inclui o trabalho do driver
entre respostas, além da latência da chamada. RSS é amostrado após as chamadas;
não substitui um perfil de heap. O Core e seus limites de retenção são idênticos.

| Ferramenta | Span MCP p95 ms B/C | Operação p95 ms B/C | CPU média ms B/C | Máx. RSS MiB B/C | Driver ops/s B/C |
|---|---:|---:|---:|---:|---:|
| `calendar_entities.query` | 255.0 / 51.4 | 14.8 / 8.8 | 199.7 / 55.7 | 259.2 / 202.1 | 4.86 / 16.14 |
| `calendar_occurrences.query` | 302.2 / 68.7 | 14.6 / 9.7 | 281.5 / 71.8 | 324.1 / 200.8 | 3.44 / 11.82 |
| `todos.query` | 120.3 / 65.7 | 10.5 / 6.0 | 85.9 / 54.4 | 188.6 / 164.4 | 14.04 / 24.13 |

O ganho está principalmente após `caldav.operation`, no guard do adaptador.
Nenhuma das 5.400 chamadas de Continue serial executou HTTP. A coleta EventPipe
retém counters de runtime; alocações/GC comparáveis do guard vêm do ensaio
síncrono descrito acima. O Aspire não foi apresentado como origem de métricas
de runtime que o produto não exporta.

## Concorrência e overhead de OTLP

Página 200. Cada célula de sessão usa 300 medidas por revisão; cada célula entre
processos usa 96 (três blocos de 32). Um processo por chamada simultânea na
segunda topologia; snapshots não atravessam processos. Todas as chamadas passaram.

| Topologia | Concorrência | Ferramenta | p95 B/C ms | Redução | Driver ops/s B/C |
|---|---:|---|---:|---:|---:|
| Mesma sessão | 2 | `calendar_entities.query` | 339.1 / 81.8 | 75.9% | 6.02 / 22.04 |
| Mesma sessão | 2 | `calendar_occurrences.query` | 509.3 / 113.6 | 77.7% | 4.01 / 15.75 |
| Mesma sessão | 2 | `todos.query` | 170.1 / 81.0 | 52.4% | 15.76 / 29.51 |
| Mesma sessão | 4 | `calendar_entities.query` | 615.8 / 135.3 | 78.0% | 6.87 / 27.12 |
| Mesma sessão | 4 | `calendar_occurrences.query` | 889.8 / 180.2 | 79.8% | 4.58 / 19.66 |
| Mesma sessão | 4 | `todos.query` | 257.1 / 95.4 | 62.9% | 18.05 / 41.60 |
| Processos distintos | 2 | `calendar_entities.query` | 279.7 / 87.0 | 68.9% | 8.17 / 24.95 |
| Processos distintos | 2 | `calendar_occurrences.query` | 423.4 / 123.5 | 70.8% | 5.45 / 17.94 |
| Processos distintos | 2 | `todos.query` | 188.7 / 90.6 | 52.0% | 12.84 / 25.22 |
| Processos distintos | 4 | `calendar_entities.query` | 331.3 / 123.1 | 62.8% | 12.19 / 31.97 |
| Processos distintos | 4 | `calendar_occurrences.query` | 508.5 / 176.1 | 65.4% | 8.16 / 23.10 |
| Processos distintos | 4 | `todos.query` | 177.9 / 95.7 | 46.2% | 23.42 / 41.61 |

OTLP foi comparado na mesma candidata, alternando ligado/desligado em três blocos
de 30 após 20 warmups. A etiqueta baseline desse run significa OTLP ligado,
não o assembly antigo. p95 ligado/desligado: entidades 78,1/76,0 ms;
ocorrências 106,2/105,4 ms; To-dos 96,6/95,7 ms. O overhead observado foi de
aproximadamente 2,8%, 0,8% e 1,0%; o tamanho pequeno desses efeitos e a dispersão
não justificam uma promessa de overhead fixo. Collector indisponível e OTLP
desligado preservaram resultados e EOF limpo. O maior shutdown observado nos
processos das comparações foi 129,2 ms.

## Hipóteses e experimento descartado

| Hipótese | Evidência inicial | Ganho esperado / custo / risco | Decisão |
|---|---|---|---|
| Diagnósticos de schema descartados | Continue grande fora da operação; perfil do guard | Alto em páginas grandes / baixo / preservar validade | Manter Flag e validar diferencialmente |
| Rodadas multiget sequenciais | Fetch 1,3–1,6 s, 24 lotes de 50 | Reduzir espera / médio / fallback, ordem e concorrência | Experimento sem benefício; removido |
| Parsing/projeção e recorrência | Parte de fetch e serialização nos spans | Incerto / alto / fidelidade, DST e limites | Sem alteração especulativa |
| Discovery, snapshots e Move | Já otimizados no SHA inicial | Ganhos históricos não atribuíveis | Preservados |

O experimento multiget permitiu quatro lotes por origem e não melhorou as oito
medidas bem-sucedidas após aquecimento (~2,0 s em ambas as versões). As duas últimas
chamadas de cada piloto falharam na admissão; não entram nessa comparação. Não se
apresenta esse protótipo descartado como otimização entregue. Não houve alteração
no Core final.

## Integração e observabilidade

Cliente direto completou as 23 ferramentas do catálogo com exact habilitado:
criação de calendário, consultas e paginação, criação/patch/conclusão de fixtures,
cinco mutações de recorrência, Move vazio/populado, exact get/create/replace/move,
MRTR e exclusões com ausência autoritativa. O catálogo padrão descoberto tem 19.
Os controles verificaram OTLP ligado/desligado/collector indisponível e EOF limpo,
escalas 1/50/200/600, excesso de 5000 ocorrências e saturação do store. Limites
esperados foram registrados à parte.

Hermes executou listagem, as três queries com Continue na sessão persistente,
criação/releitura/patch/releitura/conclusão/releitura de um To-do. O Radicale
confirmou resumo alterado e COMPLETED. A negociação tentou initialize 2025-11-25,
recebeu rejeição e recuperou via server/discover; os requests seguintes usaram
2026-07-28 e anunciaram elicitation form/url. A prova usa tráfego MCP capturado por
proxy transparente e resultados estruturados sanitizados, não só texto do agente.

**Bloqueado no Hermes:** delete devolveu `input_required`; o adaptador não enviou
requestState/inputResponses. O cliente direto completou MRTR e verificou ausência.
Isso não aprova MRTR pelo Hermes. Não se alegou propagação de trace desde inferência;
a correlação usa serviço, ferramenta, intervalo UTC e identidade do processo.

Os traces confirmam zero HTTP em Continue e Move com cinco PROPFINDs, quatro GETs
e um MOVE tanto no destino vazio como populado. Exact Move usa duas descobertas,
seis observações dos recursos envolvidos e um MOVE, sem REPORT. O defeito histórico
do 404 de delete não reapareceu: nesta revisão as observações de ausência têm
`caldav.http.request_purpose=absence_probe`, `expected_absence` e status OK.
InputRequired é separado de erro; retries reais permanecem nos spans.

Exportação usa a [API/CLI suportada do Aspire](https://aspire.dev/dashboard/apis/),
com verificação de returnedCount/totalCount e retenção configurada de 50.000 traces
/100.000 logs. A auditoria final encontrou os mesmos 124.729 spans únicos na API e no ZIP,
sem truncamento. O cruzamento identificou os 10.962 traces de operações medidas
com OTLP; outras 270 chamadas medidas do controle usaram OTLP desligado.
A auditoria de 633 arquivos exportados não encontrou sentinelas de credenciais,
corpus, cursor ou payload iCalendar, nem eventos de exceção.

## Gates, reprodução e limpeza

Primeira suíte: 2.421 Core + 1.035 MCP + 114 integração e 11 strict-preconditions
passaram. Cobertura 94,49% linhas / 86,05% branches. Alternate-time-zone falhou
antes de executar por erro de montagem overlay do Docker; logs completos retidos.
A repetição integral passou: 3.592 testes, zero falhas e zero skipped;
nenhum gate, threshold ou teste foi relaxado. Build Release: zero warnings/erros;
Slopwatch: zero findings. O pacote local `0.0.0-perf.20260905` passou conteúdo,
metadata, instalação isolada e smoke MCP (mais um teste). Metadata de origem
permaneceu em `0.0.0`; nada foi publicado. A limpeza final está registrada abaixo.

Harness: [README](../scripts/observations/mcp-performance/README.md).
Evidências completas locais: `/tmp/caldav-perf-20260905`. O [JSON de resultados](performance-schema-validation-2026-09-05-results.json)
contém estatísticas, blocos, traces representativos, contagens HTTP, testes,
catálogo e prova Hermes sanitizada. Os JSONL completos, perfis, assemblies e ZIPs
ficam no diretório externo. A limpeza foi confirmada: corpus 600/600/0 antes da remoção, dois containers
próprios e seu volume anônimo ausentes, processos MCP encerrados, configuração
Hermes isolada/credenciais/profilers/clone de pacote removidos. Caches e recursos
alheios foram preservados. `cleanup.json` registra as verificações. Assemblies,
perfis, dados de medição e exportações permanecem como artefatos de evidência.


## Traces representativos

Os identificadores abaixo estão no ZIP exportado e no JSON de evidências;
nenhum depende da permanência do Dashboard. As durações comparativas vêm de
populações completas, não destes exemplos isolados.

| Caso | Trace |
|---|---|
| calendar_entities.query / baseline / Continue 200 | `4afae28f686f347d8f216b53a89bd0b8` |
| calendar_entities.query / candidate / Continue 200 | `63307d2fca8f4c6aee386571780c2d31` |
| calendar_occurrences.query / baseline / Continue 200 | `f517c8e2b8fa91a0d978094ff7504d6d` |
| calendar_occurrences.query / candidate / Continue 200 | `b80510805a7f22e8fc7ab379066bfaee` |
| todos.query / baseline / Continue 200 | `d974bafcaf8a43a7ff993f549b155ce3` |
| todos.query / candidate / Continue 200 | `e838a2b0a6df14192c8649a28a2f9a8e` |
| calendar_resources.delete / cliente direto | `70567a58bf4fa28fc5ad047fc4e2a0cf` |
| calendar_resources.exact_move / cliente direto | `42b3b464112a34736330aa7e62e828cb` |
| calendar_resources.move / cliente direto | `b504e100589f3394cb62cd448ca0f6d6` |
