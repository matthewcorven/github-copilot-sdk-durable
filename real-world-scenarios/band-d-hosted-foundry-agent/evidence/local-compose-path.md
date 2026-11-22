# Local Docker Compose run — what was attempted, what failed, what was proven

This file records the **local path first**, as issue #159 requires: the accelerator
documents `docker compose up` as a mode that needs no Azure deployment. Everything
below is captured output, not paraphrase. Where the documented path did not work,
the verbatim error is quoted and the workaround is labelled as ours.

Environment: macOS 15 (arm64), Docker 29.9.2, `az` 2.90.0, `azd` 1.37.2 (downloaded
to a scratch directory; `brew install azure/tap/azd` fails — the tap repository does
not exist). Clone: shallow, `microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator`
at `b139b24044565b02317821b2590602f924c43311`.

## Attempt 1 — the documented path, unchanged

```sh
git clone --depth 1 https://github.com/microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator.git
cd Prior-Authorization-Multi-Agent-Solution-Accelerator
cp .env.example .env
cp backend/.env.example backend/.env
docker compose up -d --build
```

Result: all six containers built and started; all four agents and the backend
reported `healthy` within a minute, the frontend stayed `starting`.

```
 Container upstream-agent-coverage-1 Healthy
 Container upstream-backend-1 Healthy
 EXIT=0
```

`GET http://localhost:8002/readiness` → `{"status":"healthy"}` (HTTP 200).

### First real call fails at tool construction

```sh
curl -sS -X POST http://localhost:8002/responses -H 'Content-Type: application/json' \
  -d '{"input":"Prior auth request: provider NPI 1720180003, ICD-10 R91.1, CPT 31628, ...","stream":false}'
```

```
HTTP 200 in 0.016476s
{"id":"caresp_5a6119b68983bbb900WxWUmnm2xdtN2cvo9TD2JybEbVwDC1Iw","object":"response","output":[],
 "error":{"code":"server_error","message":"(\"Failed to create MCP session: cannot import name 'Client'
 from 'mcp' (/usr/local/lib/python3.12/site-packages/mcp/__init__.py)\", ImportError(\"cannot import name
 'Client' from 'mcp' (/usr/local/lib/python3.12/site-packages/mcp/__init__.py\"))"},
 "status":"failed"}
```

So `/readiness` is green while every tool-using request fails: readiness only proves
the host bound the port.

Cause, read out of the image:

```
agent-framework-core 1.24.0        requires mcp>=2.2.0,<3  (extra "all"); imports `from mcp import Client`
agent-framework-foundry-hosting 1.0.0b261015  requires mcp>=1.24.0,<2
agents/coverage/requirements.txt  pins       mcp>=1.24.0,<2
installed                                   mcp 1.30.0  (mcp.__init__ has no `Client`)
```

The accelerator's `mcp` upper bound (`<2`) pins the runtime to a library version its
own `agent-framework-core` release no longer supports. The hosting package's
`mcp<2` range was corrected upstream after `1.0.0b261015`:

```
$ pip index versions agent-framework-foundry-hosting
latest 1.0.0b261030   requires mcp<3,>=2.2.0
```

### Fix applied for the local run (this is ours, not the accelerator's)

`agents/coverage/requirements.txt`:

```diff
-agent-framework-foundry-hosting>=1.0.0b260730,<2
+agent-framework-foundry-hosting>=1.0.0b261030,<2
-mcp>=1.24.0,<2
+mcp>=2.2.0,<3
```

Rebuild → `mcp 2.3.0`, `agent-framework-foundry-hosting 1.0.0b261030`. The same two
edits were applied to the clinical and compliance agents, which are needed for the
full review. `compliance` and `synthesis` import `agent_framework_openai`, which the
base images did not install, so those were raised to
`agent-framework-openai>=1.18.0,<2` as well.

## Attempt 2 — the pins are fixed; the credential is missing

With the pins fixed the MCP import error is gone. The next failure is still at
inference, now for want of a credential.

```
Agent returned status='failed': An internal server error occurred.
```

Agent log:

```
<class 'agent_framework_foundry._chat_client.FoundryChatClient'> service failed to complete the prompt:
DefaultAzureCredential failed to retrieve a token from the included credentials.
  ...
  AzureCliCredential: Azure CLI not found on path
```

**The documented "no Azure required" claim does not survive contact.** The
containers call `FoundryChatClient` against `AZURE_AI_PROJECT_ENDPOINT` and the
accelerator's own `docs/DeploymentGuide.md` (§3.1) says the Compose path needs a real
Foundry project endpoint **and** an `az login` session. A Compose-only machine has
neither. Two separate things are required:

1. a Foundry project endpoint + a deployed `gpt-5.4`;
2. a credential reachable from inside the container.

The project endpoint used here is the one this issue's hosted deployment created
(the local stack points `AZURE_AI_PROJECT_ENDPOINT` at it). For the credential, the
Compose path's documented mechanism (`DefaultAzureCredential` →
`AzureCliCredential`) was made to work by adding `azure-cli` to the agent images and
mounting the host's `~/.azure` directory read-write at `/root/.azure` (the CLI writes
`azureProfile.json` on start, so a read-only mount fails with
`OSError: [Errno 30] Read-only file system`). That is the *legacy* path, and it is
what the accelerator documents; the hosted path uses the platform identity instead.

Installing `azure-cli` pulls `azure-ai-projects` 3.x, which drops `A2APreviewTool`
and breaks `agent_framework_foundry` on import:

```
ImportError: cannot import name 'A2APreviewTool' from 'azure.ai.projects.models'
```

so the local overlay also pins `azure-ai-projects==2.7.0 --no-deps` back to the
version the base image already had.

## Attempt 3 — the full four-agent review runs, every specialist fails at inference

With images rebuilt and credentials mounted:

```sh
curl -sS -X POST http://localhost:8000/api/review -H 'Content-Type: application/json' \
  --data-binary @request.json
```

```
HTTP 200 in 10.358327s
request_id ec55acaa-17fd-4a12-8277-6bf55542563b
recommendation pend_for_review 0.3 LOW
  compliance error= Agent returned status='failed': An internal server error occurred.
  clinical   error= Agent returned status='failed': An internal server error occurred.
  coverage   error= Agent returned status='failed': An internal server error occurred.
tool_results: ['cpt_format_validation']
```

Logs for all three:

```
<class 'agent_framework_foundry._chat_client.FoundryChatClient'> service failed to complete the prompt:
Connection error.
```

`Connection error` is the OpenAI client failing to reach the model. The
Compose configuration passes `AZURE_AI_PROJECT_ENDPOINT` and a deployment name; the
`FoundryChatClient` path then opens the project's OpenAI-compatible surface. It
answers 200 with a *failed Response* rather than an exception, which is why the
backend turns it into `recommendation: pend_for_review, confidence 0.3` with three
failed specialists — the orchestrator degrades instead of erroring, and a request
whose every agent failed still produces a "recommendation". That is a property of
the baseline worth recording for the matrix: **a degraded run is indistinguishable
from a low-confidence run at the API boundary.**

The local path was not carried further once the hosted path worked: the hosted run
is the stronger evidence (the real platform, the real identity model, the real
session semantics), and the Compose stack exists only to give the same containers a
local home.

## Where the local Compose path actually stops

| Requirement | Documented as "no Azure required" | Reality on this machine |
| --- | --- | --- |
| Six containers build and pass their health checks | yes | yes |
| Agent `/responses` reachable | yes | yes |
| MCP tool session (NPI registry, CMS coverage) | yes | **no** with the repository's pins — `mcp<2` vs `agent-framework-core` importing `mcp.Client` |
| Model inference | "any OpenAI-compatible endpoint via `DefaultAzureCredential`" | **needs a Foundry project + `az login`**; the README's "no Azure required" is only true for the container build |
| Model call after auth | — | **`Connection error`** from the project's OpenAI surface in the Compose configuration |

So: the local path **builds and starts but does not complete a prior-auth run** in
this environment. The transcript in `evidence/run-20261105T125254Z/` is from the
hosted deployment, not from Compose, and says so in `40-final-result.json`.

## Why the local path was not repaired further

It could be repaired — the likely missing piece is the `api-version`/endpoint form the
`FoundryChatClient` needs, which the accelerator does not pin in Compose. Doing that
was out of scope for what D1 has to prove: the platform behaviour. It is recorded here
so the next person does not re-derive the three failures above.
