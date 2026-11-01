"""05 — The harness. Stored names resolve against the live registry."""
from example import d, run_example
from check import Check
from tools import file_tool


async def main(harness, store, registry):
    registry.install(d.Extension("files", tools=[file_tool("read", "Read a file")]))
    notes = d.DocumentDefinition("example.notes", d.DocumentHistory.REWINDABLE, d.ForkPolicy.AS_OF, lambda: {"text": ""})
    root = await harness.root_async(d.AgentChoices(
        extensions=["files"], reasoning_effort="low", permissions=["custom-tool:read"],
    ))

    async def write(tx):
        draft = tx.doc(notes, root.id)
        draft.value["text"] = "root notes"
        draft.save(tx.append_entry(root.id, "note", "root notes"))

    await root.commit_async(write)
    print(f"effective model: {(await root.wait_for_effective_model_async()).model or '(not reported)'}")
    agent = root.agent()
    again = await harness.root_async(d.AgentChoices(reasoning_effort="high"))
    Check.equal("low", root.choices().reasoning_effort, "the stored reasoning effort is the one that was configured")
    Check.equal("low", agent.reasoning_effort, "the resolved agent carries the stored reasoning effort")
    Check.sequence(["files"], [e.name for e in agent.extensions], "the resolved agent carries the selected extension")
    Check.sequence(["read"], [t.name for t in agent.tools], "the resolved agent carries the selected tool")
    Check.equal(root.id, again.id, "the root conversation is stable across calls")
    Check.equal("low", root.choices().reasoning_effort, "a second RootAsync() does not overwrite the stored choices")
    Check.equal("root notes", root.snapshot(notes)["text"], "the committed document value is readable")


if __name__ == "__main__":
    run_example(main)
