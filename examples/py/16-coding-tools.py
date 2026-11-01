"""16 - Coding tools (LIVE). Real file effects, tool events and SDK hook timing."""
import asyncio
import json
import subprocess
import sys
import time
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from copilot.tools import Tool, ToolResult
from durable import AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry


async def main():
    with TemporaryDirectory(prefix="durable-coding-") as directory:
        workspace = Path(directory)
        (workspace / "notes.txt").write_text("hello world\n")
        store = DurableStore.create_ephemeral()
        registry = ExtensionRegistry()

        def file_for(name):
            file = (workspace / name).resolve()
            if not file.is_relative_to(workspace):
                raise ValueError("File is outside the example workspace")
            return file

        def schema(names):
            return {"type": "object", "properties": {n: {"type": "string"} for n in names}, "required": names}

        def read(invocation):
            return ToolResult(text_result_for_llm=file_for(invocation.arguments["path"]).read_text())

        def edit(invocation):
            args = invocation.arguments
            file = file_for(args["path"])
            content = file.read_text()
            if args["oldText"] not in content:
                raise ValueError(f"{args['oldText']} not found in {args['path']}")
            file.write_text(content.replace(args["oldText"], args["newText"]))
            return ToolResult(text_result_for_llm=f"edited {args['path']}")

        def shell(invocation):
            start = time.monotonic()
            result = subprocess.run(["/bin/sh", "-c", invocation.arguments["command"]],
                                    cwd=workspace, text=True, capture_output=True, timeout=10)
            if result.returncode != 0:
                return ToolResult(result_type="failure", error=result.stderr,
                                  text_result_for_llm=f"{result.stdout} (exit {result.returncode})")
            return ToolResult(text_result_for_llm=f"{result.stdout.strip()} (exit 0, {(time.monotonic() - start) * 1000:.0f} ms)")

        registry.install(Extension("coding", tools=[
            Tool("read_file", "Read a file", handler=read, parameters=schema(["path"]), overrides_built_in_tool=True),
            Tool("edit_file", "Edit a file", handler=edit, parameters=schema(["path", "oldText", "newText"])),
            Tool("run_shell", "Run a shell command", handler=shell, parameters=schema(["command"])),
        ]))
        timings = []
        started = {}

        async def pre(input, invocation):
            if input["toolName"] == "run_shell":
                started[json.dumps(input.get("toolArgs"), sort_keys=True)] = time.monotonic()

        async def post(input, invocation):
            if input["toolName"] == "run_shell":
                begin = started.pop(json.dumps(input.get("toolArgs"), sort_keys=True), None)
                if begin is not None:
                    timings.append((time.monotonic() - begin) * 1000)

        harness = await DurableHarness.open_async(store, registry)
        try:
            root = await harness.root_async(AgentChoices(
                extensions=["coding"], working_directory=directory,
                permissions=["custom-tool:read_file", "custom-tool:edit_file", "custom-tool:run_shell"],
                hooks={"on_pre_tool_use": pre, "on_post_tool_use": post}))
            report = await root.wait_for_effective_model_async()
            print(f"effective model: {report.model or '(not reported)'}")
            answer = await root.submit_async("Use the read tool on notes.txt, then the edit tool to replace 'world' with 'durable', then the bash tool to run 'cat notes.txt'. Then tell me what the file says.")
            context = await root.context_async()
            names = [e.data.tool_name for e in context if e.type.value == "tool.execution_start"]
            Check.that("read_file" in names, "the app's read_file tool ran")
            Check.that("edit_file" in names, "the app's edit_file tool ran")
            Check.that("run_shell" in names, "the app's run_shell tool ran")
            failures = [e for e in context if e.type.value == "tool.execution_complete" and e.data.error is not None]
            Check.equal(0, len(failures), "no tool call failed")
            Check.equal("hello durable", (workspace / "notes.txt").read_text().strip(), "the edit reached the file")
            Check.equal(1, len(timings), "the hook timed the shell call")
            print(f"answer: {answer.data.content if answer else '(no answer)'}; file: hello durable; bash calls timed: {len(timings)}")
        finally:
            await harness.close_async()
            store.close()


if __name__ == "__main__":
    asyncio.run(main())
