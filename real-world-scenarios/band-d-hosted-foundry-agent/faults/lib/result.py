#!/usr/bin/env python3
"""Write one fault result, and refuse the two ways a result could misrepresent the band.

Band D4's whole subject is attribution, so the result format enforces it rather than
relying on the reader:

1. **`platform` is required and structured.** Every result carries the block from
   `lib/platform.py`, which states what Foundry did and, for each of the four documented
   behaviours, whether this run reproduced it or is quoting documentation.
2. **`application` is required.** Every result carries what *this repository's* code did:
   the outcome, the evidence, the measurements. A reader can therefore never receive a
   session survival and read it as a step survival.

Two refusals, both exit-non-zero so a fault script can treat them as harness errors:

* a result claiming ``reproduced here`` for a platform behaviour while
  ``platform.live_foundry_called`` is false — a local analogue is not the platform;
* a result with a platform-only outcome and no application observation, or the reverse —
  the merge the band exists to prevent.

Usage: result.py --out FILE --fault NAME --variant baseline|durable-inside|durable-outside --template JSON

The template is a JSON object the fault script built; this file adds the envelope
(``fault``, ``variant``, ``harness``, ``recordedAt``) and validates it.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import sys

HARNESS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO_ROOT = os.path.abspath(os.path.join(HARNESS, "..", "..", ".."))
# `FAULTS_SCRATCH` can put the per-run directories outside the repository, so the scratch
# path the harness used is normalised too - a checked-in result must not carry a reader's
# absolute home directory.
SCRATCH_ROOT = os.path.abspath(
    os.environ.get("FAULTS_SCRATCH") or os.path.join(REPO_ROOT, ".scratch", "band-d4-faults")
)

REQUIRED_TOP = ["outcome", "platform", "application"]

#: The variables a harness may spell a machine path as, longest first so `<repo>/.scratch`
#: is not rewritten as `<repo>` plus a literal `/.scratch`.
_PATH_FORMS = (
    ("$FAULTS_SCRATCH", SCRATCH_ROOT),
    ("<repo>", REPO_ROOT),
)


def normalise_paths(value):
    """Replace this machine's absolute paths with the harness's own spellings.

    A fault's text is quoted verbatim from what the application printed, which can contain
    the full path the run happened in. That path is true but not evidence: it names one
    checkout of one reader. The substitution is recorded in `harness.paths_normalised`, so
    the rewrite is visible rather than a quiet edit of a quoted line.
    """
    if isinstance(value, str):
        text = value
        for token, root in _PATH_FORMS:
            if root:
                text = text.replace(root, token)
        return text
    if isinstance(value, list):
        return [normalise_paths(item) for item in value]
    if isinstance(value, dict):
        return {key: normalise_paths(item) for key, item in value.items()}
    return value


def _harness_block(fault: str, variant: str) -> dict:
    if variant == "baseline":
        runs_where = (
            "lib/baseline_driver.py against lib/fake_foundry.py, a local stand-in for the Foundry data "
            "plane that is never claimed to be Foundry. D1's hosted agents were not deployed, so the "
            "baseline column is its decision path as a client."
        )
    elif variant == "durable-inside":
        runs_where = (
            "the D2 hosted-agent image, built locally from real-world-scenarios/band-d-durable-inside/agent/ "
            "plus lib/py with D2's own Dockerfile. Each process is its own container over one named Docker "
            "volume standing in for the platform's per-session mount, so a container restart is a real stop "
            "and start."
        )
    else:
        runs_where = (
            "D3's orchestrator, dotnet-published from real-world-scenarios/band-d3-outside-orchestrator so "
            "the PID the harness starts is the application. Its agent calls go to lib/fake_foundry.py, the "
            "same local stand-in, whose request log is the call count."
        )
    # The invocation is the one that produced *this* file: a control run is a different
    # injection (none), and its result must not claim the fault's command line.
    injected = os.environ.get("FAULT_INJECT", "1") != "0"
    command = f"./faults/{fault}.sh --variant {variant}"
    if not injected:
        command += " --fault-not-injected"

    return {
        "harness": {
            "commit": os.environ.get("FAULTS_COMMIT", "unknown"),
            "python": f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}",
            "docker": os.environ.get("FAULTS_DOCKER", "not used by this fault"),
            "live_azure_calls": 0,
            "network": "none - every call a fault makes is to 127.0.0.1",
            "command": command,
            # The tokens, not the roots: a normalisation record that spelled the absolute
            # path would leak the very thing it exists to remove.
            "paths_normalised": {
                "<repo>": "the repository root",
                "$FAULTS_SCRATCH": "the harness scratch root",
            },
            "what_runs_where": runs_where,
        },
        "environment": {
            "foundry_deployment": "none - nothing was provisioned for this run",
            "model": "none - the stand-in serves fixed answers; no model was called",
        },
    }


def validate(document: dict) -> list[str]:
    problems: list[str] = []

    for key in REQUIRED_TOP:
        if key not in document:
            problems.append(f"missing required section '{key}'")

    platform = document.get("platform") or {}
    application = document.get("application") or {}

    if not platform.get("what_the_platform_did"):
        problems.append("platform.what_the_platform_did is empty - every result must say what Foundry did")
    if not isinstance(platform.get("live_foundry_called"), bool):
        problems.append("platform.live_foundry_called must be a boolean")
    if not (application.get("observations") or application.get("evidence")):
        problems.append("application carries neither observations nor evidence")

    behaviours = platform.get("behaviours")
    if not isinstance(behaviours, list) or not behaviours:
        problems.append("platform.behaviours is missing - the four documented behaviours must be listed")
        behaviours = []

    for behaviour in behaviours:
        status = behaviour.get("status")
        if status == "reproduced here" and not platform.get("live_foundry_called"):
            problems.append(
                f"behaviour '{behaviour.get('id')}' claims 'reproduced here' while "
                "platform.live_foundry_called is false - a local analogue is not the platform"
            )
        if status not in ("documented, not exercised here", "reproduced here"):
            problems.append(
                f"behaviour '{behaviour.get('id')}' has status '{status}'; the only two allowed are "
                "'documented, not exercised here' and 'reproduced here'"
            )

    if document.get("outcome") and not application.get("outcome_detail"):
        problems.append("application.outcome_detail is empty - the application side must say what happened")

    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="result")
    parser.add_argument("--out", required=True)
    parser.add_argument("--fault", required=True)
    parser.add_argument("--variant", required=True, choices=["baseline", "durable-inside", "durable-outside"])
    parser.add_argument("--template", required=True)
    args = parser.parse_args(argv)

    with open(args.template, encoding="utf-8") as handle:
        document = json.load(handle)

    document.setdefault("fault", args.fault)
    document.setdefault("variant", args.variant)
    document.update(_harness_block(args.fault, args.variant))
    document.setdefault(
        "recordedAt",
        datetime.datetime.now(datetime.timezone.utc).isoformat(),
    )
    document = normalise_paths(document)

    problems = validate(document)
    if problems:
        for problem in problems:
            print(f"result.py: refused - {problem}", file=sys.stderr)
        return 2

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=1, sort_keys=True)
        handle.write("\n")
    print(f"wrote {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
