# Changes this deployment makes to the accelerator, and why

Two files here, one file changed. Everything else is the accelerator at
`b139b24` (see `upstream/PROVENANCE.md`).

## 1. `requirements.txt` — two dependency ranges

| Constraint | Accelerator | Here | Effect if left alone |
| --- | --- | --- | --- |
| `agent-framework-foundry-hosting` | `>=1.0.0b260730,<2` | `>=1.0.0b261030,<2` | The hosting build in the old range declares `mcp<2`. |
| `mcp` | `>=1.24.0,<2` | `>=2.2.0,<3` | `agent-framework-core` 1.24.0 imports `mcp.Client`, which mcp 1.x does not export. |

Together the old pins resolve to `mcp 1.30.0` and **every request that touches an MCP
tool fails**:

```
Failed to create MCP session: cannot import name 'Client' from 'mcp'
(/usr/local/lib/python3.12/site-packages/mcp/__init__.py)
```

`/readiness` still returns 200 in that state — the host is up, the tools are not — so
the failure is only visible when a real request is made. The full transcript is in
`../evidence/local-compose-path.md`.

Neither edit changes the agent's code, its prompts, its skill, or its output schema.
They choose a newer point release of a preview package, which is what
`agent-framework-core`'s own requirement (`mcp>=2.2.0,<3`) already asks for.

## 2. `Dockerfile` — the same image, a different build context

The accelerator's `agents/coverage/Dockerfile` copies `requirements.txt` from the build
context root and `skills/` flat. `infra/deploy.sh` assembles the context from
`upstream/agents/<agent>/` plus this directory, so the two `COPY` lines match that
layout. The base image, system packages, user, port, health check and command are
unchanged.

## 3. What is *not* changed, and matters

- **`main.py` is verbatim.** In particular `default_options={"response_format": …,
  "store": False}` (`upstream/agents/coverage/main.py:177`) stays. The platform owns
  conversation history; passing `store: True` would duplicate it.
- **`agent.yaml` is vendored but not consumed.** `deploy.sh` registers the version over
  the REST API with the same fields, because `azd ai agent` — the only consumer of
  `agent.yaml` — is not part of the minimal path. Keeping the file means the two
  registrations can be diffed.
- **No `session_configuration` is set**, so the platform default (900 s) applies. That
  is a deliberate choice for this issue: it makes the observed idle behaviour the
  *default* behaviour, which is what the matrix quotes.
