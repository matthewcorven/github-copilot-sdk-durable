import asyncio
import os
import tempfile
import unittest
import json
import subprocess
from unittest.mock import patch, AsyncMock
from types import SimpleNamespace
from pathlib import Path
from dataclasses import dataclass, field
from typing import Union

from durable import (
    AgentChoices, DurableHarness, DurableStore, DurableStoreOptions, ExtensionRegistry,
    DurableSandboxPolicy, DurableSandboxFilesystem, DurableSandboxCredentials,
    DurableSandboxNetwork, DurableSandboxAuth, PermissionPolicy,
    DurableTask, TaskCommit, TaskStatus, Extension, ToolWrapper, DurableHarnessOptions,
    DurableClientBuilder,
    DurableConversation,
    DocumentDefinition, DocumentHistory, ForkPolicy, PromptSection, ForkIntent, ForkIntentState, FORK_DOCUMENT,
    DurableRecoveryRequiredException,
)
from copilot.tools import Tool, ToolResult

REPLAY_FIXTURES = Path(__file__).resolve().parents[2] / "tools" / "ReplayRuntime" / "fixtures"

def sdk_session(session_id="native", history=None):
    listeners = []
    def subscribe(handler):
        listeners.append(handler)
        return lambda: listeners.remove(handler)
    def emit(event):
        for listener in list(listeners):
            listener(event)
    return SimpleNamespace(
        session_id=session_id, on=subscribe, emit=emit, listeners=listeners,
        get_events=AsyncMock(return_value=history or []), send=AsyncMock(return_value="sent"),
        set_tools=AsyncMock(),
    )


def sdk_client(session):
    return SimpleNamespace(
        stop=AsyncMock(), create_session=AsyncMock(return_value=session),
        resume_session=AsyncMock(return_value=session),
        get_session_metadata=AsyncMock(return_value=SimpleNamespace(session_id=session.session_id)),
        rpc=SimpleNamespace(sessions=SimpleNamespace(fork=AsyncMock(
            return_value=SimpleNamespace(session_id="child")))),
    )


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_initiating_run_cancellation_settles_and_reopens_checkpoint_and_memo(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            registry = ExtensionRegistry()
            harness = DurableHarness(sdk_client(sdk_session()), store, registry)
            entered, settling, settled, finish_settlement = (asyncio.Event() for _ in range(4))
            interrupted_phase = asyncio.Event()
            interrupt = True
            attempts, effects = [], []

            async def begin(_input, _checkpoint, runtime):
                return await runtime.commit_async(lambda: TaskCommit.running({"phase": "effect", "n": 2}))

            async def effect(_input, checkpoint, runtime):
                attempts.append(checkpoint["n"])
                if await runtime.memo_async("printed-2") is None:
                    await runtime.memo_async("printed-2", True)
                    effects.append(checkpoint["n"])
                if interrupt:
                    entered.set()
                    try:
                        await interrupted_phase.wait()
                    finally:
                        settling.set()
                        await finish_settlement.wait()
                        settled.set()
                return await runtime.commit_async(lambda: TaskCommit.completed("resumed"))

            definition = DurableTask.define_phases(
                "owner-cancellation", lambda: {"phase": "begin"}, {"begin": begin, "effect": effect})
            registry.install(Extension("owner-cancellation", tasks=[definition]))
            async def create(tx):
                return tx.create_task(definition)
            record = await store.commit_async(create)
            running = asyncio.create_task(harness.run_task_async(record.id))
            try:
                await asyncio.wait_for(entered.wait(), 1)
                core = harness._in_flight[record.id]
                running.cancel("initiating caller interrupted")
                settlement = asyncio.create_task(settling.wait())
                try:
                    await asyncio.wait((running, settlement), return_when=asyncio.FIRST_COMPLETED, timeout=1)
                    self.assertTrue(settling.is_set(), "owner cancellation must reach the active phase")
                finally:
                    settlement.cancel()
                    await asyncio.gather(settlement, return_exceptions=True)
                self.assertFalse(running.done(), "the owner must await phase settlement")
                self.assertIs(harness._in_flight[record.id], core)
                finish_settlement.set()
                outcome = await asyncio.wait_for(asyncio.shield(running), 1)
                self.assertEqual((outcome.status, outcome.error), ("failed", "task did not finish"))
                self.assertTrue(settled.is_set())
                self.assertTrue(core.done())
                self.assertNotIn(record.id, harness._in_flight)
                self.assertNotIn(record.id, harness._running_tasks)
                self.assertFalse(interrupted_phase.is_set())
                self.assertIsNone(record.outcome, "interruption must not commit a terminal outcome")
                self.assertIs(record.status, TaskStatus.RUNNING)
                self.assertEqual(record.checkpoint, {"phase": "effect", "n": 2})
                self.assertEqual(record.memos, {"printed-2": True})
                harness.close()
            finally:
                finish_settlement.set()
                for task in list(harness._in_flight.values()):
                    task.cancel()
                await asyncio.gather(running, *list(harness._in_flight.values()), return_exceptions=True)
                await harness.close_async()
                store.close()

            interrupt = False
            store = DurableStore.create(options)
            harness = DurableHarness(sdk_client(sdk_session()), store, registry)
            try:
                saved = store.task(record.id)
                assert saved is not None
                self.assertEqual(saved.checkpoint, {"phase": "effect", "n": 2})
                self.assertEqual(saved.memos, {"printed-2": True})
                self.assertIsNone(saved.outcome)
                self.assertEqual(attempts, [2], "opening the store alone must not run a task")
                outcome = await asyncio.wait_for(harness.run_task_async(record.id), 1)
                self.assertEqual((outcome.status, outcome.result), ("completed", "resumed"))
                self.assertEqual(attempts, [2, 2])
                self.assertEqual(effects, [2], "the interrupted effect's memo must survive reopening")
                self.assertIs(saved.status, TaskStatus.TERMINAL)
                self.assertEqual(saved.restarts, 1)
            finally:
                await harness.close_async()
                store.close()

    async def test_duplicate_run_cancellation_does_not_cancel_initiating_or_scheduled_owner(self):
        for scheduled in (False, True):
            with self.subTest(scheduled=scheduled):
                store = DurableStore.create_ephemeral()
                registry = ExtensionRegistry()
                harness = DurableHarness(sdk_client(sdk_session()), store, registry)
                entered, release, joined = (asyncio.Event() for _ in range(3))
                calls, cancellations = [], []
                async def phase(_input, _checkpoint, runtime):
                    calls.append("entered")
                    entered.set()
                    try:
                        await release.wait()
                    except asyncio.CancelledError:
                        cancellations.append("cancelled")
                        raise
                    return await runtime.commit_async(lambda: TaskCommit.completed("owner completed"))
                definition = DurableTask.define("duplicate-cancellation", lambda: {"phase": "run"}, phase)
                registry.install(Extension("duplicate-cancellation", tasks=[definition]))
                async def create(tx):
                    return tx.create_task(definition)
                if scheduled:
                    record = await harness.create_task_async(definition)
                    owner = harness._in_flight[record.id]
                else:
                    record = await store.commit_async(create)
                    owner = asyncio.create_task(harness.run_task_async(record.id))
                async def duplicate():
                    joined.set()
                    return await harness.run_task_async(record.id)
                waiter = None
                try:
                    await asyncio.wait_for(entered.wait(), 1)
                    core = harness._in_flight[record.id]
                    waiter = asyncio.create_task(duplicate())
                    await joined.wait()
                    waiter.cancel("duplicate caller interrupted")
                    with self.assertRaisesRegex(asyncio.CancelledError, "duplicate caller interrupted"):
                        await waiter
                    self.assertFalse(core.done())
                    self.assertFalse(owner.done())
                    self.assertEqual(cancellations, [])
                    self.assertEqual(calls, ["entered"])
                    release.set()
                    outcome = await asyncio.wait_for(asyncio.shield(owner), 1)
                    self.assertEqual(outcome.result, "owner completed")
                    self.assertIs(record.status, TaskStatus.TERMINAL)
                    self.assertNotIn(record.id, harness._in_flight)
                finally:
                    release.set()
                    await asyncio.gather(owner, *([waiter] if waiter else []), return_exceptions=True)
                    await harness.close_async()
                    store.close()

    async def test_owner_cancellation_preserves_tool_checkpoint_and_cancelled_shared_close(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            session = sdk_session()
            client = sdk_client(session)
            registry = ExtensionRegistry()
            harness = DurableHarness(client, store, registry)
            chat = await harness.create_conversation_async()
            barriers = asyncio.Queue()
            barrier_exited = asyncio.Event()
            original_barrier = chat.checkpoint_barrier_async
            async def barrier():
                barriers.put_nowait("entered")
                try:
                    await original_barrier()
                finally:
                    barrier_exited.set()
            chat.checkpoint_barrier_async = barrier
            async def phase(_input, _checkpoint, runtime):
                await chat.checkpoint_barrier_async()
                return await runtime.commit_async(lambda: TaskCommit.completed("unexpected"))
            definition = DurableTask.define("checkpoint-cancellation", lambda: {"phase": "run"}, phase)
            registry.install(Extension("checkpoint-cancellation", tasks=[definition]))
            async def create(tx):
                return tx.create_task(definition)
            record = await store.commit_async(create)
            await store._gate.acquire()
            session.emit({"type": "tool.execution_complete", "id": "shared-complete",
                          "data": {"toolCallId": "shared", "success": True, "result": {"content": "received"}}})
            running = asyncio.create_task(harness.run_task_async(record.id))
            first = second = None
            try:
                await asyncio.wait_for(barriers.get(), 1)
                chain = chat._checkpoint_chain
                assert chain is not None
                running.cancel("owner interrupted during checkpoint")
                await asyncio.wait_for(barrier_exited.wait(), 1)
                self.assertFalse(chain.done(), "owner cancellation must not cancel the shared checkpoint")
                outcome = await asyncio.wait_for(asyncio.shield(running), 1)
                self.assertEqual((outcome.status, outcome.error), ("failed", "task did not finish"))
                self.assertIsNone(record.outcome)
                self.assertNotIn(record.id, harness._in_flight)
                self.assertFalse(chain.done(), "owner cancellation must not cancel the shared checkpoint")
                first = asyncio.create_task(harness.close_async())
                await asyncio.wait_for(barriers.get(), 1)
                second_entered = asyncio.Event()
                async def second_close():
                    second_entered.set()
                    await harness.close_async()
                second = asyncio.create_task(second_close())
                await second_entered.wait()
                first.cancel("closer interrupted")
                turn = asyncio.get_running_loop().create_future()
                asyncio.get_running_loop().call_soon(turn.set_result, None)
                await turn
                self.assertFalse(first.done(), "cancelled close must still await checkpoint settlement")
                self.assertFalse(second.done(), "every closer must share the drain")
                self.assertFalse(chain.done())
                client.stop.assert_not_awaited()
                store._gate.release()
                with self.assertRaisesRegex(asyncio.CancelledError, "closer interrupted"):
                    await asyncio.wait_for(first, 1)
                await asyncio.wait_for(asyncio.shield(second), 1)
                self.assertFalse(chain.cancelled())
                checkpoint = chat.checkpoint()
                assert checkpoint is not None
                self.assertEqual((checkpoint.event_id, checkpoint.content), ("tool:shared", "received"))
                client.stop.assert_awaited_once()
            finally:
                if store._gate.locked():
                    store._gate.release()
                for task in list(harness._in_flight.values()):
                    task.cancel()
                await asyncio.gather(running, *list(harness._in_flight.values()),
                                     *([first] if first else []), *([second] if second else []),
                                     return_exceptions=True)
                await asyncio.gather(harness.close_async(), return_exceptions=True)
                store.close()
            reopened = DurableStore.create(options)
            try:
                saved = reopened.conversation(chat.id)
                assert saved is not None
                checkpoint = DurableConversation(reopened, saved).checkpoint()
                assert checkpoint is not None
                self.assertEqual((checkpoint.event_id, checkpoint.content), ("tool:shared", "received"))
            finally:
                reopened.close()

    async def test_direct_awaited_run_does_not_make_shutdown_wait_for_host_task(self):
        store = DurableStore.create_ephemeral()
        client = sdk_client(sdk_session())
        harness = DurableHarness(client, store, ExtensionRegistry())
        entered, release, host_release = asyncio.Event(), asyncio.Event(), asyncio.Event()
        async def phase(_input, _checkpoint, runtime):
            entered.set()
            await release.wait()
            return await runtime.commit_async(lambda: TaskCommit.completed("saved"))
        definition = DurableTask.define("direct-host", lambda: {"phase": "run"}, phase)
        harness.registry.install(Extension("direct-host", tasks=[definition]))
        async def create(tx):
            return tx.create_task(definition)
        record = await store.commit_async(create)
        closing = None
        async def host():
            outcome = await harness.run_task_async(record.id)
            await host_release.wait()
            return outcome
        running = asyncio.create_task(host())
        try:
            await entered.wait()
            self.assertIsNot(harness._in_flight[record.id], running, "only the dedicated core run may be drained")
            closing = asyncio.create_task(harness.close_async())
            release.set()
            await asyncio.wait_for(asyncio.shield(closing), 1)
            host_release.set()
            outcome = await asyncio.wait_for(asyncio.shield(running), 1)
            self.assertEqual(outcome.result, "saved")
            client.stop.assert_awaited_once()
        finally:
            release.set()
            host_release.set()
            await asyncio.gather(running, *([closing] if closing else []), return_exceptions=True)
            store.close()

    async def test_facade_stored_choices_override_argument_and_defaults(self):
        store = DurableStore.create_ephemeral()
        registry = ExtensionRegistry()
        registry.install(Extension("tools", tools=[Tool(
            name="danger", description="danger", handler=AsyncMock(),
            parameters={"type": "object"},
        )]))
        host = DurableClientBuilder().use_store(store).use_client(sdk_client(sdk_session())).use_registry(registry).build()
        try:
            chat = await host.create_conversation_async(AgentChoices(
                model="stored-model", tools=[], permissions=[],
                sandbox=DurableSandboxPolicy(enabled=True, allow_bypass=False)))
            for fallback in (None, AgentChoices(model="host-model", permissions=["read:*"],
                                               sandbox=DurableSandboxPolicy(enabled=False))):
                config = await host.get_session_config_async(fallback, chat)
                self.assertEqual(config["model"], "stored-model")
                self.assertEqual(config["tools"], [])
                self.assertIs(config["sandbox_config"].to_dict()["enabled"], True)
                self.assertIs(config["sandbox_config"].to_dict()["allowBypass"], False)
                decision = await config["on_permission_request"](SimpleNamespace(kind="read", path="/private"), {})
                self.assertEqual(decision.kind, "reject")
            async def create(tx):
                return tx.create_conversation()
            empty = host.find_conversation((await store.commit_async(create)).id)
            fallback = await host.get_session_config_async(AgentChoices(model="fallback", tools=[]), empty)
            self.assertEqual(fallback["model"], "fallback")
        finally:
            await host.close_async()
            store.close()

    async def test_dataclass_containers_are_not_treated_as_union_alternatives(self):
        @dataclass
        class Child:
            name: str = "default"
        @dataclass
        class Container:
            children: dict[str, Child] = field(default_factory=dict)
            optional: Child | None = None
        definition = DocumentDefinition("typed-container", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, type=Container)
        store = DurableStore.create_ephemeral()
        harness = DurableHarness(sdk_client(sdk_session()), store, ExtensionRegistry())
        try:
            async def create(tx):
                record = tx.create_conversation()
                draft = tx.doc(definition, record.id)
                draft.value = Container({"kept": Child("named")}, Child("optional"))
                draft.save(tx.append_entry(record.id, "typed"))
                return record
            record = await store.commit_async(create)
            chat = harness.find_conversation(record.id)
            assert chat is not None
            value = chat.snapshot(definition)
            assert value is not None
            self.assertEqual(value.children, {"kept": {"name": "named"}})
            self.assertEqual(value.optional, Child("optional"))
            # The definition's deserializer is also used directly by callers.
            direct = definition.deserialize({"children": {"other": {"name": "second"}}})
            assert direct is not None
            self.assertEqual(direct.children, {"other": {"name": "second"}})
            @dataclass
            class RequiredChild:
                name: str
            @dataclass
            class RequiredContainer:
                children: dict[str, RequiredChild]
                optional: Union[RequiredChild, None] = None
            required = DocumentDefinition("required-container", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF,
                                          type=RequiredContainer).deserialize({
                                              "children": {"kept": {"name": "required"}},
                                              "optional": {"name": "union"},
                                          })
            assert required is not None
            self.assertEqual(required.children, {"kept": {"name": "required"}})
            self.assertEqual(required.optional, RequiredChild("union"))
        finally:
            await harness.close_async()
            store.close()

    async def test_submit_cancellation_does_not_cancel_received_tool_checkpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            session = sdk_session()
            harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
            chat = await harness.create_conversation_async()
            barrier_entered = asyncio.Event()
            original_barrier = chat.checkpoint_barrier_async
            async def checkpoint_barrier():
                barrier_entered.set()
                await original_barrier()
            chat.checkpoint_barrier_async = checkpoint_barrier
            async def send(_prompt):
                await store._gate.acquire()
                session.emit({"type": "tool.execution_start", "id": "start",
                              "data": {"toolCallId": "held", "toolName": "read", "arguments": {}}})
                session.emit({"type": "tool.execution_complete", "id": "complete",
                              "data": {"toolCallId": "held", "success": True, "result": {"content": "received"}}})
                session.emit({"type": "session.idle", "id": "idle", "data": {}})
            session.send.side_effect = send
            submitting = asyncio.create_task(chat.submit_async("read"))
            try:
                await barrier_entered.wait()
                submitting.cancel("caller cancelled")
                # The uncertain-admission settlement now waits behind the held store gate.
                turn = asyncio.get_running_loop().create_future()
                asyncio.get_running_loop().call_soon(turn.set_result, None)
                await turn
                store._gate.release()
                with self.assertRaisesRegex(asyncio.CancelledError, "caller cancelled"):
                    await submitting
                await original_barrier()
                checkpoint = chat.checkpoint()
                assert checkpoint is not None
                self.assertEqual((checkpoint.event_id, checkpoint.content), ("tool:held", "received"))
                identifier = chat.id
                await harness.close_async()
                store.close()
                reopened = DurableStore.create(options)
                try:
                    record = reopened.conversation(identifier)
                    assert record is not None
                    check = DurableConversation(reopened, record).checkpoint()
                    assert check is not None
                    self.assertEqual(check.content, "received")
                finally:
                    reopened.close()
            finally:
                if store._gate.locked():
                    store._gate.release()
                await asyncio.gather(submitting, return_exceptions=True)
                await harness.close_async()
                store.close()

    async def test_active_phase_keeps_session_until_final_checkpoint_and_all_closers_finish(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                session = sdk_session()
                client = sdk_client(session)
                registry = ExtensionRegistry()
                entered, release, checkpoint_entered, checkpoint_release = (asyncio.Event() for _ in range(4))
                gate_entered, stopped = asyncio.Event(), asyncio.Event()
                held = None
                async def stop():
                    stopped.set()
                client.stop.side_effect = stop
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store_options(options).use_client(client).use_registry(registry)
                            .configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                    assert store is not None and harness is not None
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, registry)
                chat = await harness.create_conversation_async()
                async def send(_prompt):
                    session.emit({"type": "tool.execution_complete", "id": "phase-tool",
                                  "data": {"toolCallId": "phase", "success": True, "result": {"content": "submitted"}}})
                    session.emit({"type": "session.idle", "id": "idle", "data": {}})
                session.send.side_effect = send
                async def phase(_input, _checkpoint, runtime):
                    nonlocal held
                    entered.set()
                    await release.wait()
                    await chat.context_async()
                    await chat.submit_async("during shutdown")
                    commit = await runtime.commit_async(lambda: TaskCommit.completed("phase saved"))
                    async def hold(tx):
                        gate_entered.set()
                        await checkpoint_release.wait()
                    assert store is not None
                    held = asyncio.create_task(store.commit_async(hold))
                    await gate_entered.wait()
                    session.emit({"type": "tool.execution_complete", "id": "late-tool",
                                  "data": {"toolCallId": "late", "success": True, "result": {"content": "final"}}})
                    return commit
                definition = DurableTask.define("active-session", lambda: {"phase": "run"}, phase)
                registry.install(Extension("active-session", tasks=[definition]))
                async def create(tx):
                    return tx.create_task(definition)
                task = await store.commit_async(create)
                running = asyncio.create_task(harness.run_task_async(task.id))
                await entered.wait()
                barrier = chat.checkpoint_barrier_async
                async def final_barrier():
                    if chat.session is None:
                        checkpoint_entered.set()
                    await barrier()
                chat.checkpoint_barrier_async = final_barrier
                close = host.close_async if host else harness.close_async
                first = asyncio.create_task(close())
                second = asyncio.create_task(close())
                try:
                    release.set()
                    result = await running
                    self.assertEqual(result.status, "completed")
                    reached = asyncio.create_task(checkpoint_entered.wait())
                    stopped_wait = asyncio.create_task(stopped.wait())
                    try:
                        await asyncio.wait((reached, stopped_wait), return_when=asyncio.FIRST_COMPLETED, timeout=1)
                        self.assertTrue(checkpoint_entered.is_set())
                        self.assertFalse(stopped.is_set())
                    finally:
                        reached.cancel()
                        stopped_wait.cancel()
                        await asyncio.gather(reached, stopped_wait, return_exceptions=True)
                    checkpoint = chat.checkpoint()
                    assert checkpoint is not None
                    self.assertEqual(checkpoint.content, "submitted")
                    self.assertFalse(first.done())
                    self.assertFalse(second.done())
                    client.stop.assert_not_awaited()
                    with self.assertRaises((OSError, RuntimeError)):
                        DurableStore.create(options)
                    with self.assertRaisesRegex(RuntimeError, "scheduler|runner"):
                        DurableHarness(client, store, registry)
                    with self.assertRaisesRegex(RuntimeError, "closing"):
                        await harness.create_task_async(definition)
                    with self.assertRaisesRegex(RuntimeError, "closing"):
                        await harness.create_conversation_async()
                    checkpoint_release.set()
                    await asyncio.gather(first, second, *([held] if held else []))
                    client.stop.assert_awaited_once()
                    if not facade:
                        store.close()
                    reopened = DurableStore.create(options)
                    try:
                        value = reopened.document("durable.checkpoint", chat.id)
                        assert value is not None
                        self.assertEqual(value.latest["content"], "final")
                    finally:
                        reopened.close()
                finally:
                    release.set()
                    checkpoint_release.set()
                    await asyncio.gather(running, first, second, *([held] if held else []), return_exceptions=True)
                    store.close()

    async def test_every_concurrent_close_waits_for_checkpoint_and_stop(self):
        for facade in (False, True):
            with self.subTest(facade=facade):
                store = DurableStore.create_ephemeral()
                client = sdk_client(sdk_session())
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store(store).use_client(client)
                            .configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    harness = host._harness
                    assert harness is not None
                else:
                    harness = DurableHarness(client, store, ExtensionRegistry())
                chat = await harness.create_conversation_async()
                entered, release = asyncio.Event(), asyncio.Event()
                async def barrier():
                    entered.set()
                    await release.wait()
                chat.checkpoint_barrier_async = barrier
                close = host.close_async if host else harness.close_async
                first = asyncio.create_task(close())
                await entered.wait()
                second = asyncio.create_task(close())
                turn = asyncio.get_running_loop().create_future()
                asyncio.get_running_loop().call_soon(turn.set_result, None)
                await turn
                try:
                    self.assertFalse(second.done(), "a second closer must join the entire shutdown")
                finally:
                    release.set()
                    await asyncio.gather(first, second)
                    store.close()

    async def test_repeated_checkpoint_drains_preserve_one_original_storage_failure(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        original = RuntimeError("checkpoint write failed")
        try:
            await harness.create_conversation_async()
            with patch.object(store, "commit_async", side_effect=original):
                session.emit({"type": "tool.execution_complete", "id": "failed-tool",
                              "data": {"toolCallId": "failed", "success": True}})
                with self.assertRaises(RuntimeError) as raised:
                    await harness.close_async()
                self.assertIs(raised.exception.__cause__, original)
        finally:
            store.close()

    async def test_missing_pinned_session_recreates_same_id_after_actual_reopen(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            client = sdk_client(sdk_session())
            client.create_session.side_effect = RuntimeError("native create interrupted")
            harness = DurableHarness(client, store, ExtensionRegistry())
            try:
                with self.assertRaisesRegex(RuntimeError, "native create interrupted"):
                    await harness.create_conversation_async()
                identifier = store.conversations[0].id
                chat = harness.find_conversation(identifier)
                assert chat is not None
                binding = chat.binding()
                assert binding is not None
                pinned = binding.session_id
                assert pinned is not None
            finally:
                await harness.close_async()
                store.close()
            store = DurableStore.create(options)
            client = sdk_client(sdk_session(pinned))
            client.get_session_metadata.return_value = None
            harness = DurableHarness(client, store, ExtensionRegistry())
            try:
                chat = await harness.resume_conversation_async(identifier)
                self.assertEqual(chat.session_id, pinned)
                client.get_session_metadata.assert_awaited_once_with(pinned)
                self.assertEqual(client.create_session.call_args.kwargs["session_id"], pinned)
                client.resume_session.assert_not_awaited()
            finally:
                await harness.close_async()
                store.close()

    async def test_official_sdk_transport_missing_session_guards_and_probe_errors(self):
        from copilot._jsonrpc import JsonRpcClient
        from durable import SESSION_BINDING_DOCUMENT, ADMISSION_DOCUMENT, SessionBinding, InputAdmission
        store = DurableStore.create_ephemeral()
        harness = await DurableHarness.open_async(store, ExtensionRegistry())
        original = JsonRpcClient.request
        calls = []
        present, probe_error, wrong_id = False, False, False
        async def request(client, method, params=None, **kwargs):
            calls.append((method, params))
            if method == "session.getMetadata":
                if probe_error:
                    raise RuntimeError("metadata unavailable")
                if not present:
                    return {"session": None}
            result = await original(client, method, params, **kwargs)
            if method == "session.create" and wrong_id:
                return {**result, "sessionId": "unexpected-id"}
            return result
        try:
            with patch.object(JsonRpcClient, "request", request):
                for shape in ("pinned", "allocated", "fork", "unsettled", "probe-error", "wrong-id", "existing"):
                    with self.subTest(shape=shape):
                        async def create(tx):
                            record = tx.create_conversation()
                            draft = tx.doc(SESSION_BINDING_DOCUMENT, record.id)
                            draft.value = SessionBinding(session_id=f"native-{shape}", pinned=shape != "allocated")
                            draft.save(tx.append_entry(record.id, "session-binding"))
                            if shape == "fork":
                                intent = tx.doc(FORK_DOCUMENT, record.id)
                                intent.value = ForkIntent(session_id=f"native-{shape}", state=ForkIntentState.COMPLETED)
                                intent.save(tx.append_entry(record.id, "fork"))
                            if shape == "unsettled":
                                admission = tx.doc(ADMISSION_DOCUMENT, record.id)
                                admission.value = InputAdmission(state="uncertain")
                                admission.save(tx.append_entry(record.id, "admission"))
                            return record.id
                        identifier = await store.commit_async(create)
                        present, probe_error, wrong_id = shape == "existing", shape == "probe-error", shape == "wrong-id"
                        if present:
                            await harness.client.create_session(session_id="native-existing")
                        calls.clear()
                        if shape in ("pinned", "existing"):
                            chat = await harness.resume_conversation_async(identifier)
                            self.assertEqual(chat.session_id, f"native-{shape}")
                            self.assertEqual([method for method, _ in calls], [
                                "session.getMetadata", "session.resume" if present else "session.create", "session.getMessages"])
                            if not present:
                                self.assertEqual(calls[1][1]["sessionId"], "native-pinned")
                        else:
                            expected = (self.assertRaisesRegex(RuntimeError, "metadata unavailable") if probe_error
                                        else self.assertRaisesRegex(RuntimeError, "session.create returned sessionId unexpected-id but the caller requested native-wrong-id")
                                        if shape == "wrong-id" else self.assertRaises(DurableRecoveryRequiredException))
                            with expected:
                                await harness.resume_conversation_async(identifier)
                            if shape != "wrong-id":
                                self.assertNotIn("session.create", [method for method, _ in calls])
                            if shape == "unsettled":
                                self.assertEqual(calls, [])
        finally:
            await harness.close_async()
            store.close()

    async def test_failing_recovered_task_does_not_skip_other_run_drain(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                entered = {name: asyncio.Event() for name in ("bad", "good")}
                release = {name: asyncio.Event() for name in ("bad", "good")}
                async def phase(input, _checkpoint, runtime):
                    entered[input].set()
                    await release[input].wait()
                    return await runtime.commit_async(lambda: TaskCommit.completed(input))
                definition = DurableTask.define("task-errors", lambda: {"phase": "run"}, phase)
                registry = ExtensionRegistry()
                registry.install(Extension("task-errors", tasks=[definition]))
                client = sdk_client(sdk_session())
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store_options(options).use_client(client)
                            .use_registry(registry).configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                    assert store is not None and harness is not None
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, registry)
                async def create(tx):
                    return [tx.create_task(definition, "bad"), tx.create_task(definition, "good")]
                bad, good = await store.commit_async(create)
                commit = store.commit_task_async
                original = RuntimeError("task checkpoint storage failed")
                async def fail_one(identifier, body):
                    if identifier == bad.id:
                        raise original
                    return await commit(identifier, body)
                recovery = None
                closing = None
                with patch.object(store, "commit_task_async", side_effect=fail_one):
                    try:
                        recovery = asyncio.create_task(harness.resume_async())
                        await asyncio.gather(*(event.wait() for event in entered.values()))
                        closing = asyncio.create_task(host.close_async() if host else harness.close_async())
                        await asyncio.sleep(0)
                        release["bad"].set()
                        with self.assertRaises(RuntimeError) as raised:
                            await recovery
                        self.assertIs(raised.exception, original)
                        await asyncio.sleep(0)
                        await asyncio.sleep(0)
                        client.stop.assert_not_awaited()
                        self.assertFalse(closing.done())
                        with self.assertRaisesRegex(RuntimeError, "scheduler|runner"):
                            DurableHarness(client, store, registry)
                        with self.assertRaises((OSError, RuntimeError)):
                            DurableStore.create(options)
                        release["good"].set()
                        with self.assertRaises(RuntimeError) as raised:
                            await closing
                        self.assertIs(raised.exception, original)
                        client.stop.assert_awaited_once()
                        if not facade:
                            store.close()
                        reopened = DurableStore.create(options)
                        try:
                            result = reopened.task(good.id)
                            assert result is not None
                            self.assertIs(result.status, TaskStatus.TERMINAL)
                            result = reopened.task(bad.id)
                            assert result is not None
                            self.assertIs(result.status, TaskStatus.PENDING)
                        finally:
                            reopened.close()
                    finally:
                        for event in release.values():
                            event.set()
                        await asyncio.gather(*([recovery] if recovery else []),
                                             *([closing] if closing else []), return_exceptions=True)
                        await asyncio.gather(*list(harness._in_flight.values()), return_exceptions=True)
                        store.close()

    async def test_cancelled_close_waits_for_checkpoint_and_surfaces_cancellation(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                client = sdk_client(sdk_session())
                registry = ExtensionRegistry()
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store_options(options).use_client(client)
                            .use_registry(registry).configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                    assert store is not None and harness is not None
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, registry)
                async def create(tx):
                    return tx.create_conversation()
                record = await store.commit_async(create)
                chat = harness.find_conversation(record.id)
                assert chat is not None
                entered, release, completed = asyncio.Event(), asyncio.Event(), asyncio.Event()
                async def checkpoint():
                    entered.set()
                    await release.wait()
                    completed.set()
                chat.checkpoint_barrier_async = checkpoint
                closing = asyncio.create_task(host.close_async() if host else harness.close_async())
                try:
                    await entered.wait()
                    closing.cancel("closer cancelled")
                    await asyncio.sleep(0)
                    await asyncio.sleep(0)
                    self.assertFalse(closing.done())
                    self.assertFalse(completed.is_set())
                    client.stop.assert_not_awaited()
                    with self.assertRaises((OSError, RuntimeError)):
                        DurableStore.create(options)
                    release.set()
                    with self.assertRaisesRegex(asyncio.CancelledError, "closer cancelled"):
                        await closing
                    self.assertTrue(completed.is_set())
                    client.stop.assert_awaited_once()
                finally:
                    release.set()
                    await asyncio.gather(closing, return_exceptions=True)
                    store.close()

    async def test_close_preserves_first_error_and_reports_later_checkpoint_and_stop_errors(self):
        store = DurableStore.create_ephemeral()
        client = sdk_client(sdk_session())
        harness = DurableHarness(client, store, ExtensionRegistry())
        first, second, stop = RuntimeError("first"), RuntimeError("second"), RuntimeError("stop")
        client.stop.side_effect = stop
        try:
            async def create(tx):
                return [tx.create_conversation(), tx.create_conversation()]
            records = await store.commit_async(create)
            for record, error in zip(records, (first, second)):
                conversation = harness.find_conversation(record.id)
                assert conversation is not None
                conversation.checkpoint_barrier_async = AsyncMock(side_effect=error)
            with self.assertRaises(RuntimeError) as raised:
                await harness.close_async()
            self.assertIs(raised.exception, first)
            cause = first.__cause__
            assert isinstance(cause, BaseExceptionGroup)
            self.assertEqual(list(cause.exceptions), [second, stop])
        finally:
            store.close()

    async def test_canonical_jsonl_history_and_fork_states_roundtrip(self):
        fixture = Path(__file__).resolve().parents[1] / "ts" / "canonical-fixture.cs"
        def canonical(mode, path):
            output = subprocess.check_output(
                ["dotnet", "run", "--file", str(fixture), "--", mode, str(path)], text=True)
            return json.loads(output.strip().splitlines()[-1])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "store.jsonl"
            expected = canonical("create", path)
            store = DurableStore.create(DurableStoreOptions(path=str(path)))
            client = sdk_client(sdk_session("canonical-child"))
            harness = DurableHarness(client, store, ExtensionRegistry())
            try:
                notes = store.document("interop.notes", expected["ownerId"])
                assert notes is not None
                self.assertEqual([v.value for v in notes.versions], [["first"], ["second"]])
                self.assertEqual(notes.latest, ["second"])
                parent = await harness.resume_conversation_async(expected["forkIds"][0])
                async def append(tx):
                    return tx.append_entry(parent.id, "boundary")
                boundary = await store.commit_async(append)
                child = await harness.fork_async(parent, boundary)
                intent = store.document("durable.fork", child.id)
                assert intent is not None
                self.assertEqual([v.value["state"] for v in intent.versions[-2:]], [0, 1])
            finally:
                await harness.close_async()
                store.close()
            self.assertEqual(len(canonical("verify", path)["forkIds"]), 2)

    async def test_auto_torn_utf8_and_unknown_legacy_bytes_are_distinguished(self):
        from durable import DurableStoreFormat
        prefix = json.dumps({"v": 1, "seq": 1, "type": "conversation",
                             "payload": {"id": "saved"}}).encode() + b"\n"
        torn = b'{"v":1,"seq":2,"type":"conversation","payload":{"id":"\xe9\x9b'
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "store.jsonl"
            for format in (DurableStoreFormat.AUTO, DurableStoreFormat.JSONL):
                for head in (b"", prefix):
                    with self.subTest(format=format, head=head):
                        path.write_bytes(head + torn)
                        store = DurableStore.create(DurableStoreOptions(path=str(path), format=format))
                        try:
                            self.assertEqual([c.id for c in store.conversations], ["saved"] if head else [])
                            self.assertEqual(path.read_bytes(), head)
                        finally:
                            store.close()
            for malformed in (
                b'{"sequence":4,"conversations":[{"id":"important"}]',
                b'{"conversations":[{"id":"\xe9\x9b',
                b'not a store', b'{"unknown":',
                '{"conversations":[]}'.encode("utf-16"),
                prefix + b'{"v":1,"payload":"\xe9\x9b"}\n',
                prefix + b'not json\n',
            ):
                with self.subTest(malformed=malformed):
                    path.write_bytes(malformed)
                    with self.assertRaises((ValueError, UnicodeDecodeError)):
                        DurableStore.create(DurableStoreOptions(path=str(path)))
                    self.assertEqual(path.read_bytes(), malformed)
            path.write_bytes(b'{"unidentifiable":')
            store = DurableStore.create(DurableStoreOptions(path=str(path), format=DurableStoreFormat.JSONL))
            try:
                self.assertEqual(path.read_bytes(), b"")
            finally:
                store.close()
            legacy = b'{"sequence":4,"conversations":[],"documents":[],"tasks":[],"entries":{},"entryRecords":[]}'
            path.write_bytes(legacy)
            store = DurableStore.create(DurableStoreOptions(path=str(path)))
            store.close()
            self.assertEqual(path.read_bytes(), legacy)

    async def test_recovery_resumes_committed_pending_after_actual_close_reopen(self):
        calls = []
        async def phase(_input, _checkpoint, runtime):
            calls.append(runtime.task.id)
            return await runtime.commit_async(lambda: TaskCommit.completed({"recovered": True}))
        definition = DurableTask.define("pending", lambda: {"phase": "run"}, phase)
        registry = ExtensionRegistry()
        registry.install(Extension("recovery", tasks=[definition]))
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            async def create(tx):
                return tx.create_task(definition)
            record = await store.commit_async(create)
            self.assertIs(record.status, TaskStatus.PENDING)
            self.assertEqual(calls, [])
            store.close()
            store = DurableStore.create(options)
            harness = DurableHarness(sdk_client(sdk_session()), store, registry)
            try:
                outcomes = await harness.resume_async()
                self.assertEqual(len(outcomes), 1)
                self.assertEqual(calls, [record.id])
                recovered = store.task(record.id)
                assert recovered is not None
                self.assertIs(recovered.status, TaskStatus.TERMINAL)
                await harness.close_async()
                store.close()
                store = DurableStore.create(options)
                recovered = store.task(record.id)
                assert recovered is not None
                self.assertIs(recovered.status, TaskStatus.TERMINAL)
            finally:
                await harness.close_async()
                store.close()

    async def test_shutdown_drains_recovered_runs_and_retains_scheduler_ownership(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                entered, release = asyncio.Event(), asyncio.Event()
                order = []
                async def phase(_input, _checkpoint, runtime):
                    entered.set()
                    await release.wait()
                    commit = await runtime.commit_async(lambda: TaskCommit.completed({"saved": True}))
                    order.append("run")
                    return commit
                definition = DurableTask.define("drain", lambda: {"phase": "run"}, phase)
                registry = ExtensionRegistry()
                registry.install(Extension("recovery", tasks=[definition]))
                store = DurableStore.create(options)
                async def create(tx):
                    task = tx.create_task(definition)
                    task.status = TaskStatus.RUNNING
                    return task.id
                identifier = await store.commit_async(create)
                store.close()
                client = sdk_client(sdk_session())
                async def stop():
                    order.append("stop")
                client.stop = AsyncMock(side_effect=stop)
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store_options(options).use_client(client)
                            .use_registry(registry).configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    store = host.store
                    assert store is not None
                    harness = host._harness
                    assert harness is not None
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, registry)
                recovery = asyncio.create_task(harness.resume_async())
                close = None
                try:
                    await entered.wait()
                    close = asyncio.create_task(host.close_async() if host else harness.close_async())
                    await asyncio.sleep(0)
                    await asyncio.sleep(0)
                    self.assertEqual(order, [])
                    with self.assertRaisesRegex(RuntimeError, "scheduler|runner"):
                        DurableHarness(client, store, registry)
                    with self.assertRaises((OSError, RuntimeError)):
                        DurableStore.create(options)
                    release.set()
                    await asyncio.gather(recovery, close)
                    self.assertEqual(order, ["run", "stop"])
                    if not facade:
                        replacement = DurableHarness(client, store, registry)
                        await replacement.close_async()
                        store.close()
                    reopened = DurableStore.create(options)
                    try:
                        recovered = reopened.task(identifier)
                        assert recovered is not None
                        self.assertIs(recovered.status, TaskStatus.TERMINAL)
                    finally:
                        reopened.close()
                finally:
                    release.set()
                    await asyncio.gather(recovery, *([close] if close else []), return_exceptions=True)
                    if host:
                        await host.close_async()
                    else:
                        await harness.close_async()
                    store.close()

    async def test_failed_barrier_still_drains_all_barriers_and_scheduled_runs(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                entered, release = asyncio.Event(), asyncio.Event()
                barrier_entered, release_barrier, barrier_done = asyncio.Event(), asyncio.Event(), asyncio.Event()
                order = []
                original = RuntimeError("first checkpoint")
                async def phase(_input, _checkpoint, runtime):
                    entered.set()
                    await release.wait()
                    commit = await runtime.commit_async(lambda: TaskCommit.completed(None))
                    order.append("run")
                    return commit
                definition = DurableTask.define("drain", lambda: {"phase": "run"}, phase)
                registry = ExtensionRegistry()
                registry.install(Extension("drain", tasks=[definition]))
                client = sdk_client(sdk_session())
                async def stop():
                    order.append("stop")
                client.stop = AsyncMock(side_effect=stop)
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store_options(options).use_client(client)
                            .use_registry(registry).configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                    assert store is not None and harness is not None
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, registry)
                async def create(tx):
                    return [tx.create_conversation(), tx.create_conversation()]
                records = await store.commit_async(create)
                one, two = (harness.find_conversation(r.id) for r in records)
                assert one is not None and two is not None
                one.checkpoint_barrier_async = AsyncMock(side_effect=original)
                async def delayed():
                    if barrier_done.is_set():
                        return
                    barrier_entered.set()
                    await release_barrier.wait()
                    async def append(tx):
                        tx.append_entry(records[1].id, "last-checkpoint")
                    assert store is not None
                    await store.commit_async(append)
                    order.append("barrier")
                    barrier_done.set()
                two.checkpoint_barrier_async = delayed
                record = await harness.create_task_async(definition)
                await entered.wait()
                closing = asyncio.create_task(host.close_async() if host else harness.close_async())
                try:
                    await barrier_entered.wait()
                    await asyncio.sleep(0)
                    await asyncio.sleep(0)
                    self.assertEqual(order, [])
                    self.assertFalse(closing.done())
                    with self.assertRaises((OSError, RuntimeError)):
                        DurableStore.create(options)
                    release.set()
                    await asyncio.wait_for(harness.wait_for_task_async(record.id), 1)
                    await asyncio.sleep(0)
                    await asyncio.sleep(0)
                    self.assertEqual(order, ["run"])
                    self.assertFalse(closing.done())
                    release_barrier.set()
                    await asyncio.wait_for(barrier_done.wait(), 1)
                    with self.assertRaises(RuntimeError) as raised:
                        await closing
                    self.assertIs(raised.exception, original)
                    self.assertEqual(order, ["run", "barrier", "stop"])
                    if not facade:
                        store.close()
                    reopened = DurableStore.create(options)
                    try:
                        recovered = reopened.task(record.id)
                        assert recovered is not None
                        self.assertIs(recovered.status, TaskStatus.TERMINAL)
                        self.assertEqual(len(reopened.entries(records[1].id)), 1)
                    finally:
                        reopened.close()
                finally:
                    release_barrier.set()
                    release.set()
                    await asyncio.gather(closing, return_exceptions=True)
                    await asyncio.gather(*list(harness._in_flight.values()), return_exceptions=True)
                    if host:
                        await host.close_async()
                    else:
                        await harness.close_async()
                    store.close()

    async def test_jsonl_noop_commit_after_reopen_preserves_document_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "store.jsonl"
            options = DurableStoreOptions(path=str(path))
            store = DurableStore.create(options)
            try:
                notes: DocumentDefinition[dict[str, str]] = DocumentDefinition(
                    "notes", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF)
                async def create(tx):
                    conversation = tx.create_conversation()
                    draft = tx.doc(notes, conversation.id)
                    draft.value = {"text": "saved"}
                    draft.save(tx.append_entry(conversation.id, "note"))
                    return conversation.id
                identifier = await store.commit_async(create)
                store.close()
                store = DurableStore.create(options)
                saved = path.read_bytes()
                async def noop(_tx):
                    return None
                await store.commit_async(noop)
                self.assertEqual(path.read_bytes(), saved)
                document = store.document(notes.kind, identifier)
                assert document is not None
                self.assertEqual(document.latest, {"text": "saved"})
            finally:
                store.close()

    async def test_completion_timeout_blocks_next_prompt_from_consuming_delayed_native_answer(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        try:
            chat = await harness.root_async()
            first = asyncio.create_task(chat.submit_async("first", timeout=0.01))
            second = asyncio.create_task(chat.submit_async("second", timeout=0.1))
            with self.assertRaises(TimeoutError):
                await first
            async def late_events():
                await asyncio.sleep(0.02)
                session.emit({"type": "assistant.message", "id": "late-answer", "data": {"content": "first answer"}})
                session.emit({"type": "session.idle", "data": {}})
            late = asyncio.create_task(late_events())
            try:
                with self.assertRaises(DurableRecoveryRequiredException):
                    await second
            finally:
                await late
            admission = chat.admission()
            assert admission is not None
            self.assertEqual((admission.prompt, admission.state), ("first", "uncertain"))
            self.assertEqual(session.send.await_count, 1)
            self.assertIsNone(chat.checkpoint())
        finally:
            await harness.close_async()
            store.close()

    async def test_send_failure_preserves_original_exception_and_recovery_gate(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        original = RuntimeError("native send failed")
        session.send.side_effect = original
        try:
            chat = await harness.root_async()
            with self.assertRaises(RuntimeError) as raised:
                await chat.submit_async("first")
            self.assertIs(raised.exception, original)
            with self.assertRaises(DurableRecoveryRequiredException):
                await chat.submit_async("second")
            admission = chat.admission()
            assert admission is not None
            self.assertEqual((admission.prompt, admission.state), ("first", "uncertain"))
            self.assertEqual(session.send.await_count, 1)
            self.assertEqual(len(session.listeners), 1)
        finally:
            await harness.close_async()
            store.close()

    async def test_timeout_bounds_send_and_blocks_queued_and_late_submissions(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        entered, cancelled = asyncio.Event(), asyncio.Event()
        async def send(_prompt):
            entered.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        session.send = AsyncMock(side_effect=send)
        try:
            chat = await harness.root_async()
            start = asyncio.get_running_loop().time()
            first = asyncio.create_task(chat.submit_async("first", timeout=0.02))
            await entered.wait()
            second = asyncio.create_task(chat.submit_async("second", timeout=0.1))
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(first, 0.3)
            self.assertLess(asyncio.get_running_loop().time() - start, 0.2)
            self.assertTrue(cancelled.is_set())
            with self.assertRaises(DurableRecoveryRequiredException):
                await second
            admission = chat.admission()
            assert admission is not None
            self.assertEqual((admission.prompt, admission.state), ("first", "uncertain"))
            session.emit({"type": "assistant.message", "id": "old-answer", "data": {"content": "first answer"}})
            session.emit({"type": "session.idle", "data": {}})
            with self.assertRaises(DurableRecoveryRequiredException):
                await asyncio.wait_for(chat.submit_async("third"), 0.1)
            self.assertEqual(chat.admission(), admission)
            self.assertIsNone(chat.checkpoint())
            self.assertEqual(session.send.await_count, 1)
            self.assertEqual(len(session.listeners), 1)
            self.assertFalse(chat.busy)
        finally:
            await harness.close_async()
            store.close()

    async def test_cancellation_keeps_original_error_and_blocks_followup_without_orphan_send(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        entered, cancelled = asyncio.Event(), asyncio.Event()
        async def send(_prompt):
            entered.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        session.send = AsyncMock(side_effect=send)
        try:
            chat = await harness.root_async()
            task = asyncio.create_task(chat.submit_async("cancel me"))
            await entered.wait()
            task.cancel("caller cancellation")
            with self.assertRaisesRegex(asyncio.CancelledError, "caller cancellation"):
                await task
            self.assertTrue(cancelled.is_set())
            admission = chat.admission()
            assert admission is not None
            self.assertEqual(admission.state, "uncertain")
            with self.assertRaises(DurableRecoveryRequiredException):
                await asyncio.wait_for(chat.submit_async("must not send"), 0.1)
            self.assertEqual(session.send.await_count, 1)
            self.assertEqual(len(session.listeners), 1)
        finally:
            await harness.close_async()
            store.close()

    async def test_history_hydration_is_readonly_and_idempotent_after_actual_store_reopen(self):
        history = [
            {"type": "session.start", "id": "start", "data": {"selectedModel": "reported-model"}},
            {"type": "tool.execution_start", "id": "tool-start", "data": {
                "toolCallId": "call", "toolName": "read", "arguments": {"path": "notes"}}},
            {"type": "tool.execution_complete", "id": "tool-end", "data": {
                "toolCallId": "call", "success": True, "result": {"content": "notes"}}},
            {"type": "assistant.message", "id": "answer", "data": {"content": "final answer"}},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "store.jsonl"
            options = DurableStoreOptions(path=str(path))
            store = DurableStore.create(options)
            session = sdk_session()
            client = sdk_client(session)
            harness = DurableHarness(client, store, ExtensionRegistry())
            try:
                chat = await harness.root_async()
                async def send(_prompt):
                    for event in history:
                        session.emit(event)
                    session.emit({"type": "session.idle", "data": {}})
                    return "sent"
                session.send.side_effect = send
                await chat.submit_async("read")
                identifier, checkpoint, saved = chat.id, chat.checkpoint(), path.read_bytes()
                binding = chat.binding()
                assert binding is not None and binding.session_id is not None
                resumed = sdk_session(binding.session_id, history=history)
                client.resume_session.return_value = resumed
                for _ in range(2):
                    await harness.close_async()
                    store.close()
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, ExtensionRegistry())
                    chat = await harness.resume_conversation_async(identifier)
                    await chat.checkpoint_barrier_async()
                    self.assertEqual(chat.checkpoint(), checkpoint)
                    self.assertEqual(path.read_bytes(), saved)
                    self.assertEqual(chat.answers, ["final answer"])
                    self.assertEqual((await chat.wait_for_effective_model_async(0.01)).model, "reported-model")
                    chat.detach()
                    await harness.resume_conversation_async(identifier)
                    self.assertEqual(chat.answers, ["final answer"])
                    resumed.emit({**history[2], "id": "redelivered-tool"})
                    await chat.checkpoint_barrier_async()
                    self.assertEqual(path.read_bytes(), saved)
                client.create_session.return_value = sdk_session("created", history)
                created = await harness.create_conversation_async()
                await created.checkpoint_barrier_async()
                self.assertIsNone(created.checkpoint())
                self.assertEqual(created.answers, ["final answer"])
            finally:
                await harness.close_async()
                store.close()

    async def test_pending_abandoned_and_completed_unbound_native_forks_never_create_empty_sessions(self):
        for state in ForkIntentState:
            with self.subTest(state=state):
                store = DurableStore.create_ephemeral()
                session = sdk_session()
                client = sdk_client(session)
                harness = DurableHarness(client, store, ExtensionRegistry())
                try:
                    async def create(tx):
                        parent = tx.create_conversation()
                        boundary = tx.append_entry(parent.id, "boundary")
                        child = tx.create_conversation(parent_conversation_id=parent.id, fork_entry_id=boundary)
                        intent = tx.doc(FORK_DOCUMENT, child.id)
                        intent.value = ForkIntent(parent.id, boundary, None, state)
                        intent.save(tx.append_entry(child.id, "fork"))
                        return child
                    child = await store.commit_async(create)
                    with self.assertRaises(DurableRecoveryRequiredException):
                        await harness.resume_conversation_async(child.id)
                    client.create_session.assert_not_awaited()
                    client.resume_session.assert_not_awaited()
                    restored = harness.find_conversation(child.id)
                    assert restored is not None
                    self.assertIsNone(restored.binding())
                finally:
                    await harness.close_async()
                    store.close()

    async def _fork_at_admission(self, harness, session):
        parent = await harness.root_async()
        boundary = ""
        async def send(_prompt):
            nonlocal boundary
            boundary = await parent.append_entry_async("tool-created")
            session.emit({"type": "assistant.message", "id": "parent-answer", "data": {"content": "parent done"}})
            session.emit({"type": "session.idle", "data": {}})
            return "sent"
        session.send.side_effect = send
        await parent.submit_async("parent")
        return await parent.fork_async(boundary)

    async def test_fork_hydrates_inherited_model_and_answer_without_tool_checkpoint_writes(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        client = sdk_client(session)
        child_session = sdk_session("child", [
            {"type": "session.start", "id": "start", "data": {"selectedModel": "fork-model"}},
            {"type": "tool.execution_complete", "id": "tool", "data": {"toolCallId": "call", "success": True}},
            {"type": "assistant.message", "id": "answer", "data": {"content": "inherited answer"}},
        ])
        client.resume_session.return_value = child_session
        harness = DurableHarness(client, store, ExtensionRegistry())
        try:
            child = await self._fork_at_admission(harness, session)
            self.assertEqual(child.answers, ["inherited answer"])
            self.assertEqual((await child.wait_for_effective_model_async(0.01)).model, "fork-model")
            await child.checkpoint_barrier_async()
            self.assertIsNone(child.checkpoint())
        finally:
            await harness.close_async()
            store.close()

    async def test_inherited_admission_does_not_block_child_resume_but_own_uncertainty_does(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        client = sdk_client(session)
        child_session = sdk_session("child")
        client.resume_session.return_value = child_session
        harness = DurableHarness(client, store, ExtensionRegistry())
        try:
            child = await self._fork_at_admission(harness, session)
            self.assertEqual(child.admission().state, "admitted")
            child.detach()
            self.assertIs(await harness.resume_conversation_async(child.id), child)
            with self.assertRaises(TimeoutError):
                await child.submit_async("child work", timeout=0.01)
            with self.assertRaises(DurableRecoveryRequiredException):
                await asyncio.wait_for(child.submit_async("retry"), 0.1)
            child.detach()
            with self.assertRaises(DurableRecoveryRequiredException):
                await harness.resume_conversation_async(child.id)
        finally:
            await harness.close_async()
            store.close()

    async def test_prompt_sections_preserve_sync_and_async_hook_outputs(self):
        async def async_dict(_input, _invocation):
            return {"additionalContext": "hook context", "modifiedPrompt": "modified"}
        async def async_none(_input, _invocation):
            return None
        for hook in [lambda _i, _v: {"additionalContext": "hook context", "modifiedPrompt": "modified"},
                     lambda _i, _v: None, async_dict, async_none]:
            with self.subTest(hook=hook):
                store = DurableStore.create_ephemeral()
                registry = ExtensionRegistry()
                registry.install(Extension("sections", sections=[
                    PromptSection("notes", AsyncMock(return_value="section content"))]))
                harness = DurableHarness(sdk_client(sdk_session()), store, registry)
                try:
                    choices = AgentChoices(hooks={"on_user_prompt_submitted": hook})
                    chat = await harness.root_async(choices)
                    config = await harness.build_session_config(choices, chat)
                    output = await config["hooks"]["on_user_prompt_submitted"]({"prompt": "original"}, {})
                    expected = await hook({}, {}) if asyncio.iscoroutinefunction(hook) else hook({}, {})
                    self.assertEqual(output, {
                        **(expected or {}),
                        "additionalContext": ((expected or {}).get("additionalContext", "") + "\n\n"
                                              if expected else "") + "<notes>\nsection content\n</notes>",
                    })
                finally:
                    await harness.close_async()
                    store.close()


class SdkTests(unittest.IsolatedAsyncioTestCase):
    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_official_sdk_models_envelope_support_and_raw_shape_limit(self):
        # PR123 PRRT_kwDOVUQtAs6yggpP: intercept only the test RPC boundary, not SDK fields.
        from copilot._jsonrpc import JsonRpcClient
        model = {
            "id": "replay-model", "name": "Replay model",
            "capabilities": {"supports": {}, "limits": {"max_context_window_tokens": 8192}},
            "supportedReasoningEfforts": ["low"],
        }
        for shape in ["models", "list", "array"]:
            with self.subTest(shape=shape):
                store = DurableStore.create_ephemeral()
                harness = await DurableHarness.open_async(store, ExtensionRegistry())
                original = JsonRpcClient.request
                async def request(client, method, params=None, **kwargs):
                    if method == "models.list":
                        return [model] if shape == "array" else {shape: [model]}
                    return await original(client, method, params, **kwargs)
                try:
                    with patch.object(JsonRpcClient, "request", request):
                        choices = AgentChoices(model="replay-model", reasoning_effort="minimal")
                        if shape == "array":
                            with self.assertRaisesRegex(AttributeError, "get"):
                                await harness.build_session_config(choices)
                        else:
                            config = await harness.build_session_config(choices)
                            if shape == "models":
                                self.assertEqual(config["reasoning_effort"], "minimal")
                                models = await harness.client.list_models()
                                self.assertEqual(models[0].supported_reasoning_efforts, ["low"])
                            else:
                                self.assertNotIn("reasoning_effort", config)
                                self.assertEqual(await harness.client.list_models(), [])
                finally:
                    await harness.close_async()
                    store.close()

    async def test_host_hook_tolerance_is_opt_in_and_preserves_error_and_answer(self):
        from unittest.mock import Mock
        for tolerant, message, succeeds, emit_answer in [
            (False, "postToolUse hook: Operation not permitted", False, True),
            (True, "postToolUse hook: Operation not permitted", True, True),
            (True, "hook timed out", False, True),
            (True, "Operation not permitted", False, True),
            (True, "postToolUse hook: Operation not permitted", True, False),
        ]:
            store = DurableStore.create_ephemeral()
            client = SimpleNamespace(stop=AsyncMock())
            harness = DurableHarness(client, store, ExtensionRegistry(),
                                     DurableHarnessOptions(tolerate_host_hook_failures=tolerant))
            listeners = []
            def subscribe(handler):
                listeners.append(handler)
                return lambda: listeners.remove(handler)
            answer = {"type": "assistant.message", "id": "answer", "data": {"content": "actual event"}}
            async def send(_prompt):
                events = ([answer] if emit_answer else []) + [{"type": "session.error", "data": {"message": message}}]
                for event in events:
                    for listener in list(listeners):
                        listener(event)
                return "message"
            session = Mock(session_id="sdk-session")
            session.on = subscribe
            session.send = send
            try:
                async def create(tx):
                    return tx.create_conversation()
                record = await store.commit_async(create)
                chat = harness.find_conversation(record.id)
                assert chat is not None
                chat.attach(session)
                if succeeds:
                    self.assertIs(await chat.submit_async("hello", timeout=1), answer if emit_answer else None)
                else:
                    with self.assertRaisesRegex(RuntimeError, message):
                        await chat.submit_async("hello", timeout=1)
                self.assertEqual(chat.answers, ["actual event"] if emit_answer else [])
                self.assertEqual(DurableConversation.is_host_hook_failure(message),
                                 "hook" in message.lower() and "Operation not permitted" in message)
            finally:
                await harness.close_async()
                store.close()

    async def test_close_stops_sdk_after_checkpoint_failure(self):
        store = DurableStore.create_ephemeral()
        client = SimpleNamespace(stop=AsyncMock())
        harness = DurableHarness(client, store, ExtensionRegistry())
        try:
            async def create(tx):
                return tx.create_conversation()
            record = await store.commit_async(create)
            chat = harness.find_conversation(record.id)
            assert chat is not None
            chat.checkpoint_barrier_async = AsyncMock(side_effect=RuntimeError("checkpoint failed"))
            with self.assertRaisesRegex(RuntimeError, "checkpoint failed"):
                await harness.close_async()
            await harness.close_async()
            client.stop.assert_awaited_once()
        finally:
            store.close()

    async def test_jsonl_exact_crlf_mixed_utf8_and_unterminated_tail_boundaries(self):
        from durable import DurableStoreFormat
        for endings in [("\r\n", "\r\n"), ("\n", "\r\n")]:
            for tail in ["", '{"v":1', json.dumps({
                "v": 1, "seq": 3, "type": "conversation",
                "payload": {"id": "torn", "createdAt": "2026-11-01T00:00:00Z"},
            })]:
                with tempfile.TemporaryDirectory() as directory:
                    path = os.path.join(directory, "store.jsonl")
                    ids = ["\u96ea\u00e9", "\U0001f680"]
                    prefix = "".join(json.dumps({
                        "v": 1, "seq": i + 1, "type": "conversation",
                        "payload": {"id": identifier, "createdAt": "2026-11-01T00:00:00Z"},
                    }, ensure_ascii=False) + endings[i] for i, identifier in enumerate(ids)).encode("utf-8")
                    with open(path, "wb") as handle:
                        handle.write(prefix + tail.encode("utf-8"))
                    options = DurableStoreOptions(path=path, format=DurableStoreFormat.JSONL)
                    store = DurableStore.create(options)
                    try:
                        self.assertEqual([c.id for c in store.conversations], ids)
                        with open(path, "rb") as handle:
                            self.assertEqual(handle.read(), prefix)
                        async def create(tx):
                            return tx.create_conversation()
                        added = await store.commit_async(create)
                        store.close()
                        store = DurableStore.create(options)
                        self.assertEqual([c.id for c in store.conversations], ids + [added.id])
                    finally:
                        store.close()

    async def test_jsonl_all_tail_file_truncates_to_zero(self):
        from durable import DurableStoreFormat
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "store.jsonl")
            for tail in [b'{"v":1', b'{"v":1,"seq":1,"type":"conversation","payload":{"id":"tail"}}']:
                with open(path, "wb") as handle:
                    handle.write(tail)
                store = DurableStore.create(DurableStoreOptions(path=path, format=DurableStoreFormat.JSONL))
                try:
                    self.assertEqual(len(store.conversations), 0)
                    self.assertEqual(os.path.getsize(path), 0)
                finally:
                    store.close()
    def test_sandbox_wire_and_boundary_policy(self):
        config = DurableHarness.build_sandbox_config(DurableSandboxPolicy(
            enabled=True, allow_bypass=False, sandbox_mcp_servers=False,
            sandbox_lsp_servers=True, keychain_access=False,
            auth=DurableSandboxAuth(gh=True, git=False),
            filesystem=DurableSandboxFilesystem(denied_paths=["{workspace}/private"],
                readonly_paths=[], readwrite_paths=["{workspace}"]),
            credentials=DurableSandboxCredentials(env_vars={"TOKEN": ["api.example.com"]}),
            network=DurableSandboxNetwork(allow_outbound=False, allow_local_network=True,
                allowed_hosts=["example.com"], blocked_hosts=["bad.com"]),
        ), "/work").to_dict()
        self.assertEqual(config["userPolicy"]["filesystem"]["deniedPaths"], ["/work/private"])
        self.assertEqual(config["credentials"], {"envVars": {"TOKEN": {"injectHosts": ["api.example.com"]}}})
        self.assertEqual(config["userPolicy"]["seatbelt"], {"keychainAccess": False})
        self.assertNotIn("experimental", config["userPolicy"])
        self.assertIs(config["allowBypass"], False)
        self.assertIs(config["sandboxMcpServers"], False)
        self.assertIs(config["userPolicy"]["network"]["allowOutbound"], False)
        self.assertFalse(PermissionPolicy.is_granted(["read:/tmp/foo"], "read", "/tmp/foobar"))
        self.assertTrue(PermissionPolicy.is_granted(["read:/tmp/foo"], "read", "/tmp/foo/bar"))

    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_real_sdk_lifecycle_reopen_and_models(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=os.path.join(directory, "store.jsonl"))
            store = DurableStore.create(options)
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            try:
                chat = await harness.root_async(AgentChoices(extensions=[]))
                answer = await chat.submit_async("Hello")
                self.assertEqual(answer.data.content, "Hello from the recorded transcript.")
                self.assertEqual(chat.answers, ["Hello from the recorded transcript."])
                self.assertEqual(chat.effective_model.model, "replay-model")
                self.assertEqual(chat.effective_model.source, "session")
                self.assertIsNone(chat.turn_models[0].model)
                admission, checkpoint = chat.admission(), chat.checkpoint()
                assert admission is not None and checkpoint is not None
                self.assertEqual(admission.state, "completed")
                self.assertEqual(checkpoint.content, answer.data.content)
                self.assertEqual(chat.entries(), [])
                session_id = chat.session_id
            finally:
                await harness.close_async()
                store.close()
            store = DurableStore.create(options)
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            try:
                chat = await harness.root_async()
                self.assertEqual(chat.session_id, session_id)
                choices, admission = chat.choices(), chat.admission()
                assert choices is not None and admission is not None
                self.assertEqual(choices.extensions, [])
                self.assertEqual(admission.state, "completed")
            finally:
                await harness.close_async()
                store.close()

    async def test_default_binding_starts_sdk_and_uses_keyword_configuration(self):
        # Patch the SDK boundary, not a replacement durable engine.
        from unittest.mock import AsyncMock, Mock
        session = Mock()
        session.session_id = "sdk-session"
        session.on.return_value = lambda: None
        session.get_events = AsyncMock(return_value=[])
        with patch("durable.CopilotClient") as client_type:
            client = client_type.return_value
            client.start = AsyncMock()
            client.stop = AsyncMock()
            client.create_session = AsyncMock(return_value=session)
            store = DurableStore.create_ephemeral()
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            try:
                chat = await harness.root_async(AgentChoices(extensions=[], permissions=[]))
                client.start.assert_awaited_once()
                kwargs = client.create_session.call_args.kwargs
                self.assertEqual(kwargs["tools"], [])
                self.assertTrue(kwargs["session_id"].startswith("s"))
                binding = chat.binding()
                assert binding is not None
                self.assertEqual(binding.session_id, kwargs["session_id"])
                self.assertEqual(client.create_session.call_args.args, ())
            finally:
                await harness.close_async()
                store.close()
            client.stop.assert_awaited_once()

    async def test_abort_cascade_commits_default_abort_and_preserves_pending_records(self):
        store = DurableStore.create_ephemeral()
        registry = ExtensionRegistry()
        harness = DurableHarness(None, store, registry)
        definition = DurableTask.define("work", lambda: {"phase": "run"},
            lambda _input, _checkpoint, runtime: runtime.commit_async(lambda: TaskCommit.completed(None)))
        registry.install(Extension("work", tasks=[definition]))
        try:
            async def create(tx):
                parent = tx.create_task(definition)
                child = tx.create_task(definition, owner_task_id=parent.id)
                return parent, child
            parent, child = await store.commit_async(create)
            self.assertEqual(parent.status, TaskStatus.PENDING)
            await harness.abort_task_async(parent.id)
            self.assertEqual(parent.status, TaskStatus.TERMINAL)
            self.assertEqual(child.status, TaskStatus.TERMINAL)
            self.assertEqual(child.outcome.status, "aborted")
            self.assertEqual(harness.task_graph(), [(parent, None), (child, parent.id)])
        finally:
            harness.close()
            store.close()

    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_actual_sdk_tool_override_wrapper_and_checkpoint(self):
        calls = []
        async def read(invocation):
            self.assertEqual(invocation.arguments, {"path": "notes.txt"})
            calls.append(invocation.session_id)
            return ToolResult(text_result_for_llm="recorded contents")
        def wrap(tool):
            async def invoke(invocation):
                calls.append("wrapped")
                return await tool.handler(invocation)
            from dataclasses import replace
            return replace(tool, handler=invoke)
        registry = ExtensionRegistry()
        registry.install(Extension("files", tools=[Tool(
            name="read_file", description="read", handler=read,
            parameters={"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]},
            overrides_built_in_tool=True,
        )], wraps=[ToolWrapper("read_file", wrap)]))
        options = DurableHarnessOptions(runtime_arguments=[
            "--fixture", str(REPLAY_FIXTURES / "tool-round-trip.json")])
        store = DurableStore.create_ephemeral()
        harness = await DurableHarness.open_async(store, registry, options)
        try:
            chat = await harness.root_async(AgentChoices(permissions=["custom-tool:read_file"]))
            answer = await chat.submit_async("Read notes.txt", timeout=5)
            self.assertTrue(answer.data.content)
            self.assertEqual(calls, ["wrapped", chat.session_id])
            self.assertTrue(any(
                version.value.get("eventId") == "tool:call-0001"
                for doc in store.documents if doc.kind == "durable.checkpoint"
                for version in doc.versions
            ))
        finally:
            await harness.close_async()
            store.close()

    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_facade_shares_one_harness_and_no_model_stays_unreported(self):
        store = DurableStore.create_ephemeral()
        options = DurableHarnessOptions(runtime_arguments=[
            "--fixture", str(REPLAY_FIXTURES / "no-model.json")])
        client = DurableClientBuilder().use_store(store).use_harness_options(options).build()
        try:
            chat = await client.root_async(AgentChoices(extensions=[]))
            second = await client.create_conversation_async(AgentChoices(extensions=[]))
            self.assertIs(client.find_conversation(chat.id), chat)
            self.assertNotEqual(chat.id, second.id)
            await chat.submit_async("Hello")
            self.assertIsNone(chat.effective_model.model)
            self.assertFalse(chat.effective_model.has_model)
            with self.assertRaises(RuntimeError):
                _ = chat.effective_model.required_model
            with self.assertRaises(TimeoutError):
                await chat.wait_for_effective_model_async(timeout=0.01)
        finally:
            await client.close_async()
            store.close()


if __name__ == "__main__":
    unittest.main()
