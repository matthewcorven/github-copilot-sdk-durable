"""Select plan tools without widening the session's original permission grants."""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DocumentDefinition, DocumentHistory, DurableHarness,
    DurableStore, Extension, ExtensionRegistry, ForkPolicy, PromptSection,
)
from copilot import define_tool
from pydantic import BaseModel


class PlanParams(BaseModel):
    steps: str


class ReadParams(BaseModel):
    path: str


async def main() -> None:
    with TemporaryDirectory(prefix="durable-plan-") as directory:
        Path(directory, "server.ts").write_text("app.listen(3000);\n")
        plan_doc = DocumentDefinition("app.plan", DocumentHistory.REWINDABLE,
                                      ForkPolicy.AS_OF, lambda: [])
        chat = None

        @define_tool(name="submit_plan", description="Submit the plan, one step per line.")
        async def submit_plan(params: PlanParams) -> str:
            steps = params.steps
            async def commit(tx):
                draft = tx.doc(plan_doc, chat.id)
                draft.value = [step.strip() for step in steps.splitlines() if step.strip()]
                draft.save(tx.append_entry(chat.id, "plan", "submitted"))
            await chat.commit_async(commit)
            return "Plan submitted."

        @define_tool(name="read_file", description="Read a file", overrides_built_in_tool=True)
        async def read_file(params: ReadParams) -> str:
            path = Path(directory) / params.path
            return path.read_text() if path.is_file() else f"error: {path.name} not found"

        async def plan_mode(input):
            return "You are in plan mode. Read the code, then call submit_plan. Change nothing."

        registry = ExtensionRegistry()
        registry.install(Extension("plan", tools=[submit_plan],
                                   sections=[PromptSection("plan_mode", plan_mode)]))
        registry.install(Extension("coding", tools=[read_file]))
        grants = ["custom-tool:read_file", "custom-tool:submit_plan", "read", "write"]
        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, registry)
            try:
                chat = await harness.root_async(AgentChoices(
                    extensions=["coding"], working_directory=directory, permissions=grants,
                ))
                tools = lambda: [tool.name for tool in chat.agent().tools]
                effective = await chat.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                Check.sequence(["read_file"], tools(), "the conversation starts with the coding tools")
                await chat.configure_async(AgentChoices(
                    extensions=["plan"], tools=["read_file", "submit_plan"],
                    working_directory=directory, permissions=grants,
                ))
                plan_tools = tools()
                answer = await chat.submit_async(
                    "Make the port configurable. You are in plan mode: read server.ts, then "
                    "call the submit_plan tool with the steps. Do not edit any file."
                )
                print(f"answer: {answer.data.content if answer else ''}")
                print(f"plan: {chat.snapshot(plan_doc)}")
                await chat.configure_async(AgentChoices(
                    extensions=["coding"], working_directory=directory,
                    permissions=["custom-tool:read_file", "read", "write"],
                ))
                second = await chat.submit_async("Go ahead.")
                print(f"answer: {second.data.content if second else ''}")
                Check.sequence(["submit_plan"], plan_tools, "plan mode offers only the plan tool")
                Check.sequence(["read_file"], tools(), "leaving plan mode restores the coding tools")
                plan = chat.snapshot(plan_doc)
                Check.that(plan is not None and len(plan) > 0, "the plan was written to the document")
                Check.that(all(step.strip() for step in plan), "every plan step has content")
            finally:
                await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
