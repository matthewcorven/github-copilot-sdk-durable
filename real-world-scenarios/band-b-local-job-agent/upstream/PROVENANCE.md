# Provenance

Vendored, unmodified copies of the Ralph Loop recipe from `github/awesome-copilot`.

| Field | Value |
| --- | --- |
| Repository | https://github.com/github/awesome-copilot |
| Commit | `7e408815ddcb64889a3683799a85399331cfd4ef` |
| Commit date | 2026-11-04T04:12:09Z (`main` at time of vendoring) |
| Licence | MIT, `Copyright GitHub, Inc.` (`upstream/LICENSE`) |
| Vendored on | 2026-11-04 |

## Vendored paths

| File here | Path in `github/awesome-copilot` | SHA-256 |
| --- | --- | --- |
| `ralph-loop.md` | `cookbook/copilot-sdk/nodejs/ralph-loop.md` | `ffbeee181efac5aa7f1f3b474a00d9c2c93bee55b487e5a4a319571d3921721f` |
| `ralph-loop.ts` | `cookbook/copilot-sdk/nodejs/recipe/ralph-loop.ts` | `d23305acbb710f9ff7e93cf65a1aead01363b681910dd99a42ba727fa6e57db6` |
| `LICENSE` | `LICENSE` (repository root) | `e32449d23085399adc1222f7a17408b730550258e51627c153cb108ca9955823` |

## Verification

Fetched with

```bash
curl -sSL -o ralph-loop.md \
  "https://raw.githubusercontent.com/github/awesome-copilot/7e408815ddcb64889a3683799a85399331cfd4ef/cookbook/copilot-sdk/nodejs/ralph-loop.md"
curl -sSL -o ralph-loop.ts \
  "https://raw.githubusercontent.com/github/awesome-copilot/7e408815ddcb64889a3683799a85399331cfd4ef/cookbook/copilot-sdk/nodejs/recipe/ralph-loop.ts"
curl -sSL -o LICENSE \
  "https://raw.githubusercontent.com/github/awesome-copilot/7e408815ddcb64889a3683799a85399331cfd4ef/LICENSE"
```

and compared with `cmp` against a second fetch at the same commit. All three files are
byte-identical. Re-run the same fetch at commit `7e40881` and `shasum -a 256` against the
table above to confirm.

## Licence note

`github/awesome-copilot` carries one MIT licence at the repository root; the cookbook has no
separate notice of its own. `upstream/LICENSE` is that root file, included unmodified. The
documentation page (`ralph-loop.md`) survives being reproduced verbatim under that licence;
`ralph-loop.ts` is vendored for reference only and is not executed by this tree.

The C# port under `baseline/` is an independent work and carries this repository's licence.
