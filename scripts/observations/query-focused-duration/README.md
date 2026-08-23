# Focused query duration observations

`run.sh` reproduces the small Direct GET, missing Temporal Evaluation Context,
and Occurrence replay observations cited by the dated performance reports. It
uses isolated shared Git worktrees at the exact historical revisions and the
integrated changed revision. Historical-only test fixtures are injected into
the disposable checkouts. The changed temporal checkout also receives the
source-controlled observation patch that configures the identical baseline
corpus and adds snapshot-store assertions; the changed Direct GET and
Occurrence rows run the permanent regressions unchanged.

Each command is Release, exact-counted, fail-skips, strict-zero-tests, and emits
a TRX file. A TRX duration covers the entire focused test case, including its
fixture and assertions, rather than only the production call. Durations are
single supporting observations. The script contains no timing threshold,
allocation threshold, benchmark framework, or broad load corpus.

```bash
bash scripts/observations/query-focused-duration/run.sh /absolute/empty/results
```

The caller-owned result directory retains six TRX files, command logs, and
`runner-metadata.json`. The temporary clone and its worktrees are removed on
every exit.
