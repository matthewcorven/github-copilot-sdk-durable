# Provenance

## Source

| Field | Value |
| --- | --- |
| Repository | `microsoft/agent-framework` |
| URL | https://github.com/microsoft/agent-framework |
| License | MIT (`upstream/LICENSE`) |
| Pinned commit | `cf734bc4692ccfc944496a5b3182b3d65483c44d` |
| Commit date | 2026-11-04 (committer date, GitHub API) |
| Vendored on | 2026-11-05 |

The commit is the same one Band G pinned, so both bands read the same tree.

## Vendored files

Fetched with `gh api repos/microsoft/agent-framework/contents/<path>?ref=<sha>`, which returns the
blob at that commit. Nothing was edited after download.

| Path in this directory | Path in `microsoft/agent-framework` | Blob SHA-1 | Size | `shasum -a 256` |
| --- | --- | --- | --- | --- |
| `LICENSE` | `LICENSE` | `9e841e7a26e4eb057b24511e7b92d42b257a80e5` | 1141 B | `c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383` |
| `handoff_simple.py` | `python/samples/03-workflows/orchestrations/handoff_simple.py` | `60ca155bb867b2b82745635d0c31d732e6e22583` | 14107 B | `7a49290fee2320871feee0c83df5f19bbd946c823d155d830d9a5f5eefc66f48` |
| `handoff_autonomous.py` | `python/samples/03-workflows/orchestrations/handoff_autonomous.py` | `8ae1a5204a6463b178bf2da3ae5f72de7b0ab8f4` | 6975 B | `31ec132a85d49ff4961247fbb5fa0eaf4a41fb75e8c1b844aa8c8186b0eb5699` |

## Byte-identity check

`git hash-object <file>` on each vendored file returns the blob SHA-1 in the table above, which is the
object id the GitHub API reported for that path at `cf734bc`. A re-fetch of all three paths produced
identical `shasum -a 256` values:

```
c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383  LICENSE
31ec132a85d49ff4961247fbb5fa0eaf4a41fb75e8c1b844aa8c8186b0eb5699  handoff_autonomous.py
7a49290fee2320871feee0c83df5f19bbd946c823d155d830d9a5f5eefc66f48  handoff_simple.py
```

To repeat it:

```sh
SHA=cf734bc4692ccfc944496a5b3182b3d65483c44d
for p in LICENSE \
         python/samples/03-workflows/orchestrations/handoff_simple.py \
         python/samples/03-workflows/orchestrations/handoff_autonomous.py; do
  gh api "repos/microsoft/agent-framework/contents/$p?ref=$SHA" --jq .content | base64 -d | shasum -a 256
done
```

## What is not vendored

The samples import `agent_framework`, `azure.identity` and `dotenv`. None of those is copied here. The
C# port under `baseline/` uses the .NET packages of the same framework
(`Microsoft.Agents.AI.Workflows` 1.27.0 and `Microsoft.Agents.AI.OpenAI` 1.27.0) from NuGet instead.
The .NET sample that exercises the same orchestration shape
(`dotnet/samples/03-workflows/Orchestration/Handoff/Program.cs` at the same commit) was read for API
shape only and is not vendored: the port follows the Python sample, because that is the one the issue
names as the reference.
