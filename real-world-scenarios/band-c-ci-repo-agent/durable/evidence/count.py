"""Count the variant's lines, split into durability and application, for the matrix row.

Counting rule, the same one the Band A variant states so the two numbers are comparable:

    non-blank, non-comment lines in `durable/**.cs`, where a comment is `//` (including `///` doc
    comments) or a `/* ... */` block.

The split is **per declaration**, written down in `SPLIT` rather than estimated. A declaration is
matched by its first line, and the script fails when a declaration in the file has no entry, when
an entry matches nothing, or when a declaration matches two entries. That is what stops the numbers
drifting from the code: a renamed member fails the check instead of silently moving lines.

A declaration is **durability** when removing it would remove a guarantee this variant exists to
show: a document definition, a checkpoint field, a guarded commit, a resume path, the
phase-to-checkpoint wiring, the seam, the store open and its refusal. A declaration is
**application** when it would be the same in any version of this agent: the classifier, the scorer,
the recommendation rules, the label vocabulary, the comment template, the report layout, the fixture
model, the command line.

Usage:  python3 count.py            # the table
        python3 count.py --check    # exit 1 on any mismatch
"""

from __future__ import annotations

import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
DURABLE = HERE.parent

TYPE = re.compile(
    r"^(?:public|private|internal|protected)\s+"
    r"(?:sealed\s+|static\s+|partial\s+|abstract\s+)*"
    r"(?:class|record|enum|interface)\b"
)
CONSTANT = re.compile(r"^(?:public|private|internal|protected)\s+(?:static\s+)?(?:const|readonly)\b")
MEMBER = re.compile(r"^(?:public|private|internal|protected|required)\b[^=]*[({;=]")
STATEMENTS = {"using BandC.Durable;", "return await Runner.RunAsync(args);"}

# Every file's lines before its first declaration are `using` directives and the namespace. They are
# part of the file's cost, so they are counted; they are classified with the file they belong to.
PRELUDE: dict[str, str] = {
    "Contracts.cs": "durability",
    "Fixture.cs": "application",
    "Triage.cs": "application",
    "TriageStore.cs": "durability",
    "Phases.cs": "durability",
    "ExitSeam.cs": "durability",
    "Runner.cs": "durability",
    "Program.cs": "durability",
}

SPLIT: dict[str, dict[str, str]] = {
    "Contracts.cs": {
        "public static class Store": "durability",
        "public const string": "durability",
        "public static readonly DocumentDefinition": "durability",
        "public static string PullRequestOwner": "durability",
        "public static string OwnerOf": "durability",
        "public static class Phases": "durability",
        "public sealed record RunState": "durability",
        "public sealed record PullRequestState": "durability",
        "public sealed record CommentLedger": "durability",
        "public sealed record CommentRecord": "durability",
        "public sealed record RunInput": "durability",
        "public sealed record RunResult": "durability",
    },
    "Fixture.cs": {
        # The frozen snapshot: the baseline's model, unchanged.
        "public sealed class Fixture": "application",
        "public sealed class PullRequest": "application",
        "public sealed record DiffShape": "application",
    },
    "Triage.cs": {
        # The digest is the re-assessment identity and the memo key: both exist only because state
        # is durable. The fixture partitions are fixtures too, but they are here rather than inline
        # in Phase 1 because a phase cannot carry a local variable across a process.
        "public static string InputDigest": "durability",
        "public static string Digest": "durability",
        "public static List<PullRequest> Eligible": "durability",
        "public static List<(PullRequest Pr, int Minutes)> InCooldown": "durability",
        "public static List<PullRequest> NotOurs": "durability",
        "public static List<PullRequest> NotOpen": "durability",
        "public static DateTimeOffset Clock": "durability",
        # Everything the baseline already had, unchanged: `compare.py --code` checks that.
        "public static class Triage": "application",
        "public static string Classify": "application",
        "public static string AssessRisk": "application",
        "public static ScoreBreakdown Score": "application",
        "public static string Recommend": "application",
        "public static string ActionReason": "application",
        "public static List<Batch> Batch": "application",
        "public static bool IsTriageLabel": "application",
        "public static List<string> TriageLabels": "application",
        "public static string PriorityBand": "application",
        "public static string Comment": "application",
        "public static string Report": "application",
        "public static void Section": "application",
        "public static bool IsDocsPath": "application",
        "public static bool IsTestPath": "application",
        "public static bool IsCriticalPath": "application",
        "public static JsonArray ToArray": "application",
        "public static void WriteText": "application",
        "public static string Iso": "application",
        "public static readonly System.Text.Json.JsonSerializerOptions Json": "application",
        "public sealed record ScoreBreakdown": "application",
        "public sealed record Batch": "application",
        "public sealed class Assessment": "application",
    },
    "TriageStore.cs": {
        # Every declaration: this file is the custom durability surface.
        "public static class TriageStore": "durability",
    },
    "Phases.cs": {
        # The task definition, the step wrapper, and the seam mapping are durability; the eight
        # phases are the baseline's phases and are application.
        "private static async Task<TaskCommit> CollectAsync": "application",
        "private static async Task<TaskCommit> ClassifyAsync": "application",
        "private static async Task<TaskCommit> ScoreAsync": "application",
        "private static async Task<TaskCommit> RecommendAsync": "application",
        "private static async Task<TaskCommit> BatchAsync": "application",
        "private static async Task<TaskCommit> CommentAsync": "durability",
        "private static async Task<TaskCommit> ReportAsync": "application",
        "private static async Task<TaskCommit> SaveAsync": "durability",
        "public static DurableTaskDefinition Build": "durability",
        "private static async Task<TaskCommit> StepAsync": "durability",
        "private static string Completed": "durability",
        "public static class TriagePhases": "durability",
    },
    "ExitSeam.cs": {
        "public sealed class ExitSeam": "durability",
    },
    "Runner.cs": {
        "public static class Runner": "durability",
        "public sealed class Options": "application",
    },
    "Program.cs": {
        "using BandC.Durable;": "durability",
        "return await Runner.RunAsync(args);": "durability",
    },
}


def code_lines(lines: list[str]) -> int:
    """Non-blank, non-comment lines, with `/* ... */` blocks removed first."""
    text = re.sub(r"/\*.*?\*/", "", "\n".join(lines), flags=re.S)
    return sum(1 for line in text.split("\n") if line.strip() and not line.strip().startswith("//"))


def declarations(path: pathlib.Path) -> list[tuple[str, str | None, list[str]]]:
    """The file's declarations as (name, owning type name or None, lines).

    A type at column 0 is a declaration in its own right, and so is every member declared directly
    inside it. Each member knows its owner, so a classification given to a type can be the default
    for its members and a specific member can still be classified differently.
    """
    raw = path.read_text().split("\n")
    commented = ["" if line.strip().startswith("//") else line for line in raw]

    depths: list[int] = []
    depth = 0
    for line in commented:
        depths.append(depth)
        depth += line.count("{") - line.count("}")

    out: list[tuple[str, str | None, list[str]]] = []
    owner: str | None = None
    starts: list[tuple[int, str | None]] = []
    for index, line in enumerate(commented):
        stripped = line.strip()
        if stripped in STATEMENTS:
            starts.append((index, None))
            continue
        if depths[index] == 0 and (TYPE.match(stripped) or CONSTANT.match(stripped)):
            starts.append((index, None))
            continue
        if depths[index] == 1 and MEMBER.match(stripped) and not stripped.startswith("return"):
            starts.append((index, owner))

    # Resolve each member's owner: the most recent type declaration at column 0.
    resolved: list[tuple[str, str | None, list[str]]] = []
    current_type: str | None = None
    for index, line in enumerate(commented):
        stripped = line.strip()
        if depths[index] == 0 and TYPE.match(stripped):
            current_type = stripped
        if not any(start == index for start, _ in starts):
            continue
        level_owner = None if (depths[index] == 0) else current_type
        resolved.append((raw[index].strip(), level_owner, []))

    # Assign lines: a declaration owns its lines up to the next declaration at the same or a
    # shallower depth, and a type's own lines are only those outside its members.
    boundaries = [i for i, line in enumerate(commented)
                  if (line.strip() in STATEMENTS)
                  or (depths[i] == 0 and (TYPE.match(line.strip()) or CONSTANT.match(line.strip())))
                  or (depths[i] == 1 and MEMBER.match(line.strip()) and not line.strip().startswith("return"))]
    if boundaries and boundaries[0] > 0:
        out.append(("<prelude>", None, raw[:boundaries[0]]))

    for position, start in enumerate(boundaries):
        end = boundaries[position + 1] if position + 1 < len(boundaries) else len(raw)
        out.append((raw[start].strip(),
                    None if depths[start] == 0 else (resolved[position][1] if position < len(resolved) else None),
                    raw[start:end]))
    return out


def main(argv: list[str]) -> int:
    failures = 0
    grand_total = grand_durability = grand_application = 0

    print(f"{'File':<18}{'Code':>6}{'Durability':>12}{'Application':>13}")
    print(f"{'-' * 18}{'-' * 6}{'-' * 12}{'-' * 13}")

    for name in sorted(SPLIT):
        path = DURABLE / name
        if not path.exists():
            print(f"{name:<18}  MISSING")
            failures += 1
            continue

        table = SPLIT[name]
        durability = application = 0
        seen: set[str] = set()

        for declaration, owner, body in declarations(path):
            count = code_lines(body)

            if declaration == "<prelude>":
                if PRELUDE[name] == "durability":
                    durability += count
                else:
                    application += count
                continue

            # A member's own entry wins; otherwise it inherits its type's classification.
            hits = [prefix for prefix in table if declaration.startswith(prefix)]
            inherited = [prefix for prefix in table if owner is not None and owner.startswith(prefix)]

            if len(hits) > 1:
                print(f"  AMBIGUOUS     {name}: {declaration} matches {hits}")
                failures += 1
                continue
            if hits:
                seen.add(hits[0])
                kind = table[hits[0]]
            elif inherited:
                kind = table[inherited[0]]
            else:
                print(f"  UNCLASSIFIED  {name}: {count:>4}  {declaration}")
                failures += 1
                continue

            if kind == "durability":
                durability += count
            else:
                application += count

        for prefix in table:
            if prefix not in seen and not any(
                owner is not None and owner.startswith(prefix) for _, owner, _ in declarations(path)
            ):
                print(f"  UNUSED        {name}: nothing starts with '{prefix}'")
                failures += 1

        total = code_lines(path.read_text().split("\n"))
        if durability + application != total:
            print(f"  SPLIT MISMATCH  {name}: {durability}+{application} != {total}")
            failures += 1

        print(f"{name:<18}{total:>6}{durability:>12}{application:>13}")
        grand_total += total
        grand_durability += durability
        grand_application += application

    print(f"{'-' * 18}{'-' * 6}{'-' * 12}{'-' * 13}")
    print(f"{'Total':<18}{grand_total:>6}{grand_durability:>12}{grand_application:>13}")
    if grand_total:
        print()
        print(f"durability: {grand_durability} of {grand_total} code lines "
              f"({100 * grand_durability / grand_total:.0f}%)")

    on_disk = {path.name for path in DURABLE.glob("*.cs")}
    for missing in sorted(on_disk - set(SPLIT)):
        print(f"NOT IN TABLE  {missing}")
        failures += 1
    for stale in sorted(set(SPLIT) - on_disk):
        print(f"NOT ON DISK   {stale}")
        failures += 1

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
