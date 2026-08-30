---
description: Run the eight-angle doc-drift audit (docs, comments, canon vs code) with adversarial verification
---

# /drift-audit

Audit every doc, comment, and canon rule against the code as it is now. First run
(2026-08-30) confirmed 137 findings across 44 files after the mechanical gates all passed —
the gates catch named identifiers and links; this audit catches meaning. Re-run quarterly,
after any big refactor or subsystem removal, and before publishing anything that cites the
repo as evidence.

## Method

Run eight parallel sweep angles (subagents or a workflow), each returning findings as
{file, line, verbatim quote, what the code actually does, evidence file:line, severity
wrong|stale|misleading|nit, suggested fix}:

1. **Status ledgers** — every "Implemented / Not Yet / planned / today / does not yet" in
   README, docs/STATUS.md, docs/architecture.md, docs/BRD.md, checked against code + tests.
2. **Comment references** — every "See docs/…" or named-section pointer in code/config;
   verify the target file and heading still exist.
3. **Inline paths** — backtick paths in prose exist on disk (now also mechanical:
   `.claude/scripts/check-doc-paths.sh`).
4. **Numeric claims** — counts of tests, slices, analyzers, features, rules vs `grep -c`.
5. **Walkthroughs** — docs/TOUR.md + docs/code-flows/*: every class, method, route, queue,
   status value exists under that exact name; mechanisms match the current code.
6. **Catalogs vs topology** — docs/event-catalog.md vs NextAurora.Contracts/Events and the
   real Program.cs wiring.
7. **Canon vs code** — every "every X does Y" in CLAUDE.md + frontend/CLAUDE.md: grep for
   counterexamples; distinguish "rule not followed" from "rule describes old code".
8. **Operations docs** — dev-loop, deployment, observability, frontend docs vs AppHost,
   compose, workflows, package.json.

Then verify each unique finding adversarially (default = refute; confirm only if the quote
is really there AND the code really contradicts it). Historical records kept as history —
the war story, superseded decision sections, dated changelog tables, `docs/code-review-fixes.md`
— are accurate history, NOT drift.

## Completion criteria

- Report counts by severity and cluster; fix (PR) or file each confirmed finding.
- Every fix re-verified against code before writing; tombstone patterns never reintroduced.
- If a sweep reveals a removal that never got tombstoned, add the group in the same PR
  (CLAUDE.md "Removal ships its tombstone").
