"""10 — Extension state. LIVE: a recorded answer cannot prove a tool wrote state."""
from copilot import define_tool
from pydantic import BaseModel, Field
from example import d, run_example
from check import Check


class TodoArguments(BaseModel):
    item: str = Field(description="Item to add")


async def main(harness, store, registry):
    todos = d.DocumentDefinition("example.todos", d.DocumentHistory.REWINDABLE, d.ForkPolicy.AS_OF, lambda: [])
    chat = None

    @define_tool("todo", description="Add an item to your todo list")
    async def todo(params: TodoArguments):
        conversation = Check.not_none(chat, "the todo tool has a conversation")

        async def write(tx):
            draft = tx.doc(todos, conversation.id)
            draft.value.append(params.item)
            draft.save(tx.append_entry(conversation.id, "todo", params.item))

        await conversation.commit_async(write)
        return f"added {params.item}"

    async def section(input):
        document = store.document(todos.kind, input.conversation.id) if input.conversation else None
        value = document.latest if document else None
        return "\n".join(value) if value else None

    registry.install(d.Extension("todo", tools=[todo], sections=[d.PromptSection("todos", section)]))
    chat = await harness.root_async(d.AgentChoices(extensions=["todo"], permissions=["custom-tool:todo"]))
    print(f"effective model: {(await chat.wait_for_effective_model_async()).model or '(not reported)'}")
    reply = await chat.submit_async("Add 'fix the build' to my todo list, then confirm.")
    print(f"answer: {reply.data.content if reply else '(no answer)'}")
    for event in await chat.context_async():
        if event.type == "system.message":
            print(f"system message: {event.data.content}")
    Check.sequence(["fix the build"], chat.snapshot(todos) or [], "the tool wrote the todo into the document")
    Check.not_none(reply, "the model answered")
    rendered = await harness.render_sections_async(chat)
    Check.contains(rendered, "fix the build", "the section renders the todo list from the document")
    Check.sequence(["fix the build"], chat.snapshot(todos) or [], "the document holds the todo")


if __name__ == "__main__":
    run_example(main)
