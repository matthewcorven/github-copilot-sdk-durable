# Provenance — Build26 AgentOrchestrator

The files under `upstream/` are an unmodified copy of the Build26 reference application,
vendored so Band A has a byte-identical "before" to compare the Durable variant against.

| | |
| --- | --- |
| Repository | https://github.com/microsoft/Build26-BRK206-your-agent-anywhere-multiclient-multidevice-with-github-copilot-sdk |
| Default branch | `main` |
| Pinned commit | `da8b33a55c50403e403f3f044afcc5996c50007f` |
| Commit date | 2026-08-28 (authored 09:11:27 +01:00) |
| Vendored on | 2026-11-04 |
| License | MIT — Copyright (c) Microsoft Corporation |

## What was vendored

`upstream/AgentOrchestrator/` is the complete `src/AgentOrchestrator/` directory at the
pinned commit — 123 files, everything GitHub returns for that path, including the project's
own `.gitignore` and `.sln`:

```
upstream/LICENSE                              <- repo-root LICENSE, verbatim
upstream/AgentOrchestrator/.gitignore
upstream/AgentOrchestrator/Agent.cs
upstream/AgentOrchestrator/AgentOrchestrator.csproj
upstream/AgentOrchestrator/AgentOrchestrator.sln
upstream/AgentOrchestrator/AppState.cs
upstream/AgentOrchestrator/Components/...     (App.razor, Routes.razor, _Imports.razor,
                                               ToolCallView.razor, Layout/*, Pages/*)
upstream/AgentOrchestrator/Data/Properties/00001.json … 00100.json   (100 files)
upstream/AgentOrchestrator/Phase.cs
upstream/AgentOrchestrator/Program.cs
upstream/AgentOrchestrator/Properties/launchSettings.json
upstream/AgentOrchestrator/PropertyDatabase.cs
upstream/AgentOrchestrator/appsettings.Development.json
upstream/AgentOrchestrator/appsettings.json
upstream/AgentOrchestrator/wwwroot/app.css
upstream/AgentOrchestrator/wwwroot/favicon.png
```

The issue named `Agent.cs` (90 lines), `AppState.cs` (48 lines) and `Program.cs` (28 lines).
The application does not build without the rest: `Program.cs` maps `Components/App.razor`,
the agent's system prompt tells the model to call the tools declared in
`PropertyDatabase.cs`, and `AppState.cs` has to hand out the database. The whole directory
was vendored rather than the three named files.

`LICENSE` is the repository's MIT license, copied byte for byte to `upstream/LICENSE`. The
repository also has a `LICENSE-DOCS` (CC BY-SA 4.0) for its `docs/` tree; no documentation
files were vendored, so it was not copied.

## How byte-identity was verified

Git stores each file as a blob whose object ID is `sha1("blob <length>\0" + content)`. For
every vendored file the local content was re-hashed in that format and compared against the
`sha` the GitHub tree API reports for the same path at the pinned commit:

```
GET /repos/microsoft/Build26-BRK206-.../git/trees/da8b33a...?recursive=1
```

Result: 123 of 123 files match, 0 mismatched, 0 extra files under `upstream/AgentOrchestrator/`;
`upstream/LICENSE` matches the tree entry for `LICENSE` (blob `9e841e7a26e4eb057b24511e7b92d42b257a80e5`).
A second check — a shallow clone of the repository at the same commit, then `diff -r` against
`upstream/AgentOrchestrator/` — is empty.

## Content hashes (`shasum -a 256`)

```
ac72d553f1834f9880dbfca97764dd340335a22b92bf38aae3dad88af6731dd6  AgentOrchestrator/.gitignore
acd9b4eee2ef8795b7996998bd40ed593b00e6235a095a904837161cffbb1b85  AgentOrchestrator/Agent.cs
4989d4e6f2ea98b4cb26c74a39a4463a8b9a823abd0d694b215f4071efb7a805  AgentOrchestrator/AgentOrchestrator.csproj
a4fdb82a15001d96e9d6420feb662dec7d54980a8cad4362949d419d1696577d  AgentOrchestrator/AgentOrchestrator.sln
7fde66d3adc3f01247c57bc2196b05ee3dd892285d1edda6c877f6a94a633509  AgentOrchestrator/AppState.cs
ac53b5591424ecacd6c80ac66f8a20402a220b14d9f41456c1d659395a2c688a  AgentOrchestrator/Components/App.razor
31ea80521763251c75a712d8505bec9a27ae66a8bf1b599bd38f30d2180f9a5a  AgentOrchestrator/Components/Layout/MainLayout.razor
06987994d4625c5c48cdd15373c05e3a2d81a3f92af790fcaabc7bbf80dbca5a  AgentOrchestrator/Components/Layout/ReconnectModal.razor
3f672eea19a76f24518b6a02d2014d5c5f6fd4e4bd2f97eac9e431b4dd0973fd  AgentOrchestrator/Components/Layout/ReconnectModal.razor.css
bc924313acad6186b22e5331a52fe18a4e3186a5082185e40f2e89620932814e  AgentOrchestrator/Components/Layout/ReconnectModal.razor.js
5fc97b404692b11255d0c2b53e61304f2a7670dc9ff5fdcc2edae513b3ea283d  AgentOrchestrator/Components/Pages/Error.razor
6f4f9042a142402f7dd14608c5604ad37f25251dc5a14298cf029ee214bfa8cd  AgentOrchestrator/Components/Pages/Home.razor
e58e22841fd7a4ec75e92bbd832c97fb0c700b5c7234fc631a0bcb5c099a01a7  AgentOrchestrator/Components/Routes.razor
d080ea92550c61de2d681188d1fd4785b10f9dfcd6538d344f53536a68bad1f3  AgentOrchestrator/Components/ToolCallView.razor
4771e3c151e55dcb56cc83fec757db5cce4738a0e21c431de799d6b11de1896e  AgentOrchestrator/Components/_Imports.razor
783558962e7614733c159f7db92207fbf9fb6365bc44634095c0cddf9058c9b1  AgentOrchestrator/Phase.cs
4b78c9ef67bbcd281e02d570c6c7cc4b6bf81dc4256f094f67d6fe4df39cc327  AgentOrchestrator/Program.cs
6703226b8d41899ed3001d9557cc8d92e969994a830d2400f027b9e2d850b02d  AgentOrchestrator/Properties/launchSettings.json
b7f18d87e027a271dfeef60d3bb9918e6afae359373cd692c60842a460866176  AgentOrchestrator/PropertyDatabase.cs
817db0bf2ecca78364c41dbaf7949bf2504cf47a1c3d0d54f3be8178fefc030b  AgentOrchestrator/appsettings.Development.json
2d8bb40059df22089b94c025be8baad5a869b72d4d755d199c872f03bfe6f225  AgentOrchestrator/appsettings.json
044c113babc5a019ced7f6dc5ab30df8816bf2d4cf2a53f8d368c49bbf16f568  AgentOrchestrator/wwwroot/app.css
e265ac0f2dda1e5dfa65b1adf330722bb3ef7789115283604d8cd19f098f1f08  AgentOrchestrator/wwwroot/favicon.png
c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383  LICENSE
```

The 100 property fixtures under `Data/Properties/` are omitted above for length; they are
covered by the blob-ID check described earlier.

## Licence

The vendored copy is licensed under the MIT licence reproduced verbatim at
`upstream/LICENSE`. Copyright (c) Microsoft Corporation. `baseline/` is a port derived from
this copy and is likewise MIT, under the same copyright notice; this repository's own
licence is at its root.

## Deliberate deviations in `baseline/`

`baseline/` is a copy of `upstream/AgentOrchestrator/` with exactly three changes, none of
which touch behaviour:

1. `AgentOrchestrator.csproj` — `<AssemblyName>BandABaseline</AssemblyName>` and
   `<RootNamespace>AgentOrchestrator</RootNamespace>` added.
2. `Components/App.razor` — the scoped-CSS link follows the assembly name:
   `BandABaseline.styles.css` instead of `AgentOrchestrator.styles.css`.
3. `AgentOrchestrator.sln` and the upstream `.gitignore` are not carried over; the project is
   built by path and the repository's root `.gitignore` already covers `bin/` and `obj/`.

The target framework is unchanged (`net10.0`; this repository's SDK is 10.0.402). The
package versions are unchanged. Nothing in `Agent.cs`, `AppState.cs`, `Program.cs`,
`Phase.cs` or `PropertyDatabase.cs` was edited.
