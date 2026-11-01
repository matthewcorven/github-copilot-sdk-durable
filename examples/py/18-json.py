"""18 - JSON mode (LIVE). A snapshot followed by actual SDK event JSON lines."""
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection


async def main():
    prompt = sys.argv[1] if len(sys.argv) > 1 else "What is in this directory?"
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()

    async def preamble(input):
        return "You are a concise coding assistant."

    registry.install(Extension("coding", sections=[PromptSection("preamble", preamble, tag=False)]))
    harness = await DurableHarness.open_async(store, registry)
    unsubscribe = None
    try:
        root = await harness.root_async(AgentChoices(extensions=["coding"], permissions=["read"]))
        report = await root.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}", file=sys.stderr)
        lines = []

        def emit(value):
            line = json.dumps(value, separators=(",", ":"))
            lines.append(line)
            print(line, flush=True)

        emit({"type": "snapshot", "events": len(await root.context_async())})

        def observe(event):
            data = None
            if event.type in ("user.message", "assistant.message"):
                data = event.data.content
            elif event.type == "tool.execution_start":
                data = event.data.tool_name
            emit({"type": "event", "event": event.type, "id": event.id,
                  "agentId": getattr(event, "agent_id", None), "data": data})

        unsubscribe = root.session.on(observe)
        await root.submit_async(prompt)
        await asyncio.sleep(0.1)
        Check.that(len(lines) >= 1, "the run produced at least one line")
        Check.that(all(isinstance(json.loads(line), dict) for line in lines), "every line is a JSON object")
        Check.equal("snapshot", json.loads(lines[0])["type"], "the first line is the snapshot")
        Check.that(any(json.loads(line)["type"] == "event" for line in lines), "the run produced events")
    finally:
        if unsubscribe is not None:
            unsubscribe()
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
