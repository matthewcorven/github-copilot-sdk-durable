# Provenance

## Source

| Field | Value |
| --- | --- |
| Repository | `microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator` |
| URL | https://github.com/microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator |
| License | MIT (`upstream/LICENSE`) |
| Pinned commit | `b139b24044565b02317821b2590602f924c43311` |
| Commit date | 2026-08-11 (committer date, GitHub API) |
| Commit subject | `fix(deps): resolve all known dependency vulnerabilities` |
| Vendored on | 2026-11-05 |

The commit is `main` at the time of vendoring, resolved with
`gh api repos/microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator/commits/main --jq .sha`.

## Why two agents and not one

`agents/coverage/main.py` is the agent the band is about — it is the one with the MCP
tools (NPI registry + CMS coverage) and the structured `CoverageResult`. It cannot on
its own produce a prior-authorization *decision*, so `agents/synthesis` is vendored and
deployed as well: it owns the gate-based APPROVE/PEND rubric and the audit trail, and
it is the agent that turns a coverage assessment into a recommendation a human can
accept or override. `clinical` and `compliance` are **not** vendored or deployed; their
absence is recorded in every run (`40-final-result.json` → `audit_trail.agents_absent`).
D2/D3 are where the remaining specialists arrive.

## Vendored files

Fetched with
`gh api "repos/microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator/contents/<path>?ref=<sha>"`
which returns the blob at that commit. Nothing was edited after download.

| Path in this directory | Path in the accelerator | Blob SHA-1 | Size | `shasum -a 256` |
| --- | --- | --- | --- | --- |
| `LICENSE` | `LICENSE` | `22aed37e650bbf933b6983cda9c2c5db65dcdd04` | 1074 B | `7df20dcdf9197e9945c14858d41c60f11b52b93e5b69e2b63416b874d598d322` |
| `agents/coverage/main.py` | `agents/coverage/main.py` | `c5370dbfb9ada340d2ed2ecd8f6bbac48cf896a2` | 8466 B | `67038734e07780ab21fbf40f3833e8377cb3a8159cfed17ec913d48568697ee6` |
| `agents/coverage/schemas.py` | `agents/coverage/schemas.py` | `8d145bd3624da6b543a0c043dd2ed1d628a30091` | 1905 B | `ac1089c016c8a10868915aff5b2dd9af5ecf4f93ef5de48ec7a694a9f5f04d80` |
| `agents/coverage/agent.yaml` | `agents/coverage/agent.yaml` | `6463ff4f8ba9e0ee2a79fa9ed91eb3371e5b871b` | 1628 B | `d7fe507d3dbf7b2bd614eb77e386a3500a5be4433a6fc2d407ddef2be52750a1` |
| `agents/coverage/requirements.txt` | `agents/coverage/requirements.txt` | `c39b57d18498b35bdfe8d6df2ee8e39b00917b89` | 1711 B | `82182bb9f4758437ddb33136bfe9abea314aa798dab717edc8d663dfa66db4ac` |
| `agents/coverage/Dockerfile` | `agents/coverage/Dockerfile` | `70953d2e9b9ae890d7456288d8bba8c86af80680` | 1350 B | `209ccb0796d9a388ef629559dccd921c375ba016d8046f5992406687519177ee` |
| `agents/coverage/.dockerignore` | `agents/coverage/.dockerignore` | `b6cf5f03915e87f0609c2df330b54f34e9a99e1f` | 24 B | `947f24f869242d60d487237e81d2a7b27779f30d7944c8133df06fc245c2c8ce` |
| `agents/coverage/skills/coverage-assessment/SKILL.md` | `agents/coverage/skills/coverage-assessment/SKILL.md` | `53e474fd3e5aad175aea6b9c89b06bdc49435375` | 22262 B | `d21c96eb59af016f257cbc479fd392a100cf51360647e6e060f1d4eebd27024c` |
| `agents/synthesis/main.py` | `agents/synthesis/main.py` | `7c0be33345e52deefb9b7f0141439504f3f0fec2` | 6947 B | `672679ef2de4b9ced0f600513a4ec88cdef7c2560557e6f323c8ad890a2a1c0b` |
| `agents/synthesis/schemas.py` | `agents/synthesis/schemas.py` | `7c86a51ecc2d22266736b711e98681a87f2ec9ce` | 999 B | `4a70571feb94eb02db7886200d0d07b5284d1dbc9a994a68c116d8acd1eff08e` |
| `agents/synthesis/agent.yaml` | `agents/synthesis/agent.yaml` | `8e3d5166716bc7ce685b415c15427313de6e9729` | 1204 B | `b96d78c768deb026c39b7c7e7a4c2f793752562a7b76d917059bd014dbf0370e` |
| `agents/synthesis/requirements.txt` | `agents/synthesis/requirements.txt` | `c867e762266777dc562edb491d83d904284f3a65` | 1207 B | `52f7e26c922b94a99ca3de601a629d5614e2754e37e2231c18d59aed9d9d6322` |
| `agents/synthesis/Dockerfile` | `agents/synthesis/Dockerfile` | `291b06fc0306596aa69df753d2845684e2674fc6` | 1349 B | `ace248a91608ba023dddbc617ac91949cae9b8c97f287c9266a21b2f335cbaaa` |
| `agents/synthesis/.dockerignore` | `agents/synthesis/.dockerignore` | `b6cf5f03915e87f0609c2df330b54f34e9a99e1f` | 24 B | `947f24f869242d60d487237e81d2a7b27779f30d7944c8133df06fc245c2c8ce` |
| `agents/synthesis/skills/synthesis-decision/SKILL.md` | `agents/synthesis/skills/synthesis-decision/SKILL.md` | `bedbb69d99c98941dc0d7f33bcf02daf1b50bd3d` | 16708 B | `97045634d5c8a30727876e689a1ffb63de8b26212fab6114449837a09aacc3b6` |

## Byte-identity check

`git hash-object <file>` on each vendored file returns the blob SHA-1 in the table.
A re-fetch produced identical `shasum -a 256` values. To repeat:

```sh
SHA=b139b24044565b02317821b2590602f924c43311
for p in LICENSE \
         agents/coverage/main.py \
         agents/synthesis/main.py; do
  gh api "repos/microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator/contents/$p?ref=$SHA" \
    --jq .content | base64 -d | shasum -a 256
done
```

## The two rows that are deliberately not verbatim

| File | What is different | Why |
| --- | --- | --- |
| `agent/requirements.txt` (ours, not vendored) | `agent-framework-foundry-hosting>=1.0.0b261030` and `mcp>=2.2.0,<3` instead of the vendored `>=1.0.0b260730,<2` and `>=1.24.0,<2` | With the vendored pins, `agent-framework-core` 1.24.0 imports `mcp.Client` from an mcp 1.x package and **every MCP tool call fails**: `Failed to create MCP session: cannot import name 'Client' from 'mcp'`. See `../evidence/local-compose-path.md` for the captured failure. The vendored `requirements.txt` is kept untouched so the difference is auditable; `deploy.sh` builds from `agent/`. |
| `agent/Dockerfile` (ours) | The vendored Dockerfile with a different build context (`requirements.txt` at the context root, skills copied flat) | The build context is assembled from `upstream/` + `agent/` by `deploy.sh`; nothing about the image contents changed. |

## What is not vendored

- `clinical`, `compliance`, the FastAPI `backend/`, and the Next.js `frontend/`. Each
  is a separate specialist or a consumer of all four; D1 deploys the two agents a
  prior-auth decision needs and says so in every transcript.
- The accelerator's `infra/`. `infra/` in this directory is a reduced version of it —
  the resource shapes it needs are the accelerator's own, and the comments say which
  piece is load-bearing; a project-level App Insights *connection* is the one resource
  the accelerator has and this deployment does not (see `../infra/modules/foundry.bicep`).
- The .env files. The `DefaultAzureCredential` chain is the accelerator's; nothing
  secret is copied here. The one `.env` the local Compose run needed
  (`AZURE_AI_PROJECT_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT_NAME`) is a URL and a name,
  written at run time, never committed.
