"""12 - Recovery. Run: python3 examples/py/12-recovery.py.

Real JSONL storage and the native task scheduler; no SDK client or model required.
"""
import asyncio
import copy
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import Check, DurableHarness, DurableStore, DurableStoreOptions, DurableTask, Extension, ExtensionRegistry, TaskCommit


async def main():
    printed = []
    attempted = []
    tick_two = asyncio.Event()
    release = asyncio.Event()
    interrupt = True

    async def tick(input, checkpoint, runtime):
        n = int(checkpoint["n"])
        attempted.append(n)
        if await runtime.memo_async(f"printed-{n}") is None:
            await runtime.memo_async(f"printed-{n}", True)
            printed.append(n)
            print(f"tick {n}")
        if n == 2 and interrupt:
            tick_two.set()
            # Stop after the effect and memo, BEFORE writing the next checkpoint.
            await release.wait()
        if n == input["to"]:
            return await runtime.commit_async(lambda: TaskCommit.completed(f"counted to {n}"))
        return await runtime.sleep_until_async(
            runtime.now + 0.05,
            lambda: TaskCommit.running({"phase": "tick", "n": str(n + 1)}))

    async def abort(input, checkpoint, runtime):
        return await runtime.commit_async(TaskCommit.aborted)

    ticker = DurableTask.define_phases(
        "example.ticker", lambda: {"phase": "tick", "n": "1"}, {"tick": tick}, abort)
    registry = ExtensionRegistry()
    registry.install(Extension("ticker", tasks=[ticker]))
    with TemporaryDirectory(prefix="durable-example-") as directory:
        path = str(Path(directory) / "session.jsonl")
        first_store = DurableStore.create(DurableStoreOptions(path=path))
        first = DurableHarness(None, first_store, registry)
        run = None
        try:
            root = await first.root_async()

            async def create(tx):
                return tx.create_task(ticker, {"to": 5}, owner_conversation_id=root.id)

            record = await first_store.commit_async(create)
            run = asyncio.create_task(first.run_task_async(record.id))
            await asyncio.wait_for(tick_two.wait(), 5)
            run.cancel()
            await run
            Check.that(first_store.task(record.id).outcome is None, "interruption saved no terminal outcome")
        finally:
            if run is not None and not run.done():
                run.cancel()
                await asyncio.gather(run, return_exceptions=True)
            first.close()
            first_store.close()

        reader_store = DurableStore.create(DurableStoreOptions(path=path))
        try:
            saved = copy.deepcopy(Check.not_none(reader_store.task(record.id), "the saved task exists"))
            Check.equal([1, 2], printed, "opening the reader did not resume the task")
        finally:
            reader_store.close()

        interrupt = False
        second_store = DurableStore.create(DurableStoreOptions(path=path))
        second = DurableHarness(None, second_store, registry)
        try:
            counted = await asyncio.wait_for(second.wait_for_task_async(record.id), 5)
            Check.equal("completed", counted.status, "the reopened task finished")
            Check.equal("counted to 5", counted.result, "the reopened task produced its result")
            Check.sequence([1, 2, 3, 4, 5], printed, "no tick was printed twice, and none was skipped")
            Check.equal("tick", saved.checkpoint["phase"], "the saved checkpoint is the interrupted phase")
            Check.equal("2", saved.checkpoint["n"], "the saved checkpoint is the interrupted tick")
            Check.equal(2, len(saved.memos), "the memos for the two printed ticks were saved")
            Check.sequence([1, 2, 2, 3, 4, 5], attempted, "tick 2 really reran after reopening")
            Check.equal(True, saved.memos["printed-2"], "tick 2's memo survived the interrupted write")
            Check.equal(True, await second_store.memo_async(record.id, "printed-2"),
                        "the replay retained tick 2's original memo")
            print(f"after reopen: {counted.status} {counted.result}; attempts: {attempted}; printed: {printed}")
        finally:
            second.close()
            second_store.close()


if __name__ == "__main__":
    asyncio.run(main())
