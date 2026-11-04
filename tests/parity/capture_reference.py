"""Capture or recheck actual C# stdout, not hand-authored expected fixtures.

Build ReplayRuntime first: C# uses its deployed basic.json, which must match the
source fixture byte-for-byte before any example runs or reference is written.
"""

import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "examples"))
from parity_manifest import REFERENCE_IDS
from run_all import normalise_output


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def reference_fixture_sha256(runtime):
    if not runtime.is_file() or not os.access(runtime, os.X_OK):
        raise RuntimeError(f"Build an executable ReplayRuntime first: {runtime}")
    source = ROOT / "tools/ReplayRuntime/fixtures/basic.json"
    deployed = runtime.parent / "fixtures/basic.json"
    source_hash = sha256(source)
    deployed_hash = sha256(deployed)
    if deployed_hash != source_hash:
        raise RuntimeError(
            "Deployed ReplayRuntime fixture differs from source; rebuild ReplayRuntime before capture:\n"
            f"source: {source} (sha256 {source_hash})\n"
            f"deployed: {deployed} (sha256 {deployed_hash})"
        )
    return deployed_hash


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Capture checked-in output from actual C# execution")
    args = parser.parse_args()
    runtime = (ROOT / "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime").resolve(strict=True)
    fixture_hash = reference_fixture_sha256(runtime)
    env = {**os.environ, "DURABLE_RUNTIME_PATH": str(runtime)}
    expected_dir = ROOT / "examples/expected"
    provenance = {
        "schemaVersion": 1,
        "scenario": "actual C# execution with ReplayRuntime basic.json; generated sessions are synthesised, not recorded transcripts",
        "normalizer": "examples/run_all.py:normalise_output (mirrors scripts/validate.sh plus generated c/e/t IDs and platform paths)",
        "fixture": "tools/ReplayRuntime/fixtures/basic.json",
        "fixtureSha256": fixture_hash,
        "outputs": {},
    }
    if args.write:
        expected_dir.mkdir(exist_ok=True)
    for identifier in REFERENCE_IDS:
        source, = (ROOT / "examples").glob(f"{identifier}-*.cs")
        # NuGet restore advisories (e.g. NU1903) are build noise, not example stdout: dotnet run
        # writes them to stdout, so the clean-output guard below would reject them. Suppress
        # them in the invocation rather than weakening the guard. This flag is scoped to this
        # single MSBuild invocation: the required build job and validate.sh's example runs keep
        # NuGetAudit at its default true, so the package graph is still audited there.
        command = ["dotnet", "run", str(source.relative_to(ROOT)), "--verbosity", "quiet",
                   "-p:NoWarn=GHCP001%3BCA2266", "-p:NuGetAudit=false"]
        result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True, timeout=180)
        if result.returncode:
            raise RuntimeError(f"{source.name} failed:\n{result.stdout}\n{result.stderr}")
        if result.stderr:
            sys.stderr.write(result.stderr)
        output = normalise_output(result.stdout)
        if not output or ": warning " in output or ": error " in output:
            raise RuntimeError(f"Reference capture is not clean example stdout: {source.name}\n{output}")
        expected = expected_dir / source.with_suffix(".txt").name
        if args.write:
            expected.write_text(output)
        elif expected.read_text() != output:
            raise RuntimeError(f"Actual C# output differs from {expected}\n{output}")
        provenance["outputs"][identifier] = {
            "command": command, "source": str(source.relative_to(ROOT)),
            "sourceSha256": sha256(source), "expectedSha256": sha256(expected),
        }
        print(f"Actual C# output verified: {source.name}")
    file = expected_dir / "provenance.json"
    if args.write:
        file.write_text(json.dumps(provenance, indent=2) + "\n")
    elif json.loads(file.read_text()) != provenance:
        raise RuntimeError("Reference provenance changed; recapture explicitly")


if __name__ == "__main__":
    main()
