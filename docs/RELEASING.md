# Releasing `Copilot.Durable`

This document is the release strategy for this repository. It answers four questions:
what a release *is*, how often one happens, what each branch is for, and the exact
steps to cut one.

The policy was settled in [issue #30](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/30)
(decision comment, 2026-10-25).

---

## 1. The release unit

A release is **one version of the NuGet package `Copilot.Durable`**, published from
[`lib/Copilot.Durable.csproj`](../lib/Copilot.Durable.csproj), and **one git tag carrying
the same version**.

| Artifact | Where it comes from | Example |
| --- | --- | --- |
| Package | `dotnet pack lib/Copilot.Durable.csproj` | `Copilot.Durable.0.4.0.nupkg` |
| Package version | `<Version>` in `lib/Copilot.Durable.csproj` | `0.4.0` |
| Git tag | `v` + the package version | `v0.4.0` |
| Release notes | the matching `## [x.y.z]` section of [`CHANGELOG.md`](../CHANGELOG.md) | `## [0.4.0]` |

**The repo tag and the package version are one number.** `v0.4.0` tags the commit that
packs `Copilot.Durable` 0.4.0. If they ever disagree, the release is broken: a consumer
who pins the tag and a consumer who pins the package must get the same code.

The unit is the package, not the repository, because the package is what a downstream
project depends on. The examples are documentation of the pattern; they ship inside the
tag so a reader can see the package in use, but they are not independently versioned.

### What a release is not

A tag alone is not a release. A release is the tag **plus** release notes. A tag pushed
without a changelog section is an unfinished release — finish it or delete the tag.

---

## 2. Cadence

Releases are **event-driven, not calendar-driven.** There is no monthly or weekly train.
Cut a release when all three of these are true:

1. A coherent set of changes is merged to `dev` — a theme, not a scattering of unrelated
   fixes.
2. The full gate set below passes on `dev`.
3. `CHANGELOG.md` has an `## [Unreleased]` section that is ready to be dated.

There is no obligation to release every merged PR. `dev` is allowed to sit ahead of the
last tag indefinitely; that is what an integration branch is for.

The forward roadmap lives in the `release:*` labels rather than in a schedule:

| Label | Meaning |
| --- | --- |
| `release:v0.5.0` | targeted for the next minor release |
| `release:v0.6.0` | targeted for the release after that |
| `release:v1.0.0` | the first release we consider a stable API commitment |
| `release:backlog` | acknowledged, not yet targeted at a version |

---

## 3. Branch roles

| Branch | Role | Who writes to it |
| --- | --- | --- |
| `main` | **Release branch and the repository default.** Every commit on `main` is part of a tag, or is the merge that creates one. | Only by promoting `dev`. Never directly. |
| `dev` | **Integration branch.** All feature work lands here. This is where the code is newest. | Squash-merged PRs from feature branches. |
| `squad/{issue}-{slug}` | **Feature branch.** One issue, one branch. | The agent or human doing the work. |

Rules:

- Feature branches are created from `origin/dev` and their PRs target `dev`.
- `dev` merges to `main` **only when a release is cut**, never continuously. A `dev` tip
  that is ahead of `main` is normal and expected.
- `main` is never committed to directly, and never force-pushed.
- The promotion merge is a merge commit (`--no-ff`), so the release point is explicit in
  history: `Merge branch 'dev'` immediately followed by the tag.

### Why `main` is the default branch

The repository default branch is what a new reader gets from a clone, and what GitHub
shows when someone lands on the repo. Pointing it at `dev` would hand every reader
unreleased, possibly half-finished work with no version to pin. Pointing it at `main`
means the default checkout **is** the latest release, and `git tag` tells them exactly
which one.

Readers who want the newest code know to ask for `dev`, and `dev` is documented as the
integration branch.

---

## 4. Version numbering

[Semantic Versioning](https://semver.org/) with the `0.x` caveat: while the major version
is `0`, the minor version may contain breaking changes. Say so in the changelog when it
does.

- **MAJOR** — a stable-API commitment or a break we will not repeat.
- **MINOR** — new capability. `0.4.0` → `0.5.0`.
- **PATCH** — fixes only, no new public surface. `0.4.0` → `0.4.1`.

Bump `<Version>` in `lib/Copilot.Durable.csproj` in the PR that prepares the release, and
record it in `CHANGELOG.md`. The version is never bumped on `main` after the tag.

---

## 5. Gates

All of these must pass on the commit being released. Do not tag a commit that has not
passed them.

```bash
dotnet build tests/Durable.Tests
dotnet test tests/Durable.Tests
COMPILE_ONLY=1 ./scripts/validate.sh
```

CI (`.github/workflows/build.yml`) runs the equivalent on every push to `main` and `dev`:
it builds and packs the package, runs the xUnit suite, and compiles all 38 examples while
executing the ones that need no Copilot sign-in. A green CI run on `dev` is a necessary
but not sufficient condition — run the three commands above locally before tagging.

---

## 6. Cutting a release

Substitute the version everywhere below; `0.4.0` is an example.

1. **Prepare the release on `dev`.** The version bump and the `CHANGELOG.md` section land
   as a normal PR into `dev`, with the `## [Unreleased]` heading renamed to
   `## [x.y.z] - YYYY-MM-DD`.

2. **Confirm `dev` is green.** Run the gates in §5. Confirm CI on `dev` is green.

3. **Promote `dev` to `main`** with an explicit merge commit:

   ```bash
   git checkout main
   git pull --ff-only
   git merge --no-ff dev -m "Merge branch 'dev' for v0.4.0"
   ```

4. **Verify the version on `main`** matches the intended tag:

   ```bash
   grep -o '<Version>[^<]*' lib/Copilot.Durable.csproj
   ```

5. **Tag and push.** An annotated tag, so the tag itself carries an author and a date:

   ```bash
   git tag -a v0.4.0 -m "v0.4.0"
   git push origin main
   git push origin v0.4.0
   ```

6. **Create the GitHub release**, using the changelog section as the body. A tag with no
   release notes does not count as a release:

   ```bash
   gh release create v0.4.0 --title "v0.4.0" --notes-file <(sed -n '/## \[0.4.0\]/,/## \[/p' CHANGELOG.md)
   ```

   Mark `--prerelease` for a `0.x` release that is not meant for dependents yet.

7. **Verify:**

   ```bash
   git tag                        # non-empty, newest tag is the version you just cut
   gh release list                # non-empty, v0.4.0 listed
   ```

8. **Open `## [Unreleased]` again** on `dev` for the next cycle.

### Publishing the package

Packing is automated by CI, which uploads the `.nupkg` as a build artifact on every push.
Publishing that artifact to nuget.org is a separate, manual step and is deliberately not
part of cutting a tag — the tag is the versioned reference point even before the package
is on a feed.

---

## 7. Hotfixes

A fix that cannot wait for the next scheduled release is cut the same way, with a PATCH
version: branch from `main`, land the fix, tag. If `dev` is ahead, merge `main` back into
`dev` afterwards so the fix is not lost on the next promotion. This repository has not
needed one yet; the procedure exists so the first one is not improvised.
