import asyncio
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import (
    DurableHarness, DurableStore, DurableStoreOptions, DurableTask,
    Extension, ExtensionRegistry, TaskCommit,
)


async def main():
    file, effects, mode = sys.argv[1:]
    if mode not in ("create", "interrupt", "complete"):
        raise ValueError("Expected create|interrupt|complete")
    store = DurableStore.create(DurableStoreOptions(path=file))

    async def phase(_input, checkpoint, runtime):
        await runtime.commit_async(lambda: TaskCommit.running(checkpoint))
        if await runtime.memo_async("charged") is None:
            with open(effects, "a") as handle:
                handle.write("charged\n")
                handle.flush()
                os.fsync(handle.fileno())
            await runtime.memo_async("charged", True)
        if mode != "complete":
            os._exit(73)
        return await runtime.commit_async(lambda: TaskCommit.completed("receipt"))

    definition = DurableTask.define("parity.once", lambda: {"phase": "run"}, phase)
    registry = ExtensionRegistry()
    registry.install(Extension("parity", tasks=[definition]))
    harness = DurableHarness(None, store, registry)
    try:
        if mode == "create":
            async def create(tx):
                return tx.create_task(definition)
            task = await store.commit_async(create)
            await harness.run_task_async(task.id)
        else:
            outcomes = await harness.resume_async()
            if len(outcomes) != 1 or outcomes[0].status != "completed":
                raise AssertionError(f"Recovery must complete exactly one task: {outcomes}")
    finally:
        harness.close()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
