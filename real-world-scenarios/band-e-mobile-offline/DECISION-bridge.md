# Decision: the Foundry Local Android bridge

**Status:** accepted, 2026-11-05. Nothing here has been compiled yet — see
[`README.md`](README.md) for the toolchain block on this machine and
[`evidence/maui-toolchain-probe.txt`](evidence/maui-toolchain-probe.txt) for the failure. This
document decides the *shape*; the toolchain decides when the shape can be run.

**Question.** How does .NET code on an Android device call an on-device model, such that the same
`IChatClient`-shaped seam also accepts a remote model, without letting Android types into the durable
layer?

**Answer, in one table.**

| Decision | Chosen | Rejected |
| --- | --- | --- |
| Deployment mode of the Foundry Local SDK | **IPC** (`foundry-local-ipc-sdk-0.1.6.aar`) | embedded (for the default path; kept as a packaged-only alternative) |
| Where the bridge lives | **a hand-written Kotlin facade in a `.aar` inside the MAUI Android head**, reached from .NET through a thin hand-written `Java.Lang.Object` / `Java.Lang.IRunnable` shim | (a) generated `.NET Android` binding library; (b) hand-written C# binding over `JNIEnv` calls; (c) MAUI Blazor WebView with a JS bridge; (d) a second process + local HTTP |
| Transport of the bridge | **one interface with synchronous-looking suspend calls marshalled to plain C# callbacks**, one call in flight per conversation | generated bindings over the raw coroutine/`Flow` surface; a `BroadcastReceiver`; a bound `Service` of our own |
| Disconnection, to .NET | **a first-class `IAsyncEnumerable`-independent event plus a per-call `BridgeDisconnectedException`**, so the *call* fails, not just an ambient flag | relying on the ambient callback alone; retrying inside the bridge |
| Binary in this repository | **not vendored**; `fetch-aar.sh` downloads and verifies | vendoring the AAR, vendoring only the IPC AAR, using the MIT license of the docs repo |

Each row is argued below. Where the argument depends on what the AAR actually contains, the claim is
followed by the evidence path; where it depends on Microsoft's own documentation, the file and the
quoted line are given.

---

## 1. What the SDK actually gives you

Grounded in `microsoft/FoundryLocalAndroid` at `main`, release `v0.1.6` (commit
`cc8af7a58624ea4e2328ebec85c241353f8481fe`).

**One AAR per mode.** `docs/INTEGRATION_GUIDE.md`:

> Foundry Local distributes each deployment mode as a separate AAR. One AAR does not contain both
> modes. Add exactly one AAR to your application:
>
> - The IPC AAR connects to the separately installed Foundry Local service app, where inference runs.
> - The embedded AAR packages the inference engine in your application, where inference runs.

**One Kotlin API, coroutine-based.** From `docs/API_REFERENCE.md`, and confirmed against the
`classes.jar` constant pools in `evidence/foundry-local-aar.txt` (the bundle's own symbols, not the
docs):

```
FoundryLocalManagerFactory.create(Context, Configuration, Function0<Unit> onDisconnected, Continuation)
FoundryLocalManager: getCatalog, checkCompatibility, close, isConnected, reconnect, isServiceRunning
Catalog:             listModels, getModel, getModelInfo, getCachedModels, getLoadedModels, ...
Model:               download(progress, contentIntent, timeoutMinutes), load, unload,
                     isCached, isLoaded, removeFromCache, createChatClient, createAudioClient
ChatClient:          completeChat(request): suspend -> ChatCompletion
                     completeChatStreaming(request): Flow<ChatCompletionChunk>
```

**History is the caller's problem.** `docs/INTEGRATION_GUIDE.md`, "Multi-turn chat":

> The API does not implicitly manage conversation history. Preserve previous messages and send the
> history with each request.

This is the single most consequential fact for Band E, and it is *good* for the seam: the SDK is
already stateless per request, so a `Func<IReadOnlyList<ChatMessage>, CancellationToken,
IAsyncEnumerable<string>>`-shaped seam loses nothing. There is no server-side conversation to
reconcile.

**Disconnection is an IPC-only concept, and it is coarse.** `docs/IPC_AND_EMBEDDED_MODES.md`:

> | Connection loss | Possible; reconnect | Not applicable |

and `docs/API_REFERENCE.md`:

> `isConnected` reports the current service-binding state.

`onDisconnected` is a `Function0<Unit>` — no arguments, no reason, no code. The processor cannot tell
a service crash from an upgrade from a device-level kill by the callback alone. §4 is about the
consequences.

---

## 2. IPC, not embedded

The issue asks for this on performance, packaging, service-app dependency, and licensing grounds.
They do not all point the same way, so take them one at a time.

| Concern | IPC `0.1.6` | Embedded `0.1.6` | Which wins |
| --- | --- | --- | --- |
| AAR size (download at build) | 441,036 B | 46,634,727 B (~44 MB) | IPC, by 100× |
| Native libraries in the app | none — the IPC AAR has no `jni/` at all | `arm64-v8a` and `x86_64` only: `Microsoft.AI.Foundry.Local.Core.so` (≈20 MB/ABI), `libonnxruntime.so` (≈27 MB/ABI), crypto, ssl, C++ runtime | IPC |
| Per-request latency | one Android IPC hop through AIDL to a service process | in-process, no hop | **embedded** |
| Process isolation | native crash kills the service, your app survives | native crash kills your app | IPC |
| Model cache | service-managed, one isolated copy per client app | app-managed | IPC (shared service work is not duplicated) |
| Runtime updates | come with the Play Store app | require shipping a new APK | IPC |
| Companion app | `com.microsoft.foundrylocal.app` from Google Play | none | **embedded** |
| Licensing | one SDK license either way (same terms for both assets) | same | tie |

Sizes and library lists are from `evidence/foundry-local-aar.txt`, which lists both archives.

**Packaging is the decisive argument, and it is not close.** This repository is a set of scenarios
someone clones and runs; a 44 MB-binary-per-ABI AAR committed or fetched for a demo is a real cost,
and the 100× ratio is measured, not estimated. Embedded also makes the *demonstration* worse in one
specific way: `docs/IPC_AND_EMBEDDED_MODES.md` warns

> A native runtime failure also terminates your app process.

Band E's whole subject is surviving interruption. An embedded runtime whose failure mode is "your app
dies" is the wrong liability to add to a scenario about surviving failure — variant 1 (#166) needs an
offline turn, and it needs the *Durable* layer to be the thing that survives, not to be co-resident
with a native library that can take the process down mid-commit.

**Performance loses, and loses little.** The hop is over AIDL to a foreground service; the model
already dominates latency by orders of magnitude, and `Configuration` gives no knob where the IPC hop
is measurable next to token generation. No benchmark has been run — that is a limitation of this
record, stated rather than papered over. If a later variant's evidence shows IPC latency material, that
is the trigger to re-open this decision, and the embedded path is already scripted (`./fetch-aar.sh
embedded`) so the comparison is cheap.

**The service-app dependency is real and is priced in.** IPC requires the Google Play app. The AAR's
manifest declares the query so the dependency is visible to the platform:

```xml
<queries><package android:name="com.microsoft.foundrylocal.app" /></queries>
```

and its manifest raises the app's own floor: `<uses-sdk android:minSdkVersion="33" />`, matching the
documented baseline (min API 33, compile SDK 36). Neither mode works below API 33, so that cost is
common, not differential. What *is* differential is that IPC adds a precondition an operator can fail
to meet, and the guide gives the answer for that:

> Confirm the SDK AAR and service app come from compatible releases. […] Retry
> `FoundryLocalManager.create(...)`.

with `manager.checkCompatibility()` for the version question. The variant's README must therefore
record the installed service-app version; the AAR does not pin it.

**Verdict: IPC, with embedded as a documented, fetchable fallback.** The fallback exists so the
decision can be reversed on evidence rather than on argument.

---

## 3. Bridge technology

The issue's recommendation is: *a small Kotlin facade returning plain callbacks, exposed through a
.NET Android binding library, Durable entirely on the .NET side.* The direction is right — a Kotlin
facade is the only layer that can own the coroutines — but **"exposed through a .NET Android binding
library" is the wrong mechanism for this facade**, and this section explains why, because "generate a
binding" is the reflex answer and it is worth showing it does not fit.

### 3.1 The facade

A Kotlin module (`net/…/durable/foundry/Bridge.kt`) compiled into a `.aar` and consumed by the MAUI
Android head. Its whole job:

```kotlin
class FoundryBridge(context: Context, appName: String) {
    interface Listener {                 // implemented on the .NET side
        fun onChunk(text: String)
        fun onCompleted(text: String)
        fun onFailed(code: Int, message: String, disconnected: Boolean)
        fun onDisconnected()
    }

    fun start(listener: Listener)                          // create manager, warm catalog
    fun ensureModel(alias: String, onProgress: (Float) -> Unit)
    fun complete(prompt: String, requestId: Long)          // returns immediately; results via listener
    fun completeStreaming(prompt: String, requestId: Long)
    fun cancel(requestId: Long)
    fun close()
}
```

Three properties matter and each is deliberate:

1. **No suspend functions, no `Flow`, no `ChatCompletionRequest` crosses the boundary.** Only `String`,
   `Int`, `Float` and a callback interface do. Coroutines stay on the Kotlin side, started in a
   `CoroutineScope(SupervisorJob() + Dispatchers.IO)` owned by the bridge, exactly as
   `docs/BEST_PRACTICES.md` requires ("Use lifecycle-owned coroutines … avoid unmanaged
   application-wide scopes for UI work"). The bridge is a process-level component intentionally
   outliving the Activity, which is the sanctioned exception the same document names.
2. **One call in flight per conversation.** Band E needs a deterministic transcript; two overlapping
   `complete` calls would make the order of `onChunk` non-deterministic for no benefit. `cancel` is
   therefore a `Job.cancel()` on the collecting coroutine, which is the documented stop mechanism
   ("Cancel the collecting coroutine to stop generation").
3. **The history is passed in, not held.** The .NET side owns the message list (it is the durable
   record), concatenates history into the prompt, and sends it every call — the SDK's own contract.

### 3.2 Alternatives, and why each is rejected

**(a) A generated .NET Android binding library over the raw AAR — rejected.** This is the
recommendation's mechanism, applied to the AAR directly, and it fails on three specifics that are
visible in the artifact:

- The public surface is Kotlin coroutines. `completeChat` compiles to
  `(ChatCompletionRequest, Continuation<-ChatCompletion>)Ljava/lang/Object;`, and
  `completeChatStreaming` returns `kotlinx.coroutines.flow.Flow`. A generated binding yields
  `Task<Java.Lang.Object>`-ish shapes around `Continuation` — the caller must fabricate a
  `kotlin.coroutines.Continuation` from C#, which means shipping `Xamarin.Kotlin.StdLib` and hand-rolling
  the state-machine contract of a Kotlin suspend function. That is more hand-written interop than the
  facade it was meant to avoid, and it is hand-written in the language with the weaker tooling for it.
- The AAR is obfuscator-kept end to end: its own `proguard.txt` is
  `-keep class com.microsoft.foundrylocal.** { *; }` and `-keepclasseswithmembers class * { native <methods>; }`.
  A binding must model every kept member faithfully, including synthetic `$Companion`,
  `$DefaultImpls` and `$Sdk*` classes that exist only to satisfy that keep rule.
- The AAR ships no `api.txt`/documented metadata, and it is a preview whose "API surface … may change
  between preview releases" (`docs/INTEGRATION_GUIDE.md`). A generated binding regenerates that whole
  surface on every upgrade; a facade absorbs the change in one Kotlin file.

**(b) A generated .NET Android binding library over a *hand-written* Kotlin facade — rejected, but
only just.** This is the recommendation read charitably, and it is the closest runner-up. It is
rejected because the facade above already reduces the surface to four primitive-taking methods and a
listener, and a generator given an interface with `Long`/`String`/`Float` parameters and a
`Listener` callback produces exactly what a hand-written shim would — plus a build step, a second
`.aar` to version, and a regeneration on every change. The facade *is* the binding; generating a
binding for it is ceremony. Note what is shared with the recommendation and is not in dispute: **the
facade has no Android type in its public surface, and Durable is entirely on the .NET side.**

**(c) Hand-written C# over `JNIEnv` directly — rejected.** It works and is what the shim in (d)
ultimately does for registration, but doing it *by hand* for every call means encoding JNI signatures
as strings, which is exactly the class of error that surfaces at runtime on device and not at build
time. There is no gain over a generated shim for the same narrow surface.

**(d) A second process plus a local HTTP endpoint — rejected.** The natural way to make Android look
like the rest of this repository, where a remote runtime is already an option
(`RuntimeConnection.ForUri`, used by `tests/Durable.Tests/TaskResumeTests.cs:181`). It is rejected
because it puts a socket, a port, and a serialization format between .NET and a library that is
*already* an IPC client. The latency argument cuts against IPC mode in §2 and would cut against this
far harder; and it turns "the service disconnected" into "my own hop failed too", which is precisely
the ambiguity §4 exists to remove.

**(e) MAUI Blazor WebView with a JS bridge — rejected.** It replaces one hand-written Kotlin file
with a JS interop layer, a Kotlin↔JS bridge, and a serialization boundary, and it makes the .NET side
need a WebView to make a model call. No part of Band E wants a browser.

### 3.3 What the .NET side looks like

```
lib/Copilot.Durable.csproj           (unchanged)
        ▲  IChatClient-shaped seam — no Android types
MyMobileApp  (MAUI, net10.0-android)
  ├─ BridgeShim.cs     : Java.Lang.Object, FoundryBridge.IListener   ← only file with Android types
  ├─ FoundryChatClient : IChatClient                                 ← the seam
  └─ transport selection: FoundryChatClient | CopilotRuntimeChatClient
```

The shim is the boundary and the only file that may name a type from `Android.*` or from the
generated Java binding. Everything above it — including every Durable call — sees `IChatClient`. That
is the property the issue asks for and it is preserved.

---

## 4. How disconnection reaches .NET

Variant 2 (#167) depends on this, so it is specified rather than left to the implementer.

Foundry Local gives two things in IPC mode: a `Function0<Unit>` notification, and a suspend
`reconnect()` after which "handles obtained before reconnecting may be stale"
(`docs/INTEGRATION_GUIDE.md`). The stale-handle warning is the important one: a handle held across a
disconnection is not merely disconnected, it is invalid.

**The rule: the call fails, the ambient flag is secondary.** A notification with no argument is not
evidence about a particular in-flight request — it may arrive after the service recovered, and it
cannot say which request it affected. So:

| Signal | Where it is produced | What .NET sees | Who reacts |
| --- | --- | --- | --- |
| In-flight `completeChat`/stream throws `FoundryLocalException` **and** `manager.isConnected == false` | Kotlin `catch` around the SDK call | `onFailed(code, message, disconnected = true)` | the bridge reconnects and reacquires; the .NET call surfaces `BridgeDisconnectedException` |
| `onDisconnected` fires with no call in flight | Kotlin `Function0` | `onDisconnected()` → a `BridgeStateChanged(Disconnected)` event | the UI shows a disconnected state; nothing else |
| `onDisconnected` fires during a call | both of the above | `onFailed(..., disconnected = true)` **wins**; the event is also raised | one path, so no double-handling |
| Reconnection succeeded | after `reconnect()` and reacquire | `onConnected()` → `BridgeStateChanged(Connected)` | the *next* call proceeds; the failed one is **not** retried by the bridge |
| Reconnect failed | `reconnect()` threw | `onFailed(code, message, disconnected = true)` for the pending call; state stays `Disconnected` | the caller decides |

The bridge performs exactly the four steps `docs/BEST_PRACTICES.md` prescribes after `reconnect()` —
re-get the catalog, re-acquire the model by alias, re-check cached and loaded state, recreate the chat
client — and performs no retry of the caller's request. Retrying inside the bridge would hide the
failure from the durable layer, and hiding it is the one thing a scenario about surviving failure
cannot afford.

**Why the error is an exception and not a return value.** The durable layer's turn boundary is where
it commits. A turn that loses its model connection must fail *at that boundary* so the failure is
recorded as a failed turn rather than committed as an empty answer. An exception on the `.NET` side of
`IChatClient` is the only shape that reaches that boundary without the durable layer knowing about
Android.

**What the caller does with it.** Nothing is automatic. Variant 2's job is to show what Durable does
when a turn fails this way — whether the pre-disconnect committed state survives, how many turns were
lost, whether the retry duplicates a committed answer. The bridge's contract is only to report the
failure unambiguously and leave the retry decision to the durable side. That is the boundary between
"proved the gap" and "invented capability", and this document stays on the reporting side.

**A note for variants 2 and 3, from reading the current layer.** `DurableClientBuilder.UseClient(
CopilotClientOptions)` (`lib/Durable.cs:6638`) accepts arbitrary `CopilotClientOptions`, so a host
built through the builder can already select `RuntimeConnection.ForUri` — the mobile-safe remote
transport the epic identifies. `DurableHarnessOptions`, the other entry point
(`DurableHarness.OpenAsync`, `lib/Durable.cs:6212`), exposes only `RuntimePath`/`RuntimeArguments` and
builds `RuntimeConnection.ForStdio` or the bundled runtime. A mobile host therefore has one route to a
URI transport and not the other. This is recorded here as an observation for #167/#169; it is not
changed in this PR, because no scenario has yet failed for that reason and the program's rules require
the failure first.

---

## 5. The license, and why the AAR is not vendored

Two licenses are in play and they are not the same:

**The repository is MIT, and says what that does not cover.** `microsoft/FoundryLocalAndroid` `LICENSE`:

> This license applies only to the documentation, example source code, and repository tooling
> contained in this repository. Binary SDK release assets are licensed under the separate terms
> provided with each release and are not covered by this license.

`README.md` repeats it: "The SDK AARs are distributed under the license and notices included with
those artifacts and are not granted under the repository's MIT license." So the docs being MIT says
nothing about the AAR.

**The AAR's own license is a Microsoft EULA that forbids redistribution.**
`foundry-local-android-sdk-license-0.1.6.txt`, vendored verbatim at
[`evidence/foundry-local-android-sdk-license-0.1.6.txt`](evidence/foundry-local-android-sdk-license-0.1.6.txt).
The operative clause, §2(e):

> **share, publish, distribute, or lease the software, provide the software as a stand-alone offering
> for others to use, or transfer the software or this agreement to any third party.**

Committing a 441 KB AAR to a public repository is publishing and distributing it. There is no
carve-out for a subdirectory of an MIT-licensed sample.

**Therefore: not vendored, and `fetch-aar.sh` instead.** This is the same conclusion the SDK's own
samples reach — `examples/ipc/ApiExplorerAppIPC/README.md` states "Release AARs are not committed to
this repository. Place the verified IPC AAR in this sample's `libs/` directory before building." The
script pins version `0.1.6` and the published SHA-256 of both AARs and deletes a download whose hash
does not match, so "the binary someone has" is checkable against "the binary Microsoft published".

**Two obligations that come with using it, which a clone inherits:**

1. **The notices must travel.** §1(c) of the EULA defers third-party components to "the
   ThirdPartyNotices file(s) accompanying the software". The release's
   `foundry-local-android-third-party-notices-0.1.6.md` is therefore vendored alongside the license at
   `evidence/`, and the variant apps that embed the AAR into an APK must carry it into their
   distribution. The license text is vendored for the same reason: §2(c) forbids removing or modifying
   notices, and a reader should be able to check the terms without a download.
2. **Telemetry is on by default, and Windows/macOS Foundry Local does not make this explicit for
   Android.** `Configuration.disableTelemetry` defaults to `false`, and the EULA §5(a) is a data
   collection clause. Band E's README claims "genuinely offline turns"; the best-practices document is
   careful about exactly this:
   > Do not promise that the application never communicates externally unless the complete release
   > configuration and application behavior have been verified.

   The bridge will construct `Configuration(appName = …, disableTelemetry = true)` and the variants
   must state that catalog access and model download are network operations, so "offline" means
   *inference is local after the model is cached*, not *the app never opens a socket*. Without that
   sentence the scenario would be claiming something the SDK does not promise.

**Not a licensing but a packaging consequence:** the IPC AAR's manifest merges three permissions and
one foreground service into the app:

```xml
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
<service android:name="…DownloadForegroundService" android:foregroundServiceType="dataSync" … />
```

A user of the app sees those in its permission list. They are the AAR's, not ours, and a reviewer
asking "why does this demo want a data-sync foreground service?" deserves the answer in the README.

---

## 6. Consequences and what would change the decision

| Accepted cost | Mitigation / owner |
| --- | --- |
| An Android device or emulator is required to run any Band E variant; nothing is `dotnet run` | README states it; the epic (#164) already accepts the structural deviation |
| IPC needs the Play Store app; its version is not pinned by the AAR | `checkCompatibility()` at start; record the installed version in variant evidence |
| No latency measurement of the IPC hop | re-open §2 if evidence shows it material; `./fetch-aar.sh embedded` makes the comparison cheap |
| The bridge is hand-written Kotlin, so it is a per-upgrade maintenance cost | front the SDK's changing surface in one file; README pins the version and hash |
| `DurableHarnessOptions` cannot select a URI transport | recorded in §4; the builder route works today |
| Nothing in this directory has been compiled | `evidence/maui-toolchain-probe.txt`; README lists the three missing downloads |

**Re-open the decision if any of these becomes true:** an official .NET binding for the AAR appears; the
embedded AAR stops shipping native code (it would remove the process-death objection); the SDK ships a
documented stateless HTTP surface, which would make (d) in §3.2 competitive; or the IPC hop is
measured to be a material share of turn latency.

## Sources

All `microsoft/FoundryLocalAndroid` at `main`, retrieved 2026-11-05; release `v0.1.6`, repository
commit `cc8af7a58624ea4e2328ebec85c241353f8481fe`.

- `docs/INTEGRATION_GUIDE.md` — modes, prerequisites, quick example, IPC setup, multi-turn, errors,
  cleanup.
- `docs/API_REFERENCE.md` — `FoundryLocalManager`, `Configuration`, `Catalog`, `Model`, `ChatClient`,
  `ChatCompletionRequest`/`ChatCompletion`/`ChatCompletionChunk`, `FoundryLocalException`.
- `docs/IPC_AND_EMBEDDED_MODES.md` — the operational-differences table quoted in §2.
- `docs/BEST_PRACTICES.md` — coroutine ownership, disconnect handling and the four-step reacquire,
  the "do not promise … never communicates externally" warning.
- `docs/TROUBLESHOOTING.md` — "No Foundry Local SDK implementation found", IPC connection failures.
- `LICENSE`, `README.md` (Licensing) — what MIT does and does not cover.
- Release `v0.1.6` assets — both AARs, `…-sdk-license-0.1.6.txt`, `…-third-party-notices-0.1.6.md`,
  and the published SHA-256 values.
- This repository: `lib/Durable.cs` (6212 `DurableHarness.OpenAsync`, 6638 `UseClient`),
  `tests/Durable.Tests/TaskResumeTests.cs:181` (`RuntimeConnection.ForUri`).
