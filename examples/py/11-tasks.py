"""11 - Tasks. Run: python3 examples/py/11-tasks.py.

Uses the native harness task scheduler without an SDK client. No model is reported.
"""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import Check, DurableHarness, DurableStore, DurableTask, Extension, ExtensionRegistry, TaskCommit, TaskStatus


async def main():
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()
    payments = {}

    async def prepare(input, checkpoint, runtime):
        return await runtime.commit_async(lambda: TaskCommit.running(
            {"phase": "charge", "key": f"payment-{runtime.task.id}"}))

    async def charge(input, checkpoint, runtime):
        key = checkpoint["key"]
        if key not in payments:
            payments[key] = input["amount"] * 100
        return await runtime.commit_async(lambda: TaskCommit.completed({"receipt": payments[key]}))

    async def abort(input, checkpoint, runtime):
        return await runtime.commit_async(TaskCommit.aborted)

    payment = DurableTask.define_phases("example.payment", lambda: {"phase": "prepare"},
                                       {"prepare": prepare, "charge": charge}, abort)
    registry.install(Extension("payments", tasks=[payment]))
    harness = DurableHarness(None, store, registry)
    try:
        root = await harness.root_async()
        task = await harness.create_task_async(payment, {"amount": 5}, owner_conversation_id=root.id)
        paid = await harness.wait_for_task_async(task.id)
        Check.equal("completed", paid.status, "the task completed")
        Check.equal(500, paid.result["receipt"], "the receipt holds the amount that was charged, in cents")
        Check.equal(1, len(payments), "the payment effect happened exactly once")
        Check.equal(500, next(iter(payments.values())), "the charged amount is the one that was asked for, in cents")
        record = Check.not_none(store.task(task.id), "the saved task exists")
        Check.equal(TaskStatus.TERMINAL, record.status, "the saved record is terminal")
        Check.equal("charge", record.checkpoint["phase"], "the saved checkpoint is the last phase")
        Check.that(record.outcome is not None, "the saved record holds the outcome")
        rerun = await harness.wait_for_task_async(task.id)
        Check.equal("completed", rerun.status, "waiting on a finished task returns its outcome")
        Check.equal(1, len(payments), "waiting on a finished task does not repeat the effect")
        print("payment outcome: completed; receipt: 500; payments charged: 1")
    finally:
        harness.close()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
