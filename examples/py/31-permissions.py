"""Default-deny permissions survive reopen; a native fork inherits exact grants."""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DocumentDefinition, DocumentHistory, DurableHarness,
    DurableStore, DurableStoreOptions, Extension, ExtensionRegistry, ForkPolicy, PermissionPolicy,
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
                deletions_after_denied = deletions
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
                review = DocumentDefinition(
                    "example.permission-review", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, lambda: [])
                reviewed_choices = allowed.choices()
                if reviewed_choices is None or reviewed_choices.permissions is None:
                    raise RuntimeError("the granted conversation has no grants to review")
                reviewed_grants = list(reviewed_choices.permissions)
                async def commit_review(tx):
                    entry = tx.append_entry(allowed.id, "permission-review", "reviewed grants")
                    draft = tx.doc(review, allowed.id)
                    draft.value = reviewed_grants
                    draft.save(entry)
                    return entry
                boundary = await allowed.commit_async(commit_review)
                Check.sequence([boundary], allowed.entries(1), "the app review is a visible fork boundary")
                await allowed.configure_async(AgentChoices(
                    extensions=["dangerous"],
                    permissions=["custom-tool:delete_everything", "custom-tool:unreviewed_tool"],
                ))
                fork = await allowed.fork_async(boundary)
                print(f"fork permissions: {fork.choices().permissions or []}")
                Check.equal(0, deletions_after_denied, "the denied conversation never ran the tool")
                Check.that(refused is not None, "the model answered the denied request")
                Check.that(not restored_choices.permissions, "the reopened conversation still has no grants")
                Check.equal(0, deletions_after_restart, "the reopened conversation still never ran the tool")
                Check.that(refused_again is not None, "the model answered the reopened request")
                Check.equal(1, deletions, "the granted conversation ran the tool exactly once")
                Check.that(permitted is not None, "the model answered the granted request")
                Check.sequence(["custom-tool:delete_everything"], fork.choices().permissions or [],
                               "the fork inherited the grants at the reviewed boundary")
                Check.sequence(["custom-tool:delete_everything"], fork.snapshot(review) or [],
                               "the fork inherited the app's permission review")
                Check.that(PermissionPolicy.is_granted(
                    allowed.choices().permissions, "custom-tool", "unreviewed_tool"),
                    "the parent's later choices include the unused grant")
                Check.that(not PermissionPolicy.is_granted(
                    fork.choices().permissions, "custom-tool", "unreviewed_tool"),
                    "the parent's later grant did not widen the fork")
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
