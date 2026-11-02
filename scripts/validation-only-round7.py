"""Isolated manual validation, not a PR-head Build run or source acceptance."""
import argparse
import ast
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import time
import unittest
import xml.etree.ElementTree as ET

CANDIDATES = {
    119: ("85ad85b53c5372234ef590309b6dc0c43d793bfe", "3cd75c9e43f66e0ced61e1ab5c7d2d05893eeef2"),
    122: ("9eaf2f7c5bbeebe38e1e4ac6edae631e021b678e", "f67100358b3fab899799fad34b727c8b3ea26c08"),
    123: ("b178c6197260cdf12ef999f782e9edab1d0aabc4", "0ed19931ba00d9fdb2f9b4608c9cf9a00778f350"),
    125: ("9ae5a6ef175ab3a9590eb910291152bdb7e5cad6", "bb3d4ec6238193754fe50c97a9f1d4e8f156b5e5"),
    126: ("dfdb8f346a2e0e224d75ccd19ebda17d4915afff", "c4f63b22e9578ae88dd4a68a345e075c54b06dfd"),
}
PATCH_IDS = ["44c48124bcbd9ad70c717d98f40f307e44ea806a",
             "fad801f911dbf684818f2ccba0b52b57e8dc5165",
             "bf1226a23d7fc8e414c96b5cc76e7f9b51c19a89",
             "b0def37b4bb8d5a06a6e3be0ae6d659e6e54a80b",
             "d2e9d7c04d508263a21c4bdd71f26235c48d9c68"]
SDK_PATHS = ["lib/py/durable.py", "lib/py/test_sdk.py", "lib/ts/durable.ts", "lib/ts/durable.test.ts"]
CRLF_PATHS = ["examples/py/parity_test_11_21.py", "tests/Durable.Tests/LateJoinExampleTests.cs"]
APPROVED_BLOBS = {
    "lib/py/durable.py": "4379e4f1b37ba2c8d140cc4d557d512f54f552fd",
    "lib/py/test_sdk.py": "867421286eeb33aa085d6d60512e5d767b19b959",
    "lib/ts/durable.test.ts": "1eab2404da9f2a8f74d6747b30b11cd08d94f8ac",
    "lib/ts/durable.ts": "b3e9ba31b9f0d55410165b147ec2ebcb9a045724",
    "examples/py/parity_test_11_21.py": "3a677ad67b85c5f405a2ede54a964acf3753cc06",
    "tests/Durable.Tests/LateJoinExampleTests.cs": "5b8d7abe9cf32216c00dc5dfce177f3d62e1a1f2",
    ".github/workflows/build.yml": "3722e49f3322a685707ac60e1dda52d3395e7dc3",
}
CONTROL_PATHS = [".github/workflows/build.yml", "scripts/validation-only-round7.py"]
TRAILER = "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
GIB = 1024 ** 3
MIB = 1024 ** 2
ENV_KEYS = ["PATH", "PYTHONPATH", "PYTHONDONTWRITEBYTECODE", "VIRTUAL_ENV",
            "DURABLE_RUNTIME_PATH", "CopilotSkipCliDownload", "TMPDIR", "TSX_DISABLE_CACHE",
            "NODE_OPTIONS", "ROUND7_NODE_AUDIT", "ROUND7_PY_AUDIT", "ROUND7_GATE",
            "GITHUB_SHA", "GITHUB_REF", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "RUNNER_OS", "RUNNER_ARCH", "GOBIN"]


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n")


def inventory(output):
    values = {str(p.relative_to(output)): {"bytes": p.stat().st_size, "sha256": sha(p)}
              for p in sorted(output.rglob("*")) if p.is_file() and p.name != "sha256.json"}
    write_json(output / "sha256.json", values)


def node_counts(text, expected):
    values = {}
    for key in ["tests", "pass", "fail", "cancelled", "skipped", "todo"]:
        matches = re.findall(rf"^# {key} (\d+)$", text, re.M)
        require(matches, f"Missing Node {key} counter")
        values[key] = int(matches[-1])
    require(values == dict(tests=expected, **{"pass": expected}, fail=0, cancelled=0, skipped=0, todo=0),
            f"Node inventory/outcomes mismatch: {values}, expected {expected}")
    return values


def python_counts(text, expected, red=False):
    totals = re.findall(r"^Ran (\d+) tests? in ", text, re.M)
    require(totals and int(totals[-1]) == expected, "Python test inventory mismatch")
    require(not re.search(r"\bskipped[= ]|^.*\.\.\. skipped ", text, re.M), "Unexpected Python skip")
    if red:
        require("FAILED (failures=1)" in text and "AssertionError" in text, "Wrong Python mutation failure")
    else:
        require(re.search(r"^OK$", text, re.M) is not None, "Python suite not green")
    return {"tests": expected, "failed": 1 if red else 0, "skipped": 0}


def trx_counts(path, expected=None, red=False):
    document = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = document.findall(".//t:UnitTestResult", ns)
    counts = document.find(".//t:ResultSummary/t:Counters", ns)
    require(counts is not None and results, "Missing/zero-test TRX")
    total = int(counts.attrib["total"])
    require(total == len(results) and (expected is None or total == expected), "TRX inventory mismatch")
    require(int(counts.attrib["executed"]) == total
            and int(counts.attrib["passed"]) == (0 if red else total)
            and int(counts.attrib["failed"]) == (total if red else 0), "TRX counter mismatch")
    wanted = "Failed" if red else "Passed"
    require(all(r.attrib["outcome"] == wanted for r in results), "Unexpected TRX failed/skipped/nonexecuted test")
    return {"tests": total, "passed": 0 if red else total, "failed": total if red else 0,
            "skipped": 0, "names": [r.attrib["testName"] for r in results]}


class Runner:
    def __init__(self, args):
        self.control, self.pr, self.output = args.control.resolve(), args.pr, args.output.resolve()
        self.old, self.head = CANDIDATES[self.pr]
        require(args.candidate == self.head, "Candidate argument not authorized exact SHA")
        self.tree = self.output.parent / f"round7-candidate-pr{self.pr}"
        require(not self.tree.exists(), "Refusing to reuse a candidate worktree")
        self.records = []
        self.env = dict(os.environ)
        for key in ["DURABLE_RUNTIME_PATH", "CopilotSkipCliDownload", "NODE_OPTIONS", "PYTHONPATH",
                    "DURABLE_EXAMPLE_FIXTURE", "COPILOT_CLI_PATH"]:
            self.env.pop(key, None)
        self.env.update(PYTHONDONTWRITEBYTECODE="1", TSX_DISABLE_CACHE="1",
                        TMPDIR=str(self.output.parent / f"round7-tmp-pr{self.pr}"))
        Path(self.env["TMPDIR"]).mkdir()
        self.output.mkdir(exist_ok=True)
        self.py = self.tree / "lib/py/.venv/bin/python"
        self.runtime = self.tree / "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"

    def git(self, *args, cwd=None, data=None):
        return subprocess.check_output(["git", *args], cwd=cwd or self.control, input=data).decode().strip()

    def save(self):
        write_json(self.output / "commands.json", self.records)

    def run(self, label, command, *, env=None, cwd=None, floor=GIB, timeout=1800, allow_failure=False):
        require(not any(r["label"] == label for r in self.records), "Refusing to overwrite command evidence")
        before = shutil.disk_usage(self.output).free
        require(before >= floor, f"SPACE BLOCKER {label}: {before} free, requires {floor}")
        environment = dict(self.env if env is None else env)
        environment["ROUND7_GATE"] = label
        record = {"label": label, "command": [str(x) for x in command], "cwd": str(cwd or self.tree),
                  "environment": {k: environment.get(k) for k in ENV_KEYS},
                  "started": time.time(), "free_before": before, "minimum_free": before,
                  "required_start_free": floor, "reserve": 128 * MIB, "stop_threshold": 384 * MIB,
                  "timeout_seconds": timeout, "timed_out": False, "space_stopped": False}
        out, err = self.output / f"{label}.stdout", self.output / f"{label}.stderr"
        with out.open("xb") as stdout, err.open("xb") as stderr:
            try:
                process = subprocess.Popen(record["command"], cwd=cwd or self.tree, env=environment,
                                           stdout=stdout, stderr=stderr, start_new_session=True)
            except FileNotFoundError as error:
                stderr.write(str(error).encode())
                record["status"] = 127
            else:
                record["pid"] = process.pid
                while process.poll() is None:
                    available = shutil.disk_usage(self.output).free
                    record["minimum_free"] = min(record["minimum_free"], available)
                    record["space_stopped"] = available < 384 * MIB
                    record["timed_out"] = time.time() - record["started"] > timeout
                    if record["space_stopped"] or record["timed_out"]:
                        os.killpg(process.pid, signal.SIGTERM)
                        break
                    time.sleep(0.05)
                try:
                    record["status"] = process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    record["status"] = process.wait()
        record.update(finished=time.time(), free_after=shutil.disk_usage(self.output).free,
                      stdout=out.name, stderr=err.name, stdout_sha256=sha(out), stderr_sha256=sha(err))
        self.records.append(record)
        self.save()
        require(not record["space_stopped"] and not record["timed_out"], f"Interrupted {label}; raw partial retained")
        require(allow_failure or record["status"] == 0, f"Command failed: {label}, exit {record['status']}")
        print(f"{label}: exit {record['status']}", flush=True)
        return record, out.read_text(errors="replace") + "\n" + err.read_text(errors="replace")

    def missing(self, label, command, pattern, install, *, env=None, cwd=None, floor=GIB):
        record, text = self.run(label, command, env=env, cwd=cwd, floor=floor, allow_failure=True)
        if record["status"]:
            require(re.search(pattern, text) is not None, f"Not a missing dependency: {label}")
            record["classification"] = "EXPECTED_MISSING_DEPENDENCY_NOT_GATE_GREEN"
            self.save()
            self.run(label + "-provision", install, env=env, cwd=cwd, floor=floor)

    def result(self, label, counts):
        self.records[-1]["asserted_counts"] = counts
        require(self.records[-1]["label"] == label, "Count attached to wrong command")
        self.save()

    def dotnet(self, label, selector=None, expected=None, full=False):
        result = self.output / label
        result.mkdir()
        command = ["dotnet", "test", "tests/Durable.Tests", "--nologo", "-v", "normal",
                   "--logger", "trx;LogFileName=results.trx", "--results-directory", str(result)]
        if selector:
            command += ["--filter", f"FullyQualifiedName~{selector}"]
        record, text = self.run(label, command, env=self.env if full else self.synthetic(),
                                floor=8 * GIB if full else GIB, timeout=2700)
        counts = trx_counts(result / "results.trx", expected)
        self.result(label, counts)
        return counts

    def synthetic(self):
        return dict(self.env, DURABLE_RUNTIME_PATH=str(self.runtime), CopilotSkipCliDownload="true")

    def python(self, label, arguments, expected, *, cwd=None, env=None):
        _, text = self.run(label, [self.py, *arguments], cwd=cwd, env=env or self.synthetic())
        self.result(label, python_counts(text, expected))

    def verify_sources(self):
        control = self.git("rev-parse", "HEAD")
        require(control == os.environ["GITHUB_SHA"], "Actions/control SHA mismatch")
        require(os.environ["GITHUB_EVENT_NAME"] == "workflow_dispatch"
                and os.environ["GITHUB_REF"].startswith("refs/heads/validation-only/"),
                "Not an isolated manual validation")
        parents = self.git("show", "-s", "--format=%P", control).split()
        require(parents == [pair[1] for pair in CANDIDATES.values()], "Wrong transport parents/order")
        changes = self.git("diff", "--name-only", CANDIDATES[126][1], control).splitlines()
        require(sorted(changes) == sorted(CONTROL_PATHS), "Wrong control-tree scope")
        self.git("worktree", "add", "--detach", str(self.tree), self.head)
        require(self.git("rev-parse", "HEAD", cwd=self.tree) == self.head, "Wrong detached candidate HEAD")
        changed = self.git("diff", "--name-only", self.old, self.head).splitlines()
        expected = SDK_PATHS + (CRLF_PATHS + [".github/workflows/build.yml", "README.md"]
                                if self.pr in (122, 126) else [])
        require(sorted(changed) == sorted(expected), "Wrong candidate scope")
        chain = self.git("rev-list", "--reverse", f"{self.old}..{self.head}").splitlines()
        require(len(chain) == (5 if self.pr in (122, 126) else 3), "Wrong own chain length")
        parent = self.old
        for commit, patch in zip(chain, PATCH_IDS):
            require(self.git("show", "-s", "--format=%P", commit) == parent, "Nonlinear candidate chain")
            require(TRAILER in self.git("show", "-s", "--format=%B", commit), "Missing coauthor trailer")
            diff = subprocess.check_output(["git", "show", "--format=", "--binary", commit], cwd=self.control)
            actual = self.git("patch-id", "--stable", data=diff).split()[0]
            require(actual == patch, "Wrong approved own patch")
            parent = commit
        # Pin entries rather than requiring unpublished original source objects to be
        # remotely fetchable. Transport ancestry carries candidates, not source branches.
        for path in SDK_PATHS + (CRLF_PATHS + [".github/workflows/build.yml"] if self.pr in (122, 126) else []):
            require(self.git("ls-tree", self.head, "--", path) == f"100644 blob {APPROVED_BLOBS[path]}\t{path}",
                    f"Final entry/mode mismatch: {path}")
        for path in ["lib/py/test_sdk.py", "lib/ts/durable.test.ts"] + ([CRLF_PATHS[1]] if self.pr in (122, 126) else []):
            require(self.git("diff", "--numstat", self.old, self.head, "--", path).split()[1] == "0",
                    f"Original test bytes removed: {path}")
        for path in ["lib/py/test_sdk.py"] + ([CRLF_PATHS[0]] if self.pr in (122, 126) else []):
            methods = []
            for commit in [self.old, self.head]:
                document = ast.parse(self.git("show", f"{commit}:{path}"))
                methods.append({f"{cls.name}.{method.name}": ast.dump(method, include_attributes=False)
                    for cls in document.body if isinstance(cls, ast.ClassDef)
                    for method in cls.body if isinstance(method, (ast.FunctionDef, ast.AsyncFunctionDef))
                    and method.name.startswith("test_")})
            require(all(methods[1].get(key) == value for key, value in methods[0].items()),
                    f"Original test AST changed: {path}")
        self.protected = self.git("ls-tree", "-r", self.head).splitlines()
        write_json(self.output / "source-provenance.json", {
            "control_sha": control, "candidate_sha": self.head, "old_published_sha": self.old,
            "transport_parents": parents, "candidate_tree": self.git("rev-parse", f"{self.head}^{{tree}}"),
            "changed_paths": changed, "tracked_entries": self.protected,
            "own_chain": chain, "runner_sha256": sha(Path(__file__)),
            "limits": "Isolated candidate validation, NOT published-head Build attempt1 or native/model certification"})

    def audits(self):
        audit = self.output / "audit"
        audit.mkdir()
        (audit / "load.mjs").write_text("""
import {appendFileSync,readFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
export async function load(url,context,next) {
 const result=await next(url,context);
 if(url.includes('/@github/copilot-sdk/') || /\\/lib\\/ts\\/durable\\.ts(?:\\?|$)/.test(url)) {
  const file=new URL(url); file.search='';
  appendFileSync(process.env.ROUND7_NODE_AUDIT,JSON.stringify({pid:process.pid,ppid:process.ppid,
   gate:process.env.ROUND7_GATE,url,format:result.format,
   sha256:createHash('sha256').update(readFileSync(file)).digest('hex')})+'\\n');
 }
 return result;
}
""")
        (audit / "init.mjs").write_text("""
import {register} from 'node:module';
import Module from 'node:module';
import {appendFileSync,readFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
register(new URL('./load.mjs',import.meta.url));
const original=Module._load;
Module._load=function(request,parent,isMain) {
 const result=original.apply(this,arguments);
 if(String(request).includes('copilot-sdk')) {
  const file=Module._resolveFilename(request,parent,isMain);
  appendFileSync(process.env.ROUND7_NODE_AUDIT,JSON.stringify({pid:process.pid,ppid:process.ppid,
   gate:process.env.ROUND7_GATE,file,format:'commonjs',
   sha256:createHash('sha256').update(readFileSync(file)).digest('hex')})+'\\n');
 }
 return result;
};
""")
        (audit / "sitecustomize.py").write_text("""
import atexit,json,os,sys,importlib.metadata
def record():
 durable=sys.modules.get('durable'); copilot=sys.modules.get('copilot')
 try: version=importlib.metadata.version('github-copilot-sdk')
 except importlib.metadata.PackageNotFoundError: version=None
 with open(os.environ['ROUND7_PY_AUDIT'],'a') as f:
  f.write(json.dumps({'pid':os.getpid(),'ppid':os.getppid(),'gate':os.environ.get('ROUND7_GATE'),
   'python':sys.executable,'version':sys.version,
   'durable':getattr(durable,'__file__',None),'copilot':getattr(copilot,'__file__',None),
   'sdk_version':version})+'\\n')
atexit.register(record)
""")
        self.env.update(NODE_OPTIONS=f"--import={audit / 'init.mjs'}",
                        ROUND7_NODE_AUDIT=str(self.output / "actual-node-loads.jsonl"),
                        ROUND7_PY_AUDIT=str(self.output / "actual-python-loads.jsonl"),
                        PYTHONPATH=os.pathsep.join([str(audit), str(self.tree / "lib/py")]),
                        PATH=str(self.py.parent) + os.pathsep + self.env["PATH"],
                        VIRTUAL_ENV=str(self.py.parent.parent))

    def bootstrap(self):
        self.run("versions", [sys.executable, "-c",
            "import sys; assert sys.version_info[:2]==(3,13); print(sys.version)"])
        self.run("node-version", ["node", "-e", "if(process.versions.node.split('.')[0]!=='22')process.exit(1);console.log(process.version)"])
        self.run("dotnet-info", ["dotnet", "--info"])
        self.missing("runtime-no-assets", ["dotnet", "build", "tools/ReplayRuntime/ReplayRuntime.csproj", "--no-restore", "-v:q"],
                     r"NETSDK1004", ["dotnet", "restore", "tools/ReplayRuntime/ReplayRuntime.csproj"])
        self.run("runtime-build", ["dotnet", "build", "tools/ReplayRuntime/ReplayRuntime.csproj", "--no-restore", "-v:q"])
        require(self.runtime.is_file(), "Fresh Linux managed ReplayRuntime not built")
        write_json(self.output / "runtime-provenance.json", {
            "sources": {p: self.git("ls-tree", self.head, "--", p) for p in
                        self.git("ls-files", "tools/ReplayRuntime", "Directory.Build.props", "Directory.Build.targets", "global.json", cwd=self.tree).splitlines()},
            "fresh_files": {p.name: sha(p) for p in self.runtime.parent.iterdir() if p.is_file()}})
        self.missing("missing-python-venv", [self.py, "-c", "import sys;print(sys.executable)"], r"No such file",
                     [sys.executable, "-m", "venv", str(self.py.parent.parent)])
        self.missing("missing-python-sdk", [self.py, "-c", "import copilot"], r"No module named 'copilot'",
                     [self.py, "-m", "pip", "install", "-e", "lib/py"])
        self.run("python-source-import", [self.py, "-c",
            f"import durable,copilot,sys,pathlib,importlib.metadata;assert pathlib.Path(durable.__file__).resolve()==pathlib.Path({str(self.tree / 'lib/py/durable.py')!r});"
            "assert importlib.metadata.version('github-copilot-sdk')=='1.0.28';print(sys.executable,durable.__file__,copilot.__file__)"])
        self.missing("missing-tsx", ["node", str(self.tree / "lib/ts/node_modules/tsx/dist/cli.mjs"), "--version"],
                     r"MODULE_NOT_FOUND|Cannot find module", ["npm", "ci", "--prefix", "lib/ts", "--no-audit", "--no-fund"])
        self.missing("missing-fs-ext", ["node", "-e", "require('fs-ext')"],
                     r"MODULE_NOT_FOUND|Cannot find module|bindings", ["npm", "rebuild", "fs-ext"],
                     cwd=self.tree / "lib/ts")
        self.missing("missing-pyright", [self.py, "-m", "pyright", "--version"], r"No module named pyright",
                     [self.py, "-m", "pip", "install", "pyright"])
        if (self.tree / "examples/ts/package.json").is_file():
            self.missing("missing-example-types", ["npm", "--prefix", "examples/ts", "run", "typecheck"],
                         r"Cannot find|not found|TS2688|TS2307", ["npm", "ci", "--prefix", "examples/ts", "--no-audit", "--no-fund"])
        self.run("pip-check", [self.py, "-m", "pip", "check"])
        self.run("npm-versions", ["npm", "ls", "--prefix", "lib/ts", "--depth=0"])
        self.run("node-sdk-pin", ["node", "-e",
            "const p=require('./lib/ts/node_modules/@github/copilot-sdk/package.json');"
            "if(p.version!=='1.0.28')process.exit(1);console.log(p.version)"])
        if self.pr in (122, 126):
            tools = self.output.parent / f"round7-tools-pr{self.pr}"
            tools.mkdir()
            toolenv = dict(self.env, GOBIN=str(tools))
            self.missing("missing-actionlint", [tools / "actionlint", "-version"], r"No such file",
                         ["go", "install", "github.com/rhysd/actionlint/cmd/actionlint@v1.7.12"], env=toolenv)
            self.run("candidate-actionlint", [tools / "actionlint", ".github/workflows/build.yml"], env=toolenv)
        self.missing("test-assets", ["dotnet", "build", "tests/Durable.Tests", "--no-restore", "-v:q"],
                     r"NETSDK1004", ["dotnet", "restore", "tests/Durable.Tests"], floor=8 * GIB)

    def full_sdk(self):
        tests = sorted((self.tree / "lib/ts").glob("*.test.ts"))
        expected = 119 + (4 if (self.tree / "lib/ts/parity.test.ts").is_file() else 0)
        _, text = self.run("sdk-ts", ["node", "--import", str(self.tree / "lib/ts/node_modules/tsx/dist/loader.mjs"),
                                     "--test", "--test-concurrency=1", *tests], cwd=self.tree / "lib/ts", env=self.synthetic())
        self.result("sdk-ts", node_counts(text, expected))
        self.python("sdk-python", ["-m", "unittest", "discover", "-s", "lib/py", "-p", "test_sdk.py", "-v"], 72)
        self.run("sdk-ts-types", ["npm", "--prefix", "lib/ts", "run", "typecheck"], env=self.synthetic())
        self.run("sdk-python-types", [self.py, "-m", "pyright"], cwd=self.tree / "lib/py")
        selector = "DurableStoreTests.JsonlBackend|FullyQualifiedName~TaskResumeTests"
        self.dotnet("canonical", selector, 9)

    def owning(self):
        if (self.tree / "examples/ts/package.json").is_file():
            self.run("examples-ts-types", ["npm", "--prefix", "examples/ts", "run", "typecheck"])
        elif self.pr == 122:
            files = sorted((self.tree / "examples/ts").glob("1*.ts")) + sorted((self.tree / "examples/ts").glob("2[01]-*.ts"))
            self.run("examples-ts-types", [self.tree / "lib/ts/node_modules/.bin/tsc", "--noEmit", "--target", "ES2022",
                "--module", "ESNext", "--moduleResolution", "Bundler", "--allowImportingTsExtensions", "--strict",
                "--skipLibCheck", "--types", "node", *files], cwd=self.tree / "lib/ts")
            files = sorted((self.tree / "examples/py").glob("1*.py")) + sorted((self.tree / "examples/py").glob("2[01]-*.py"))
            self.run("examples-python-types", [self.py, "-m", "pyright", *files], cwd=self.tree / "lib/py")
        if self.pr in (119, 126):
            self.python("range00-10", ["examples/py/test_parity_00_10.py", "-v"], 11)
        if (self.tree / "examples/py/parity_test_11_21.py").is_file():
            self.python("range11-21", ["examples/py/parity_test_11_21.py", "-v"], 18 if self.pr in (122, 126) else 17)
        if self.pr in (122, 126):
            self.dotnet("original-wire-lf-crlf", "LateJoinExampleTests.ActualExample_BarrierReadinessFailuresCleanupAndSourceMutations", 2)
            self.dotnet("physical-line-endings-fact", "LateJoinExampleTests.PortHarness_PhysicalLineEndingsPreserveGeneratedClients", 1)
            self.run("default-discovery", ["dotnet", "test", "tests/Durable.Tests", "--list-tests", "--nologo", "-v:q"],
                     env=self.env, floor=8 * GIB, timeout=2700)
            discovery = (self.output / "default-discovery.stdout").read_text()
            marker = "The following Tests are available:"
            require(marker in discovery, "No actual default .NET discovery inventory")
            available = [line.strip() for line in discovery.split(marker, 1)[1].splitlines()
                         if line.strip().startswith("Durable.Tests.")]
            require(available, "Zero discovered .NET tests")
            counts = self.dotnet("default-full-durable", full=True, expected=len(available))
            write_json(self.output / "default-test-inventory.json", {"discovered": available, "trx": counts})
            deployment = self.tree / "tests/Durable.Tests/bin/Debug/net10.0"
            write_json(self.output / "fresh-test-deployment.json",
                       {str(path.relative_to(deployment)): {"sha256": sha(path), "bytes": path.stat().st_size}
                        for path in sorted(deployment.rglob("*")) if path.is_file()
                        and path.suffix in [".dll", ".json", ".so", ".node"]})
            self.crlf_mutation()
        if self.pr in (119, 122):
            fixture = "setup" if self.pr == 119 else "coding-tools"
            _, text = self.run("offline-node", ["node", "--import", str(self.tree / "lib/ts/node_modules/tsx/dist/loader.mjs"),
                                               "--test", f"tests/parity/{fixture}.test.mjs"], env=self.synthetic())
            self.result("offline-node", node_counts(text, 2))
        if self.pr in (125, 126):
            if self.pr == 126:
                command = ["npm", "run", "test:parity:ts"]
            else:
                command = ["node", "--import", str(self.tree / "lib/ts/node_modules/tsx/dist/loader.mjs"),
                           "--test", *sorted((self.tree / "tests/parity").glob("*.test.mjs"))]
            _, text = self.run("offline-node", command, env=self.synthetic())
            self.result("offline-node", node_counts(text, 40 if self.pr == 126 else 32))
            self.python("offline-python", ["-m", "unittest", "discover", "-s", "tests/parity", "-p", "test_*.py", "-v"],
                        30 if self.pr == 126 else 6)
        if self.pr == 126:
            self.aggregate()

    def crlf_mutation(self):
        copy = self.output.parent / f"round7-crlf-disposable-pr{self.pr}"
        self.git("worktree", "add", "--detach", str(copy), self.head)
        binary = Path("tests/Durable.Tests/bin/Debug/net10.0")
        shutil.copytree(self.tree / binary, copy / binary)
        assembly = copy / binary / "Durable.Tests.dll"
        require(sha(assembly) == sha(self.tree / binary / "Durable.Tests.dll"), "Fresh assembly copy mismatch")
        bare = self.output.parent / f"round7-unprovisioned-python-pr{self.pr}"
        self.run("negative-venv", [sys.executable, "-m", "venv", str(bare)])
        negative = dict(self.env, PATH=str(bare / "bin") + os.pathsep + self.env["PATH"],
                        PYTHONPATH=os.pathsep.join([str(self.output / "audit"), str(copy / "lib/py")]),
                        VIRTUAL_ENV=str(bare))
        directory = self.output / "unprovisioned-fact"
        directory.mkdir()
        record, text = self.run("unprovisioned-fact", ["dotnet", "vstest", assembly,
            "/TestCaseFilter:FullyQualifiedName~PortHarness_PhysicalLineEndingsPreserveGeneratedClients",
            "/Logger:trx;LogFileName=results.trx", f"/ResultsDirectory:{directory}"],
            cwd=copy, env=negative, allow_failure=True)
        require(record["status"] != 0 and re.search(r"No module named ['\"]copilot['\"]", text)
                and re.search(r"(?:Cannot find module|ERR_MODULE_NOT_FOUND)[^\n]*(?:typescript|/node_modules/)", text),
                "Unprovisioned Fact did not fail for both missing Python/TypeScript prerequisites")
        record["classification"] = "EXPECTED_UNPROVISIONED_FAILURE_NOT_PRODUCT_FAILURE"
        self.result("unprovisioned-fact", trx_counts(directory / "results.trx", 1, red=True))
        for relative in ["lib/ts/node_modules", "examples/ts/node_modules"]:
            source = self.tree / relative
            if source.is_dir():
                (copy / relative).symlink_to(source, target_is_directory=True)
        environment = dict(self.synthetic(), PYTHONPATH=os.pathsep.join([str(self.output / "audit"), str(copy / "lib/py")]))
        helper = copy / "examples/py/parity_test_11_21.py"
        before, mode = helper.read_bytes(), stat.S_IMODE(helper.stat().st_mode)
        needle = rb".replace(/\\r\\n?/g, '\\n')"
        require(before.count(needle) == 1, "Normalization-only mutation target changed")
        arguments = [self.py, "-m", "unittest",
            "examples.py.parity_test_11_21.LateJoinParityTests.test_physical_line_endings_preserve_generated_clients", "-v"]
        try:
            helper.write_bytes(before.replace(needle, b"", 1))
            record, text = self.run("normalization-only-mutant", arguments, cwd=copy, env=environment, allow_failure=True)
            require(record["status"] != 0 and "generated client must contain the shortened overall deadline" in text
                    and "90000" in text and "language='ts'" in text and "TimeoutExpired" not in text,
                    "Mutation did not fail at generated-client assertion")
            record["classification"] = "EXPECTED_REAL_SOURCE_MUTANT_ASSERTION_FAILURE"
            self.result("normalization-only-mutant", python_counts(text, 1, red=True))
        finally:
            helper.write_bytes(before)
            helper.chmod(mode)
            require(helper.read_bytes() == before and stat.S_IMODE(helper.stat().st_mode) == mode, "Mutation restore failed")
            write_json(self.output / "mutation-restore.json", {
                "copy": str(copy), "source_sha": self.head, "helper_sha256": sha(helper), "mode": mode,
                "restored_exact_bytes_mode": True, "assembly_sha256": sha(assembly),
                "protected_candidate_unchanged": not self.git("diff", "--name-only", cwd=self.tree)})
        self.python("restored-physical", arguments[1:], 1, cwd=copy, env=environment)
        require(not self.git("diff", "--name-only", cwd=copy), "Disposable source copy not restored")

    def aggregate(self):
        self.run("manifest-help", [self.py, "examples/parity_manifest.py", "--help"], env=self.synthetic())
        before = {p: self.git("hash-object", p, cwd=self.tree) for p in self.git("ls-files", cwd=self.tree).splitlines()}
        status = self.git("status", "--porcelain", cwd=self.tree)
        self.run("manifest-write", [self.py, "examples/parity_manifest.py", "--write"], env=self.synthetic())
        require(before == {p: self.git("hash-object", p, cwd=self.tree) for p in before},
                "Manifest regeneration not byte-identical")
        require(status == self.git("status", "--porcelain", cwd=self.tree), "Manifest created unexpected paths/modes")
        write_json(self.output / "manifest-byte-identity.json", before)
        for language, command in [("TypeScript", ["npm", "run", "examples:ts", "--", "--deterministic"]),
                                  ("Python", [self.py, "examples/run_all.py", "--deterministic"])]:
            _, text = self.run("deterministic-" + language, command, env=self.synthetic(), timeout=3600)
            require(f"{language}: 15 selected, 0 failures, 4 actual C# output comparisons" in text,
                    "Missing exact deterministic inventory/fresh C# comparisons")
            self.result("deterministic-" + language, {"selected": 15, "failures": 0, "actual_csharp_comparisons": 4})
        _, text = self.run("reference-capture", [self.py, "tests/parity/capture_reference.py"], env=self.synthetic())
        require(re.findall(r"Actual C# output verified: (\S+)", text) ==
                ["00-conversation.cs", "01-documents.cs", "03-owned-conversations.cs", "04-watches.cs"],
                "Missing actual C# reference capture inventory")
        for language, command in [("ts", ["node", "examples/run_all.mjs", "--list"]),
                                  ("python", [self.py, "examples/run_all.py", "--list"])]:
            _, text = self.run("manifest-list-" + language, command, env=self.synthetic())
            require(re.findall(r"^SELECT (\d\d) ", text, re.M) == [f"{i:02}" for i in range(36)],
                    "Incorrect full manifest list inventory")
        _, text = self.run("manifest-check", [self.py, "examples/parity_manifest.py"], env=self.synthetic())
        require("Global parity manifest: 36 examples x 2 languages" in text, "Wrong final manifest inventory")

    def execute(self):
        self.verify_sources()
        self.audits()
        self.bootstrap()
        self.full_sdk()
        self.owning()
        require(not self.git("diff", "--name-only", cwd=self.tree), "Tracked candidate source changed")
        self.run("final-diff-check", ["git", "diff", "--check"])
        nodes = [json.loads(line) for line in (self.output / "actual-node-loads.jsonl").read_text().splitlines()]
        pythons = [json.loads(line) for line in (self.output / "actual-python-loads.jsonl").read_text().splitlines()]
        require(any(row.get("gate") == "sdk-ts" and str(self.tree / "lib/ts/durable.ts") in row.get("url", "")
                    and row["sha256"] == sha(self.tree / "lib/ts/durable.ts") for row in nodes),
                "No actual SDK suite current candidate TS load")
        require(any(row.get("gate") == "sdk-ts"
                    and "/@github/copilot-sdk/" in row.get("url", row.get("file", "")) for row in nodes),
                "No actual SDK suite official module load")
        require(any(row["durable"] == str(self.tree / "lib/py/durable.py") and row["sdk_version"] == "1.0.28"
                    and row.get("gate") == "sdk-python" for row in pythons), "No actual suite candidate Python SDK load")
        if self.pr in (122, 126):
            require(any(row.get("gate") == "default-full-durable" and row["version"].startswith("3.13.")
                        and row["durable"] == str(self.tree / "lib/py/durable.py") for row in pythons),
                    "Default full .NET cross-language child import not proven")
        write_json(self.output / "result.json", {
            "state": "ISOLATED_CANDIDATE_GATES_PASSED_NOT_PR_HEAD_CI_NOT_ACCEPTED",
            "candidate": self.head, "control": self.git("rev-parse", "HEAD"), "pr": self.pr,
            "commands": len(self.records), "tracked_candidate_unchanged": True,
            "limits": "No native/LIVE/model/sandbox/human/platform/domain acceptance or publication authorization"})


class OrchestrationTests(unittest.TestCase):
    def test_node_counts_fail_closed(self):
        text = "# tests 119\n# pass 119\n# fail 0\n# cancelled 0\n# skipped 0\n# todo 0\n"
        self.assertEqual(node_counts(text, 119)["tests"], 119)
        for bad in [text.replace("# tests 119", "# tests 0"), text.replace("# skipped 0", "# skipped 1")]:
            with self.assertRaises(RuntimeError):
                node_counts(bad, 119)

    def test_python_inventory_and_red_cause(self):
        self.assertEqual(python_counts("Ran 72 tests in 1.0s\n\nOK\n", 72)["tests"], 72)
        for text in ["Ran 0 tests in 1s\nOK\n", "Ran 72 tests in 1s\nOK (skipped=1)\n"]:
            with self.assertRaises(RuntimeError):
                python_counts(text, 72)
        with self.assertRaises(RuntimeError):
            python_counts("Ran 1 test in 1s\nFAILED (errors=1)\n", 1, red=True)

    def test_transport_inventory(self):
        self.assertEqual(len(CANDIDATES), 5)
        self.assertEqual(len(set(pair[1] for pair in CANDIDATES.values())), 5)
        for pair in CANDIDATES.values():
            self.assertTrue(all(re.fullmatch("[0-9a-f]{40}", value) for value in pair))

    def test_trx_zero_skip_and_counter_mismatch_rejected(self):
        template = ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                    '<Results>{results}</Results><ResultSummary><Counters total="{total}" executed="{executed}" '
                    'passed="{passed}" failed="0"/></ResultSummary></TestRun>')
        result = '<UnitTestResult testName="one" outcome="Passed"/>'
        good = template.format(results=result, total=1, executed=1, passed=1)
        self.assertEqual(trx_counts(io.StringIO(good), 1)["tests"], 1)
        for bad in [template.format(results="", total=0, executed=0, passed=0),
                    good.replace('outcome="Passed"', 'outcome="NotExecuted"'),
                    good.replace('passed="1"', 'passed="0"')]:
            with self.assertRaises(RuntimeError):
                trx_counts(io.StringIO(bad), 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--control", type=Path)
    parser.add_argument("--pr", type=int, choices=CANDIDATES)
    parser.add_argument("--candidate")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--inventory-only", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return not unittest.TextTestRunner(verbosity=2).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(OrchestrationTests)).wasSuccessful()
    require(args.output is not None, "--output required")
    args.output.mkdir(parents=True, exist_ok=True)
    if args.inventory_only:
        inventory(args.output)
        return 0
    require(args.control and args.pr and args.candidate, "Exact control/PR/candidate required")
    try:
        Runner(args).execute()
    except Exception as error:
        write_json(args.output / "failure.json", {"type": type(error).__name__, "error": str(error),
                                                "state": "FAILED_OR_BLOCKED_NO_GREEN_NO_RETRY"})
        raise
    finally:
        inventory(args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
