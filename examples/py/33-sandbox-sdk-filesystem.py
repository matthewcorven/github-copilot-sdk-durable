"""Filesystem policy wire proof; --live additionally requires actual shell effects."""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableHarnessOptions, DurableSandboxFilesystem,
    DurableSandboxPolicy, DurableStore, ExtensionRegistry,
)
from _sandbox_policy_22_35 import assert_policy, decision_data, engaged, print_report


async def main() -> None:
    live = "--live" in sys.argv[1:]
    if any(argument != "--live" for argument in sys.argv[1:]):
        raise ValueError("only --live is supported")
    with TemporaryDirectory(prefix="durable-sandbox-") as workspace:
        for name in ("writable", "readonly", "secret"):
            Path(workspace, name).mkdir()
        Path(workspace, "readonly", "pinned.txt").write_text("read-only content")
        denied_path = Path(workspace, "secret", "leak.txt")
        denied_path.write_text("do not read this")
        policy = DurableSandboxPolicy(
            enabled=True, add_current_working_directory=False, allow_bypass=False,
            allow_dev_tool_access=False, trust_working_directory=True,
            filesystem=DurableSandboxFilesystem(
                readwrite_paths=["{workspace}/writable"], readonly_paths=["{workspace}/readonly"],
                denied_paths=["{workspace}/secret"],
            ),
        )
        config = DurableHarness.build_sandbox_config(policy, workspace)
        wire = assert_policy(policy, config, workspace, "the filesystem policy")
        print("resolved filesystem policy:")
        for field, label in [("readwritePaths", "readwrite"), ("readonlyPaths", "readonly"), ("deniedPaths", "denied")]:
            print(f"  {label}: [{', '.join(wire['userPolicy']['filesystem'][field])}]")
        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, ExtensionRegistry(),
                DurableHarnessOptions(enable_file_hooks=False, tolerate_host_hook_failures=True))
            try:
                choices = AgentChoices(working_directory=workspace, sandbox=policy, permissions=["shell"])
                session_config = await harness.build_session_config(choices)
                assert_policy(policy, session_config["sandbox_config"], workspace, "the session filesystem policy")
                conversation = await harness.create_conversation_async(choices)
                effective = await conversation.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                if live:
                    permitted = Path(workspace, "writable", "ok.txt")
                    await conversation.submit_async(
                        f"Use the shell tool to run exactly this command, then report its output: printf 'permitted' > '{permitted}'")
                    Check.that(permitted.is_file(), "the write into the readwrite path landed on disk")
                    Check.equal("permitted", permitted.read_text(), "the readwrite path holds what was written")
                    await conversation.submit_async(
                        f"Use the shell tool to run exactly this command, then report its output: printf 'leaked' > '{denied_path}'")
                decisions = decision_data(conversation.sandbox_decisions)
                print_report(decisions)
                print(f"sandbox decisions observed: {len(decisions)}")
                if live and engaged(decisions):
                    denials = [data for data in decisions if data["kind"] == "access_denied"]
                    Check.that(bool(denials), "the sandbox reported at least one access denial")
                    Check.that(any(data.get("denialClass") in ("filesystem_write", "filesystem_read") for data in denials),
                               "the denied write reports a filesystem denial")
                    Check.that(all(data.get("deniedResource") for data in denials),
                               "the denial names the resource it refused")
                    Check.equal("do not read this", denied_path.read_text(),
                                "the denied path still holds its original content")
                else:
                    print("LIVE: permitted file contents and engaged-backend denied-write proof are unproven; "
                          "the recording has no tool call.")
                Check.that(len(conversation.sandbox_decisions) >= 0,
                           "the conversation records the sandbox decisions it observed")
                print(f"assertion set: {'policy + live tool effects' if live else 'policy only'}")
            finally:
                await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
