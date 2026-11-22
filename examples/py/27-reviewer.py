"""A read-only reviewer repeats its stop hook until no further findings remain."""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection,
)
from copilot import define_tool
from pydantic import BaseModel


class ReadParams(BaseModel):
    path: str

DONE = "No further findings."


async def main() -> None:
    with TemporaryDirectory(prefix="durable-review-") as worktree:
        Path(worktree, "user.ts").write_text("export const name = (user) => user.name;\n")
        @define_tool(name="read_file", description="Read a file", overrides_built_in_tool=True)
        async def read_file(params: ReadParams) -> str:
            path = Path(worktree) / params.path
            return path.read_text() if path.is_file() else f"(no such file: {path.name})"

        async def role(input):
            return "You review diffs. Report problems as a list. Never edit files."

        registry = ExtensionRegistry()
        registry.install(Extension("reviewer", tools=[read_file],
                                   sections=[PromptSection("role", role)]))
        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, registry)
            try:
                root = await harness.root_async(AgentChoices(
                    extensions=["reviewer"], tools=["read_file"], working_directory=worktree,
                    permissions=["custom-tool:read_file", "read"],
                ))
                effective = await root.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                passes = 0
                reviewer = None
                async def on_stop(input, invocation):
                    nonlocal passes
                    passes += 1
                    answer = reviewer.answers[-1] if reviewer and reviewer.answers else ""
                    if DONE.lower() in answer.lower():
                        return None
                    return {"decision": "block",
                            "reason": f'Look again for anything you missed. Say "{DONE}" when there is nothing left.'}

                reviewer = await harness.create_conversation_async(AgentChoices(
                    model="mai-code-1.1-flash", extensions=["reviewer"], tools=["read_file"],
                    working_directory=worktree, hooks={"on_agent_stop": on_stop},
                    permissions=["custom-tool:read_file", "read"],
                ))
                agent = reviewer.agent()
                print(f"reviewer: {agent.model} "
                      f"{[extension.name for extension in agent.extensions]} "
                      f"{[tool.name for tool in agent.tools]} cwd={agent.working_directory == worktree}")
                await reviewer.submit_async("Review user.ts.")
                print(f"review passes: {passes}")
                for answer in reviewer.answers:
                    print(f"reviewer: {answer}")
                Check.equal("mai-code-1.1-flash", agent.model, "the reviewer uses its own model")
                Check.sequence(["reviewer"], [extension.name for extension in agent.extensions],
                               "the reviewer selects its extension")
                Check.sequence(["read_file"], [tool.name for tool in agent.tools],
                               "the reviewer has only the read tool")
                Check.equal(worktree, agent.working_directory, "the reviewer has its own working directory")
                Check.that(passes >= 2, "the review loop ran more than one pass")
                Check.that(len(reviewer.answers) >= 2, "each pass produced an answer")
                Check.contains(reviewer.answers[-1], DONE, "the last pass says there is nothing left")
            finally:
                await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
