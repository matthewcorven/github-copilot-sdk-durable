# Band E evidence

Captured output, not summaries. Each file names the command that produced it.

| File | What it is |
| --- | --- |
| `maui-toolchain-probe.txt` | The complete toolchain report from `../probe-toolchain.sh` on 2026-11-05 on macOS arm64: SDK, workloads, Android SDK environment, Java, and the `dotnet build -f net10.0-android` failure. This is the evidence that #165's build half is blocked, and what by. |
| `foundry-local-aar.txt` | Download and SHA-256 verification of both `v0.1.6` AARs against the release's published hashes, plus an inspection of what each archive contains and the public Kotlin API surface read from `classes.jar`. |
| `foundry-local-android-sdk-license-0.1.6.txt` | The SDK license shipped with the AAR, verbatim. Vendored because §2(c) forbids removing notices and because `DECISION-bridge.md` §5 quotes §2(e) from it. |
| `foundry-local-android-third-party-notices-0.1.6.md` | The third-party notices shipped with the AAR, verbatim. The license defers bundled components to this file; an app that ships the AAR must carry it. |

The two license texts are Microsoft's, not this repository's. They are here as quotable evidence and
as the notices a redistribution needs — not as material relicensed under this repository's MIT license.

`maui-toolchain-probe.txt` was produced after the `maui-android` workload had been installed twice.
That history matters to anyone reproducing it — the first install reported success and garbage-collected
the packs it had just written — and it is described in `../README.md`.
