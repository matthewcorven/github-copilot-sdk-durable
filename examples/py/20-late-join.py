"""20 - Late join (LIVE). A real SDK tool holds generation until the client joins."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from copilot.tools import Tool, ToolResult
from durable import AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry


async def main():
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()
    loop = asyncio.get_running_loop()
    expires = loop.time() + 90
    started = asyncio.Event()
    release = asyncio.Event()
    cancelled = False
    harness = None
    unsubscribe = None
    run = None

    async def wait_for_join(invocation):
        started.set()
        await asyncio.wait_for(release.wait(), timeout=max(0, expires - loop.time()))
        # SDK cancellation cancels the handler task; host cancellation must also prevent a result.
        if cancelled or loop.time() >= expires:
            raise asyncio.CancelledError()
        return ToolResult(
            result_type="success",
            text_result_for_llm="The client has joined. Now count from one to five, then say DONE.")

    registry.install(Extension("late-join", tools=[Tool(
        name="wait_for_join",
        description="Wait for the client to join before generating the counting answer. Call this first.",
        parameters={"type": "object", "properties": {}},
        handler=wait_for_join,
    )]))
    deadline = asyncio.timeout_at(expires)
    try:
        # Bootstrap exposes no cancellation/cleanup handle until the facade owns the client.
        harness = await DurableHarness.open_async(store, registry)
        async with deadline:
            root = await harness.root_async(AgentChoices(
                streaming=True, extensions=["late-join"], tools=["wait_for_join"],
                permissions=["custom-tool:wait_for_join"]))
            report = await root.wait_for_effective_model_async()
            print(f"effective model: {report.model or '(not reported)'}")
            run = asyncio.create_task(root.submit_async(
                "First call wait_for_join exactly once, and wait for its result before writing any counting text. "
                "Then count slowly from one to five, one number per sentence, then say DONE."))

            async def require_pending_run(milestone, phase, phase_expires=expires):
                milestone = asyncio.ensure_future(milestone)
                try:
                    done, _ = await asyncio.wait(
                        (run, milestone), timeout=max(0, phase_expires - loop.time()),
                        return_when=asyncio.FIRST_COMPLETED)
                    if run.done():
                        await run
                        raise RuntimeError(f"The run ended before joining while {phase}.")
                    if not done:
                        raise TimeoutError(f"Late join timed out while {phase}.")
                    await milestone
                except asyncio.CancelledError:
                    if loop.time() >= phase_expires:
                        if run.done() and not run.cancelled() and run.exception() is not None:
                            await run
                        raise TimeoutError(f"Late join timed out while {phase}.") from None
                    raise
                finally:
                    if not milestone.done():
                        milestone.cancel()
                        await asyncio.gather(milestone, return_exceptions=True)

            await require_pending_run(started.wait(), "waiting for wait_for_join")
            print("synchronization tool started; joining while the run is pending")
            snapshot_expires = min(expires, loop.time() + 15)
            while True:
                read = asyncio.create_task(root.context_async())
                await require_pending_run(read, "reading the SDK snapshot", snapshot_expires)
                snapshot = await read
                if any(event.type.value == "user.message" for event in snapshot):
                    break
                await require_pending_run(
                    asyncio.sleep(0.05), "waiting for an actual UserMessageEvent in the SDK snapshot",
                    snapshot_expires)
            print(f"snapshot events: {len(snapshot)}")
            for event in snapshot:
                print(f"  {event.type.value}")
            deltas = []

            def observe(event):
                if event.type.value == "assistant.message_delta" and event.data.delta_content:
                    deltas.append(event.data.delta_content)
                    print(f"event text delta: {event.data.delta_content}")
                elif event.type.value == "assistant.streaming_delta":
                    print(f"event: streamed {event.data.total_response_size_bytes} bytes")
                elif event.type.value == "assistant.message":
                    print(f"event: final message ({len(event.data.content.encode('utf-16-le')) // 2} chars)")
                else:
                    print(f"event: {event.type.value}")

            unsubscribe = root.session.on(observe)
            if run.done():
                await run
                raise RuntimeError("The run ended before the live subscription was installed.")
            release.set()
            print("live subscription installed; synchronization tool released")
            answer = await asyncio.shield(run)
            print(f"answer: {answer.data.content if answer else None}")
            print(f"deltas received after joining: {len(deltas)}")
            Check.that(len(snapshot) >= 1, "the late joiner received the current state")
            Check.that(any(e.type.value == "user.message" for e in snapshot), "the snapshot holds the user message")
            Check.that(len(deltas) >= 1, "the late joiner received live deltas")
            Check.that(answer is not None, "the run finished")
            Check.that(len(answer.data.content.strip()) > 0, "the answer is not empty")
    except TimeoutError as error:
        if deadline.expired():
            if run is not None and run.done() and not run.cancelled() and run.exception() is not None:
                await run
            if not str(error).startswith("Late join timed out while "):
                raise TimeoutError("Late join timed out while awaiting the completed counting answer.") from error
        raise
    finally:
        cancelled = True
        release.set()
        if unsubscribe is not None:
            unsubscribe()
        if run is not None and not run.done():
            run.cancel()
            await asyncio.gather(run, return_exceptions=True)
        try:
            if harness is not None:
                await harness.close_async()
        finally:
            store.close()


if __name__ == "__main__":
    asyncio.run(main())
