# Provenance

## Source

| Field | Value |
| --- | --- |
| Repository | `github/gh-aw` |
| URL | https://github.com/github/gh-aw |
| License | MIT (`upstream/LICENSE`) |
| Pinned commit | `380ae7bb0436b9b72a0be3ed026d3998914d7ee8` |
| Commit date | 2026-11-05T10:47:19Z (committer date, `gh api repos/github/gh-aw/commits/main`) |
| Vendored on | 2026-11-05 |

`main` at the time of vendoring. The SHA was read from the commits API, not from a moving
branch name, and every fetch below used `?ref=<sha>`, so a later `main` cannot change what
is on disk here.

## Vendored files

Fetched with `gh api "repos/github/gh-aw/contents/<path>?ref=<sha>" --jq .content | base64 -d`.
Nothing was edited after download.

| Path in this directory | Path in `github/gh-aw` | Blob SHA-1 | Size | `shasum -a 256` |
| --- | --- | --- | --- | --- |
| `LICENSE` | `LICENSE` | `28a50fa22639e32febe14e4ffc7a732b0ba8c90a` | 1060 B | `2510b446bc1f0cf9702453075d20cd88631e20e5642658edb7325d9c1eb534f7` |
| `pr-triage-agent.md` | `.github/workflows/pr-triage-agent.md` | `3766874718fa334b3d98ee2e7e37cd6d43d716ba` | 6298 B | `4d093197e509e06e1b1bb373ed4deee05f29dbf4f772575b19f6a284531ed1bb` |

## Byte-identity check

`git hash-object <file>` on each vendored file returns the blob SHA-1 the GitHub contents API
reported for that path at `380ae7b`. To repeat it:

```sh
SHA=380ae7bb0436b9b72a0be3ed026d3998914d7ee8
for p in LICENSE .github/workflows/pr-triage-agent.md; do
  gh api "repos/github/gh-aw/contents/$p?ref=$SHA" --jq .content | base64 -d | shasum -a 256
done
```

The two `shasum -a 256` values must match the table above.

## What is not vendored

The workflow declares `imports:` — `shared/reporting.md`, `shared/pr-review-base.md` and
`shared/otlp.md` — plus a trailing `{{#runtime-import .github/triage.md}}`. Those are resolved
by the gh-aw compiler before the workflow runs and are not part of the triage phases this band
reproduces; they are not copied. The `evals:`, `safe-outputs:`, `max-ai-credits:`, `timeout-minutes:`
and `messages:` blocks are configuration for the Actions runner and are not reproduced either.
What `baseline/` reproduces is the prose under `---`, phases 1 through 8.

## Licence note

`github/gh-aw` carries one MIT licence at the repository root; the workflow directory has no
separate notice. `upstream/LICENSE` is that root file, included unmodified. The workflow
document is vendored verbatim for reference; it is not executed by this tree.

The C# reproduction under `baseline/` is an independent work and carries this repository's licence.
