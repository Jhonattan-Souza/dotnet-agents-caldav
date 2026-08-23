# Query page-assembly observation

`run.sh` is a deliberately non-gating observation harness.  It compiles the
three historical private `CreatePage` implementations in isolated, shared Git
clones, creates an open-instance delegate with `MethodInfo.CreateDelegate`,
and compares their admitted page output with the actual current page codecs.

The historical revisions are intentionally fixed:

| Family | Historical revision | Current revision |
| --- | --- | --- |
| Calendar Entity | `4df75347477ca6dae463d60b938c7d28ab9b6ea6` | `61f2607383807f96464f33350e608180c1abee49` |
| Occurrence | `e63ea4d62fa4b4062566a6819127c18a30a1a38d` | `61f2607383807f96464f33350e608180c1abee49` |
| To-do | `8a9d887a0b5e44ffbca3025a41ae7c8f6705dd77` | `61f2607383807f96464f33350e608180c1abee49` |

For each family the fixed corpus contains 201 safe synthetic rows and is
measured at page sizes 1, 50, and 200 after 12 warmups and over 9 samples.
Each sample uses `GC.GetAllocatedBytesForCurrentThread()` and `Stopwatch` only;
there is no forced collection and no pass/fail timing or allocation threshold.
The fixture exports each historical family's complete projected 201-item corpus,
then feeds those exact JSON item bytes to its current codec. It verifies page
length, continuation presence, JSON item order, and SHA-256 item-byte equality
between historical and current admitted pages before reporting medians.

The sole deliberate cardinality exception is the valid fixed To-do corpus at
requested page size 200: the historical 64 KiB response admission accepts its
bounded prefix while the current 4 MiB codec accepts 200. The harness records
both full outputs and asserts the historical prefix's bytes, order, and hash in
the current page. That row is explicitly a differing-work observation, not an
equal-output allocation ratio.

Run from the repository checkout:

```bash
bash scripts/observations/query-page-assembly/run.sh /absolute/results-directory
```

The runner makes a `git clone --shared` under `mktemp -d`, injects fixture
sources only into that disposable clone, and removes the clone on every exit.
The caller-owned results directory receives one JSON object per observation and
the full runner metadata.  No project source, checkout, or working-tree state
is changed by a successful run.

The historical measurement intentionally covers the private `CreatePage` method
and its `CallToolResult` construction. The current measurement covers the
successor Core page codec's `Admit` operation, which is the production
page-assembly seam that replaces it; the thin MCP success wrapper is excluded.
Consequently the observation compares page-assembly allocation, not an
end-to-end MCP result allocation ratio.
