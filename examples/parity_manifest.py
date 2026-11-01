"""Build the checked-in 00..35 contract from the range owners' declarations."""

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RANGES = ["00-10", "11-21", "22-35"]
REFERENCE_IDS = ["00", "01", "03", "04"]


def reference_selection(root=ROOT):
    text = (root / "scripts" / "validate.sh").read_text()
    live_block = re.search(r"LIVE_EXAMPLES=\((.*?)\)", text, re.S)
    deterministic_block = re.search(r"DETERMINISTIC_EXAMPLES=\((.*?)\)", text, re.S)
    reasons_block = re.search(r"deterministic_exclusion_reason\(\) \{(.*?)\n\}", text, re.S)
    if not live_block or not deterministic_block or not reasons_block:
        raise ValueError("Cannot read exact C# selection from scripts/validate.sh")
    live = set(live_block[1].split())
    deterministic = set(deterministic_block[1].split())
    reasons = dict(re.findall(r'(\d\d-[\w-]+)\) echo "([^"]+)" ;;', reasons_block[1]))
    return live, deterministic, reasons


def build(root=ROOT):
    live, deterministic, reasons = reference_selection(root)
    sources = sorted(path.name for path in (root / "examples").glob("[0-3][0-9]-*.cs")
                     if int(path.name[:2]) <= 35)
    if [source[:2] for source in sources] != [f"{n:02}" for n in range(36)]:
        raise ValueError("C# contract must contain every numbered example exactly once, 00..35")
    rows = {}
    for language, extension in [("ts", "ts"), ("py", "py")]:
        rows[language] = {}
        for range_name in RANGES:
            path = root / "examples" / language / f"parity-{range_name}.json"
            declaration = json.loads(path.read_text())
            first, last = map(int, range_name.split("-"))
            if declaration.get("schemaVersion") != 1 or declaration.get("range") != [first, last]:
                raise ValueError(f"{path}: unsupported range schema/version")
            for row in declaration["examples"]:
                identifier = row.get("id")
                if identifier not in {f"{n:02}" for n in range(first, last + 1)} or identifier in rows[language]:
                    raise ValueError(f"{path}: invalid or duplicate id {identifier!r}")
                source = next(s for s in sources if s[:2] == identifier)
                if row.get("source") != source:
                    raise ValueError(f"{path}: {identifier} source must equal {source}")
                expected = f"examples/{language}/{source[:-3]}.{extension}"
                if row.get("path") not in (expected, Path(expected).name):
                    raise ValueError(f"{path}: {identifier} path must identify {expected}")
                if not (root / expected).is_file():
                    raise ValueError(f"Missing example: {expected}")
                status = row.get("status")
                if status not in ("ported", "pending-native-api"):
                    raise ValueError(f"{path}: {identifier} invalid implementation status {status}")
                if status == "pending-native-api" and not row.get("pendingReason"):
                    raise ValueError(f"{path}: {identifier} needs an actionable pendingReason")
                sandbox = row.get("sandboxMode")
                if sandbox not in ("supported", "unsupported", "not-applicable"):
                    raise ValueError(f"{path}: {identifier} missing sandboxMode")
                if sandbox == "unsupported" and not row.get("sandboxReason"):
                    raise ValueError(f"{path}: {identifier} needs a proven sandboxReason")
                rows[language][identifier] = {
                    "path": expected, "status": status, "sandboxMode": sandbox,
                    "rangeManifest": str(path.relative_to(root)), "evidence": row,
                }
        if set(rows[language]) != {f"{n:02}" for n in range(36)}:
            raise ValueError(f"{language}: range declarations must cover 00..35 exactly")
    entries = []
    for source in sources:
        identifier = source[:2]
        name = source[:-3]
        selected = name in deterministic
        reason = reasons.get(name)
        if not selected and not reason:
            raise ValueError(f"{source}: missing exact deterministic exclusion reason")
        entries.append({
            "id": identifier, "source": source, "live": identifier in live,
            "deterministic": selected, "liveReason": reason,
            "expectedOutput": f"examples/expected/{name}.txt" if identifier in REFERENCE_IDS else None,
            "languages": {language: rows[language][identifier] for language in ("ts", "py")},
        })
    return {
        "schemaVersion": 1, "scope": [0, 35],
        "reference": "scripts/validate.sh",
        "examples": entries,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Regenerate after a range handoff")
    options = parser.parse_args()
    manifest = build()
    file = ROOT / "examples" / "parity.json"
    if options.write:
        file.write_text(json.dumps(manifest, indent=2) + "\n")
    elif json.loads(file.read_text()) != manifest:
        raise SystemExit("Global parity manifest differs from current range/source declarations")
    print("Global parity manifest: 36 examples x 2 languages")
