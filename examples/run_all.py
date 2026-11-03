"""Run the Python parity inventory; only --deterministic is credential-free."""

import argparse
import difflib
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

from parity_manifest import ROOT, REFERENCE_IDS, build


def normalise_output(text):
    replacements = [
        (r"/(?:private/)?var/folders/[A-Za-z0-9_.+/-]+|/tmp/[A-Za-z0-9_.+/-]+", "<temp>"),
        (r"[A-Za-z]:[\\/](?:Users[\\/][^\s\\/]+[\\/]AppData[\\/]Local[\\/]Temp|Windows[\\/]Temp)[\\/][^\s]+", "<temp>"),
        (r"s(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})", "<session>"),
        (r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32}", "<guid>"),
        (r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+(?:Z|[+-][0-9]{2}:[0-9]{2})", "<timestamp>"),
        (r"[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?", "<timestamp>"),
        (r"\b[cet][0-9]+\b", "<id>"),
    ]
    text = text.replace("\r\n", "\n")
    for pattern, replacement in replacements:
        text = re.sub(pattern, replacement, text)
    return text


def load_manifest():
    manifest = json.loads((ROOT / "examples" / "parity.json").read_text())
    if (type(manifest["schemaVersion"]) is not int
            or any(type(value) is not int for value in manifest["scope"])
            or any(type(entry["live"]) is not bool or type(entry["deterministic"]) is not bool
                   for entry in manifest["examples"])):
        raise ValueError("Global parity manifest has invalid version/scope/boolean field types")
    expected = build()
    if manifest != expected:
        drifted = [entry["id"] for entry, actual in zip(expected["examples"], manifest["examples"])
                   if entry != actual]
        drifted += [entry["id"] for entry in expected["examples"][len(manifest["examples"]):]]
        drifted += [entry["id"] for entry in manifest["examples"][len(expected["examples"]):]]
        raise ValueError("Global parity manifest differs from range/source declarations; regenerate explicitly; "
                         f"drifted example id(s): {', '.join(drifted) or 'none'}")
    provenance = json.loads((ROOT / "examples" / "expected" / "provenance.json").read_text())
    if sorted(provenance["outputs"]) != sorted(REFERENCE_IDS):
        raise ValueError("Reference output inventory must include four actual C# examples")
    def sha256(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()
    if provenance["fixtureSha256"] != sha256(ROOT / "tools/ReplayRuntime/fixtures/basic.json"):
        raise ValueError("Reference fixture changed; recapture actual C# output explicitly")
    for entry in manifest["examples"]:
        if entry["expectedOutput"]:
            expected = ROOT / entry["expectedOutput"]
            if not expected.is_file() or not expected.read_text():
                raise ValueError(f"Missing or empty expected output: {expected}")
            proof = provenance["outputs"][entry["id"]]
            if proof["expectedSha256"] != sha256(expected) or proof["sourceSha256"] != sha256(ROOT / "examples" / entry["source"]):
                raise ValueError(f"Changed actual C# reference output/source: {expected}")
    return manifest


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--deterministic", action="store_true")
    mode.add_argument("--all-available", action="store_true", help="Default: run every entry, including LIVE effects")
    parser.add_argument("--list", action="store_true", help="Validate inventory and print the exact selection")
    parser.add_argument("ids", nargs="*", help="Optional exact example IDs; cannot widen deterministic selection")
    args = parser.parse_args(argv)
    try:
        manifest = load_manifest()
        known = {entry["id"] for entry in manifest["examples"]}
        if len(set(args.ids)) != len(args.ids) or set(args.ids) - known:
            raise ValueError(f"Invalid or duplicate requested IDs: {args.ids}")
        if args.deterministic and any(entry["id"] in args.ids and not entry["deterministic"]
                                      for entry in manifest["examples"]):
            raise ValueError("Requested IDs include an example excluded from deterministic mode")
        chosen = []
        for entry in manifest["examples"]:
            if args.deterministic and not entry["deterministic"]:
                print(f"EXCLUDE {entry['id']} {entry['source']}: {entry['liveReason']}")
                continue
            if args.ids and entry["id"] not in args.ids:
                continue
            chosen.append(entry)
            language = entry["languages"]["py"]
            print(f"SELECT {entry['id']} {language['path']} [{language['status']}]"
                  f"{' LIVE' if entry['live'] and not args.deterministic else ''}")
        if args.list:
            return 0
        env = os.environ.copy()
        if args.deterministic:
            runtime = ROOT / "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
            if not runtime.is_file():
                raise ValueError(f"Build the actual ReplayRuntime first: {runtime}")
            env["DURABLE_RUNTIME_PATH"] = str(runtime)
            env["DURABLE_EXAMPLE_FIXTURE"] = str(ROOT / "tools/ReplayRuntime/fixtures/basic.json")
            env.pop("DURABLE_EXAMPLE_REPLAY_FIXTURE", None)
            print("Replay sessions generated by the runtime are synthesised, NOT recorded transcripts or LIVE/model proof.")
        elif env.get("DURABLE_RUNTIME_PATH") or env.get("DURABLE_EXAMPLE_FIXTURE") or env.get("DURABLE_EXAMPLE_REPLAY_FIXTURE"):
            raise ValueError("All-available requires the native runtime; remove replay/runtime override variables")
        failures = 0
        compared = 0
        for entry in chosen:
            language = entry["languages"]["py"]
            if language["status"] != "ported":
                print(f"FAIL {entry['id']}: pending-native-api: {language['evidence']['pendingReason']}", file=sys.stderr)
                failures += 1
                continue
            command = [sys.executable, str(ROOT / language["path"])]
            if not args.deterministic and language["sandboxMode"] == "supported":
                command.append("--live")
            try:
                result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True,
                                        timeout=language["evidence"].get("timeoutSeconds", 180))
                sys.stdout.write(result.stdout)
                sys.stderr.write(result.stderr)
                if result.returncode:
                    raise ValueError(f"example exit code {result.returncode}")
                if entry["expectedOutput"] and args.deterministic:
                    expected = (ROOT / entry["expectedOutput"]).read_text()
                    actual = normalise_output(result.stdout)
                    if actual != expected:
                        diff = "".join(difflib.unified_diff(expected.splitlines(True), actual.splitlines(True),
                                                          fromfile=entry["expectedOutput"], tofile=language["path"]))
                        raise ValueError(f"actual C# output mismatch:\n{diff}")
                    compared += 1
                print(f"PASS {entry['id']}")
            except (ValueError, OSError, subprocess.TimeoutExpired) as error:
                print(f"FAIL {entry['id']}: {error}", file=sys.stderr)
                failures += 1
        required = sum(bool(entry["expectedOutput"]) for entry in chosen) if args.deterministic else 0
        if compared != required:
            print(f"FAIL output comparisons: expected {required}, passed {compared}", file=sys.stderr)
            failures += 1
        print(f"Python: {len(chosen)} selected, {failures} failures, {compared} actual C# output comparisons")
        return 1 if failures else 0
    except (ValueError, OSError, KeyError, TypeError) as error:
        print(f"FAIL parity contract: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
