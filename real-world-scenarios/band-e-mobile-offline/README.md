# Band E — mobile and offline

Band E is the program's mobile band (epic #164). Its three variants each need two things this
repository has never had: a MAUI application with an Android head, and a way for .NET code on the
device to call an on-device model.

- **E1 (#165)** built that foundation: the decision record for the bridge, the Kotlin facade, and the
  evidence for what the toolchain allowed.
- **E2 (#166, variant 1)** is this directory's working application: Durable state and the model both
  on the device, a genuinely offline turn, and a conversation that survives the process. §"E2: what
  the app actually does" is the part to read first.

```
README.md                        this file — status, prerequisites, what runs, what is blocked
DECISION-bridge.md               the bridge decision: mode, technology, disconnection, licensing
probe-toolchain.sh               reports the toolchain and builds a throwaway MAUI app
fetch-aar.sh                     downloads and verifies the pinned Foundry Local AAR
verify-android-boundary.sh       fails the build if the durable layer names an Android type
app/                             the MAUI Android head (net10.0-android)
  Chat/IChatClient.cs              the transport seam: streaming, no Android type
  Chat/FoundryLocalChatClient.cs   the transport: embedded Foundry Local
  Chat/BridgeShim.cs               the app's only file that names an Android type
  Durability/BandEDurable.cs       the app's whole durability surface (transcript, document, turns)
  Durability/Demonstration.cs      the scripted turns and how a reply becomes the document
  Durability/EvidenceExporter.cs   writes the store and the measurements where a shell can read them
bridge/                          the Kotlin facade (Gradle 8.13, Kotlin 1.9.25, AGP 8.13.0)
evidence/                        captured command output and screenshots; see evidence/README.md
```

## Status

### E2 — variant 1: on-device Durable with an on-device model, fully offline (#166)

| Acceptance criterion (issue #166) | State |
| --- | --- |
| Transcripts and checkpoints in the on-device Durable store | **met** — `band-e.transcript` and `band-e.artifact` documents plus one task per turn, in `files/durable/durable-store.jsonl` |
| A turn completes with no network; tokens stream as produced | **met** — four turns with `airplane_mode_on=1`, 220 deltas each (the cap), screenshots taken mid-stream |
| Kill and reopen continues from the store | **met** — `force-stop`, new pid, `restored from the durable store: store 128720 B sha256:f02b209c3e84` (same bytes as the pre-kill export), then turn 4 |
| The Durable layer references no Android type | **met and enforced** — `verify-android-boundary.sh` runs in the build and fails it |
| The bridge boundary stays clean | **met** — one file, `app/Chat/BridgeShim.cs` |
| Resource reality recorded (size, download, memory, tokens/s) | **met** — see §"The resource reality", and both legs: `evidence/e2-resources.txt` (emulator), `evidence/e2-larger-model.txt` (1.5B on the emulator), `evidence/e2-physical-device.txt` (0.5B on the phone). The emulator legs are 1.5B wherever the model is named; the 0.5B emulator leg is where the other figures come from — the provenance note says which line belongs to which build |
| The model choice is justified, not assumed | **met** — the emulator ran 1.5B offline, so it is not the constraint; the phone run is the recorded alternative |

A hold on the word "fully": the model **download** needs the network, once, which the issue allows
("It must not need the network for anything but the first model download"). Everything after that is
airplane mode. `evidence/e2-offline-demonstration.txt` is the capture; `evidence/e2-store-on-device.txt`
reads the store's bytes without the app's help.

### E1 — the bridge foundation (#165)

| Acceptance criterion (issue #165) | State |
| --- | --- |
| A MAUI app builds on this machine | **met** — `app/` builds with 0 errors; `./probe-toolchain.sh` exits 0 |
| The app runs on an Android emulator (API 33+) | **met** — API 36 emulator, and a physical API 37 device |
| Kotlin bridge compiled and bound | **met** — facade AAR builds; JNI call reaches the SDK on device |
| One completion against Foundry Local, answer displayed | **met** — qwen2.5-0.5b answered on the physical device |
| AAR version and SHA-256 pinned and documented | **met** — 0.1.6, verified by `./fetch-aar.sh` |
| License forbids vendoring, `fetch-aar.sh` used instead | **met** — `.gitignore` excludes `libs/`; stated below |

`evidence/e1-maui-run.txt` is the capture for the build and run rows: the exact build and install
commands, the package on the device, and the text the running window displays.
`evidence/e1-bridge.txt` is the capture for the bridge row.
`evidence/e1-completion.txt` is the capture for the completion row.

## The bridge

`bridge/` holds the Kotlin facade `DECISION-bridge.md` §3.1 specifies. It compiles
(Gradle **8.13**, Kotlin **1.9.25**, AGP **8.13.0**, Java 17, compileSdk 36) and the app binds it:

```
app (C# net10.0-android)
  └─ Chat/BridgeShim.cs      : Java.Lang.Object, FoundryBridge.IListener   ← the boundary
       └─ FoundryBridge.kt   : Kotlin facade, no suspend types in its surface
            └─ foundry-local-embedded-sdk-0.1.6.aar → the inference engine, in-process
```

(The diagram above is E1's: it shows the IPC mode that decision record §2 chose. E2 runs the same
facade against the embedded AAR — `foundry-local-embedded-sdk-0.1.6.aar`, no service app — per §7.)

`evidence/e1-bridge.txt` captures the proof that the boundary is real rather than merely
compiled: on the emulator, tapping "Send turn" runs the Kotlin `start()`, the SDK raises
`FoundryLocalException: Failed to bind to service using explicit intent`, and that exception
crosses the generated invoker into a classified `ChatFailureKind` on screen. That capture was
taken before the service app was installed; `e1-completion.txt` is the same boundary carrying a
real answer.

Two things are worth knowing before editing this:

- The raw AAR is referenced with `Bind="false"` and
  `AndroidGenerateJavaBindingAnnotations=false`. Binding it directly fails with `CS0260`/`CS0579`
  on the obfuscator-kept synthetic classes — precisely the failure §3.2(a) predicts. **The facade
  is the binding.**
- The facade files it compiles to a 19.7 KB AAR that contains **no** Microsoft classes: the binary
  is a `compileOnly` input and the app supplies it at runtime. Nothing is redistributed.

To build the bridge before the app (E2 packages the **embedded** AAR, so pass the mode):

```bash
cd real-world-scenarios/band-e-mobile-offline
./fetch-aar.sh embedded                              # verify the pinned binary into libs/ (gitignored)
cd bridge && ./gradlew :assembleRelease -Pbande.foundryAarMode=embedded
```

**The completion criterion is met.** With the companion app installed (0.1.6) the app displays a
real answer, captured in [`evidence/e1-completion.txt`](evidence/e1-completion.txt):

```
user: Is the device online?
assistant: Yes, I am an active participant and can be accessed via the network.
completed via Foundry Local (IPC)
```

The service's own log carries the same text (`Raw response: {"model":"qwen2.5-0.5b", ...}`), so
the transcript is the model's output rather than anything this app invented.

Three things that run taught, all fixed or recorded:

- **A second manager tears down the first.** Foundry Local Core initializes once per service
  process; creating a bridge just to inspect state dropped the live connection mid-turn. The
  transport now reports progress through an event and never opens a second manager.
- **`download()` reports Failure when nothing needs downloading.** With the model already on disk
  the service logs `0 to download, 6 skipped` and `Status:Failure` anyway. Download is therefore
  best-effort and `load()` decides usability.
- **`phi-3.5-mini` kills the preview service on this device** — `Service process gone during
  FoundryChatCompletionClient.completeChat`, then a restart. `qwen2.5-0.5b` completes on the same
  code path. The alias is a constant in `FoundryLocalChatClient`; pick from the catalog the app
  prints.

## E2: what the app actually does, and where the durability is

### The mode changed, and why

[`DECISION-bridge.md`](DECISION-bridge.md) §2 chose IPC mode and named the triggers that would re-open
that choice. On this emulator one fired: Google Play reports `Foundry Local isn't compatible with this
version` for `com.microsoft.foundry.app`, and `com.microsoft.foundrylocal.app` is not installed. E1
found the same wall from the other side on the physical device — there is no published APK, so Play is
the only source.

So variant 1 takes §2's own documented fallback: the **embedded** AAR. What makes that a small change
rather than a rewrite is that both AARs publish the same Kotlin API (verified with `javap`, in
[`evidence/e2-sdk-surface.txt`](evidence/e2-sdk-surface.txt)), so:

| Layer | Change |
| --- | --- |
| `app/BandEMobile.csproj` | the IPC AAR line is replaced by the embedded one, `Bind="false"` unchanged |
| `bridge/build.gradle.kts` | the mode is a property: `-Pbande.foundryAarMode=embedded` compiles the same facade against the same API |
| `app/Chat/*.cs` | the transport gained a streaming path and a token cap; nothing about the mode |
| `lib/Durable.cs` | untouched |

`DECISION-bridge.md` §7 is the record, with the costs §2 predicted and this variant pays: an 81 MB
APK, no process isolation, an app-private model cache. For a variant whose subject is a store that
survives the process, that is the intended trade.

### Durable is the history — the SDK's is not

This is the band's point, so it is worth being precise about.

The on-device SDK **keeps no conversation history**. Its own integration guide says so:

> The API does not implicitly manage conversation history. Preserve previous messages and send the
> history with each request.

Every request therefore carries the whole transcript, and if the transcript existed only in memory
there would be nothing to send after a restart. `BandEDurable` is what makes it exist:

- `band-e.transcript` — a rewinding document holding every turn, committed in the same transaction
  that closes the turn's task.
- `band-e.artifact` — a rewinding document the model edits every turn. Rewinding because "the document
  is intact" should be checkable from the store, not asserted from the screen.
- `band-e.turn` — one durable **task** per turn, parked on an external event between *requested* and
  *completed*. It is committed **before** the model is called, so a process killed mid-stream leaves a
  task the next start can name. In practice the demonstration never leaves one: every turn closed its
  task in the transaction that stored the reply, which is why the reopened app reported
  `interrupted 0`.

The trace makes the distinction visible: the message count grows 2 → 4 → 6 → 8 across the four turns.
The model has no memory; the store does.

**What the task layer does not do here, said rather than implied.** There is no multi-phase task
machine in this variant. A turn is one checkpoint and one completion, because the work between them is
a model call this layer must not know about. A phase machine would mean putting the transport inside
the durable layer, which is the coupling the variant exists to avoid. So the task layer contributes
exactly one thing — an interrupted turn is *visible as interrupted* rather than silently absent — and
the README claims no more than that.

### The model, and its declared cost

`qwen2.5-0.5b`. Not `phi-3.5-mini`, and not quietly: E1 measured that `phi-3.5-mini` kills the
inference process on the physical device before an answer arrives
([`evidence/e1-completion.txt`](evidence/e1-completion.txt), defect 3). The declared cost of choosing
the smaller model is instruction-following, measured in
[`evidence/e2-model-protocol.txt`](evidence/e2-model-protocol.txt):

| What was tried | What happened |
| --- | --- |
| Reply in two parts separated by `===DOCUMENT===` | marker never emitted; model wrote markdown prose instead |
| …separated by a plain-word `New version:` line | same; no marker line anywhere in the reply |
| **The reply is the document** | works; four turns, every one committed |

The document shown after each turn is therefore the model's whole reply. It rewrites rather than
edits: turn 2's "Risks" section is not literally two bullet points, and markdown bold it was told not
to write survives into the text. The demonstration's claim is that the document **survives a kill**,
not that a 0.5B model writes well — and the resource table below is where the difference is priced.

### Was the emulator the reason for the smaller model? No.

The issue's note says that if the emulator cannot run the model acceptably, record the measurement
and use a physical device rather than quietly swapping to a smaller model. That condition had not
been tested, so it was: **qwen2.5-1.5b** — 3x the parameters, same family — was run on the same AVD
with the network off. It loads (2,679.6 MB, 117.1 s), it turns (32.9–56.5 s per turn, 1.8–3.9
delta/s), and it survives a kill and reopen from identical bytes. **The emulator is not the
constraint**, so "the emulator forced a 0.5B model" is not an available justification.
[`evidence/e2-larger-model.txt`](evidence/e2-larger-model.txt) has it.

What that run also exposed is an interaction worth knowing before the next variant: with a model that
replies surgically ("here is the new section") rather than rewriting the whole document, the size
guard refuses the reply as a collapse, and two of four turns kept the document unchanged. The guard
conflates *the model collapsed* with *the model edited surgically*. The demonstration is written
around whole-document rewrites, which is a 0.5B behaviour; that is a real limitation of the demo's
shape, recorded rather than tuned away.

The 0.5B demonstration was therefore run a second time **on the physical device**, which is the
honest alternative the note offers. Both legs are captured, and the comparison is the useful part:

| Metric | Emulator, qwen2.5-1.5b | Phone, qwen2.5-0.5b |
| --- | --- | --- |
| Model on disk | 2,679.6 MB / 21 files | 837.4 MB / 11 files |
| Download | 117.1 s (+1,842.2 MB) | 46.8 s (+837.4 MB) |
| Per-turn wall clock | 32.9–56.5 s | 2.9–3.7 s |
| Streaming rate | 1.8–3.9 delta/s | 59.1–76.5 delta/s |
| PSS | 1,186 MB of 2,473 MB | 620 MB of 7,073 MB |
| Turns that revised the document | 2 of 4 (guard refused 2) | 4 of 4 |
| Preamble leaked into body | no | yes |

So the honest statement of the model choice is: the pinned **0.5B** model is what the demonstration
uses, on both devices, because it is fast enough to iterate and its whole-document rewrites match the
demo's shape. It is **not** used because the emulator could not do better. Its cost is the
instruction-following in the table above and the leaked preamble in the document body. The phone run
is [`evidence/e2-physical-device.txt`](evidence/e2-physical-device.txt).

### The resource reality

Measured on AVD `band-e-api36-playstore-arm64` (arm64-v8a, API 36, emulator), Release build
`sha256:2607726ff7938085cdd353aeb104c5d43ec67f5ccbb46ea1c4223ba5fe6cda9e`. Method beside each number;
[`evidence/e2-resources.txt`](evidence/e2-resources.txt) has the raw output.

| Measured | Value | Method |
| --- | --- | --- |
| Model size on disk | **878,065,731 B (837.4 MiB) in 11 files** | recursive directory walk of the model cache, written per file to the app's trace |
| Download time, once | **229.8 s** of download, **266.9 s** including the catalog round trip | the SDK's own `Command:ModelDownload Time:` telemetry in logcat |
| …as the app saw it | **278.6 s** wall clock | the app's own timer around `Prepare`, screenshot `e2-14` |
| Model load, per process | **7.9 s** cached (13.3 s on the first, cold load) — no network, cache unchanged byte for byte | app timer around `Prepare`, before/after cache walk |
| Process memory | **603.7 MB PSS** of 2,473 MB total (777 MB free) | `Debug.MemoryInfo.TotalPss` / `ActivityManager.MemoryInfo`, read by the app; `evidence/e2-resources.txt` §3 |
| Generation speed | **4.8–13.4 tok/s** across two runs and eight turns — **5.7 / 9.1 / 9.9 / 10.4 tok/s** on `86e58d9d…` (the demonstration) and **4.8 / 12.6 / 12.7 / 13.4 tok/s** on the earlier build; slowest on the cold first turn and on the longest prompts | SDK `Generator.TokenCount` ÷ SDK `CoreChatCompletion Time`, both blocks in `evidence/e2-resources.txt` §4; the `16359`/`16956` lines are the demonstration's pids, the `12:54:31`–`12:59:36` lines the earlier build's |
| Streaming granularity | **220 deltas per turn on all four demonstration turns** (the cap) | the app's chunk counter; `evidence/e2-offline-demonstration.txt` §4 and §7, `evidence/e2-resources.txt` §5 |
| Turn latency | **28.8–50.6 s** per turn on the SDK's own `CoreChatCompletion Time` (28.8 / 46.8 / 48.6 / 50.6 s), capped at 220 generated tokens; the app's own timer reads **28.9 / 46.9 / 48.6 / 50.9 s** (28.9–50.9 s) for the same turns, tens of milliseconds apart because the two clocks start at different points | the SDK clock is `evidence/e2-resources.txt` §4; the app clock is the `<n> ms` on `evidence/e2-offline-demonstration.txt`'s `turn N: stream closed` lines, `:100` / `:108` / `:116` / `:246`, repeated by `e2-resources.txt` §6 |
| APK size | **85,253,322 B (81.3 MiB)** | `ls -l` on the signed APK in `evidence/e2-resources.txt` §5; includes 55.5 MB (arm64-v8a) and 60.9 MB (x86_64) of native engine per shipped ABI, summed from the AAR's `jni/` entries. A different build's signed APK measured 84,975,920 B (81.0 MiB) — `evidence/e2-aar-decision.txt` §6 and `evidence/e2-offline-demonstration.txt` header |
| Store growth over the session | **128,720 B → 133,801 B** | the store's own byte length, exported per turn. Both ends are the committed run's own exports: `evidence/e2-offline-demonstration.txt` §5 (`store_bytes: 128720`) and §8 (`store_bytes: 133801`). The pre-turn figure is not a committed export — see the provenance note below |

The honest reading: on this emulator a 0.5B model generates at 4.8–13.4 tokens/s depending on the
turn, and costs ~600 MB of a 2.5 GB device. Two effects are in that range rather than averaged out of
it — the first turn after a load is slowest, and a longer prompt is slower per token because the SDK
is stateless per request and re-processes the whole transcript every turn. That second effect is the
durability's own cost: the transcript has to be re-sent because the SDK keeps none. A turn is capped
at 220 generated tokens because without a cap an earlier attempt ran 45 s and 53 s with no natural
end.

> **Provenance of the table above.** Two Release builds appear in this evidence set, and the table
> does not merge them silently. Each row resolves to a capture line and to the build that line belongs
> to. That build is not inherited from a file's opening hash: `e2-resources.txt`'s own second
> paragraph states which of its sections its header hash (`2607726f…`) covers — §2 and §5 — and that
> §3, §4 and §6 are the other, `86e58d9d…` run. §1 is the case in between, and the header hash is not
> what makes its row true: its file walk is timestamped `18:00:18`, which is neither §2's download
> telemetry (`11:54`/`12:10`) nor the demonstration's window (`18:23`–`18:29`), and §3's own reading
> list calls the `18:00:18 measure` "an earlier build". The figure is used because the `86e58d9d…`
> run's own trace carries the same bytes, not because a file's header named a build. Where a row's
> line carries no build at all, it is attributed from what the line does carry — an export tag or a
> pid — and says so on that row.
>
> | Row | Resolves to | Build |
> | --- | --- | --- |
> | Model size on disk | `e2-resources.txt` §1 (`878,065,731` bytes, 11 files walked, from the `18:00:18` trace); the same file/count pair in §3 and in `e2-offline-demonstration.txt` §4/§7 | **both captures** — the `18:00:18` walk is not attributed to either build, and the `86e58d9d…` run's own trace reports the same bytes, so the row states what two captures agree on |
> | Download time | `e2-resources.txt` §2 (`229,813` / `266,876` ms) | `2607726f…` |
> | …as the app saw it | `e2-resources.txt` §2, `278.6 s` screen figure | `2607726f…` |
> | Model load, per process | `e2-offline-demonstration.txt` §4 (`13.3 s`) and §7 (`7.9 s`) | `86e58d9d…` |
> | Process memory | `e2-resources.txt` §3 (`603.7` PSS), from the measurement export tagged `20261125T182918Z`, about 10 s after the turn-4 export `20261125T182908Z` that `e2-offline-demonstration.txt` §8 republishes | `86e58d9d…` — from the export tag; §3 names no build |
> | Generation speed | `e2-resources.txt` §4 (`4.8`–`13.4` tok/s); the pid grep in §4 selects `16359`/`16956`, and those are the demonstration run's own pids (`e2-offline-demonstration.txt` §5 and §6) | `86e58d9d…` — from the pids; §4 names no build, and this file's header hash does not apply to it |
> | Streaming granularity | `e2-offline-demonstration.txt` §4/§7 and `e2-resources.txt` §5 (four `220`s) | `86e58d9d…` |
> | Turn latency | `e2-resources.txt` §4 (`28.8`–`50.6` s, the SDK clock) and §6 (`28.9`–`50.9` s, the app clock, one line per turn); §6 cites `e2-offline-demonstration.txt`'s `turn N: stream closed` lines — `:100`, `:108`, `:116` / `:246` — and not a figure of its own | `86e58d9d…` — from that citation; §6 names no build |
> | APK size | `e2-resources.txt` §5 (`85253322` bytes) | `2607726f…` |
> | Store growth | `e2-offline-demonstration.txt` §5 (`128720`) and §8 (`133801`) | `86e58d9d…` |
>
> Three rows are `2607726f…` only: both download rows and APK size. Model size is the one figure that
> genuinely holds for both runs, so its row names both captures rather than one build: it is the same
> model and the same cache, and §1's `18:00:18` walk and the `86e58d9d…` run's own traces agree on
> `878,065,731 B / 11 files` byte for byte.
> `e2-resources.txt` is one file whose own §5 reports that build's hash while its §3, §4 and §6 are
> the `86e58d9d…` run — §5 hashes `bande-e2.apk` to `2607726f…` one line after `ls -l` reports a
> different filename, which is the single unpaired hash-and-size in this set and is left to the file
> quoted below. "Which file" and "which build" are therefore different questions, and this table
> answers them separately.
>
> The two builds' signed APKs are genuinely different sizes: `85,253,322 B (81.3 MiB)` for the
> `2607726f…` build (`e2-resources.txt` §5) and `84,975,920 B (81.0 MiB)` for the `86e58d9d…` build
> (`e2-offline-demonstration.txt` header, `e2-aar-decision.txt` §6). The APK-size row quotes the build
> whose file reports it; the other build's size is stated in its own cell rather than dropped.
>
> Two things this evidence set does not contain, and the table does not claim: the **pre-turn store
> size** of the `86e58d9d…` run — the earliest figure its committed evidence holds is the turn-3
> export, `128,720 B` — and a **hash and size stated for the same APK file** in `e2-resources.txt` §5,
> whose `shasum` names `bande-e2.apk` while its `ls -l` names
> `dev.durable.band.e.mobile-Signed.apk`. The table cites the size §5 reports and leaves that pairing
> to the file.

### Where the store lives, and how to check it

```
/data/user/0/dev.durable.band.e.mobile/files/durable/durable-store.jsonl   the store
/data/user/0/dev.durable.band.e.mobile/files/models_cache                  the weights
/sdcard/Android/data/dev.durable.band.e.mobile/files/e2-export/            exports for capture
```

The store is in app-private storage: it survives the process, and is removed only by uninstalling the
app or clearing its data. It is JSONL, so a kill mid-write is a truncated last line the store discards
on open rather than a corrupt file.

A release-signed app on this emulator cannot be read with `adb` — `run-as` says "package not
debuggable" and `/data/data/...` is permission denied (both quoted in
[`evidence/e2-store-on-device.txt`](evidence/e2-store-on-device.txt)). So the app writes a byte copy of
the store, plus its SHA-256 and its measurements, to its app-external directory. The screen prints the
same hash prefix, which is what makes a screenshot and an export checkably the same store.

### Running it

```bash
export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
export ANDROID_HOME=$HOME/Library/Android/sdk
cd real-world-scenarios/band-e-mobile-offline

./fetch-aar.sh embedded                     # verify the pinned embedded AAR into libs/ (gitignored)
./verify-android-boundary.sh                # the boundary rule, standalone; the build also runs it
cd bridge && ./gradlew :assembleRelease -Pbande.foundryAarMode=embedded && cd ..
cd app && dotnet build -f net10.0-android -c Release && cd ..

adb install -r app/bin/Release/net10.0-android/dev.durable.band.e.mobile-Signed.apk
adb shell am start -n dev.durable.band.e.mobile/crc64cf2f27458e91f277.MainActivity
```

Then, in order — the first step is the **only** one that needs the network:

```bash
# 1. with the network ON, once
#    tap "Prepare model"; wait ~4.5 min; expect "model ready in <n> s · cache 837.4 MB"

# 2. network OFF for everything that follows
adb shell cmd connectivity airplane-mode enable
adb shell settings get global airplane_mode_on        # expect 1
adb shell dumpsys connectivity | grep "Active default network"   # expect "none"

# 3. three offline turns. "Next scripted turn" fills the box; "Send turn" runs it.
#    28.9 / 46.9 / 48.6 s on this emulator (the app's own timer; the fourth turn is 50.9 s).

# 4. kill and reopen
adb shell am force-stop dev.durable.band.e.mobile
adb shell am start -n dev.durable.band.e.mobile/crc64cf2f27458e91f277.MainActivity
#    expect "restored from the durable store: store <bytes> B sha256:<prefix>"
#    tap "Prepare model" again (7 s reload), then the fourth scripted turn.

# 5. read the evidence the app wrote
adb shell ls /sdcard/Android/data/dev.durable.band.e.mobile/files/e2-export
adb shell cat /sdcard/Android/data/dev.durable.band.e.mobile/files/e2-export/e2-trace.log
```

The buttons are, in order: `Send turn`, `Next scripted turn`, `Prepare model`, `Catalog`, `Measure`,
`Export`, `Reset`. `Measure` writes a resource reading, `Export` writes a snapshot of the store, and
`Reset` commits a fresh transcript and the seed document as new revisions — it does not delete history,
which is why the store's "committed versions" count keeps growing across resets.

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
| 1 — on-device Durable + on-device SLM | #166 | **implemented** — the embedded AAR in-process, `IChatClient.CompleteStreamingAsync` as the streaming seam, and `app/Durability/BandEDurable.cs` as the integration idiom |
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
