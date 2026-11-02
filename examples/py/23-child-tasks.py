"""Child ownership, fail-fast payment fan-out, and cancellation rollback.

Run: python3 examples/py/23-child-tasks.py
Use DURABLE_RUNTIME_PATH to select the recorded runtime.
"""

import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    Check,
    DurableHarness,
    DurableStore,
    DurableTask,
    Extension,
    ExtensionRegistry,
    TaskCommit,
    TaskRuntime,
    TaskStatus,
    Transaction,
)


async def main() -> None:
    charged: set[str] = set()
    aborted: set[str] = set()

    async def charge(input, checkpoint, runtime: TaskRuntime):
        def commit():
            if not input["card"].startswith("expired"):
                charged.add(input["card"])
            return TaskCommit.running({"phase": "confirm"})

        return await runtime.commit_async(commit)

    async def confirm(input, checkpoint, runtime: TaskRuntime):
        return await runtime.sleep_until_async(
            float(input["answerAt"]),
            lambda: (
                TaskCommit.failed(f'{input["card"]} declined')
                if input["card"].startswith("expired")
                else TaskCommit.completed({"card": input["card"]})
            ),
        )

    async def undo(input, checkpoint, runtime: TaskRuntime):
        def commit():
            aborted.add(input["card"])
            charged.discard(input["card"])
            return TaskCommit.aborted()

        return await runtime.commit_async(commit)

    payment = DurableTask.define_phases(
        "example.payment",
        lambda: {"phase": "charge"},
        {"charge": charge, "confirm": confirm},
        undo,
    )

    async def pay(input, checkpoint, runtime: TaskRuntime):
        async def commit(tx: Transaction):
            # Absolute deadlines preserve the reference ordering across scheduler delays.
            answer_at = runtime.now + 1.0
            children = []
            for card in input["cards"].split(","):
                child = runtime.create_child_task(
                    tx, payment, {"card": card, "answerAt": str(answer_at)}
                )
                children.append(child.id)
                answer_at += 1.0
            for child_id in children:
                runtime.schedule_task(child_id)
            return TaskCommit.waiting(
                {"phase": "decide", "payments": children}, children
            )

        return await runtime.commit_async(commit)

    async def decide(input, checkpoint, runtime: TaskRuntime):
        async def commit(tx: Transaction):
            outcomes = await runtime.outcomes_async(checkpoint["payments"])
            print("  payments:", ", ".join(outcome.status for outcome in outcomes))
            return (
                TaskCommit.completed("order placed")
                if all(outcome.is_completed for outcome in outcomes)
                else TaskCommit.failed("payment failed")
            )

        return await runtime.commit_async(commit)

    async def cancel(input, checkpoint, runtime: TaskRuntime):
        print("  checkout aborted")
        return await runtime.commit_async(TaskCommit.aborted)

    checkout = DurableTask.define_phases(
        "example.checkout",
        lambda: {"phase": "pay"},
        {"pay": pay, "decide": decide},
        cancel,
    )
    registry = ExtensionRegistry()
    registry.install(Extension("checkout", tasks=[payment, checkout]))

    with DurableStore.create_ephemeral() as store:
        harness = await DurableHarness.open_async(store, registry)
        try:
            root = await harness.root_async()
            effective = await root.wait_for_effective_model_async()
            print(f"effective model: {effective.model or '(not reported)'}")

            print("One card is declined:")
            declined = await harness.create_task_async(
                checkout,
                {"cards": "visa-1,expired-2,visa-3,visa-4"},
                owner_conversation_id=root.id,
            )
            declined_outcome = await harness.wait_for_task_async(declined.id)
            declined_aborted = sorted(aborted)
            print(f"  checkout: {declined_outcome.status}")
            print("  aborted payments:", ", ".join(declined_aborted))

            print("The customer cancels:")
            cancelled = await harness.create_task_async(
                checkout,
                {"cards": "visa-5,visa-6,visa-7,visa-8"},
                owner_conversation_id=root.id,
            )
            deadline = asyncio.get_running_loop().time() + 30
            while True:
                children = [
                    task for task in store.tasks if task.owner_task_id == cancelled.id
                ]
                if (
                    cancelled.status == TaskStatus.WAITING
                    and len(children) == 4
                    and all(
                        task.status == TaskStatus.RUNNING
                        and isinstance(task.checkpoint, dict)
                        and task.checkpoint.get("phase") == "confirm"
                        for task in children
                    )
                ):
                    break
                if asyncio.get_running_loop().time() > deadline:
                    raise TimeoutError("the payments never reached the bank")
                await asyncio.sleep(0.005)

            def print_graph():
                nodes = list(store.tasks)

                def print_task(task, depth):
                    status = (
                        f"waiting on {', '.join(task.waiting_on)}"
                        if task.status == TaskStatus.WAITING else task.status.name.title()
                    )
                    print(f"  {'  ' * depth}{task.name} {task.id}: {status}")
                    for child in nodes:
                        if child.owner_task_id == task.id:
                            print_task(child, depth + 1)

                for task in nodes:
                    if task.owner_task_id is None:
                        print_task(task, 0)

            print("Task graph:")
            print_graph()
            await harness.abort_task_async(cancelled.id)
            cancelled_outcome = await harness.wait_for_task_async(cancelled.id)
            cancelled_aborted = sorted(aborted)
            print(f"  checkout: {cancelled_outcome.status}")
            print("  aborted payments:", ", ".join(cancelled_aborted))
            print("Task graph after the abort:")
            print_graph()

            declined_children = [
                task for task in store.tasks if task.owner_task_id == declined.id
            ]
            Check.equal(4, len(declined_children), "the checkout owns four payment children")
            Check.that(
                all(task.name == "example.payment" for task in declined_children),
                "the children are payments",
            )
            Check.equal(
                root.id, declined.owner_conversation_id,
                "the checkout is owned by the conversation",
            )
            Check.equal(
                "failed", declined_outcome.status, "a declined payment fails the checkout"
            )
            Check.that(
                declined_outcome.error is not None, "the failed checkout carries an error"
            )
            Check.that("expired-2" not in charged, "the declined card was never charged")
            Check.that("visa-1" in charged, "the other cards were charged")
            Check.sequence(
                ["visa-3", "visa-4"], declined_aborted,
                "the declined card aborts the siblings still waiting at the bank",
            )
            Check.equal(
                "aborted", cancelled_outcome.status, "the cancelled checkout is aborted"
            )
            Check.that("visa-5" not in charged, "an aborted checkout undoes its effects")
            Check.that("visa-6" not in charged, "every aborted payment undoes its effect")
            Check.equal(4, len(children), "the cancelled checkout still owns its four children")
            Check.that(
                all(task.status == TaskStatus.TERMINAL for task in children),
                "every child of an aborted checkout is terminal",
            )
            Check.that(
                all(task.outcome is not None and task.outcome.status == "aborted"
                    for task in children),
                "every child of an aborted checkout is aborted, not completed",
            )
            Check.sequence(
                ["visa-3", "visa-4", "visa-5", "visa-6", "visa-7", "visa-8"],
                cancelled_aborted, "both fan-outs aborted every payment they reached",
            )
        finally:
            await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
