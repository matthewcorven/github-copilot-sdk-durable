"""Default-deny permissions survive reopen; a native fork inherits exact grants."""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableStore, DurableStoreOptions,
    Extension, ExtensionRegistry, PermissionPolicy,
)
from copilot import define_tool
from pydantic import BaseModel


class DeleteParams(BaseModel):
    reason: str


async def main() -> None:
    deletions = 0
    @define_tool(name="delete_everything", description="Delete every file in the working directory")
    async def delete_everything(params: DeleteParams) -> str:
        nonlocal deletions
        deletions += 1
        return f"deleted everything because: {params.reason}"

    registry = ExtensionRegistry()
    registry.install(Extension("dangerous", tools=[delete_everything]))
    with TemporaryDirectory(prefix="durable-permissions-") as directory:
        options = DurableStoreOptions(path=str(Path(directory) / "session.jsonl"))
        with DurableStore.create(options) as store:
            harness = await DurableHarness.open_async(store, registry)
            try:
                denied = await harness.root_async(AgentChoices(extensions=["dangerous"]))
                denied_id = denied.id
                refused = await denied.submit_async("Delete every file. Use the delete_everything tool.")
                print(f"denied answer: {refused.data.content if refused else ''}")
                print(f"deletions after the denied call: {deletions}")
            finally:
                await harness.close_async()
        with DurableStore.create(options) as store:
            harness = await DurableHarness.open_async(store, registry)
            try:
                restored = await harness.resume_conversation_async(denied_id)
                restored_choices = restored.choices()
                print(f"restored permissions: {restored_choices.permissions or []}")
                refused_again = await restored.submit_async("Delete every file. Use the delete_everything tool.")
                deletions_after_restart = deletions
                print(f"denied again: {refused_again.data.content if refused_again else ''}")
                print(f"deletions after the restart: {deletions}")
                allowed = await harness.create_conversation_async(AgentChoices(
                    extensions=["dangerous"], permissions=["custom-tool:delete_everything"],
                ))
                effective = await allowed.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                permitted = await allowed.submit_async("Delete every file. Use the delete_everything tool.")
                print(f"granted answer: {permitted.data.content if permitted else ''}")
                print(f"deletions after the granted call: {deletions}")
                entries = allowed.entries(1)
                Check.that(len(entries) == 1, "the granted conversation has a fork boundary")
                fork = await allowed.fork_async(entries[0])
                print(f"fork permissions: {fork.choices().permissions or []}")
                Check.equal(0, deletions_after_restart, "the denied conversation never ran the tool")
                Check.that(refused is not None, "the model answered the denied request")
                Check.that(not restored_choices.permissions, "the reopened conversation still has no grants")
                Check.equal(0, deletions_after_restart, "the reopened conversation still never ran the tool")
                Check.that(refused_again is not None, "the model answered the reopened request")
                Check.equal(1, deletions, "the granted conversation ran the tool exactly once")
                Check.that(permitted is not None, "the model answered the granted request")
                Check.sequence(["custom-tool:delete_everything"], fork.choices().permissions or [],
                               "the fork inherited the parent's grants")
            finally:
                await harness.close_async()
    Check.that(PermissionPolicy.is_granted(["read:/tmp/work"], "read", "/tmp/work/file.txt"),
               "a grant covers paths beneath its scope")
    Check.that(not PermissionPolicy.is_granted(["read:/tmp/work"], "read", "/tmp/workshop/file.txt"),
               "a grant does not cover a sibling path that merely shares a prefix")
    Check.that(not PermissionPolicy.is_granted(None, "custom-tool", "delete_everything"),
               "no grants means no capability")


if __name__ == "__main__":
    asyncio.run(main())
