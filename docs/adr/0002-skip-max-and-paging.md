# ADR-0002: Skip and Max are the contract; paging is transport tuning

- **Status**: Proposed
- **Date**: 2026-09-07
- **Deciders**: @Chris-Wolfgang
- **Tags**: `api-shape`, `paging`, `performance`

## Context

`DbExtractor` exposes four knobs that bound a result set, and two of them say the same thing twice:

| | "start later" | "how many" |
|---|---|---|
| client-side | `SkipItemCount` | `MaximumItemCount` |
| server-side | `ServerOffset` | `ServerLimit` |

Every hard question raised while designing [#394](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/394) and [#398](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/398) traces back to that duplication — *does `Skip` apply before or after the page? what if both offsets are set? does `ServerLimit` cap the total or the page?* The duplication is self-inflicted, so documenting the resulting combination matrix would entrench the problem rather than solve it.

Two further forces shape the answer:

**Paging syntax is not portable.** `LIMIT … OFFSET …` is the PostgreSQL / MySQL / SQLite form; SQL Server rejects `LIMIT` outright and wants the SQL:2008 `OFFSET … ROWS FETCH NEXT … ROWS ONLY`, as do Oracle 12c+ and Db2. There is no clause this library can emit unconditionally, which is why `PagingClauseTemplate` exists and why [#391](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/391) made `PagingClauseTemplates.None` the default rather than guessing a dialect.

**Paging today is not paging.** `ApplyServerPaging` runs once and feeds a single `ExecuteReaderAsync`, so one extractor yields exactly one page and the caller writes the loop. Both of this repo's own consumers — `ShadowConsumer` and the #386 integration contract test — hand-roll the same loop. When the sample and the tests both write it, it belongs in the library.

## Decision

Split the knobs by **what the caller wants** versus **how the extractor gets it**:

- `SkipItemCount` and `MaximumItemCount` are the **contract**. They are the only two knobs with an observable effect on the rows yielded.
- `PagingClauseTemplate` and `PageSize` are **transport tuning**. They change the round-trip shape and the server's workload; the rows delivered are identical either way.

Three consequences follow directly.

**`ServerOffset` is removed** (`[Obsolete]`, forwarding to `SkipItemCount`). It was `SkipItemCount` spelled differently, and deleting it deletes the entire combination matrix rather than documenting it.

**The template is the switch.** Template `None` executes the command unchanged with `Skip`/`Max` applied client-side. A template that is set means the paging clause is applied.

**One formula, no special cases.** `ExtractorBase` declares `public int SkipItemCount` (default `0`) and `public int MaximumItemCount` (default `int.MaxValue`) — neither is nullable, so "unset" is already expressible and the branches collapse:

```text
offset    = SkipItemCount + rowsReceivedSoFar
remaining = MaximumItemCount - rowsYielded          // int.MaxValue when unset
limit     = PageSize.HasValue ? min(PageSize.Value, remaining) : remaining
```

That yields three modes without any of them being a special case in the code:

| configuration | behaviour |
|---|---|
| no template | execute as-is; `Skip`/`Max` client-side |
| template, no `PageSize` | **single query**, skip and max pushed server-side |
| template + `PageSize` | multi-page walk, auto-advancing |

Extraction stops on a short page, on `MaximumItemCount`, or on cancellation. When `SkipItemCount == 0 && MaximumItemCount == int.MaxValue && PageSize == null` there is nothing to page and no clause is appended — we do not send `FETCH NEXT 2147483647`, a value no test shows every engine accepts.

`ServerLimit` is renamed to `PageSize` rather than reused, because its meaning genuinely changes from "total rows to return" to "rows per round-trip". Retaining the name would let existing code keep compiling while silently returning a different number of rows. Both obsolete properties **forward** to their replacements rather than shadowing them, which keeps one field of truth and dissolves the "both set" conflict — last write wins, and there is nothing to throw on.

Setting `PageSize` with template `None` throws `InvalidOperationException`: silently ignoring a configured knob is worse than failing, because the failure mode is a surprise full-table scan in production that looks correct in development against SQLite.

### Two counters, deliberately distinct

The implementation keeps `rowsReceived` (rows the reader produced, **including rows that failed to parse**) separate from `rowsYielded` (rows handed to the caller). `rowsReceived` drives the next offset and the short-page test; `rowsYielded` drives the `Max` clamp and termination.

They diverge exactly when `ErrorPolicy = Skip` drops a malformed row, and conflating them is silently destructive in both directions: advancing the offset by `rowsYielded` makes a 100-row page containing 3 bad rows advance by 97, re-fetching those 3 and duplicating the 97 good rows behind them; testing the short page against `rowsYielded` sees 97 < 100, concludes the source is exhausted, and truncates the result set at the first page containing any bad row.

## Consequences

- **Positive**: one extractor walks a whole result set, so `ShadowConsumer` and the #386 contract test drop their hand-rolled loops. A large `SkipItemCount` stops dragging rows across the wire to be discarded on arrival. The combination matrix disappears instead of being documented. The clamp loses its `SkipItemCount` term — which also removes the `int` overflow the parked `feat/394-auto-advance-paging` branch had to compute in `long` to avoid, since `MaximumItemCount` defaults to `int.MaxValue` and adding any skip overflows.
- **Negative**: the library now owns two costs the caller previously owned by writing the loop — the O(n²) server work of OFFSET paging, and result drift under concurrent writes. Both are detailed below and must be documented on the public surface rather than assumed away.
- **Neutral**: rows that fail to parse inside the skip window used to consume a skip slot, because the `DataException` path increments the row index before the skip check. Server-side they never materialise, so the skip window is measured purely in source rows. This is arguably more correct, but it is a behaviour change and is documented rather than left to be discovered.
- **Neutral**: `CurrentSkippedItemCount` is incremented by `SkipItemCount` once when the skip is pushed server-side. The rows genuinely were skipped, just not locally; leaving it at zero would silently change an observable.

### The O(n²) cost, stated plainly

`OFFSET n` is not a seek. The engine produces rows in order and discards the first *n*, so a page at offset *n* costs O(n + p). Across a full walk of *N* rows in pages of *p*:

```text
sum(i = 0 .. k-1) of (i·p + p)  =  p·k(k+1)/2  ≈  N² / 2p        where k = N/p
```

Quadratic in table size, and only **linearly** helped by a larger page. For a 1,000,000-row table:

| page size | pages | rows the server scans | amplification |
|---|---|---|---|
| 100 | 10,000 | 5 billion | 5,000× |
| 1,000 | 1,000 | 500 million | 500× |
| 10,000 | 100 | 50 million | 50× |
| 100,000 | 10 | 5 million | 5× |

An index covering the `ORDER BY` softens the constant, not the shape. Single-query mode has none of this — one query, one offset, O(Skip + Max) — which is why it is a first-class mode rather than a degenerate one.

Paging therefore buys **bounded per-query work, shorter transactions, and resumability**. It costs *more* total server work, not less. The existing `ServerOffset` remarks justify paging as avoiding "streaming everything to the client", which is already false: extraction runs off a `DbDataReader` and streams row by row either way. Those remarks are corrected as part of this change.

## Alternatives considered

- **Document the four-knob combination matrix.** Entrenches the duplication that caused the confusion; every future feature has to respect a matrix nobody can hold in their head.
- **Keep `ServerLimit` and change its meaning in place.** Rejected: code keeps compiling and silently returns a different number of rows — the worst available failure mode. The rename makes the change visible at compile time.
- **Trigger paging on `PageSize` rather than the template.** Makes template-without-page-size meaningless and forces an invented default page size or a sentinel limit. Triggering on the template gives single-query pushdown a natural home.
- **Default to a page size when a template is set but `PageSize` is not.** Any number the library picks is wrong for someone, and it converts the cheapest path (one query) into the most expensive (an O(n²) walk) by default.
- **Send `int.MaxValue` as the limit when nothing is bounded.** Unverified across the six supported engines; omitting the clause entirely is both safer and cheaper.
- **Throw when both `SkipItemCount` and the obsolete `ServerOffset` are set.** Unnecessary once the obsolete property forwards rather than shadows — there is only one value, so there is no conflict.
- **Keyset (seek) pagination.** The robust answer to both the O(n²) cost and concurrent-write drift: `WHERE id > @lastId ORDER BY id FETCH NEXT @p` is O(log N + p) per page and stable under inserts. It requires a unique ordering key, so it cannot be the default for arbitrary caller-supplied SQL. A separate and larger design.

## Notes

- [#410](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/410) — the issue this ADR records; supersedes the design halves of [#394](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/394) and [#398](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/398).
- [#384](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/384) — dialect lock; the reason `PagingClauseTemplates` exists.
- [#391](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/391) — made `PagingClauseTemplates.None` the default rather than guessing a dialect.
- [Chris-Wolfgang/ETL-Abstractions#454](https://github.com/Chris-Wolfgang/ETL-Abstractions/issues/454) — widening the base counters and `Skip`/`Max` to `long`. `ServerOffset`/`ServerLimit` are `long?` while `Skip`/`Max` are `int`, so the forwarding setters need a checked narrowing conversion until that lands; afterwards the forwarding becomes lossless.
- Prior implementation work is parked on `feat/394-auto-advance-paging` and `feat/398-server-side-skip`. Read #410 before resuming either — the trigger they assume is not the one decided here.
- Status flips to **Accepted** with the PR that implements the model.
