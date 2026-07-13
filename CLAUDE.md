# CLAUDE.md — Yggdrasil (`Norse.Hosting`)

## 0. Wrong Root — Halt

If you are reading this because **Yggdrasil itself is the Claude Code session root** — someone ran `claude` from inside this directory instead of `../Bifrost` — stop here. Do not read further, do not propose changes, do not run anything.

Tell the user: every Norse Architecture session starts from **Bifrost**. Org-wide settings (the `superpowers` plugin, permission rules) only apply when Bifrost is the actual session root — Claude Code never merges a submodule's own `.claude/settings.json` into a parent-launched session. Exit, `cd ../Bifrost`, and run `claude` there instead.

This repo's own `.claude/settings.json` carries a `SessionStart` hook that should already have blocked this session before this file was ever read. If you're reading this anyway, hooks were bypassed, disabled, or failed — halt regardless; this rule does not depend on the hook to hold.

---

> **Do not commit, push, or rewrite git history.** Stage edits (`git add`), show the diff, and stop — the human reviews and commits.

> **Use US English spelling** in code, identifiers, comments, docs, and commit/PR copy.

## 1. What This Repository Is

Yggdrasil is **connective tissue** — `Norse.Hosting`: the web, worker, and migration service chassis (`Norse.Hosting.Web.Server`/`.Web.Client`/`.App`/`.Worker`/`.Migrations.Service`/`.Stories.Client`/`.Stories.Server`) and the deployables built on it. It hosts the runtime endpoints that Bifrost composes but never provides itself. In the dependency chain it rides on Midgard and everything below; Himinbjorg and Heimdall ride above it. `Hosting.Stories.Client`, `Hosting.Web.Client`, and `Hosting.Web.Server` directly reference Midgard's `Infrastructure.Components.Theme.FluentUI` for theming.

This repo is scaffolded — three source projects (`src/Hosting.Web.Server`, `src/Hosting.Worker`, `src/Hosting.Migrations.Service`) and three matching test projects wired into `Yggdrasil.slnx`. **`Hosting.Migrations.Service` is live** — Task 9 of the cross-realm migrations framework rollout (`../Glitnir/docs/Platform/plans/2026-06-28-migrations-framework-identity-schema.md`) replaced its `Placeholder.cs` for real. `Program.cs` is now exactly three lines — `Host.CreateApplicationBuilder`, `builder.AddNorseMigrations()`, `RunAsync()` — and never changes regardless of how many bounded contexts join the platform, because `AddNorseMigrations()` is the source-generated extension from Urdarbrunnr that discovers every contributor at compile time. This is deliberate scaffolding, not the deployable's permanent shape (see `../Glitnir` memory on the temporary chassis) — a lift-and-shift wave is expected once the hosting abstractions this realm is waiting on land for real.

`Hosting.Web.Server` and `Hosting.Worker` remain minimal stubs: `Placeholder.cs` covering both branches of a ternary (so CI coverage is well-defined from day one) and a `Program.cs` that compiles and runs clean with no hosted services. These are replaced wholesale when the hosting abstractions from Asgard and Midgard land. Every subsequent implementation plan for this realm follows the same discipline: brainstorm → spec → plan in `../Glitnir/docs/Yggdrasil/`, greenlit by the human, then code. Each plan's REQUIRED SUB-SKILL line names `superpowers:subagent-driven-development` as the default (not a recommendation among equals — `executing-plans` is the narrow fallback for separate-session review checkpoints) paired with `superpowers:test-driven-development` — implementation here is subagent-orchestrated and test-driven, never one without the other (`../Glitnir/CLAUDE.md` §2.8).

See `../Bifrost/CLAUDE.md` (§2 The Naming Model) and `../Glitnir/CLAUDE.md` (§3 Bounded Context Map) for the full realm table and how Yggdrasil fits the rest of the cosmos.
