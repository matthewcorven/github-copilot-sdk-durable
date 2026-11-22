"""Persistent named subagents: spawn, send, stop, status, and registry recovery."""

import asyncio
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DocumentDefinition, DocumentHistory, DurableHarness,
    DurableStore, DurableStoreOptions, Extension, ExtensionRegistry, ForkPolicy,
)
from copilot import define_tool
from pydantic import BaseModel


class SubagentParams(BaseModel):
    action: str
    name: str = ""
    message: str = ""


async def main() -> None:
    subagents = DocumentDefinition("app.subagents", DocumentHistory.LATEST,
                                   ForkPolicy.INITIAL, lambda: {})
    host = None
    main_chat = None

    @define_tool(name="subagent", description="Manage persistent subagents: spawn, send, stop, status.")
    async def subagent(params: SubagentParams) -> str:
        agents = main_chat.snapshot(subagents) or {}
        if params.action == "status":
            lines = []
            for name in [params.name] if params.name else agents:
                if name in agents:
                    child = host.find_conversation(agents[name]["conversationId"])
                    lines.append(f"{name}: {'working' if child and child.busy else 'idle'}")
            return "\n".join(lines) if lines else "No subagents."
        if not params.name:
            return f"{params.action} needs a name."
        if params.action == "spawn":
            if params.name in agents:
                return f"{params.name} already exists; use send."
            child = await host.create_conversation_async(AgentChoices(
                model="mai-code-1.1-flash",
                instructions=f'You are the subagent "{params.name}". Answer the main agent\'s requests.',
            ), owner_conversation_id=main_chat.id)
            async def commit(tx):
                draft = tx.doc(subagents, main_chat.id)
                draft.value[params.name] = {"conversationId": child.id, "reported": "0"}
                draft.save(tx.append_entry(main_chat.id, "subagent", f"spawned {params.name}"))
            await main_chat.commit_async(commit)
            return f"Started {params.name}."
        if params.name not in agents:
            return f"No subagent named {params.name}."
        child = await host.resume_conversation_async(agents[params.name]["conversationId"])
        if params.action == "stop":
            await child.abort_async()
            return f"Stopped {params.name}."
        if not params.message:
            return f"{params.action} needs a message."
        answer = await child.submit_async(params.message)
        return f"[{params.name} answered] {answer.data.content if answer else ''}"

    registry = ExtensionRegistry()
    registry.install(Extension("subagent-tools", tools=[subagent]))
    choices = AgentChoices(extensions=["subagent-tools"], permissions=["custom-tool:subagent"])
    with TemporaryDirectory(prefix="durable-subagents-") as directory:
        options = DurableStoreOptions(path=str(Path(directory) / "session.jsonl"))
        with DurableStore.create(options) as store:
            host = await DurableHarness.open_async(store, registry)
            try:
                main_chat = await host.root_async(choices)
                effective = await main_chat.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                for label, prompt in [
                    ("spawn", "Use the subagent tool to spawn a subagent named reader."),
                    ("send", "Use the subagent tool to send 'What is the whale called?' to reader."),
                    ("status", "Use the subagent tool to list my subagents."),
                ]:
                    answer = await main_chat.submit_async(prompt)
                    print(f"{label}: {answer.data.content if answer else ''}")
                before = main_chat.snapshot(subagents)
                Check.that(before is not None and "reader" in before,
                           "the first process registered the reader subagent")
                print(f"registry before restart: {', '.join(before)}")
            finally:
                await host.close_async()
        print("(process restarts)")
        with DurableStore.create(options) as store:
            host = await DurableHarness.open_async(store, registry)
            try:
                main_chat = await host.root_async(choices)
                after = main_chat.snapshot(subagents)
                Check.that(after is not None and "reader" in after,
                           "the background subagent survived the restart")
                print(f"registry after restart: {', '.join(after)}")
                listed = await main_chat.submit_async("Use the subagent tool to list my subagents.")
                print(f"status after restart: {listed.data.content if listed else ''}")
                Check.equal(before["reader"]["conversationId"], after["reader"]["conversationId"],
                            "the restarted process points at the same subagent conversation")
                Check.that(listed is not None, "the restarted process can list the subagent")
                Check.contains(listed.data.content, "reader",
                               "the restarted process still knows the subagent by name")
            finally:
                await host.close_async()


if __name__ == "__main__":
    asyncio.run(main())
