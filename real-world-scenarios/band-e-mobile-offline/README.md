# Band E — mobile and offline

Band E is the program's mobile band (epic #164). Its three variants each need two things this
repository has never had: a MAUI application with an Android head, and a way for .NET code on the
device to call an on-device model. This directory is the foundation issue E1 (#165): it holds the
decision record for the bridge and the evidence for what the toolchain allowed.

```
README.md                        this file — status, prerequisites, what runs, what is blocked
DECISION-bridge.md               the bridge decision: mode, technology, disconnection, licensing
probe-toolchain.sh               reports the toolchain and builds a throwaway MAUI app
fetch-aar.sh                     downloads and verifies the pinned Foundry Local AAR
app/                             the MAUI Android head (net10.0-android), with the IChatClient seam
bridge/                          the Kotlin facade (Gradle 8.13, Kotlin 1.9.25, AGP 8.13.0)
evidence/                        captured command output; see evidence/README.md
```

## Status

| Acceptance criterion (issue #165) | State |
| --- | --- |
| A MAUI app builds on this machine | **met** — `app/` builds with 0 errors; `./probe-toolchain.sh` exits 0 |
| The app runs on an Android emulator (API 33+) | **met** — installed and resumed on API 36, arm64-v8a |
| Kotlin bridge compiled and bound | **met** — facade AAR builds; JNI call reaches the SDK on device |
| One completion against Foundry Local, answer displayed | **not met** — the IPC companion app is not installed |
| AAR version and SHA-256 pinned and documented | **met** — 0.1.6, verified by `./fetch-aar.sh` |
| License forbids vendoring, `fetch-aar.sh` used instead | **met** — `.gitignore` excludes `libs/`; stated below |

`evidence/e1-maui-run.txt` is the capture for the build and run rows: the exact build and install
commands, the package on the device, and the text the running window displays.
`evidence/e1-bridge.txt` is the capture for the bridge row.

The completion row is a device precondition, not a code defect. IPC mode needs Microsoft's companion
app `com.microsoft.foundrylocal.app`, which is distributed through Google Play. The emulator image
ships Play but has no signed-in account, so installing it needs a manual sign-in. Until it is
installed the app reports the fact it can verify rather than a fabricated answer:

```
companion: absent — com.microsoft.foundrylocal.app is not installed (API 36)
failed [TransportUnavailable] retryable: companion app com.microsoft.foundrylocal.app absent ...
```

## The bridge, and what is still missing

`bridge/` holds the Kotlin facade `DECISION-bridge.md` §3.1 specifies. It compiles
(Gradle **8.13**, Kotlin **1.9.25**, AGP **8.13.0**, Java 17, compileSdk 36) and the app binds it:

```
app (C# net10.0-android)
  └─ Chat/BridgeShim.cs      : Java.Lang.Object, FoundryBridge.IListener   ← the boundary
       └─ FoundryBridge.kt   : Kotlin facade, no suspend types in its surface
            └─ foundry-local-ipc-sdk-0.1.6.aar → com.microsoft.foundrylocal.app
```

`evidence/e1-bridge.txt` captures the proof that the boundary is real rather than merely
compiled: on the emulator, tapping "Send turn" runs the Kotlin `start()`, the SDK raises
`FoundryLocalException: Failed to bind to service using explicit intent`, and that exception
crosses the generated invoker into a classified `ChatFailureKind` on screen. **The cross-language
path works; the service it talks to is not installed.**

Two things are worth knowing before editing this:

- The raw AAR is referenced with `Bind="false"` and
  `AndroidGenerateJavaBindingAnnotations=false`. Binding it directly fails with `CS0260`/`CS0579`
  on the obfuscator-kept synthetic classes — precisely the failure §3.2(a) predicts. **The facade
  is the binding.**
- The facade files it compiles to a 19.7 KB AAR that contains **no** Microsoft classes: the binary
  is a `compileOnly` input and the app supplies it at runtime. Nothing is redistributed.

To build the bridge before the app:

```bash
cd real-world-scenarios/band-e-mobile-offline
./fetch-aar.sh                       # verify the pinned binary into libs/ (gitignored)
cd bridge && ./gradlew :assembleRelease
```

**What is still missing** is the service app itself. IPC mode needs
`com.microsoft.foundrylocal.app` from Google Play. The emulator image ships Play but has no
signed-in account, so installing it needs a manual operator sign-in; no agent entered
credentials. Until it is installed the app reports the fact it can verify:

```
companion: absent — com.microsoft.foundrylocal.app is not installed (API 36)
failed [TransportUnavailable] retryable: ... bridge loaded; start() failed [TransportUnavailable]:
FoundryLocalException: Failed to bind to service using explicit intent
```

## Historical record: the toolchain block this issue started from

> **This section describes the machine and state the issue was opened on, and is kept for
> provenance. It no longer describes the current machine** — the Android SDK, a JDK 17 and an
> emulator were installed, and `app/` now builds and runs (see "Status" above). Do not read the
> table below as the present state.

`evidence/maui-toolchain-probe.txt` is the whole story — it is the stdout of
`./probe-toolchain.sh` captured after the workload was installed, so it shows the state the build
fails in rather than the install history that led there. The history is in the subsection below, and
is labelled as recollected. In short:

| Layer | State | Result |
| --- | --- | --- |
| `dotnet` SDK 10.0.402 (Homebrew) | present | fine |
| `maui-android` workload | now installed (see below) | `dotnet workload list` shows it |
| Android SDK (compile SDK 36) | **absent** | `error XA5300: The Android SDK directory could not be found` |
| JDK 17 (the Android build needs one; the SDK baseline names Java 17) | **absent** | `/usr/libexec/java_home` reports no runtime; `java` is the macOS shim |
| Android emulator / device | **absent** | no `adb`, no `emulator`, no `sdkmanager` on `PATH`; no `~/Library/Android/sdk` |

The build fails at the Android SDK check before anything is compiled:

```text
error XA5300: The Android SDK directory could not be found. Install the Android SDK by following
the instructions at: https://aka.ms/dotnet-android-install-sdk
```

That is the whole of it. The workload alone is not a mobile toolchain: the Android SDK, a JDK, and a
platform image are separate downloads, and the workload install on its own does not fetch them.

### Installing the workload is not straightforward on this machine

> **Provenance of this subsection.** The reports below are **recollected from the session that
> produced this directory, not captured at the time** — the commands were run before anyone thought
> to save their output. `evidence/maui-toolchain-probe.txt`'s "Retrospective captures" section has
> what can still be replayed on this machine (a second install run's garbage collection, the
> template package version, free space, and that `dotnet new maui` is no longer reproachable here
> because the templates are now installed), and says which parts are not replayable. Treat the
> rest as a narrative to verify, not as a log.

`dotnet workload install maui-android` reports success **twice over**, and the first success is a
lie:

1. First invocation: printed `Successfully installed workload(s) maui-android.`, then — in the same
   run — `Garbage collecting for SDK feature band(s) 10.0.400` uninstalled every pack it had just
   written. `dotnet workload list` nonetheless listed `maui-android`, and `dotnet build` still failed
   `error NETSDK1147: To build this project, the following workloads must be installed: maui-android`.
2. Second invocation (`dotnet workload install maui-android --skip-sign-check`) persisted the packs at
   `~/.dotnet/packs` (manifest 10.0.110, Android SDK packs 36.1.118), and the NETSDK1147 went away.

The `maui` project templates were **not** registered by either install: `dotnet new maui` failed with
template exit code 103 even with the workload present.
`dotnet new install Microsoft.Maui.Templates.net10` (which resolved to 10.0.120) registers them, and the
`dotnet new list maui` block in `evidence/maui-toolchain-probe.txt` shows the result.

`probe-toolchain.sh` installs nothing — it reports, it does not mutate the machine. The two
install commands above are the ones that have to be run by hand first, and they are why the workload
column in the table reads "now installed" rather than "absent". It exits non-zero when the build
fails, so `./probe-toolchain.sh` can be used as a gate.

Disk: the packs land under `~/.dotnet/packs` (3.2 GB as measured in the probe evidence). The machine
that produced the probe had roughly 557–583 GB free throughout, so disk was not the limiting factor
there — the Android SDK is what is missing. The repo's own environment notes
(`.squad/ralph-instructions.md`, "Environment prerequisites") record this volume filling before, so
re-check free space before adding several GB of Android SDK on top of these packs.

## Prerequisites for unblocking it

Install, in this order, then re-run the probe:

1. **A JDK 17.** The integration guide's "Prerequisites" table names Java 17 (and AGP 8.13.0).
   Verify `/usr/libexec/java_home -V` lists a 17 and that `java -version` runs.
2. **The Android SDK with compile platform 36** — `dotnet build` finds it through `ANDROID_HOME`,
   `ANDROID_SDK_ROOT`, or `AndroidSdkDirectory`. Install `platforms;android-36`,
   `build-tools;36.0.0`, and `platform-tools`.
3. **A system image and an emulator.** The embedded AAR ships native libraries for `arm64-v8a` and
   `x86_64` only (`evidence/foundry-local-aar.txt`), so an image of either ABI works. On Apple silicon
   an `arm64-v8a` image runs natively; an `x86_64` image runs under translation and is slower but is
   the more portable choice if the results are shared. API 33 minimum; 36 matches the compile SDK.

Then:

```bash
cd real-world-scenarios/band-e-mobile-offline
./probe-toolchain.sh                 # expect "Build succeeded"
```

Only after that is a checked-in project worth having. The shape it should take is already decided —
see `DECISION-bridge.md`, which also states the target frameworks, the AAR mode, and the disconnection
contract the later variants depend on.

## The pinned binary

The AAR is **not** in this repository, and `.gitignore` excludes `libs/`. That is a licensing
decision, made from the license shipped with the artifact rather than from the repository's MIT
license; `DECISION-bridge.md` §5 quotes the clause. `fetch-aar.sh` downloads and verifies it instead:

```bash
./fetch-aar.sh                  # ipc mode, the mode the decision record selects
./fetch-aar.sh embedded         # the other mode, for the packaging comparison
./fetch-aar.sh ipc --verify-only   # re-verify what is already in libs/, download nothing
```

Pinned version and hashes, from
[`microsoft/FoundryLocalAndroid` v0.1.6](https://github.com/microsoft/FoundryLocalAndroid/releases/tag/v0.1.6)
(repository commit `cc8af7a58624ea4e2328ebec85c241353f8481fe`):

| Asset | SHA-256 | Size |
| --- | --- | --- |
| `foundry-local-ipc-sdk-0.1.6.aar` | `7dc74260d2dbd16c2c3c02bcd9de1d852bb89366f71a63ea9813fe764c2665c1` | 441,036 B |
| `foundry-local-embedded-sdk-0.1.6.aar` | `6a8ba85433fca1f48535986f529702fb43ad8d83bcd5df30f30a6c7969474e87` | 46,634,727 B |
| `foundry-local-android-sdk-license-0.1.6.txt` | `f18ec4ba34d49b0ed91f22e5277c36734d4dba536afbc0aa3c83ec49c56c362d` | 11,489 B |
| `foundry-local-android-third-party-notices-0.1.6.md` | `6687afbd77ff947e0951bc4f9464ac57e7f787c8a6f9cefbcdf5adcca2f08c40` | 3,509 B |

`evidence/foundry-local-aar.txt` records the download, the hash comparison, and an inspection of both
AARs. The license and third-party notices are vendored verbatim under `evidence/` because the license
**requires** them to travel with use of the software and because a reader of this directory should be
able to read the terms without a network round trip.

## What the later variants inherit

| Variant | Issue | What it needs from here |
| --- | --- | --- |
| 1 — on-device Durable + on-device SLM | #166 | the bridge in-process, and a model alias chosen at runtime from the catalog |
| 2 — on-device Durable + cloud model, network drop | #167 | `IChatClient`-shaped cloud transport, plus the fact that Foundry Local's own disconnection is a *separate* signal (see decision record §4) |
| 3 — host-side authoritative Durable | #168 | nothing from the bridge; it needs a transport, not a model |
| fault evidence + README + matrix | #169 | the blocked/unblocked state above, and the licensing row |

## References

- Guide this record is grounded in: `microsoft/FoundryLocalAndroid` `docs/INTEGRATION_GUIDE.md`,
  `docs/API_REFERENCE.md`, `docs/IPC_AND_EMBEDDED_MODES.md`, `docs/BEST_PRACTICES.md`, at `main`.
- Build baseline it names: min Android API 33, compile SDK 36, Kotlin 1.9.25, Gradle 8.13, AGP
  8.13.0, Java 17.
- The service app for IPC mode: `com.microsoft.foundrylocal.app` on Google Play. Its version is not
  pinned by the AAR; the SDK offers `manager.checkCompatibility()` instead. Record the installed app
  version in the variant evidence when it is first captured.
