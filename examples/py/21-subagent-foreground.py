"""21 - Foreground subagent (LIVE). Real parent tool call and owned child session."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from copilot.tools import Tool, ToolResult
from durable import AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry


async def main():
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()
    harness = await DurableHarness.open_async(store, registry)
    parent = None
    try:
        async def delegate(invocation):
            if parent is None:
                raise RuntimeError("The parent must be ready before its tool runs")
            child = await harness.create_conversation_async(
                AgentChoices(model="mai-code-1.1-flash"), owner_conversation_id=parent.id)
            try:
                answer = await child.submit_async(invocation.arguments["task"])
            except asyncio.CancelledError:
                await child.abort_async()
                raise
            Check.that(answer is not None, "the child answered")
            return ToolResult(text_result_for_llm=f"{answer.data.content} (child conversation: {child.id})")

        tool = Tool(
            name="subagent",
            description="Delegate a self-contained task to a subagent and get its answer back.",
            parameters={"type": "object", "properties": {"task": {"type": "string", "description": "What the subagent should do"}},
                        "required": ["task"]},
            handler=delegate,
        )
        registry.install(Extension("subagent", tools=[tool]))
        parent = await harness.root_async(AgentChoices(extensions=["subagent"], permissions=["custom-tool:subagent"]))
        report = await parent.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}")
        answer = await parent.submit_async("Use the subagent tool to name three prime numbers, then tell me what it said.")
        children = [c for c in store.conversations if c.owner_conversation_id == parent.id]
        Check.equal(1, len(children), "the subagent tool created exactly one child conversation")
        Check.not_none(store.conversation(children[0].id), "the child conversation is in the store")
        Check.that(children[0].id != parent.id, "the child is a different conversation")
        Check.that(answer is not None, "the parent answered")
        Check.contains(answer.data.content, "2", "the parent's answer carries the child's answer back")
        print(f"answer: {answer.data.content}")
        for conversation in store.conversations:
            print(f"conversation {conversation.id}: owner conversation={conversation.owner_conversation_id or '(none)'}")
    finally:
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
