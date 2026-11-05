# Band E evidence

Captured output, not summaries. Each file records how it was captured in its own header or section,
and says where it is recollected rather than captured. Nothing here is a paraphrase of a command's
output.

| File | What it is | How it was produced |
| --- | --- | --- |
| `maui-toolchain-probe.txt` | The toolchain report for #165: SDK, workloads, Android SDK environment, Java, and the `dotnet build -f net10.0-android` failure that blocks the issue. **Post-install**: the workload is present in it, so it is not a record of installing it. A second section, "Retrospective captures", holds what could be replayed later and marks what could not. | stdout of `../probe-toolchain.sh`; the second section names each command above its output |
| `foundry-local-aar.txt` | Download and SHA-256 verification of both `v0.1.6` AARs against the release's published hashes, plus an inspection of what each archive contains and the public Kotlin API surface read from `classes.jar`. | A shell session whose commands are quoted inline; hashes are machine-checkable against the release |
| `foundry-local-android-sdk-license-0.1.6.txt` | The SDK license shipped with the AAR, verbatim. Vendored because §2(c) forbids removing notices and because `DECISION-bridge.md` §5 quotes §2(e) from it. | The release asset, downloaded and hash-verified in `foundry-local-aar.txt` |
| `foundry-local-android-third-party-notices-0.1.6.md` | The third-party notices shipped with the AAR, verbatim. The license defers bundled components to this file; an app that ships the AAR must carry it. | The release asset, downloaded and hash-verified in `foundry-local-aar.txt` |

The two license texts are Microsoft's, not this repository's. They are here as quotable evidence and
as the notices a redistribution needs — not as material relicensed under this repository's MIT license.

`maui-toolchain-probe.txt` was produced **after** the `maui-android` workload had been installed, so
it cannot show the install. The install history matters to anyone reproducing the state — the first
run reported success and garbage-collected the packs it had just written — and it is told in
`../README.md`, which labels it as recollected rather than captured. The file's own "Retrospective
captures" section holds the parts that were still replayable when this was reviewed, and states the
parts that were not.
