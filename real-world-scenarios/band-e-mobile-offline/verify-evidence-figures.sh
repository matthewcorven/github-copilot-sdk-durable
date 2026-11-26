#!/usr/bin/env bash
# Verifies that the figures the summary tables carry appear in the evidence those tables cite.
#
# Three review rounds rejected this PR on one defect class: a summary table quoting a number that the
# files beneath it do not contain. It was reintroduced by hand every time, so the check belongs in a
# script rather than in another careful pass. This is that check.
#
#   ./verify-evidence-figures.sh            # check; exit 1 on any unresolved figure
#   ./verify-evidence-figures.sh --list     # also print every figure checked
#
# What it checks:
#
#   1. Every row of every README table that states a measurement contributes its figures. The tables
#      that describe the shape of the work — "What was tried", "Layer | Change", "What it needs from
#      here" — carry ranges, issue numbers and prose instead, and are skipped by name.
#   2. A figure resolves when it appears as a standalone number under evidence/, in the README's own
#      fenced captures (this record quotes whole commands and their output there), or in
#      probe-toolchain.sh, whose stdout evidence/maui-toolchain-probe.txt is.
#   3. Matching is boundary-aware and separator-blind. 21 does not match inside 2,107 or 214, and the
#      README's "878,065,731" resolves against the same number written "878 065 731" or "878065731".
#      The separators are a transcription habit, not the claim.
#   4. A figure is the unit and a cell is the scope. "PSS | 1,186 MB of 2,473 MB" contributes 1,186
#      and 2,473 — not their ratio — because the pairing of a number with the caption beside it is a
#      semantic claim this script cannot make.
#
# What it cannot see, said here rather than left to be discovered:
#
#   A figure with no matching number anywhere is caught, and so is one that exists only as digits of a
#   longer number. What is *not* caught is misattribution — a real measurement quoted against the wrong
#   row, model, build or unit. "21 files" is a real number, of the 1.5B model, and this script cannot
#   tell it from the 0.5B model's 11. Cross-model, cross-build and cross-unit claims stay a reviewer's
#   job, which is what ../README.md's provenance note is for and what evidence/README.md says this does
#   not prove.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readme="$here/README.md"
evidence="$here/evidence"
toolchain="$here/probe-toolchain.sh"
show_list=0
[[ "${1:-}" == "--list" ]] && show_list=1

if [[ ! -f "$readme" ]]; then
  echo "verify-evidence-figures: $readme is missing; there is nothing to check against" >&2
  exit 1
fi
if [[ ! -d "$evidence" ]]; then
  echo "verify-evidence-figures: $evidence is missing; a README table cannot resolve to nothing" >&2
  exit 1
fi

# A figure, as this record writes them: 878,065,731 / 878 065 731 / 878065731 / 87.9 / 85253322.
#
# The space form matches only in groups of three, so "1 269" is one figure while "at 6 tok/s" does not
# swallow the "t" beside the digit. Separators are then dropped: they are a transcription habit, not
# the claim.
figure_pattern='[0-9]+([, ][0-9]{3})*(\.[0-9]+)*'

# Every figure in the passed text, one per line.
#
# Two things are stripped first, because both are the same failure in different clothes: a figure that
# resolves on another figure's digits.
#
# A hash: "sha256:f02b209c3e84" is an identity, not a measurement. Without stripping it, the leading
# "02" is checked as a figure and the "209" inside it can resolve against a real 209 elsewhere. A run
# of hex digits is only a hash when it holds a letter — "85253322" is a byte count and must survive,
# "f02b209c3e84" is a digest and must not.
#
# A clock time: "19:51:28" is a timestamp, and without stripping it this evidence set contains a "51",
# which is exactly the kind of coincidence that lets an unsourced "29–51 s" turn-latency range pass.
# A real 51 is written as a standalone number somewhere else, so nothing is lost by dropping the
# components of a time of day.
figures_in() {
  awk '{
    s = $0; out = ""
    while (match(s, /[0-9a-fA-F]{8,}/)) {
      run = substr(s, RSTART, RLENGTH)
      out = out substr(s, 1, RSTART - 1)
      if (run !~ /[a-fA-F]/) out = out run
      out = out " "
      s = substr(s, RSTART + RLENGTH)
    }
    print out s
  }' <<<"$1" \
    | sed -E 's/[0-9]+:[0-9]{2}(:[0-9]{2})?(\.[0-9]+)?/ /g' \
    | grep -oE "$figure_pattern" \
    | sed -e 's/[ ,]//g'
}

# --- the material a figure may resolve to -------------------------------------------------------
#
# Captured text only: the .txt and .md files under evidence/. The two license texts are included
# because the download table's sizes are those files' byte lengths. Each file is reduced to its set of
# figures once, so the lookup below is a sorted-text search rather than a regex per figure per file.
normdir="$(mktemp -d)"
trap 'rm -rf "$normdir"' EXIT

tokens_of() {
  figures_in "$(cat)" | sort -u
}

sources=()
while IFS= read -r file; do
  sources+=("$file")
done < <(find "$evidence" -type f \( -name '*.txt' -o -name '*.md' \) | sort)
[[ -f "$toolchain" ]] && sources+=("$toolchain")

# The README's fenced blocks are a capture too: this record quotes whole commands and their output
# there. Its tables are excluded on purpose, so a figure may not resolve to the table that needs it
# checked.
awk '/^[[:space:]]*```/ { fence = !fence; next } fence { print }' "$readme" \
  | tokens_of > "$normdir/README_fenced"

i=0
for file in "${sources[@]}"; do
  tokens_of < "$file" > "$normdir/$i"
  i=$((i + 1))
done

figure_resolves() {
  local figure="$1" file
  # A version is a coordinate, not a measurement: "0.1.6" names the AAR release and "10.0.402" the
  # SDK, and evidence/foundry-local-aar.txt is titled "v0.1.6" rather than containing that token. A
  # thousands-separated byte count is a measurement and is always three digits in each group, so the
  # exclusion is narrow: only dotted numbers are let through unchecked.
  [[ "$figure" == *.*.* ]] && return 0
  [[ "$figure" =~ ^[0-9]+(\.[0-9]+)?$ ]] || return 1
  for file in "$normdir"/*; do
    if LC_ALL=C grep -qxF -- "$figure" "$file" 2>/dev/null; then
      return 0
    fi
  done
  return 1
}

# --- reading the README's tables -----------------------------------------------------------------
#
# Emits one line per data row of a table:
#
#   <readme line> <TAB> <table header> <TAB> <row>
#
# A blockquote table (the provenance note) is a table too, so the leading "> " is stripped before the
# pipe is tested. The header is the table's first row, which is what the skip rules key on.
table_rows() {
  awk '
    function unquote(s) { sub(/^[[:space:]]*(>[[:space:]]*)+/, "", s); return s }
    {
      line = unquote($0)
      if (line ~ /^[[:space:]]*\|/) {
        if (!intable) { intable = 1; header = line; firstrow = NR }
        if (NR != firstrow) print NR "\t" header "\t" line
      } else if (intable) {
        intable = 0
      }
    }
  ' "$readme"
}

# Table cells, one per line, delimiters and padding removed.
row_cells() {
  sed -e 's/^[[:space:]]*|//' -e 's/|[[:space:]]*$//' -e 's/|/\n/g' \
    | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'
}

row_caption() { row_cells <<<"$1" | head -n1; }

# Tables whose rows describe the work rather than measure it. They carry ranges, section and issue
# numbers and prose, and a range's endpoints have no evidence of their own to resolve against.
skips_table() {
  local header="$1"
  case "$header" in
    *"What was tried"*) return 0 ;;
    *"Layer | Change"*) return 0 ;;
    *"What it needs from here"*) return 0 ;;
  esac
  return 1
}

# --- the check ------------------------------------------------------------------------------------

fail=0
checked=0

while IFS=$'\t' read -r line header row; do
  [[ -z "$line" ]] && continue
  skips_table "$header" && continue

  # A "---" separator row is not data: every cell is hyphens.
  [[ "$(tr -d '| ' <<<"$row" | tr -d '-')" == "" ]] && continue

  caption="$(row_caption "$row")"
  while IFS= read -r cell; do
    [[ -z "$cell" ]] && continue
    while IFS= read -r figure; do
      [[ -z "$figure" ]] && continue
      # 0, 1 and 2 are excluded: answer counts, list indices and "component 2" far more often than they
      # are measurements, and "2 of 4" is a claim about turns this script cannot weigh. 3 and up are
      # checked, so a section reference costs one lookup and a wrong 214 is caught.
      case "$figure" in 0|1|2) continue ;; esac
      checked=$((checked + 1))
      [[ $show_list -eq 1 ]] && printf 'checked %-14s README.md:%-4s %s\n' "$figure" "$line" "$caption"
      if ! figure_resolves "$figure"; then
        echo "UNSOURCED FIGURE: $figure — README.md:$line — $caption" >&2
        echo "  not in evidence/, the README's fenced captures, or probe-toolchain.sh" >&2
        fail=$((fail + 1))
      fi
    done < <(figures_in "$cell")
  done <<<"$(row_cells <<<"$row")"
done < <(table_rows)

if [[ $fail -ne 0 ]]; then
  echo "verify-evidence-figures: $fail unsourced figure(s) among $checked checked" >&2
  exit 1
fi

echo "verify-evidence-figures: OK — $checked figures in README's measurement tables resolve to" \
     "evidence/, the README's own captures, or probe-toolchain.sh (attribution is not checked)"
