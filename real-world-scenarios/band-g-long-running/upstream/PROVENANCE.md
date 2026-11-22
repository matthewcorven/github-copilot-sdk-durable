# Provenance

## Source

| Field | Value |
| --- | --- |
| Repository | `microsoft/agent-framework` |
| URL | https://github.com/microsoft/agent-framework |
| License | MIT (`upstream/LICENSE`) |
| Pinned commit | `cf734bc4692ccfc944496a5b3182b3d65483c44d` |
| Commit date | 2026-11-04 (committer date, GitHub API) |
| Vendored on | 2026-11-04 |

## Vendored files

Fetched with `gh api repos/microsoft/agent-framework/contents/<path>?ref=<sha>`, which returns the
blob at that commit. Nothing was edited after download.

| Path in this directory | Path in `microsoft/agent-framework` | Blob SHA-1 | Size | `shasum -a 256` |
| --- | --- | --- | --- | --- |
| `LICENSE` | `LICENSE` | `9e841e7a26e4eb057b24511e7b92d42b257a80e5` | 1141 B | `c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383` |
| `magentic_checkpoint.py` | `python/samples/03-workflows/orchestrations/magentic_checkpoint.py` | `7c653f810b6ac92d0fbc251888224f901cf24901` | 14519 B | `94d6197391b67144e3f191d77f70db2c024eb70e6756020812ab3f4ab58a449c` |
| `handoff_with_tool_approval_checkpoint_resume.py` | `python/samples/03-workflows/orchestrations/handoff_with_tool_approval_checkpoint_resume.py` | `4dfeba3bbc3f3cc33429871a060baada6e08bbc7` | 9829 B | `7c951c0df1ca130bc0e1f6938fda3531b7f8e2ce5630bd97a19b025c240eac3f` |

## Byte-identity check

`git hash-object <file>` on each vendored file returns the blob SHA-1 in the table above, which is the
object id the GitHub API reported for that path at `cf734bc`. A re-fetch of all three paths into a
scratch directory produced identical `shasum -a 256` values:

```
c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383  LICENSE
7c951c0df1ca130bc0e1f6938fda3531b7f8e2ce5630bd97a19b025c240eac3f  handoff_with_tool_approval_checkpoint_resume.py
94d6197391b67144e3f191d77f70db2c024eb70e6756020812ab3f4ab58a449c  magentic_checkpoint.py
```

To repeat it:

```sh
SHA=cf734bc4692ccfc944496a5b3182b3d65483c44d
for p in LICENSE \
         python/samples/03-workflows/orchestrations/magentic_checkpoint.py \
         python/samples/03-workflows/orchestrations/handoff_with_tool_approval_checkpoint_resume.py; do
  gh api "repos/microsoft/agent-framework/contents/$p?ref=$SHA" --jq .content | base64 -d | shasum -a 256
done
```

## What is not vendored

The samples import `agent_framework`, `azure.identity` and `dotenv`. None of those is copied here. The
C# port under `baseline/` uses the .NET packages of the same framework
(`Microsoft.Agents.AI.Workflows` 1.27.0 and `Microsoft.Agents.AI.OpenAI` 1.27.0) from NuGet instead.
