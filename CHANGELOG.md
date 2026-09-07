# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `DbExtractor.PageSize` / `DbExtractorOptions.PageSize` — rows per round-trip. A single extractor
  now walks the whole result set instead of yielding one page, so callers no longer write the page
  loop themselves ([#410](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/410),
  [#394](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/394)).
- `IDbExtractorBuilder.PageSize()`, `.SkipItemCount()` and `.MaximumItemCount()`, so the builder can
  express the row contract as well as the transport tuning.
- [ADR-0002](docs/adr/0002-skip-max-and-paging.md) records the model: `SkipItemCount` and
  `MaximumItemCount` are the contract, `PagingClauseTemplate` and `PageSize` are transport tuning.

### Changed

- **`PagingClauseTemplate` is now the switch that activates paging.** Left at
  `PagingClauseTemplates.None` the command runs unchanged; set, the paging clause is applied. With a
  template and no `PageSize`, the skip and the maximum are pushed into a **single** query — the
  cheapest path, since it pays the offset once instead of once per page.
- **`SkipItemCount` is pushed into the query's offset when a template is set**, so the skipped rows
  are never fetched instead of being fetched and discarded on arrival
  ([#398](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/398)). Without a template the
  client-side skip stays, and now logs a warning naming the faster route.
- A page never asks for more than is still needed: the requested limit is
  `min(PageSize, MaximumItemCount - rowsYielded)`.
- `SkipItemCount` with a template is reported through `CurrentSkippedItemCount` as before — the rows
  were skipped, just not locally.
- Setting `PageSize` without a `PagingClauseTemplate` throws `InvalidOperationException` rather than
  silently running unpaged.
- Paging docs corrected: paging costs *more* total server work, not less. `OFFSET n` is not a seek,
  so a full walk scans roughly `N² / (2 × PageSize)` rows. The previous claim that paging avoids
  "streaming everything to the client" was never true — extraction streams off a `DbDataReader`
  either way. What paging buys is bounded per-query work, shorter transactions and resumability.

### Deprecated

- `ServerOffset` now forwards to `SkipItemCount` — the two were always the same idea, so there is
  one value rather than two that can disagree. Out-of-`int` values throw rather than truncating.
- `ServerLimit` now forwards to `PageSize`. **Its meaning changed** from "total rows to return" to
  "rows per round-trip"; use `MaximumItemCount` for the total. Renamed rather than reused so the
  change surfaces at the call site instead of silently returning a different number of rows.

### Removed

- The `ServerOffset`-without-`ServerLimit` error. An offset no longer needs a page size, because it
  is now just `SkipItemCount`.

### Fixed

### Security


## [0.10.0] - 2026-09-04

> **The server-side paging API is provisional in this release.** It arrived across #385, #391
> and #393 and is still settling: in particular the two guard clauses — activating paging
> without a `PagingClauseTemplate`, and setting `ServerOffset` without `ServerLimit` — both
> currently throw, and a follow-up (#398) may relax them so a missing template degrades to
> client-side work with a warning instead. Relaxing a throw cannot break code that already
> works, so nothing here is a trap; but if you are building on paging, expect it to get more
> permissive rather than less. The surface itself — `PagingClauseTemplates`, the `string?`
> template property, the builder overload — is settled.

### Added

- **Third-party notices are now packed into the package (#353).** `THIRD-PARTY-NOTICES.md`
  ships at the package root, listing every runtime dependency and its licence. Previously the
  `license-audit` workflow generated this file as a build artifact only, so consumers installing
  the package never received it.

- **Configuration via options records.** `DbExtractor<TRecord>` and `DbLoader<TRecord>` gained
  constructors taking `DbExtractorOptions` / `DbLoaderOptions`, so configuration travels through the
  constructor instead of post-construction property assignment. One overload per existing input
  shape; defaults live on the records' property initializers, so no constructor can diverge from
  them. Neither record is generic — no setting depends on the record type.

  Purely additive: every existing constructor and property still works exactly as before.

  `DbLoaderOptions` carries no `IsDryRun`. That member implements `ISupportDryRun.IsDryRun`, which
  declares a `set` accessor and so cannot become `init`-only while that interface stands; set it on
  the loader after construction as before.

### Changed

- **BREAKING: `ServerLimit` alone now enables paging; `ServerOffset` defaults to `0` (#393).**
  Setting only `ServerLimit` was previously a silent no-op — a caller who asked for 50 rows got
  *every* row, with no error. `ServerLimit` now switches server-side paging on, and an unset
  `ServerOffset` means "start at the top".

  The mirror case is now loud too: setting `ServerOffset` without `ServerLimit` throws
  `InvalidOperationException` instead of silently ignoring the offset, since no page size can be
  inferred (every template in `PagingClauseTemplates` references `@PageLimit`).

  Every paging caller must already revisit this code in this release to satisfy #391's template
  requirement, so neither change can be reached silently.

- **BREAKING: `PagingClauseTemplate` now defaults to `PagingClauseTemplates.None` (`null`)
  instead of the `LIMIT`/`OFFSET` form (#391).** There is no portable paging syntax, so any
  default privileges some engines and breaks the rest: the old default produced
  `Incorrect syntax near 'LIMIT'` on SQL Server, Oracle and Db2. Activating server-side paging
  (setting **both** `ServerOffset` and `ServerLimit`) without choosing a template now throws
  `InvalidOperationException` naming the presets, instead of emitting SQL only some engines
  accept.

  Callers on SQLite, PostgreSQL or MySQL who relied on the implicit default must name a
  template — a one-line change, e.g. `PagingClauseTemplate = PagingClauseTemplates.Sqlite`.
  `DbExtractor<TRecord>.PagingClauseTemplate` changed from `string` to `string?`, and the
  builder's `PagingClauseTemplate(string?)` now accepts `null`, meaning `None`.

- **Source-compatibility note: a positional `null` in the transaction argument is now ambiguous.**
  The new options overloads sit in the same position as the optional `DbTransaction?`, so a call
  passing an untyped `null` there — `new DbExtractor<T>(conn, "SELECT 1", null)` — no longer compiles
  (CS0121), because `null` is convertible to both `DbTransaction?` and the options record.

  **This is source-only.** Already-compiled callers are unaffected: the signatures they bound to are
  unchanged and still present, so there is no binary break and PackageValidation reports none.

  The fix at each affected call site is to name the argument — `transaction: null` — or simply omit
  it, since it is optional. Passing an explicit positional `null` for an optional parameter is
  unusual; no call site in this repository does it.

### Deprecated

### Removed

### Fixed

### Security

## [0.9.1] - 2026-08-21

PATCH release. Sweeps the repo's open code-scanning alerts down from 67
to ~0 across four tools (InspectCode, Scorecard, zizmor, Semgrep OSS).
Zero source or public-API changes; consumers see no behavioural
difference.

### Security

- **Semgrep OSS (1 alert cleared)** — `csharp-sqli` false positive on
  `benchmarks/Wolfgang.Etl.DbClient.Benchmarks/BenchmarkContext.cs`
  where a compile-time DDL literal is assigned to `DbCommand.CommandText`.
  Added `.semgrepignore` covering `benchmarks/` and three
  test-fixture SQL utilities whose only callers pass string literals
  (`tests/**/Fixtures/`, `tests/**/TestDb.cs`,
  `tests/**/EtlPipelineDbClientExtensionsTests.cs`).
- **zizmor (3 alerts cleared)** — `zizmor/excessive-permissions` on
  `benchmarks.yaml` (`contents: write`, `deployments: write`) and
  `integration-status.yaml` (`contents: write`) at the workflow top level.
  Moved the writes down to only the jobs that push to `gh-pages`
  (5 `benchmark-*` jobs and the `publish-status` job); top level is now
  `read-all`.
- **Scorecard TokenPermissionsID (10 alerts cleared)** — added top-level
  `permissions: read-all` to `aot-smoke.yaml` and `workflow-security.yaml`
  (both previously had no top-level block), and moved
  `security-events: write` in `semgrep.yaml` down to the `semgrep` job.
  Four remaining job-level `contents: write` findings on legitimately-writing
  jobs (`release.yaml` trigger-docs + update-release-artifacts,
  `docfx.yaml` build-and-deploy, `shadow-baseline.yaml` publish) are
  filtered from the SARIF at upload time — see below.
- **Scorecard SARIF filter (18 alerts cleared)** — added a
  `jq`-based filter step to `scorecard.yaml` that drops known-noise rules
  before the SARIF is uploaded to Code Scanning, following the durable
  pattern from session-memory `reference_scorecard_dismissal_not_durable`
  (UI dismissals decay across Scorecard's weekly re-runs due to
  fingerprint drift). Rules dropped:
  - `FuzzingID` — Scorecard misses SharpFuzz; `fuzz.yaml` already runs it.
  - `CIIBestPracticesID` — no OpenSSF badge; policy call.
  - `CodeReviewID` — solo-maintainer repo; N-approvers not viable.
  - `PinnedDependenciesID` — flags `dotnet`/`pip`/`bash download-then-run`,
    none of which are hash-pinnable.
  - `BranchProtectionID` — repo uses org rulesets, not classic branch
    protection.
  Plus 4 specific TokenPermissionsID findings on `release.yaml:1013`,
  `release.yaml:1054`, `docfx.yaml:59`, `shadow-baseline.yaml:120` where
  the job genuinely needs `contents: write`.
- **InspectCode noise floor (26 alerts cleared)** — extended
  `Wolfgang.Etl.DbClient.slnx.DotSettings` with `DO_NOT_SHOW` for four
  style-noise rules that fired only in `tests/`, `examples/`, and
  `benchmarks/` — never in `src/`:
  `RedundantTypeArgumentsOfMethod`, `RedundantCast`,
  `RedundantArgumentDefaultValue`, and `PartialTypeWithSinglePart`
  (the last is a source-generator false positive:
  `DbTableGenerator` requires consumer records to be declared `partial`).
- **InspectCode safety findings (9 alerts cleared)** — narrow-suppressed
  9 verified false positives via per-file `// ReSharper disable` comments,
  each with an inline justification:
  - 7× `AccessToDisposedClosure` across the three Coyote cancel-race
    concurrency test files (`Task.WaitAll` joins the closures before the
    `using` scope exits — InspectCode can't see through the join).
  - 1× `ShortLivedHttpClient` in `SourceLinkPdbTests.cs` (one-shot
    per-test client; socket exhaustion doesn't apply to tests).
  - 1× `UsingStatementResourceInitialization` in the same file
    (initializer is a value-type construction that cannot throw).

### Fixed

- No source-code fixes in this release. All changes are CI/analyzer
  configuration and test-code comment annotations.

## [0.9.0] - 2026-08-13

### Changed

- Adopted **ETL core 0.22.0** — `Wolfgang.Etl.Abstractions` 0.21.0 -> 0.22.0, along with the
  test-only `Wolfgang.Etl.TestKit` / `Wolfgang.Etl.TestKit.Xunit` references. 0.22.0 is the release in
  which the TestKit packages were folded into the ETL-Abstractions repository and now build and ship
  from there. The public API of all four core packages is unchanged.
- Inherited from Abstractions 0.22.0: the `await foreach` sites in `ExtractorBase` and
  `TransformerBase` now use `ConfigureAwait(false)`, removing a sync-over-async deadlock risk for
  consumers on the `net462` and `netstandard2.0` targets that drive the pipeline from a
  synchronization context. No behavioural change on the modern targets.

## [0.8.0] - 2026-08-11

MINOR release. Adds per-row error handling on `DbExtractor<T>`, plus
reproducible-build and shadow-testing CI infrastructure. Includes one
narrow, behaviorally-inert protected-API removal — see below.

### Added

- **`DbExtractor<T>.ErrorPolicy`** — per-row error handling for
  extraction, driven by `Wolfgang.Etl.ErrorPolicies`:
  ```csharp
  using Wolfgang.Etl.ErrorPolicies;

  var extractor = new DbExtractor<Order>(conn, sql)
  {
      ErrorPolicy = ItemErrorPolicy.SkipAndLog(logger)
      // or .Skip / .Abort / .SkipAndDeadLetter(deadLetters) / .SkipDeadLetterAndLog(...)
  };
  ```
  A row that fails to materialize (a Dapper column-mapping error) is
  routed through the policy instead of always aborting the whole
  extraction. Default is `Abort`, preserving prior behavior when unset.
  `DbLoader<T>` wiring is deferred — batching + auto-transaction make
  Skip-vs-Abort semantics non-trivial there.
- **Reproducible builds**: `<PathMap>` normalizes MSBuild-computed
  intermediate paths so Linux and Windows CI produce byte-identical
  PDBs (#255); `release.yaml` now publishes a
  `reproducible-build-manifest.json` release asset with the sha256 of
  every shipped artifact, and `docs/REPRODUCIBLE-BUILD.md` documents
  the consumer-side verification recipe (#155).
- **Shadow testing** (#130) — a sample consumer workload + CI
  (`shadow.yaml`, `shadow-baseline.yaml`, `shadow-regression.yaml`)
  captures latency/allocation/GC metrics on a nightly cadence and gates
  PRs against a recorded baseline.

### Changed

- Bumped `Wolfgang.Etl.Abstractions` to `0.21.0` and
  `Wolfgang.Etl.TestKit`(.Xunit) to `0.14.0`.

### Removed

- **`DbExtractor<T>.CreateProgressTimer` / `DbLoader<T>.CreateProgressTimer`
  protected overrides removed** (subclassing surface only — no public,
  init, or settable member disappears). These existed solely to support
  an `internal`, test-only `IProgressTimer`-injection constructor that's
  also removed; for every external caller the override always delegated
  straight to the base implementation, so behavior is unchanged for any
  real consumer. Progress-timer testability now goes through
  `Wolfgang.Etl.TestKit`'s `ManualProgressTimerCore` +
  `WithManualProgressTimer<>`, which needs no per-library plumbing.
  Flagged as a MINOR-triggering change out of caution, since these were
  part of the shipped API surface since v0.2.1.

### Fixed

- `SQLitePCLRaw.lib.e_sqlite3` GHSA-2m69-gcr7-jv3q (high severity) —
  pinned `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 across dev/test-only
  projects. Never affected the shipped package.

## [0.7.1] - 2026-08-09

Patch release — one correctness fix, one packaging-safety addition,
plus docs/CI cleanup. No public API changes.

### Added

- **`PackageValidation` gate**, enabled via `EnablePackageValidation` +
  `PackageValidationBaselineVersion` (pinned to `0.7.0`, the last
  published version) — now diffs the packed public API surface against
  the previous release on every pack, catching unintentional
  binary-breaking changes before they ship (#290).

### Changed

- CI: bumped the `github-actions` dependency group (checkout, setup-dotnet,
  upload-artifact, paths-filter, attest-build-provenance,
  action-gh-release, scorecard-action, zizmor-action) to their latest
  pinned SHAs (#306).
- README: corrected the Supported Frameworks TFM list to match the
  csproj's actual multi-targeting (#288).
- README: added the canonical publication badge set (nuget-version,
  nuget-downloads, wf:pr, wf:release) (#286).

### Fixed

- **`DbLoader<T>.InsertBatchSize > 1` corrupted SQL when one bound
  parameter's name was a textual prefix of another** (e.g. `@Total` /
  `@TotalTax`) — the per-parameter `String.Replace` rewrite matched the
  shorter name inside the longer one, producing a parameter Dapper
  never bound and throwing at execution time. The multi-row row
  template is now rebuilt from pre-scanned parameter spans instead of
  sequential substring replacement, which also removes the per-row
  full-template rescans (#279).
- CHANGELOG: corrected the v0.7.0 entry date to the actual NuGet
  publish date (#308).
- Fixed a stale comment describing the version-picker's auto-select
  behavior in the docfx template (#291).

## [0.7.0] - 2026-08-08

Additive release. New runtime schema-validation type, opt-in
extractor/loader property that wires it in, `[DbKey]` attribute + full
Update/Delete source-generator emit, and a fluent `EtlPipeline` chain
surface over `DbExtractor` / `DbLoader`.

### Added

- **`DbSchemaValidator.Validate<TRecord>` + `ValidateAsync<TRecord>`** —
  provider-agnostic pre-flight schema check via
  `SELECT * FROM <table> WHERE 1 = 0`. Error message names both the
  missing columns AND the columns the table actually has, so
  copy-paste typos surface at the top of a batch instead of mid-loop
  (#20).
- **`DbExtractor<T>.ValidateSchemaOnStart` / `DbLoader<T>.ValidateSchemaOnStart`**
  — opt-in bool that runs `DbSchemaValidator.ValidateAsync` before
  reading / writing the first row. Default `false`; adds a single
  zero-row round-trip when enabled (#20).
- **`[DbKey]` attribute + source-generator `Update` / `Delete` const
  emit** — completes source-generator CRUD (`Insert` / `Select` /
  `Update` / `Delete` + `Bind`). Composite keys preserve declaration
  order in the WHERE clause; `Update` emitted only when the type has
  ≥ 1 `[DbKey]` AND ≥ 1 non-key column; `Delete` emitted only when the
  type has ≥ 1 `[DbKey]` (#23).
- **Fluent `EtlPipeline` chain surface** — `DbExtractor<T>(…)` and
  `DbLoader<T>(…)` extension methods on `EtlPipeline` /
  `IEtlPipeline<T>`, returning `IDbExtractorBuilder<T>` /
  `IDbLoaderBuilder<T>` with fluent setters that mirror every
  configurable property on the underlying extractor / loader
  (`CommandType`, `ManageConnection`, `Parameters`, `ServerOffset`,
  `ServerLimit`, `PagingClauseTemplate`, `TotalCountQuery`,
  `ErrorHandling`). Composes cleanly with sibling ETL packages
  (JSON, CSV, FixedWidth, Transformers). Requires
  `Wolfgang.Etl.Abstractions` 0.16.0 (#280).
- **`docs/HOT-PATH-ALLOCATION.md`** — snapshot explaining why DbClient
  intentionally has no zero-alloc guards (Dapper + ADO.NET +
  `IAsyncEnumerable` state machines allocate by contract) (#147).
- **`docs/etl-pipeline.md`** — full reference for the EtlPipeline
  chain surface + `examples/Wolfgang.Etl.DbClient.Example.EtlPipeline/`
  runnable console app (#280).

### Changed

- Bumped `Wolfgang.Etl.Abstractions` to `0.16.0`.

## [0.6.0] - 2026-07-06

Feature-rich release: dry-run mode, source-generator scaffolding, batching + paging + connection lifecycle knobs, plus a broad InspectCode fix pass.

### Added

- **IsDryRun mode for `DbLoader`** — validate pipelines without writing to the target DB. Requires Abstractions 0.15.0 (#21).
- **`DbLoader.BatchCommitSize`** — chunked transactional commits during long loads (#22).
- **Row-level error handling on `DbLoader`** — continue-on-error / stop-on-error policies with per-row failure callbacks (#24).
- **`DbExtractor.CountAsync()`** convenience method for pre-flight sizing (#32).
- **`ManageConnection` on `DbExtractor` and `DbLoader`** — opt-in library-owned connection lifecycle (#31).
- **`DbExtractor.Parameters` property** — output-parameter support for stored procedures (#27).
- **Server-side paging on `DbExtractor`** — `ServerOffset` + `ServerLimit` + `PagingClauseTemplate` for streaming large tables without buffering (plus optional `TotalCountQuery` for pre-flight sizing) (#33).
- **Multi-row `INSERT` batching on `DbLoader`** — SQL Server / PostgreSQL / MySQL / MariaDB batch-insert paths (#30).
- **Source generator scaffolding** for compile-time SQL generation from `DbTableAttribute` / `DbColumnAttribute` — the generator DLL ships embedded in the main package under `analyzers/dotnet/cs` (#23).

### Changed

- Bumped `Wolfgang.Etl.Abstractions` to `0.15.0` and `Wolfgang.Etl.TestKit.Xunit` to `0.10.0`.
- Silenced non-applicable analyzer rules on the SourceGenerator project (RS1036 / RS2008 / NU1701) — quieter analyzer set for source-gen code (#213).
- Suppressed VSTHRD200 on the `AsAsyncEnumerable` adapter (#213).

### Fixed

- Real-bug findings from the InspectCode audit (#202 follow-up).
- Remaining InspectCode findings via actual source changes rather than suppressions (#202 follow-up).
- Replaced file-scope ReSharper disables with documented JetBrains annotations (#202 follow-up).
## [0.5.0] — robustness + extractor ergonomics + source generator

### Added — DbLoader robustness
- `IsDryRun` ([#21](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/21)) — when `true`, the loader runs the full pipeline (enumerate, evaluate skip/max, increment counters, fire progress) but skips both `ExecuteAsync` call sites (per-row + batched). The DB is not modified. Implements `ISupportDryRun` from Abstractions 0.15.0.
- `ErrorHandling` ([#24](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/24)) — new `RowErrorHandling` enum (`Abort` default, `Skip`). In `Skip` mode the per-row catch fires a new `RowFailed` event (`EventHandler<RowFailedEventArgs<TRecord>>`), advances `CurrentErrorCount`, and continues. `MaxErrorCount` caps the threshold; `OperationCanceledException` always propagates. Per-row path only — batched mode still aborts.
- `BatchCommitSize` ([#22](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/22)) — commit every N successfully-loaded rows in auto-managed-transaction mode. Failures roll back only the current chunk; previously-committed chunks survive. Trades all-or-nothing semantics for resumability + lower undo-log pressure.
- `InsertBatchSize` ([#30](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/30)) — replaces N per-row `INSERT`s with a single multi-row `INSERT … VALUES (…), (…), …` statement per chunk. Requires `CommandText` to end with `VALUES (template)`; properties bound by reflection via case-insensitive name match. Mutually exclusive with `BatchSize > 1`, `IsDryRun`, stored-procedure `CommandType`.

### Added — Extractor ergonomics
- `CountAsync(CancellationToken)` ([#32](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/32)) — runs the configured `TotalCountQuery` (or default) and returns the row count without streaming. Side-effect-free on extractor state.
- `Parameters` (`DynamicParameters?`) ([#27](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/27)) — opt-in override of the dictionary-built parameters. Useful for stored procedures with OUT / INOUT parameters that need explicit `ParameterDirection`. Both data and default-count queries honor the override.
- `ServerOffset` / `ServerLimit` (`long?`) + `PagingClauseTemplate` ([#33](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/33)) — server-side paging via configurable clause template. Default `LIMIT @PageLimit OFFSET @PageOffset` fits SQLite/Postgres/MySQL; SQL Server users set the OFFSET/FETCH form. Engages only when both `ServerOffset` and `ServerLimit` are non-null.

### Added — Connection lifecycle
- `ManageConnection` on both `DbExtractor` and `DbLoader` ([#31](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/31)) — when `true`, opens a closed connection before the first command and **closes (not disposes)** it after. The connection returns to the pool. Already-open connections are left open. Ignored on the owned-connection ctor path (which still disposes).

### Added — Source generator
- New project **`Wolfgang.Etl.DbClient.SourceGenerator`** ([refs #23](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/23)) — `netstandard2.0`, `IIncrementalGenerator`, packed into the runtime NuGet under `analyzers/dotnet/cs` so it ships transparently to consumers.
- New public attributes `[DbTable("name")]` and `[DbColumn("col", Skip = bool)]`.
- For every `partial class`/`partial record` decorated with `[DbTable]`, the generator emits a partial with `public const string Insert = "INSERT INTO … VALUES (…)";` and a reflection-free `public static void Bind(DynamicParameters, T record)` helper. `Update` / `Delete` / `Select` + `DbLoader` / `DbExtractor` wire-up tracked as follow-up.

### Dependency bumps
- `Wolfgang.Etl.Abstractions` 0.13.0 → **0.15.0** — introduces `ISupportDryRun` interface; `Report.TotalItemCount` moved to base (`DbReport` now inherits it instead of declaring locally).
- `Wolfgang.Etl.TestKit` 0.7.0 → **0.9.0**, `Wolfgang.Etl.TestKit.Xunit` 0.6.0 → **0.9.0**.
- `Microsoft.Bcl.AsyncInterfaces` 10.0.5 → **10.0.9** (TestKit 0.9.0 floor).

### CI / release hardening
- **`release.yaml` integration-test gate** ([#206](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/206)) — added a 5×2 RDBMS × TFM matrix (sqlserver / postgres / mysql / mariadb / sqlite × net8.0 / net10.0) + aggregator job between `pack-and-validate` and `publish-nuget`. Closes the gap where a release could ship while the integration suite was red.

### Code quality
- **InspectCode hygiene** ([refs #202](https://github.com/Chris-Wolfgang/Etl-DbClient/issues/202)) — first canonical pass on this repo. Real bug fixes: `AccessToDisposedClosure` in a test (TotalCountQuery captured a `using var conn`), `S127` loop-counter mutation in `ExtractTemplateParamNames`. Code-quality cleanups: redundant `?.` on non-null benchmark `Dispose` receivers, redundant casts/qualifiers/usings, always-true nullable-API checks, `NonReadonlyMemberInGetHashCode` on fixtures (converted to `init`). Reflection-consumed surfaces now carry `[PublicAPI]` (src) or `[UsedImplicitly]` (benchmarks/examples/tests) from `JetBrains.Annotations` to document why ReSharper can't see consumers — replacing the broader `// ReSharper disable` comment approach. `jb inspectcode` clean (0 findings, 0 errors).

### Breaking
- **Removed**: `DbReport.TotalItemCount` (locally declared) — the property is now inherited from `Report` (Abstractions 0.14+ moved it to the base). Callers reading `report.TotalItemCount` continue to work via inheritance lookup; recompilation against Abstractions 0.15+ may be needed for tightly-coupled consumers.

## [0.4.0] — production-readiness knobs

### Added
- `DbExtractor<TRecord>.CommandTimeout` / `DbLoader<TRecord>.CommandTimeout` (`TimeSpan?`) — controls how long each underlying command can run before timing out. `null` (the default) falls back to the ADO.NET provider default (~30 s). Negative values throw `ArgumentOutOfRangeException`. Wired through every Dapper call site (extractor's main query + default count query; loader's per-record and batched `ExecuteAsync`).
- `DbExtractor<TRecord>.CommandType` / `DbLoader<TRecord>.CommandType` (`System.Data.CommandType`) — enables stored-procedure invocation. Default `CommandType.Text` preserves prior behavior. Set to `CommandType.StoredProcedure` and `CommandText` becomes the sproc name; Dapper binds parameters from the POCO properties as usual. Not wired to `DefaultTotalCountQuery` (that path wraps `CommandText` in `SELECT COUNT(*) FROM (...)` which is incompatible with sprocs by construction; supply a custom `TotalCountQuery` instead).
- `DbExtractor<TRecord>(DbProviderFactory, string connectionString, string commandText, ILogger?)` — owned-connection ctor overload. The extractor creates the connection via the supplied `DbProviderFactory`, opens it lazily before the first command, and disposes it when extraction completes (or throws). Saves callers the `using var conn = …; await conn.OpenAsync();` boilerplate for one-off scenarios.
- `DbLoader<TRecord>(DbProviderFactory, string connectionString, string commandText, ILogger?)` — owned-connection ctor overload with the same semantics (open lazily, dispose at end). Defaults to auto-managed transaction.

[Unreleased]: https://github.com/Chris-Wolfgang/Etl-DbClient/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/Chris-Wolfgang/Etl-DbClient/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/Chris-Wolfgang/Etl-DbClient/releases/tag/v0.5.0
[0.4.0]: https://github.com/Chris-Wolfgang/Etl-DbClient/releases/tag/v0.4.0

## [0.3.0] — code-review pass + integration-test surface

### Added
- `DbClientOptions.StrictColumnMapping` (default `false`) — opt-in flag that converts unmapped result-set columns from Dapper's silent-drop default into a descriptive `InvalidOperationException` naming the column and target type. Preserves out-of-the-box Dapper behavior by default. Useful for catching `[Column]` typos during development.
- `DbLoader<TRecord>.BatchSize` — new batched-execution knob. Defaults to `1` (one round-trip per record, unchanged behavior); raise it to amortize per-call overhead on networked databases. `SkipItemCount` / `MaximumItemCount` are still honored at per-record granularity.
- Integration-test surface (Testcontainers): SQL Server, PostgreSQL, MySQL, MariaDB, CockroachDB, SQLite across net8.0 and net10.0, gated by the `Integration (all)` aggregator on every PR.
- Per-RDBMS dynamic shields.io status badges + per-RDBMS BenchmarkDotNet charts published to `gh-pages`.

### Changed
- **Major perf win** in `DbCommandBuilder`: per-`Type` cache of reflection results + pre-built SQL strings. BuildSelect / BuildInsert / BuildUpdate went from ~7–8 µs to ~5 ns each (~1500× faster, zero allocations on cache hits).
- **Major perf win** in `ColumnAttributeTypeMapper`: replaced per-column reflection + LINQ scan with a single-pass `Dictionary<string, PropertyInfo>` built once at `Register<T>()`. 6-column lookup went from 15.69 µs to 202 ns (~77× faster).
- `DbExtractor` now takes a defensive copy of the caller's parameter dictionary at construction; cached `DynamicParameters` wrapper for reuse across the data query and the default total-count query.
- `DbLoader.LoadWorkerAsync` split into named caller-managed vs auto-managed transaction helpers for readability.
- Thread-safety hardening across extractor + loader (`Interlocked.CompareExchange` for the progress-timer wiring one-shot; documented single-use contract).
- CI Stage 1 / 2 / 3 path-gated via stub jobs (docs-only PRs skip the multi-TFM matrix while keeping ruleset-required check names green).

### Fixed
- `DbReport` restored the 4-arg constructor as a `[EditorBrowsable(Never)]` binary-compat shim so already-compiled consumers don't hit `MissingMethodException`.

## [0.2.x] and earlier

See git history.

[0.3.0]: https://github.com/Chris-Wolfgang/Etl-DbClient/releases/tag/v0.3.0
