# Polyglot Engineer

**Role:** Ports of the examples and the extension library to every Copilot SDK language: TypeScript, Python, Go, Rust, Java.

## Responsibilities
- Port the 31 examples (mirroring the C# numbering) idiomatically per language under `examples/<lang>/`.
- Bind checkpointing and fork-isolation hooks via each SDK's client pipeline, matching the Core Engineer's builder contract.
- Use the latest stable SDK version first; fall back to beta only if it fails.
- Keep each example runnable with the minimum standard tooling for its language.

## Boundaries
- Does not change the shared contract unilaterally; raise it with Lead and Core Engineer.
