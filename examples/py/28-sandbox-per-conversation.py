"""Per-conversation directory isolation plus SDK policy mapping.

Default: policy mode. --live additionally asserts the two actual tool-written files.
The C# reference's sandbox document is custom-tool isolation, not OS enforcement.
"""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DocumentDefinition, DocumentHistory, DurableHarness,
    DurableHarnessOptions, DurableSandboxFilesystem, DurableSandboxPolicy,
    DurableStore, Extension, ExtensionRegistry, ForkPolicy,
)
from copilot import define_tool
from copilot.tools import ToolInvocation
from pydantic import BaseModel
from _sandbox_policy_22_35 import assert_policy


class WriteParams(BaseModel):
    path: str
    content: str


async def main() -> None:
    live = "--live" in sys.argv[1:]
    if any(argument != "--live" for argument in sys.argv[1:]):
        raise ValueError("only --live is supported")
    sandbox_doc = DocumentDefinition("example.sandbox", DocumentHistory.LATEST,
                                      ForkPolicy.INITIAL, lambda: {"path": None})
    by_session = {}
    @define_tool(name="write_file", description="Write a file into your sandbox",
                 overrides_built_in_tool=True)
    async def write_file(params: WriteParams, invocation: ToolInvocation) -> str:
        conversation = by_session.get(invocation.session_id)
        if conversation is None:
            return "(no conversation for this session)"
        sandbox = conversation.snapshot(sandbox_doc)
        if not sandbox or not sandbox["path"]:
            return "(this conversation has no sandbox)"
        base = Path(sandbox["path"]).resolve()
        full = (base / params.path).resolve()
        if not full.is_relative_to(base):
            raise ValueError("write_file path must remain inside this conversation's directory")
        full.parent.mkdir(parents=True, exist_ok=True)
        full.write_text(params.content)
        return f"wrote {params.path} ({len(params.content)} bytes)"

    registry = ExtensionRegistry()
    registry.install(Extension("sandbox", tools=[write_file]))
    with TemporaryDirectory(prefix="durable-sandbox-alice-") as alice_path, \
         TemporaryDirectory(prefix="durable-sandbox-bob-") as bob_path, \
         DurableStore.create_ephemeral() as store:
        harness = await DurableHarness.open_async(
            store, registry, DurableHarnessOptions(
                enable_file_hooks=False, tolerate_host_hook_failures=live)
        )
        try:
            async def conversation_for(path):
                policy = DurableSandboxPolicy(
                    enabled=True, add_current_working_directory=False, allow_bypass=False,
                    allow_dev_tool_access=False, trust_working_directory=True,
                    filesystem=DurableSandboxFilesystem(readwrite_paths=["{workspace}"],
                                                         readonly_paths=[], denied_paths=[]),
                )
                choices = AgentChoices(extensions=["sandbox"], tools=["write_file"],
                                       working_directory=path, sandbox=policy,
                                       permissions=["custom-tool:write_file"])
                config = await harness.build_session_config(choices)
                assert_policy(policy, config["sandbox_config"], path, "the conversation policy")
                conversation = await harness.create_conversation_async(choices)
                async def commit(tx):
                    draft = tx.doc(sandbox_doc, conversation.id)
                    draft.value = {"path": path}
                    draft.save(tx.append_entry(conversation.id, "sandbox", path))
                await conversation.commit_async(commit)
                Check.that(conversation.session_id is not None, "the conversation has a native session ID")
                by_session[conversation.session_id] = conversation
                return conversation

            alice = await conversation_for(alice_path)
            bob = await conversation_for(bob_path)
            effective = await alice.wait_for_effective_model_async()
            print(f"effective model: {effective.model or '(not reported)'}")
            Check.that(alice_path != bob_path, "the two conversations have different sandboxes")
            Check.equal(alice_path, alice.snapshot(sandbox_doc)["path"], "alice's document points at her sandbox")
            Check.equal(bob_path, bob.snapshot(sandbox_doc)["path"], "bob's document points at his sandbox")
            if live:
                await alice.submit_async("Use the write_file tool to write 'from alice' into note.txt.")
                await bob.submit_async("Use the write_file tool to write 'from bob' into note.txt.")
                Check.equal("from alice", Path(alice_path, "note.txt").read_text(),
                            "alice's note landed in alice's sandbox")
                Check.equal("from bob", Path(bob_path, "note.txt").read_text(),
                            "bob's note landed in bob's sandbox")
            else:
                print("LIVE: alice/bob tool-written note contents are unproven; the recording has no tool call.")
            print(f"alice's document: {alice.snapshot(sandbox_doc)['path']}")
            print(f"bob's document: {bob.snapshot(sandbox_doc)['path']}")
            print(f"assertion set: {'policy + tool effects' if live else 'policy only'}")
        finally:
            await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
