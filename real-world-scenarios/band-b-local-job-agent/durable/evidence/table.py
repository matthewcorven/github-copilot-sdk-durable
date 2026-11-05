"""The §6 durability table, computed from the loop source.

Regions are line ranges of `durable/ralph-loop-durable.cs`; a region is `None` for application
lines, or a set of line numbers that are application while the rest of the range is durability.
The sum is checked against the file's own code-line count, so a stale table fails loudly instead
of rendering.
"""
import os

HERE = os.path.dirname(__file__)
SOURCE = os.path.abspath(os.path.join(HERE, "..", "ralph-loop-durable.cs"))

REGIONS = [
    ("File header: `#:` directives and `using`", "1-60", 1, 60, []),
    ("Top-level statements", "61-62", 61, 62, []),
    ("`DurableLoopAsync` — target, args, model, store path", "64-103", 64, 103,
     [66, 67, 68, 69, 70, 71, 74, 75, 76, 77, 78, 84, 86, 87, 93, 94, 95, 96, 97, 98, 99, 100,
      102, 107, 115, 116, 120]),
    ("`DurableLoopAsync` — store, harness, extension install", "104-124", 104, 124, []),
    ("`DurableLoopAsync` — first-run branch", "125-151", 125, 151,
     [128, 129, 143, 144]),
    ("`DurableLoopAsync` — resume branch: stored input, terminal check, resume banner", "152-185",
     152, 185, [153, 154, 155, 156, 173, 174, 175, 176, 177, 178, 179, 182]),
    ("`DurableLoopAsync` — `try`/`finally` around the store", "186-191", 186, 191, []),
    ("`AwaitRunAsync` — await the runner, the stopped-at-seam path", "192-207", 192, 207,
     [194, 199, 200, 201, 202, 203, 204]),
    ("`Report` — the outcome, the cap line, the exit statuses", "208-235", 208, 235,
     [208, 209, 210, 211, 214, 215, 216, 217, 218, 219, 220, 221, 224, 225, 228, 229]),
    ("`Definition` — the task definition and its one phase", "237-250", 237, 250, []),
    ("`RunIterationAsync` — plan read, completion and cap from the stored input", "251-286",
     251, 286, [252, 259, 260, 261]),
    ("`RunIterationAsync` — in-flight detection and the report it prints", "287-295", 287, 295, []),
    ("`RunIterationAsync` — banner, checkpoint before the turn, memo read, no-turn branches",
     "296-333", 296, 333, []),
    ("`RunIterationAsync` — the turn, checkpoint after it, the memo, the two seams", "334-390",
     334, 390, []),
    ("`RunIterationAsync` — reconcile, settle checkpoint, stop at the seam", "391-437", 391, 437,
     [392, 393, 394, 395, 396, 397, 398, 399, 400, 401, 402, 403, 404, 405, 406, 407, 409, 410,
      411, 412, 413, 417]),
    ("`RunAgentTurnAsync` — the SDK turn and the seam that leaves it in flight", "439-501",
     439, 501, []),
    ("`Choices`", "502-510", 502, 510, []),
    ("`BuildTurnPrompt`", "511-539", 511, 539, []),
    ("`ReportInFlightTurn` — reading the store's checkpoint documents back", "548-620", 548, 620, []),
    ("`Reconcile`, its conditions and parsers, `NormalizeSectionHeadings`", "621-879", 621, 879, []),
    ("Helpers — `StepKey`, the three seams, `Digest`, `IsComplete`, `OpenItems`, …", "880-919",
     880, 919, [880, 881, 882, 883, 884, 885, 886, 887, 888, 889, 890, 891, 892, 908, 909,
                910, 911, 912, 913, 914, 915, 916, 917, 918]),
    ("`Job` constants and the `LoopInput`/`LoopCheckpoint`/`StepOutcome`/`LoopResult` records",
     "920-968", 920, 968, []),
]

WHOLLY_APPLICATION = {
    "File header: `#:` directives and `using`",
    "Top-level statements",
    "`RunAgentTurnAsync` — the SDK turn and the seam that leaves it in flight",
    "`Choices`",
    "`BuildTurnPrompt`",
    "`Reconcile`, its conditions and parsers, `NormalizeSectionHeadings`",
}

WHOLLY_DURABILITY = {
    "`DurableLoopAsync` — store, harness, extension install",
    "`DurableLoopAsync` — `try`/`finally` around the store",
    "`Definition` — the task definition and its one phase",
    "`RunIterationAsync` — in-flight detection and the report it prints",
    "`RunIterationAsync` — banner, checkpoint before the turn, memo read, no-turn branches",
    "`RunIterationAsync` — the turn, checkpoint after it, the memo, the two seams",
    "`ReportInFlightTurn` — reading the store's checkpoint documents back",
    "`Job` constants and the `LoopInput`/`LoopCheckpoint`/`StepOutcome`/`LoopResult` records",
}


def _code_lines(source):
    lines = {}
    for number, text in enumerate(source, 1):
        stripped = text.strip()
        if stripped and not stripped.startswith("//") and not stripped.startswith("#:"):
            lines[number] = text
    return lines


def render():
    with open(SOURCE) as handle:
        source = handle.read().split("\n")
    code = _code_lines(source)

    rows, code_total, dur_total = [], 0, 0
    for name, label, first, last, app_lines in REGIONS:
        in_range = [n for n in code if first <= n <= last]
        count = len(in_range)
        if name in WHOLLY_APPLICATION:
            durability = 0
        elif name in WHOLLY_DURABILITY:
            durability = count
        else:
            app = set(app_lines)
            durability = count - len(app & set(in_range))
        code_total += count
        dur_total += durability
        rows.append(f"| {name} | {label} | {count} | {durability} | {count - durability} |")

    if code_total != len(code):
        raise SystemExit(f"table covers {code_total} code lines, file has {len(code)}")
    pct = round(100 * dur_total / code_total)

    table = (
        "| Region | Lines | Code | Durability | Application |\n"
        "| --- | --- | --- | --- | --- |\n"
        + "\n".join(rows)
        + f"\n| **Total** | | **{code_total}** | **{dur_total}** | **{code_total - dur_total}** |"
    )
    totals = {
        "TABLE_REGIONS": str(len(REGIONS)),
        "TABLE_TOTAL": str(code_total),
        "TABLE_DUR": str(dur_total),
        "TABLE_APP": str(code_total - dur_total),
        "TABLE_PCT": str(pct),
    }
    return table, totals


if __name__ == "__main__":
    table, totals = render()
    print(table)
    print(totals)
