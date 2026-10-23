# Squad Team

> github-copilot-sdk-durable

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Lead | Lead | .squad/agents/lead/charter.md | 🏗️ Active |
| Core Engineer | Core Engineer | .squad/agents/core-engineer/charter.md | 🔧 Active |
| Polyglot Engineer | Polyglot Engineer | .squad/agents/polyglot-engineer/charter.md | 🌐 Active |
| Tester | Tester | .squad/agents/tester/charter.md | 🧪 Active |
| Docs | Docs | .squad/agents/docs/charter.md | 📝 Active |
| Scribe | Scribe | .squad/agents/scribe/charter.md | 📋 Silent |
| Ralph | Work Monitor | .squad/agents/ralph/charter.md | 🔄 Monitor |
| Rai | RAI Reviewer | .squad/agents/Rai/charter.md | 🛡️ RAI |
| Fact Checker | Fact Checker | .squad/agents/fact-checker/charter.md | 🔍 Verifier |

## Project Context

- **Project:** github-copilot-sdk-durable
- **Created:** 2026-10-23
- **User:** Copilot App
- **Stack:** C# (.NET 10 file-based apps), GitHub Copilot SDK; xUnit tests; `scripts/validate.sh`
- **Built so far:** `lib/Durable.cs`, 31 C# examples, xUnit suite, docs/ (design, audits, SDK notes)
- **Initiatives:** (1) Examples in TypeScript, Python, Go, Rust, Java. (2) Unified extension library: a single builder extension binding checkpointing and fork-isolation hooks into the Copilot client pipeline.
