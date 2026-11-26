# Band E evidence

Captured output, not summaries. Each file records how it was captured in its own header or section,
and says where it is recollected rather than captured. Nothing here is a paraphrase of a command's
output.

| File | What it is | How it was produced |
| --- | --- | --- |
| `maui-toolchain-probe.txt` | The toolchain report for #165: SDK, workloads, Android SDK environment, Java, and the `dotnet build -f net10.0-android` failure that blocks the issue. **Post-install**: the workload is present in it, so it is not a record of installing it. A second section, "Retrospective captures", holds what could be replayed later and marks what could not. | stdout of `../probe-toolchain.sh`; the second section names each command above its output |
| `e1-maui-run.txt` | The #165 build-and-run capture: pinned SDK and JDK home, `probe-toolchain.sh` exit 0, the app build, the signed APK, install and launch on API 36 arm64-v8a, the package on device, the text the running window displays, and the on-device `TransportUnavailable` result with the reason. | Transcribed from commands run on this machine on 2026-11-17; each command and its output are quoted inline |
| `e1-bridge.txt` | The #165 bridge capture: Gradle/Kotlin/AGP versions, the facade AAR build, the four API mistakes `javap` corrected, the CS0260/CS0579 failure of binding the raw AAR, the dex scan proving both class sets are packaged, and the on-device `FoundryLocalException` that proves the JNI boundary runs. | Transcribed from commands run on this machine on 2026-11-17 |
| `e1-physical-device.txt` | Run on the attached Samsung SM-S918U1 (Android 17, API 37, arm64-v8a): the same bridge behaviour as the emulator, plus the finding that no APK is published for `com.microsoft.foundrylocal.app`, so Play is the only source. | Transcribed from commands run on 2026-11-25 |
| `e1-completion.txt` | The acceptance capture: an on-device Foundry Local completion displayed by the app (qwen2.5-0.5b, companion app 0.1.6, API 37), corroborated by the service's own raw response log, plus the three defects the run exposed. | Transcribed from the device on 2026-11-25 |
| `e2-larger-model.txt` | Qwen2.5-1.5b run on the AVD to test whether the emulator is what forced the 0.5B model: it loads, turns offline, and survives a kill, so the emulator is not the constraint. Also records that the size guard refuses the larger model's surgical replies. | Transcribed from the booted AVD on 2026-11-25, app trace and logcat quoted |
| `e2-physical-device.txt` | The full demonstration on the Galaxy S24 Ultra (Android 17, API 37) with the committed 0.5B model: airplane mode, four turns, kill and reopen from identical bytes, and the platform comparison table. | Transcribed from the attached phone on 2026-11-26 |
| `foundry-local-aar.txt` | Download and SHA-256 verification of both `v0.1.6` AARs against the release's published hashes, plus an inspection of what each archive contains and the public Kotlin API surface read from `classes.jar`. | A shell session whose commands are quoted inline; hashes are machine-checkable against the release |
| `foundry-local-android-sdk-license-0.1.6.txt` | The SDK license shipped with the AAR, verbatim. Vendored because §2(c) forbids removing notices and because `DECISION-bridge.md` §5 quotes §2(e) from it. | The release asset, downloaded and hash-verified in `foundry-local-aar.txt` |
| `foundry-local-android-third-party-notices-0.1.6.md` | The third-party notices shipped with the AAR, verbatim. The license defers bundled components to this file; an app that ships the AAR must carry it. | The release asset, downloaded and hash-verified in `foundry-local-aar.txt` |
| `e2-offline-demonstration.txt` | The #166 demonstration: airplane mode on, three offline turns with the document edited, `am force-stop`, reopen, a fourth turn. Includes the store's bytes and SHA-256 before and after the kill, and the app's per-step trace. | Commands and their output, plus the app's own export read off the device with `adb shell cat`; every block names which produced it |
| `e2-resources.txt` | The #166 resource reality: model size in 11 files, download time from the SDK's telemetry, process PSS, and tokens/second measured from the SDK's own token count and turn time. Method beside every number. | App trace, SDK telemetry lines from `adb logcat`, and the app's resource export |
| `e2-model-protocol.txt` | The #166 model record: which model and why, the two reply protocols that failed (with the replies quoted), the one that works, and a refusal kept as evidence rather than hidden. | The app's trace and the transcript on screen, quoted; screenshots referenced by name |
| `e2-aar-decision.txt` | Why this device runs the embedded AAR: the Play Store refusal, both AARs' hashes, the identical Kotlin API, the ContentProvider registration, and the APK's native libraries. | `fetch-aar.sh --verify-only`, `javap` on both `classes.jar`s, `unzip -l` on the APK, `adb logcat` |
| `e2-sdk-surface.txt` | The SDK surface the app is written against, read from the embedded archive: auto-registration, `FoundryLocalManager.create`, `Configuration`, `Model`, `ChatClient`, and `ChatCompletionRequest`'s `maxTokens`. Plus the SDK's own cache-directory log line and what its catalog says when offline. | `unzip -p` and `javap` on `libs/foundry-local-embedded-sdk-0.1.6.aar`, and `adb logcat` |
| `e2-store-on-device.txt` | The store read without the app's help: both files' byte length and SHA-256, the record types they contain, the two document kinds, a completed turn's task, and why a copy rather than the file itself. | `adb pull` + `shasum -a 256` + Python's `json`, and the `run-as` / permission refusals that explain the copy |
| `screenshots/e2-*.png` | Fourteen screenshots of the demonstration: airplane mode on a clean store, each turn streaming and committed, the kill, the restored state after reopen, turn 4 with the document intact, the catalog failing offline, the download's wall clock, and the final resources. | `adb exec-out screencap -p` at the moment named in each file's name; `e2-offline-demonstration.txt` says where each one belongs |

The two license texts are Microsoft's, not this repository's. They are here as quotable evidence and
as the notices a redistribution needs — not as material relicensed under this repository's MIT license.

`maui-toolchain-probe.txt` was produced **after** the `maui-android` workload had been installed, so
it cannot show the install. The install history matters to anyone reproducing the state — the first
run reported success and garbage-collected the packs it had just written — and it is told in
`../README.md`, which labels it as recollected rather than captured. The file's own "Retrospective
captures" section holds the parts that were still replayable when this was reviewed, and states the
parts that were not.

## The E2 screenshots

The screenshots are PNGs captured with `adb exec-out screencap -p` on the emulator, in order:

| File | The moment it captures |
| --- | --- |
| `screenshots/e2-01-airplane-on-clean-store.png` | airplane mode on, a reset store, no turns, model already loaded from cache |
| `screenshots/e2-02-turn1-streaming-offline.png` | turn 1 mid-stream: partial text, buttons disabled, network off |
| `screenshots/e2-03-turn1-committed-rev1.png` | turn 1 committed: revision 1, one transcript entry |
| `screenshots/e2-04-turn2-streaming-offline.png` | turn 2 mid-stream |
| `screenshots/e2-05-turn2-committed-rev2.png` | turn 2 committed: revision 2, two entries |
| `screenshots/e2-06-turn3-streaming-offline.png` | turn 3 mid-stream |
| `screenshots/e2-07-turn3-committed-rev3.png` | turn 3 committed: revision 3, three entries — the state before the kill |
| `screenshots/e2-08-app-killed.png` | after `am force-stop`: the launcher, the app's window gone |
| `screenshots/e2-09-reopened-restored.png` | the new process's first render: three turns and revision 3 restored, with the store's size and hash prefix on screen |
| `screenshots/e2-10-turn4-streaming-offline.png` | turn 4 mid-stream, after the reopen |
| `screenshots/e2-11-turn4-committed-dana.png` | turn 4 committed: revision 4, the document ending with the line the turn asked for |
| `screenshots/e2-12-catalog-needs-network.png` | Prepare with airplane mode on and an empty cache: the catalog fetch failing, classified |
| `screenshots/e2-13-final-state.png` | the final screen: four turns, revision 4, and the measured resources |
| `screenshots/e2-14-download-wall-clock-278s.png` | the end of the one download: "model ready in 278.6 s · cache 837.4 MB (11 files, +837.4 MB this run)" |

A screenshot is not proof on its own: each one is tied to an export or a trace line in
`e2-offline-demonstration.txt`, and the store's hash prefix on screen is the same one the export
recorded.
