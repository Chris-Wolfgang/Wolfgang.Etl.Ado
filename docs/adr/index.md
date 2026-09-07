# Architecture Decision Records

Non-obvious design choices in `Wolfgang.Etl.DbClient` — the "why" that would otherwise get lost between the code and the PR discussion. Format loosely follows [MADR](https://adr.github.io/madr/) via [TEMPLATE.md](TEMPLATE.md).

New ADRs land alongside the PR that introduces the decision; the ADR is part of the review, not a follow-up.

## Index

| ID | Title | Status | Date |
|---|---|---|---|
| [0001](0001-source-generator-ships-in-runtime-nuget.md) | Source generator ships in the runtime NuGet, not as a separate package | Accepted | 2026-07-01 |
| [0002](0002-skip-max-and-paging.md) | Skip and Max are the contract; paging is transport tuning | Proposed | 2026-09-07 |

## Statuses

- **Proposed** — under review; not yet reflected in code.
- **Accepted** — in force. New code should conform.
- **Deprecated** — no longer in force but no replacement chosen. Do not extend.
- **Superseded** — replaced by a later ADR (link to it in the header).

## Adding an ADR

1. Copy `TEMPLATE.md` to `NNNN-short-title.md` using the next number in sequence.
2. Fill in Context, Decision, Consequences, Alternatives, Notes.
3. Add a row to the table above.
4. Land it in the same PR as the code change that motivates it.
