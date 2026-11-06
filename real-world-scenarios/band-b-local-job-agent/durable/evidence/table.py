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
      102]),
    ("`DurableLoopAsync` — store, harness, extension install", "104-139", 104, 139, []),
    ("`DurableLoopAsync` — first-run branch", "140-166", 140, 166,
     [143, 144, 158, 159]),
    ("`DurableLoopAsync` — resume branch: stored input, terminal check, resume banner", "167-200",
     167, 200, [168, 169, 170, 171, 188, 189, 190, 191, 192, 193, 194, 197]),
    ("`DurableLoopAsync` — `try`/`finally` around the store", "201-206", 201, 206, []),
    ("`AwaitRunAsync` — await the runner, the stopped-at-seam path", "207-222", 207, 222,
     [209, 214, 215, 216, 217, 218, 219]),
    ("`Report` — the outcome, the cap line, the exit statuses", "223-250", 223, 250,
     [223, 224, 225, 226, 229, 230, 231, 232, 233, 234, 235, 236, 239, 240, 243, 244]),
    ("`Definition` — the task definition and its one phase", "252-265", 252, 265, []),
    ("`RunIterationAsync` — plan read, completion and cap from the stored input", "266-301",
     266, 301, [267, 274, 275, 276]),
    ("`RunIterationAsync` — in-flight detection and the report it prints", "302-310", 302, 310, []),
    ("`RunIterationAsync` — banner, checkpoint before the turn, memo read, no-turn branches",
     "311-348", 311, 348, []),
    ("`RunIterationAsync` — the turn, checkpoint after it, the memo, the two seams", "349-405",
     349, 405, []),
    ("`RunIterationAsync` — reconcile, settle checkpoint, stop at the seam", "406-452", 406, 452,
     [407, 408, 409, 410, 411, 412, 413, 414, 415, 416, 417, 418, 419, 420, 421, 422, 424, 425,
      426, 427, 428, 432]),
    ("`RunAgentTurnAsync` — the SDK turn and the seam that leaves it in flight", "454-516",
     454, 516, []),
    ("`Choices`", "517-525", 517, 525, []),
    ("`BuildTurnPrompt`", "526-554", 526, 554, []),
    ("`ReportInFlightTurn` — reading the store's checkpoint documents back", "563-635", 563, 635, []),
    ("`Reconcile`, its conditions and parsers, `NormalizeSectionHeadings`", "636-894", 636, 894, []),
    ("Helpers — `StepKey`, the three seams, `Digest`, `IsComplete`, `OpenItems`, …", "895-934",
     895, 934, [895, 896, 897, 898, 899, 900, 901, 902, 903, 904, 905, 906, 907, 923, 924, 925,
                926, 927, 928, 929, 930, 931, 932, 933]),
    ("`Job` constants and the `LoopInput`/`LoopCheckpoint`/`StepOutcome`/`LoopResult` records",
     "935-989", 935, 989, []),
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
