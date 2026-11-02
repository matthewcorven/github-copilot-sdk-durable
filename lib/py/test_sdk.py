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
from collections.abc import Awaitable

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
from copilot import CopilotClient, RuntimeConnection

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
        set_tools=AsyncMock(), disconnect=AsyncMock(),
    )


def sdk_client(session):
    return SimpleNamespace(
        stop=AsyncMock(), create_session=AsyncMock(return_value=session),
        resume_session=AsyncMock(return_value=session),
        get_session_metadata=AsyncMock(return_value=SimpleNamespace(session_id=session.session_id)),
        rpc=SimpleNamespace(sessions=SimpleNamespace(fork=AsyncMock(
            return_value=SimpleNamespace(session_id="child")))),
    )


class ControlledSdkPeer:
    """Controlled public TCP protocol, not native runtime certification."""

    def __init__(self):
        self.held_method: str | None = None
        self.received = asyncio.Event()
        self.release = asyncio.Event()
        self.finished = asyncio.Event()
        self.detach_received = asyncio.Event()
        self.detach_release = asyncio.Event()
        self.detach_release.set()
        self.methods = []
        self.notifications = []
        self.detached = []
        self.detach_attempts = []
        self.requests = []
        self.peers = []
        self.writers = []
        self.failure: str | None = None
        self.detach_error: str | None = None
        self.metadata_exists = False

    async def __aenter__(self):
        self.server = await asyncio.start_server(self._peer, "127.0.0.1", 0)
        self.port = self.server.sockets[0].getsockname()[1]
        return self

    async def __aexit__(self, *_):
        self.release.set()
        self.detach_release.set()
        await asyncio.gather(*self.requests)
        for writer in self.writers:
            writer.close()
            await writer.wait_closed()
        self.server.close()
        await self.server.wait_closed()
        await asyncio.gather(*self.peers)

    async def send_event(self, session_id, event):
        data = json.dumps({"jsonrpc": "2.0", "method": "session.event",
                           "params": {"sessionId": session_id, "event": event}}).encode()
        for writer in self.writers:
            writer.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
            await writer.drain()

    async def _peer(self, reader, writer):
        self.peers.append(asyncio.current_task())
        self.writers.append(writer)
        while True:
            headers = {}
            while line := await reader.readline():
                if line == b"\r\n":
                    break
                key, value = line.decode().split(":", 1)
                headers[key.lower()] = value.strip()
            if not line:
                return
            message = json.loads(await reader.readexactly(int(headers["content-length"])))
            if "id" not in message:
                self.notifications.append(message)
            else:
                self.requests.append(asyncio.create_task(self._respond(message, writer)))

    async def _respond(self, message, writer):
        method, params = message["method"], message.get("params", {})
        self.methods.append(method)
        if method == self.held_method:
            self.received.set()
            await self.release.wait()
            self.finished.set()
        if method == "connect":
            result = {"ok": True, "protocolVersion": 3, "version": "controlled-public-rpc"}
        elif method in ("session.create", "session.resume"):
            result = {"sessionId": params["sessionId"], "capabilities": {}}
        elif method == "sessions.fork":
            result = {"sessionId": "controlled-child"}
        elif method == "session.getMessages":
            result = {"events": []}
        elif method == "session.getMetadata" and self.metadata_exists:
            result = {"session": {"sessionId": params["sessionId"], "startTime": "2026-11-02T00:00:00Z",
                                  "modifiedTime": "2026-11-02T00:00:00Z", "isRemote": False}}
        elif method == "session.detach":
            self.detach_attempts.append(params["sessionId"])
            self.detach_received.set()
            await self.detach_release.wait()
            if self.detach_error is None:
                self.detached.append(params["sessionId"])
                result = {"success": True}
            else:
                result = {"success": False, "error": self.detach_error}
        else:
            result = {}
        response = {"jsonrpc": "2.0", "id": message["id"], "result": result}
        if method == self.held_method and self.failure is not None:
            response.pop("result")
            response["error"] = {"code": -32000, "message": self.failure}
        data = json.dumps(response).encode()
        writer.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        await writer.drain()


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_round_seven_configuration_drains_store_and_public_tools_rpc(self):
        for facade, owns_client in ((False, True), (True, True), (True, False)):
            for stage in ("store", "rpc", "cancel", "late-error"):
                with self.subTest(facade=facade, owns_client=owns_client, stage=stage):
                    with tempfile.TemporaryDirectory() as directory:
                        async with ControlledSdkPeer() as peer:
                            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                            assert options.path is not None
                            path = Path(options.path)
                            store = DurableStore.create(options)
                            client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                            await client.start()
                            host = None
                            if facade:
                                host = (DurableClientBuilder().use_store(store).use_client(client)
                                        .configure(lambda o: setattr(o, "owns_store", True))
                                        .configure(lambda o: setattr(o, "owns_client", owns_client)).build())
                                await host.initialize_async()
                                harness = host._harness
                                assert harness is not None
                            else:
                                harness = DurableHarness(client, store, ExtensionRegistry())
                            chat = await harness.root_async()
                            stopped = False
                            stop = client.stop
                            async def observe_stop():
                                nonlocal stopped
                                stopped = True
                                return await stop()
                            client.stop = observe_stop
                            peer.held_method = "session.tools.set"
                            if stage == "late-error":
                                peer.failure = "late tools failure"
                            held, release, entered = asyncio.Event(), asyncio.Event(), asyncio.Event()
                            holding = None
                            if stage == "store":
                                async def hold(tx):
                                    held.set()
                                    await release.wait()
                                holding = asyncio.create_task(store.commit_async(hold))
                                await held.wait()
                                commit = store.commit_async
                                async def observe_commit(body):
                                    entered.set()
                                    return await commit(body)
                                store.commit_async = observe_commit
                            pending = asyncio.create_task(chat.configure_async(AgentChoices(instructions="accepted")))
                            closing = drain = None
                            try:
                                await asyncio.wait_for((entered if stage == "store" else peer.received).wait(), 5)
                                if stage in ("cancel", "late-error"):
                                    pending.cancel("tools caller interrupted")
                                    with self.assertRaisesRegex(asyncio.CancelledError, "tools caller interrupted"):
                                        await pending
                                closing = asyncio.create_task((host or harness).close_async())
                                drain = await self._start_close_gate(harness, closing)
                                async def observe_ownership_or_release():
                                    assert harness is not None and closing is not None
                                    while (not harness._conversation_operations and not harness._sdk_operations
                                           and not closing.done() and not stopped and not store._closed):
                                        await asyncio.sleep(0)
                                await asyncio.wait_for(observe_ownership_or_release(), 5)
                                self.assertFalse(stopped, "SDK stop must follow accepted configuration")
                                self.assertFalse(store._closed, "owned writer must survive pending configuration")
                                self.assertFalse(closing.done(), "configuration ownership must delay close")
                                self.assertTrue(harness._conversation_operations or harness._sdk_operations,
                                                "accepted configuration must retain shutdown ownership")
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                                sequence = store.provider.sequence
                                with self.assertRaisesRegex(RuntimeError, "closing"):
                                    await chat.configure_async(AgentChoices(instructions="rejected"))
                                self.assertEqual(store.provider.sequence, sequence)
                                release.set()
                                if holding is not None:
                                    await holding
                                await asyncio.wait_for(peer.received.wait(), 5)
                                self.assertFalse(closing.done(), "received public tools ACK remains shutdown-owned")
                                peer.release.set()
                                if not pending.cancelled():
                                    await pending
                                if stage == "late-error":
                                    with self.assertRaisesRegex(Exception, "late tools failure"):
                                        await closing
                                else:
                                    await closing
                                await drain
                                choices = chat.choices()
                                assert choices is not None
                                self.assertEqual(choices.instructions, "accepted")
                                self.assertEqual(peer.methods.count("session.tools.set"), 1)
                                self.assertEqual(stopped, owns_client)
                                self.assertEqual(store._closed, facade)
                                sequence, contents = store.provider.sequence, path.read_bytes()
                                with self.assertRaisesRegex(RuntimeError, "closing"):
                                    await chat.configure_async(AgentChoices(instructions="after-close"))
                                await asyncio.sleep(0)
                                self.assertEqual((store.provider.sequence, path.read_bytes()),
                                                 (sequence, contents), "no post-close durable writes")
                                if facade:
                                    reopened = DurableStore.create(options)
                                    reopened.close()
                            finally:
                                release.set()
                                peer.release.set()
                                await self._settle_lifecycle_tasks(holding, pending, closing, drain)
                                await client.stop()
                                store.close()

    async def test_round_seven_public_abort_owns_whole_child_first_cascade(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                async with ControlledSdkPeer() as peer:
                    options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                    assert options.path is not None
                    path = Path(options.path)
                    store = DurableStore.create(options)
                    client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                    await client.start()
                    registry = ExtensionRegistry()
                    order = []
                    async def phase(_input, _checkpoint, runtime):
                        return TaskCommit.completed()
                    async def abort(_input, _checkpoint, runtime):
                        order.append(runtime.task.id)
                        await runtime.commit_async(lambda: TaskCommit.aborted())
                        if runtime.task.owner_task_id is not None:
                            await chat.configure_async(AgentChoices(instructions="abort-internal"))
                            assert harness is not None
                            with self.assertRaisesRegex(RuntimeError, "closing"):
                                await asyncio.create_task(harness.abort_task_async("external"))
                        return TaskCommit.aborted()
                    definition = DurableTask.define("cascade", lambda: {}, phase, abort)
                    registry.install(Extension("cascade", tasks=[definition]))
                    host = None
                    if facade:
                        host = (DurableClientBuilder().use_store(store).use_client(client).use_registry(registry)
                                .configure(lambda o: setattr(o, "owns_store", True))
                                .configure(lambda o: setattr(o, "owns_client", True)).build())
                        await host.initialize_async()
                        harness = host._harness
                        assert harness is not None
                    else:
                        harness = DurableHarness(client, store, registry)
                    chat = await harness.root_async()
                    async def seed(tx):
                        parent = tx.create_task(definition)
                        child = tx.create_task(definition, owner_task_id=parent.id)
                        return parent, child
                    parent, child = await store.commit_async(seed)
                    stop = client.stop
                    released_states = []
                    async def observe_stop():
                        released_states.append((list(order), parent.status, child.status))
                        return await stop()
                    client.stop = observe_stop
                    await store._gate.acquire()
                    pending = asyncio.create_task(harness.abort_task_async(parent.id))
                    entered = asyncio.Event()
                    asyncio.get_running_loop().call_soon(entered.set)
                    await entered.wait()
                    closing = asyncio.create_task((host or harness).close_async())
                    drain = await self._start_close_gate(harness, closing)
                    try:
                        async def observe_ownership_or_release():
                            assert harness is not None
                            while not harness._conversation_operations and not released_states and not closing.done():
                                await asyncio.sleep(0)
                        await asyncio.wait_for(observe_ownership_or_release(), 5)
                        self.assertEqual(order, [child.id])
                        self.assertEqual(released_states, [], "SDK release must wait for both terminal records")
                        self.assertFalse(store._closed, "owned writer must survive the pending abort cascade")
                        self.assertTrue(harness._conversation_operations,
                                        "accepted public cascade must retain shutdown ownership")
                        self.assertFalse(closing.done(), "whole public cascade must be enrolled before its first await")
                        self.assertFalse(store._closed)
                        with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                            DurableStore.create(options)
                        with self.assertRaisesRegex(RuntimeError, "closing"):
                            await harness.abort_task_async(parent.id)
                        store._gate.release()
                        await pending
                        await closing
                        await drain
                        self.assertEqual(order, [child.id, parent.id], "accepted recursion survives closing")
                        self.assertEqual(peer.methods.count("session.tools.set"), 1)
                        self.assertEqual(released_states,
                                         [([child.id, parent.id], TaskStatus.TERMINAL, TaskStatus.TERMINAL)])
                        self.assertTrue(all(record.status is TaskStatus.TERMINAL for record in (parent, child)))
                        self.assertTrue(all(record.outcome is not None and record.outcome.is_aborted
                                            for record in (parent, child)))
                        sequence, contents = store.provider.sequence, path.read_bytes()
                        with self.assertRaisesRegex(RuntimeError, "closing"):
                            await harness.abort_task_async(parent.id)
                        await asyncio.sleep(0)
                        self.assertEqual((store.provider.sequence, path.read_bytes()), (sequence, contents))
                    finally:
                        if store._gate.locked():
                            store._gate.release()
                        await self._settle_lifecycle_tasks(pending, closing, drain)
                        await client.stop()
                        store.close()

    async def test_round_seven_queued_configuration_and_abort_remain_caller_cancellable(self):
        for operation in ("configure", "abort"):
            with self.subTest(operation=operation):
                async with ControlledSdkPeer() as peer:
                    store = DurableStore.create_ephemeral()
                    client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                    await client.start()
                    registry = ExtensionRegistry()
                    async def phase(_input, _checkpoint, runtime):
                        return TaskCommit.completed()
                    definition = DurableTask.define("cancel-abort", lambda: {}, phase)
                    registry.install(Extension("cancel-abort", tasks=[definition]))
                    harness = DurableHarness(client, store, registry)
                    chat = await harness.root_async()
                    async def seed(tx):
                        return tx.create_task(definition)
                    record = await store.commit_async(seed)
                    await store._gate.acquire()
                    pending = asyncio.create_task(
                        chat.configure_async(AgentChoices(instructions="cancelled"))
                        if operation == "configure" else harness.abort_task_async(record.id))
                    entered = asyncio.Event()
                    asyncio.get_running_loop().call_soon(entered.set)
                    await entered.wait()
                    try:
                        pending.cancel("queued owner interrupted")
                        with self.assertRaisesRegex(asyncio.CancelledError, "queued owner interrupted"):
                            await pending
                        sequence = store.provider.sequence
                        self.assertFalse(harness._conversation_operations)
                        self.assertFalse(harness._sdk_operations)
                        store._gate.release()
                        await harness.close_async()
                        self.assertEqual(store.provider.sequence, sequence)
                        self.assertNotEqual(record.status, TaskStatus.TERMINAL)
                        self.assertNotIn("session.tools.set", peer.methods)
                    finally:
                        if store._gate.locked():
                            store._gate.release()
                        await self._settle_lifecycle_tasks(pending)
                        await harness.close_async()
                        store.close()

    async def test_round_seven_active_task_can_configure_and_abort_child_during_close(self):
        async with ControlledSdkPeer() as peer:
            store = DurableStore.create_ephemeral()
            client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
            await client.start()
            registry = ExtensionRegistry()
            harness = DurableHarness(client, store, registry)
            chat = await harness.root_async()
            entered, release = asyncio.Event(), asyncio.Event()
            child = None
            async def phase(_input, _checkpoint, runtime):
                entered.set()
                await release.wait()
                await chat.configure_async(AgentChoices(instructions="internal"))
                assert child is not None
                await harness.abort_task_async(child.id)
                return await runtime.commit_async(lambda: TaskCommit.completed())
            definition = DurableTask.define("reentrant-configure", lambda: {}, phase)
            registry.install(Extension("reentrant-configure", tasks=[definition]))
            async def seed(tx):
                parent = tx.create_task(definition)
                return parent, tx.create_task(definition, owner_task_id=parent.id)
            parent, child = await store.commit_async(seed)
            running = asyncio.create_task(harness.run_task_async(parent.id))
            await entered.wait()
            closing = asyncio.create_task(harness.close_async())
            drain = await self._start_close_gate(harness, closing)
            try:
                release.set()
                await running
                await closing
                await drain
                self.assertEqual(peer.methods.count("session.tools.set"), 1)
                choices = chat.choices()
                assert choices is not None
                self.assertEqual(choices.instructions, "internal")
                self.assertTrue(child.outcome is not None and child.outcome.is_aborted)
            finally:
                release.set()
                await self._settle_lifecycle_tasks(running, closing, drain)
                store.close()

    async def _settle_lifecycle_tasks(self, *tasks: Awaitable[object] | None):
        await asyncio.gather(*(task for task in tasks if task is not None), return_exceptions=True)

    async def test_revision_cancelled_public_history_error_disconnects_known_session(self):
        await self._check_public_history_error_cleanup(cancelled=True, detach_error=None)

    async def test_revision_cancelled_public_history_and_disconnect_errors_both_reach_shutdown(self):
        await self._check_public_history_error_cleanup(
            cancelled=True, detach_error="late known-session disconnect failure")

    async def test_revision_public_history_failure_preserves_caller_error_and_cleanup(self):
        for detach_error in (None, "late known-session disconnect failure"):
            await self._check_public_history_error_cleanup(cancelled=False, detach_error=detach_error)

    async def _check_public_history_error_cleanup(self, *, cancelled, detach_error):
        for facade, owned in ((False, False), (True, False), (True, True)):
            for operation in ("create", "root-new", "root-existing", "resume-unbound",
                              "resume-existing", "resume-missing", "fork"):
                with self.subTest(facade=facade, owned=owned, operation=operation,
                                  cancelled=cancelled, detach_error=detach_error):
                    with tempfile.TemporaryDirectory() as directory:
                        async with ControlledSdkPeer() as peer:
                            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                            store = DurableStore.create(options)
                            client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                            await client.start()
                            host = None
                            if facade:
                                host = (DurableClientBuilder().use_store(store).use_client(client)
                                        .configure(lambda o: setattr(o, "owns_store", owned))
                                        .configure(lambda o: setattr(o, "owns_client", owned)).build())
                                await host.initialize_async()
                                harness = host._harness
                                assert harness is not None
                            else:
                                harness = DurableHarness(client, store, ExtensionRegistry())
                            target = host if host else harness
                            parent = None
                            identifier = ""
                            if operation in ("root-existing", "resume-existing", "resume-missing", "fork"):
                                parent = await target.create_conversation_async()
                                identifier = parent.id
                                if operation != "fork":
                                    await parent.session.disconnect()
                                    parent.detach()
                            elif operation == "resume-unbound":
                                async def seed(tx):
                                    return tx.create_conversation().id
                                identifier = await store.commit_async(seed)
                            peer.metadata_exists = operation in ("root-existing", "resume-existing")
                            peer.held_method = "session.getMessages"
                            peer.failure = "late known-session history RPC failure"
                            peer.detach_error = detach_error
                            peer.detach_received.clear()
                            peer.detach_release.clear()
                            detach_count = peer.methods.count("session.detach")
                            action = (target.create_conversation_async() if operation == "create"
                                      else target.root_async() if operation.startswith("root-")
                                      else target.fork_async(parent, store.entries(parent.id)[-1])
                                      if operation == "fork" and parent is not None
                                      else target.resume_conversation_async(identifier))
                            pending = asyncio.create_task(action)
                            closing = detaching = drain = None
                            try:
                                await asyncio.wait_for(peer.received.wait(), 10)
                                conversation = harness.conversations[-1]
                                session = conversation.session
                                self.assertIsNotNone(session, "history must already own a real public SDK session")
                                session_id = session.session_id
                                detached_count = peer.detached.count(session_id)
                                detach_attempts = peer.detach_attempts.count(session_id)
                                received_event = asyncio.Event()
                                unsubscribe_probe = session.on(lambda _: received_event.set())
                                sequence = store.provider.sequence
                                if cancelled:
                                    pending.cancel("owner cancelled after public history RPC receipt")
                                    with self.assertRaises(asyncio.CancelledError) as cancellation:
                                        await pending
                                    self.assertEqual(str(cancellation.exception),
                                                     "owner cancelled after public history RPC receipt")
                                    self.assertIsNone(conversation.session)
                                    self.assertIsNone(conversation._unsubscribe)
                                closing = asyncio.create_task(target.close_async())
                                drain = await self._start_close_gate(harness, closing)
                                self.assertTrue(harness._sdk_operations)
                                self.assertFalse(peer.finished.is_set())
                                self.assertFalse(closing.done())
                                self.assertFalse(store._closed)
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                                with self.assertRaisesRegex(RuntimeError, "another durable runtime"):
                                    DurableHarness(client, store, ExtensionRegistry())
                                peer.release.set()
                                if not cancelled:
                                    with self.assertRaisesRegex(Exception, "late known-session history RPC failure"):
                                        await pending
                                detaching = asyncio.create_task(peer.detach_received.wait())
                                await asyncio.wait((detaching, closing), return_when=asyncio.FIRST_COMPLETED)
                                self.assertTrue(peer.detach_received.is_set(),
                                                "late history RPC failure must still disconnect the known session")
                                await detaching
                                self.assertTrue(peer.finished.is_set())
                                self.assertEqual(peer.methods.count("session.detach"), detach_count + 1)
                                self.assertEqual(peer.detach_attempts.count(session_id), detach_attempts + 1)
                                self.assertTrue(harness._sdk_operations,
                                                "cleanup ownership must survive the history error until detach settles")
                                self.assertFalse(closing.done(), "shutdown must retain the held disconnect response")
                                self.assertFalse(drain.done(), "scheduler ownership must include disconnect cleanup")
                                self.assertFalse(store._closed)
                                with self.assertRaisesRegex(RuntimeError, "pending work"):
                                    harness.close()
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                                with self.assertRaisesRegex(RuntimeError, "another durable runtime"):
                                    DurableHarness(client, store, ExtensionRegistry())
                                event_id = "00000000-0000-0000-0000-000000000001"
                                await peer.send_event(session_id, {
                                    "type": "tool.execution_complete", "id": event_id,
                                    "timestamp": "2026-11-02T00:40:27.912-05:00",
                                    "data": {"toolCallId": "late-history-tool", "success": True,
                                             "result": {"content": "must not persist after cancellation"}},
                                })
                                await asyncio.wait_for(received_event.wait(), 10)
                                unsubscribe_probe()
                                await conversation.checkpoint_barrier_async()
                                self.assertNotIn(event_id, conversation._observed_events,
                                                 "a cancelled/failed history attachment must remove its listener")
                                self.assertIsNone(conversation.session)
                                self.assertIsNone(conversation._unsubscribe)
                                self.assertEqual(store.provider.sequence, sequence, "no late checkpoint or durable continuation")
                                peer.detach_release.set()
                                if cancelled or detach_error is not None:
                                    with self.assertRaises(Exception) as failure:
                                        await closing
                                    if cancelled:
                                        self.assertIn("late known-session history RPC failure", str(failure.exception))
                                        self.assertIs(failure.exception, harness._sdk_failures[0])
                                        if detach_error is not None:
                                            self.assertEqual(len(harness._sdk_failures), 2,
                                                             "shutdown must retain both history and disconnect failures")
                                            additional = failure.exception.__cause__
                                            self.assertIsInstance(additional, BaseExceptionGroup)
                                            assert isinstance(additional, BaseExceptionGroup)
                                            self.assertIs(additional.exceptions[0], harness._sdk_failures[1])
                                            self.assertIn(detach_error, str(additional.exceptions[0]),
                                                          "shutdown must preserve disconnect failure as well as history failure")
                                    else:
                                        self.assertIn(detach_error, str(failure.exception))
                                else:
                                    await closing
                                await drain
                                self.assertFalse(harness._sdk_operations)
                                self.assertFalse(harness._conversation_operations)
                                self.assertEqual(harness.conversations, [])
                                # An owned client's public stop also attempts failed sessions.
                                stop_attempt = int(detach_error is not None and (not facade or owned))
                                self.assertEqual(peer.detach_attempts.count(session_id),
                                                 detach_attempts + 1 + stop_attempt)
                                self.assertEqual(peer.detached.count(session_id),
                                                 detached_count + (0 if detach_error else 1))
                                self.assertEqual(store.provider.sequence, sequence)
                                if operation == "fork":
                                    intent = harness.fork_intent_of(conversation.id)
                                    assert intent is not None
                                    self.assertIs(intent.state, ForkIntentState.COMPLETED)
                                    self.assertEqual(intent.session_id, session_id)
                                self.assertEqual(store._closed, owned)
                                if owned:
                                    reopened = DurableStore.create(options)
                                    reopened.close()
                                else:
                                    with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                        DurableStore.create(options)
                                if facade and not owned:
                                    metadata_calls = peer.methods.count("session.getMetadata")
                                    await client.get_session_metadata("borrowed-client-still-open")
                                    self.assertEqual(peer.methods.count("session.getMetadata"), metadata_calls + 1)
                                    if detach_error is None:
                                        with self.assertRaisesRegex(RuntimeError, "Session is disconnected"):
                                            session.on(lambda _: None)
                            finally:
                                peer.release.set()
                                peer.detach_release.set()
                                peer.detach_error = None
                                if detaching is not None and not detaching.done():
                                    detaching.cancel()
                                await self._settle_lifecycle_tasks(pending, closing, drain, detaching)
                                await target.close_async()
                                await client.stop()
                                store.close()

    async def test_revision_published_closed_harness_rejects_new_facade_operations(self):
        for synchronous in (False, True):
            for operation in ("create", "root", "resume", "fork"):
                with self.subTest(synchronous=synchronous, operation=operation):
                    store = DurableStore.create_ephemeral()
                    session = sdk_session()
                    client = sdk_client(session)
                    async def create_session(**kwargs):
                        session.session_id = kwargs["session_id"]
                        return session
                    client.create_session.side_effect = create_session
                    host = DurableClientBuilder().use_store(store).use_client(client).build()
                    await host.initialize_async()
                    harness = host._harness
                    assert harness is not None
                    parent = await host.create_conversation_async()
                    boundary = store.entries(parent.id)[-1]
                    if synchronous:
                        harness.close()
                    else:
                        await harness.close_async()
                    before = store.provider.sequence
                    calls = client.create_session.await_count
                    try:
                        with self.assertRaisesRegex(RuntimeError, "closing"):
                            if operation == "create":
                                await host.create_conversation_async()
                            elif operation == "root":
                                await host.root_async()
                            elif operation == "resume":
                                await host.resume_conversation_async(parent.id)
                            else:
                                await host.fork_async(parent, boundary)
                        self.assertEqual(store.provider.sequence, before, "no writes after harness drain")
                        self.assertEqual(client.create_session.await_count, calls, "no SDK calls after stop")
                        self.assertEqual(session.listeners, [], "no listeners after harness drain")
                        self.assertEqual(harness.conversations, [])
                    finally:
                        await host.close_async()
                        store.close()

    async def test_revision_issued_public_sdk_rpc_cancellation_retains_shutdown_ownership(self):
        for facade, owned in ((False, False), (True, False), (True, True)):
            for operation in ("create", "root", "resume-unbound", "resume-existing", "resume-missing",
                              "fork", "fork-resume", "create-history", "resume-existing-history", "fork-history"):
                with self.subTest(facade=facade, owned=owned, operation=operation):
                    kind = operation.removesuffix("-history")
                    if kind == "fork-resume":
                        kind = "fork"
                    with tempfile.TemporaryDirectory() as directory:
                        async with ControlledSdkPeer() as peer:
                            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                            store = DurableStore.create(options)
                            client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                            await client.start()
                            host = None
                            if facade:
                                host = (DurableClientBuilder().use_store(store).use_client(client)
                                        .configure(lambda o: setattr(o, "owns_store", owned))
                                        .configure(lambda o: setattr(o, "owns_client", owned)).build())
                                await host.initialize_async()
                                harness = host._harness
                                assert harness is not None
                            else:
                                harness = DurableHarness(client, store, ExtensionRegistry())
                            target = host if host else harness
                            parent = None
                            if kind in ("fork", "resume-existing", "resume-missing"):
                                parent = await target.create_conversation_async()
                                identifier = parent.id
                                if kind in ("resume-existing", "resume-missing"):
                                    parent.detach()
                            else:
                                async def seed(tx):
                                    return tx.create_conversation().id
                                identifier = await store.commit_async(seed) if kind == "resume-unbound" else ""
                            peer.metadata_exists = kind == "resume-existing"
                            peer.held_method = ("session.getMessages" if operation.endswith("-history")
                                                else "session.resume" if operation == "fork-resume"
                                                else "sessions.fork" if kind == "fork"
                                                else "session.resume" if kind == "resume-existing"
                                                else "session.create")
                            peer.detach_release.clear()
                            action = (target.create_conversation_async() if kind == "create"
                                      else target.root_async() if kind == "root"
                                      else target.fork_async(parent, store.entries(parent.id)[-1]) if parent and kind == "fork"
                                      else target.resume_conversation_async(identifier))
                            creating = asyncio.create_task(action)
                            closing = second = drain = detaching = None
                            try:
                                await asyncio.wait_for(peer.received.wait(), 10)
                                creating.cancel("initiating caller cancelled after public RPC receipt")
                                with self.assertRaisesRegex(asyncio.CancelledError, "initiating caller"):
                                    await creating
                                binding = harness.conversations[-1].binding()
                                if kind == "fork":
                                    late_id = "controlled-child"
                                else:
                                    assert binding is not None
                                    late_id = binding.session_id
                                closing = asyncio.create_task(target.close_async())
                                second = asyncio.create_task(target.close_async())
                                drain = await self._start_close_gate(harness, closing)
                                self.assertFalse(peer.finished.is_set(), "the server is still executing the issued RPC")
                                self.assertTrue(harness._conversation_operations or getattr(harness, "_sdk_operations", ()),
                                                "cancelled caller must leave issued RPC ownership with shutdown")
                                self.assertFalse(closing.done())
                                self.assertFalse(second.done())
                                closing.cancel("cancelled closer")
                                with self.assertRaisesRegex(RuntimeError, "pending work"):
                                    harness.close()
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                                with self.assertRaisesRegex(RuntimeError, "another durable runtime"):
                                    DurableHarness(client, store, ExtensionRegistry())
                                peer.release.set()
                                detaching = asyncio.create_task(peer.detach_received.wait())
                                await asyncio.wait((detaching, second), return_when=asyncio.FIRST_COMPLETED)
                                self.assertTrue(peer.detach_received.is_set(),
                                                "shutdown must disconnect the late session, not merely forget its fence")
                                await detaching
                                self.assertTrue(peer.finished.is_set())
                                self.assertFalse(second.done(), "SDK detach response also belongs to shutdown")
                                self.assertFalse(store._closed)
                                sequence = store.provider.sequence
                                peer.detach_release.set()
                                with self.assertRaisesRegex(asyncio.CancelledError, "cancelled closer"):
                                    await closing
                                await asyncio.gather(second, drain)
                                self.assertEqual(peer.detached.count(late_id), 1,
                                                 "late SDK session must be disconnected exactly once")
                                self.assertIn("session.detach", peer.methods)
                                self.assertEqual(peer.notifications, [], "create cancellation does not cancel the server RPC")
                                self.assertFalse(harness._conversation_operations)
                                self.assertFalse(harness._sdk_operations)
                                self.assertEqual(harness.conversations, [])
                                self.assertEqual(store.provider.sequence, sequence, "no late durable-core continuation")
                                self.assertEqual(store._closed, owned)
                                if owned:
                                    reopened = DurableStore.create(options)
                                    reopened.close()
                                else:
                                    with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                        DurableStore.create(options)
                            finally:
                                peer.release.set()
                                peer.detach_release.set()
                                if detaching is not None and not detaching.done():
                                    detaching.cancel()
                                await self._settle_lifecycle_tasks(creating, closing, second, drain, detaching)
                                await target.close_async()
                                await client.stop()
                                store.close()

    async def test_revision_direct_harness_close_retains_prepublication_facade_admission(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        client = sdk_client(session)
        async def create_session(**kwargs):
            session.session_id = kwargs["session_id"]
            return session
        client.create_session.side_effect = create_session
        host = DurableClientBuilder().use_store(store).use_client(client).build()
        async def seed(tx):
            return tx.create_conversation()
        parent = DurableConversation(store, await store.commit_async(seed), host.registry)
        published, release = asyncio.Event(), asyncio.Event()
        initialize = host._initialize_core_async
        async def initialize_held():
            await initialize()
            published.set()
            await release.wait()
        host._initialize_core_async = initialize_held
        accepted = asyncio.create_task(host.root_async())
        closing = None
        try:
            await published.wait()
            harness = host._harness
            assert harness is not None
            with self.assertRaisesRegex(RuntimeError, "pending work"):
                harness.close()
            closing = asyncio.create_task(harness.close_async())
            drain = await self._start_close_gate(harness, closing)
            for operation in (host.root_async(), host.create_conversation_async(),
                              host.resume_conversation_async("missing"), host.fork_async(parent, "missing")):
                with self.assertRaisesRegex(RuntimeError, "closing"):
                    await operation
            self.assertFalse(closing.done())
            release.set()
            chat = await accepted
            await asyncio.gather(closing, drain)
            self.assertEqual(client.create_session.await_count, 1)
            self.assertIsNone(chat.session)
            self.assertEqual(session.listeners, [])
            self.assertFalse(store._closed)
        finally:
            release.set()
            await self._settle_lifecycle_tasks(accepted, closing)
            await host.close_async()
            store.close()

    async def test_revision_public_sdk_failure_after_cancellation_belongs_to_shutdown(self):
        for operation in ("create", "resume", "fork"):
            for cancelled in (False, True):
                with self.subTest(operation=operation, cancelled=cancelled):
                    async with ControlledSdkPeer() as peer:
                        client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                        await client.start()
                        store = DurableStore.create_ephemeral()
                        harness = DurableHarness(client, store, ExtensionRegistry())
                        parent = await harness.create_conversation_async() if operation != "create" else None
                        if operation == "resume":
                            assert parent is not None
                            parent.detach()
                            peer.metadata_exists = True
                        peer.held_method = {"create": "session.create", "resume": "session.resume",
                                            "fork": "sessions.fork"}[operation]
                        peer.failure = "original controlled SDK failure"
                        if operation == "create":
                            action = harness.create_conversation_async()
                        else:
                            assert parent is not None
                            action = (harness.resume_conversation_async(parent.id) if operation == "resume"
                                      else harness.fork_async(parent, store.entries(parent.id)[-1]))
                        pending = asyncio.create_task(action)
                        closing = None
                        try:
                            await asyncio.wait_for(peer.received.wait(), 10)
                            if cancelled:
                                pending.cancel("caller cancellation is preserved")
                                with self.assertRaisesRegex(asyncio.CancelledError, "caller cancellation"):
                                    await pending
                            closing = asyncio.create_task(harness.close_async())
                            drain = await self._start_close_gate(harness, closing)
                            peer.release.set()
                            if cancelled:
                                with self.assertRaisesRegex(Exception, "original controlled SDK failure") as failure:
                                    await closing
                                self.assertIs(failure.exception, harness._sdk_failures[0])
                            else:
                                with self.assertRaisesRegex(Exception, "original controlled SDK failure"):
                                    await pending
                                await closing
                            await drain
                            self.assertTrue(peer.finished.is_set())
                            self.assertEqual(peer.detached.count("controlled-child"), 0)
                            self.assertFalse(harness._sdk_operations)
                            if operation == "fork":
                                intent = harness.fork_intent_of(store.conversations[-1].id)
                                assert intent is not None
                                self.assertIs(intent.state, ForkIntentState.PENDING)
                        finally:
                            peer.release.set()
                            await self._settle_lifecycle_tasks(pending, closing)
                            await harness.close_async()
                            store.close()

    async def test_revision_cancelled_before_store_dispatch_issues_no_public_sdk_rpc(self):
        for facade in (False, True):
            with self.subTest(facade=facade):
                async with ControlledSdkPeer() as peer:
                    client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                    await client.start()
                    store = DurableStore.create_ephemeral()
                    host = DurableClientBuilder().use_store(store).use_client(client).build() if facade else None
                    if host:
                        await host.initialize_async()
                        harness = host._harness
                        assert harness is not None
                    else:
                        harness = DurableHarness(client, store, ExtensionRegistry())
                    target = host if host else harness
                    held, release, queued = asyncio.Event(), asyncio.Event(), asyncio.Event()
                    async def hold(tx):
                        held.set()
                        await release.wait()
                    holding = asyncio.create_task(store.commit_async(hold))
                    await held.wait()
                    commit = store.commit_async
                    async def committing(body):
                        queued.set()
                        return await commit(body)
                    store.commit_async = committing
                    pending = asyncio.create_task(target.create_conversation_async())
                    try:
                        await queued.wait()
                        pending.cancel("queued before SDK dispatch")
                        with self.assertRaisesRegex(asyncio.CancelledError, "queued before SDK dispatch"):
                            await pending
                        self.assertEqual(peer.methods, ["connect"])
                        self.assertEqual(store.conversations, [])
                        self.assertFalse(harness._sdk_operations)
                        release.set()
                        await holding
                        await target.close_async()
                        self.assertEqual(peer.methods.count("session.create"), 0)
                    finally:
                        release.set()
                        await self._settle_lifecycle_tasks(pending, holding)
                        await target.close_async()
                        await client.stop()
                        store.close()

    async def test_revision_fork_post_sdk_transaction_cancellation_and_failure_disconnect(self):
        for commit_number in (2, 3):
            for cancelled in (False, True):
                with self.subTest(commit_number=commit_number, cancelled=cancelled):
                    with tempfile.TemporaryDirectory() as directory:
                        async with ControlledSdkPeer() as peer:
                            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                            store = DurableStore.create(options)
                            client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
                            await client.start()
                            host = (DurableClientBuilder().use_store(store).use_client(client)
                                    .configure(lambda o: setattr(o, "owns_store", True))
                                    .configure(lambda o: setattr(o, "owns_client", True)).build())
                            await host.initialize_async()
                            harness = host._harness
                            assert harness is not None
                            parent = await host.create_conversation_async()
                            commit = store.commit_async
                            entered, release = asyncio.Event(), asyncio.Event()
                            original = ValueError("original post-SDK transaction failure")
                            count = 0
                            before = 0
                            async def committing(body):
                                nonlocal count, before
                                count += 1
                                if count != commit_number:
                                    return await commit(body)
                                before = store.provider.sequence
                                async def failing(tx):
                                    await body(tx)
                                    entered.set()
                                    await release.wait()
                                    raise original
                                return await commit(failing)
                            store.commit_async = committing
                            peer.detach_release.clear()
                            pending = asyncio.create_task(host.fork_async(parent, store.entries(parent.id)[-1]))
                            closing = detaching = None
                            try:
                                await entered.wait()
                                if cancelled:
                                    pending.cancel("cancelled post-SDK transaction")
                                else:
                                    release.set()
                                with self.assertRaises(asyncio.CancelledError if cancelled else ValueError) as caught:
                                    await pending
                                if not cancelled:
                                    self.assertIs(caught.exception, original)
                                self.assertEqual(store.provider.sequence, before, "the failed transaction must roll back")
                                closing = asyncio.create_task(host.close_async())
                                drain = await self._start_close_gate(harness, closing)
                                detaching = asyncio.create_task(peer.detach_received.wait())
                                await asyncio.wait((detaching, closing), return_when=asyncio.FIRST_COMPLETED)
                                self.assertTrue(peer.detach_received.is_set(), "post-SDK failure must disconnect the child")
                                await detaching
                                self.assertFalse(closing.done())
                                self.assertFalse(store._closed)
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                                peer.detach_release.set()
                                await asyncio.gather(closing, drain)
                                self.assertEqual(peer.detached.count("controlled-child"), 1)
                                self.assertEqual(store.provider.sequence, before)
                                self.assertTrue(store._closed)
                                reopened = DurableStore.create(options)
                                reopened.close()
                            finally:
                                release.set()
                                peer.detach_release.set()
                                if detaching is not None and not detaching.done():
                                    detaching.cancel()
                                await self._settle_lifecycle_tasks(pending, closing, detaching)
                                await host.close_async()

    async def test_revision_public_sdk_cancelled_session_cleanup_failure_is_reported(self):
        async with ControlledSdkPeer() as peer:
            client = CopilotClient(connection=RuntimeConnection.for_uri(f"127.0.0.1:{peer.port}"))
            await client.start()
            store = DurableStore.create_ephemeral()
            host = DurableClientBuilder().use_store(store).use_client(client).build()
            await host.initialize_async()
            harness = host._harness
            assert harness is not None
            peer.held_method = "session.create"
            peer.detach_error = "explicit controlled detach failure"
            pending = asyncio.create_task(host.create_conversation_async())
            closing = None
            try:
                await peer.received.wait()
                pending.cancel("caller cancelled")
                with self.assertRaisesRegex(asyncio.CancelledError, "caller cancelled"):
                    await pending
                closing = asyncio.create_task(host.close_async())
                peer.release.set()
                with self.assertRaisesRegex(RuntimeError, "explicit controlled detach failure") as failure:
                    await closing
                self.assertIs(failure.exception, harness._sdk_failures[0])
                self.assertEqual(peer.methods.count("session.detach"), 1, "cleanup must not retry indefinitely")
                self.assertFalse(harness._sdk_operations)
                self.assertEqual(harness.conversations, [])
                self.assertFalse(store._closed, "borrowed store must not be closed even on cleanup failure")
            finally:
                peer.release.set()
                peer.detach_error = None
                await self._settle_lifecycle_tasks(pending, closing)
                await host.close_async()
                await client.stop()
                store.close()

    async def test_review_conversation_lifecycle_retains_resources(self):
        from copilot._jsonrpc import JsonRpcClient
        for facade, owned in ((False, False), (True, False), (True, True)):
            for operation in ("create", "root", "resume-unbound", "resume-existing", "resume-missing", "fork"):
                for stage in ("store", "sdk", "history"):
                    if stage == "store" and operation in ("resume-existing", "resume-missing"):
                        continue
                    with self.subTest(facade=facade, owned=owned, operation=operation, stage=stage), tempfile.TemporaryDirectory() as directory:
                        path = Path(directory) / "store.jsonl"
                        options = DurableStoreOptions(path=str(path))
                        host = None
                        if facade:
                            builder = DurableClientBuilder()
                            builder.use_store_options(options) if owned else builder.use_store(DurableStore.create(options))
                            host = builder.build()
                            await host.initialize_async()
                            harness, store = host._harness, host.store
                        else:
                            store = DurableStore.create(options)
                            harness = await DurableHarness.open_async(store, ExtensionRegistry())
                        assert harness is not None and store is not None
                        entered, release = asyncio.Event(), asyncio.Event()
                        stopped = False
                        boundary = None
                        stop = harness.client.stop
                        async def stop_client():
                            nonlocal stopped, boundary
                            stopped = True
                            assert store is not None
                            boundary = (store.provider.sequence, path.read_bytes() if path.exists() else b"")
                            return await stop()
                        harness.client.stop = stop_client
                        create_session = harness.client.create_session
                        resume_session = harness.client.resume_session
                        async def guarded_create(**kwargs):
                            self.assertFalse(stopped, "session create must not run after SDK stop")
                            return await create_session(**kwargs)
                        async def guarded_resume(session_id, **kwargs):
                            self.assertFalse(stopped, "session resume must not run after SDK stop")
                            return await resume_session(session_id, **kwargs)
                        harness.client.create_session = guarded_create
                        harness.client.resume_session = guarded_resume
                        holding = pending = closing = drain = None
                        result = None
                        try:
                            parent = None
                            fork_session_id = None
                            if operation == "fork":
                                # The replay fixture has no native fork response. Adapt
                                # that RPC alone; resume/history use the installed SDK.
                                fork_session_id = (await harness.client.create_session()).session_id
                            identifier = ""
                            if operation in ("fork", "resume-existing", "resume-missing"):
                                parent = await harness.create_conversation_async()
                                identifier = parent.id
                                if operation != "fork":
                                    parent.detach()
                                if operation == "resume-missing":
                                    harness.client.get_session_metadata = AsyncMock(return_value=None)
                            elif operation == "resume-unbound":
                                async def seed(tx):
                                    return tx.create_conversation().id
                                identifier = await store.commit_async(seed)
                            if stage == "store":
                                held = asyncio.Event()
                                async def hold(tx):
                                    held.set()
                                    await release.wait()
                                holding = asyncio.create_task(store.commit_async(hold))
                                await held.wait()
                                commit = store.commit_async
                                async def observe_commit(body):
                                    entered.set()
                                    return await commit(body)
                                store.commit_async = observe_commit
                            original = JsonRpcClient.request
                            async def request(client, method, params=None, **kwargs):
                                targeted = (stage == "sdk" and method in ("session.create", "session.resume", "sessions.fork")
                                            or stage == "history" and method == "session.getMessages")
                                if targeted:
                                    entered.set()
                                    await release.wait()
                                if method in ("session.create", "session.resume", "sessions.fork", "session.getMessages"):
                                    self.assertFalse(stopped, f"{method} must settle before SDK stop")
                                if method == "sessions.fork":
                                    return {"sessionId": fork_session_id}
                                return await original(client, method, params, **kwargs)
                            with patch.object(JsonRpcClient, "request", request):
                                target = host if host else harness
                                if operation == "create":
                                    action = target.create_conversation_async()
                                elif operation == "root":
                                    action = target.root_async()
                                elif operation == "fork":
                                    assert parent is not None
                                    action = target.fork_async(parent, store.entries(parent.id)[-1])
                                else:
                                    action = target.resume_conversation_async(identifier)
                                pending = asyncio.create_task(action)
                                await asyncio.wait_for(entered.wait(), 2)
                                closing = asyncio.create_task(target.close_async())
                                if not facade:
                                    drain = await self._start_close_gate(harness, closing)
                                marker = asyncio.Event()
                                asyncio.get_running_loop().call_soon(marker.set)
                                await marker.wait()
                                self.assertTrue(harness._closed, "facade close must also reject direct harness admission")
                                self.assertFalse(stopped, "accepted lifecycle must retain SDK while held")
                                with self.assertRaisesRegex(RuntimeError, "pending work"):
                                    harness.close()
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                                with self.assertRaisesRegex(RuntimeError, "closing"):
                                    await harness.resume_conversation_async(identifier)
                                with self.assertRaisesRegex(RuntimeError, "closing"):
                                    await harness.create_conversation_async()
                                if parent:
                                    with self.assertRaisesRegex(RuntimeError, "closing"):
                                        await harness.fork_async(parent, store.entries(parent.id)[-1])
                                release.set()
                                if holding:
                                    await holding
                                result = await pending
                                binding = result.binding()
                                assert binding is not None
                                self.assertIsNotNone(binding.session_id,
                                                     "successful lifecycle must persist its SDK binding")
                                await closing
                                self.assertIsNone(result._unsubscribe, "shutdown must detach every accepted session")
                                self.assertIsNone(result.session)
                                self.assertFalse(harness._conversation_operations)
                            self.assertEqual((store.provider.sequence, path.read_bytes()), boundary,
                                             "all lifecycle writes must precede resource release")
                            if owned:
                                reopened = DurableStore.create(options)
                                self.assertIsNotNone(reopened.conversation(result.id))
                                reopened.close()
                            else:
                                with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                                    DurableStore.create(options)
                        finally:
                            release.set()
                            await self._settle_lifecycle_tasks(holding, pending, closing, drain)
                            await (host.close_async() if host else harness.close_async())
                            print(json.dumps({"facade": facade, "owned": owned, "operation": operation,
                                              "stage": stage, "stopped": stopped,
                                              "releaseSequence": boundary[0] if boundary else None,
                                              "sequence": store.provider.sequence,
                                              "result": result.id if result else None}))
                            store.close()

    async def test_review_closing_rejects_external_attached_conversation_admission(self):
        store = DurableStore.create_ephemeral()
        harness = await DurableHarness.open_async(store, ExtensionRegistry())
        chat = await harness.root_async()
        entered, release = asyncio.Event(), asyncio.Event()
        create = harness.client.create_session
        async def creating(**kwargs):
            entered.set()
            await release.wait()
            return await create(**kwargs)
        harness.client.create_session = creating
        pending = asyncio.create_task(harness.create_conversation_async())
        closing = None
        try:
            await entered.wait()
            closing = asyncio.create_task(harness.close_async())
            drain = await self._start_close_gate(harness, closing)
            before = store.provider.sequence
            with self.assertRaisesRegex(RuntimeError, "closing",
                                        msg="external resume must reject an already attached SDK session"):
                await harness.resume_conversation_async(chat.id)
            with self.assertRaisesRegex(RuntimeError, "closing"):
                await harness.root_async()
            self.assertEqual(store.provider.sequence, before)
            release.set()
            await asyncio.gather(pending, closing, drain)
        finally:
            release.set()
            await self._settle_lifecycle_tasks(pending, closing)
            await harness.close_async()
            store.close()

    async def test_review_lifecycle_failure_and_cancellation_drain_cleanup(self):
        from copilot._jsonrpc import JsonRpcClient
        for operation in ("create", "resume", "fork"):
            for stage in ("store", "sdk"):
                for cancel in (False, True):
                    with self.subTest(operation=operation, stage=stage, cancel=cancel), tempfile.TemporaryDirectory() as directory:
                        path = Path(directory) / "store.jsonl"
                        options = DurableStoreOptions(path=str(path))
                        store = DurableStore.create(options)
                        harness = await DurableHarness.open_async(store, ExtensionRegistry())
                        parent = await harness.create_conversation_async()
                        async def seed(tx):
                            return tx.create_conversation().id
                        identifier = await store.commit_async(seed)
                        before, count = store.provider.sequence, len(store.conversations)
                        entered, release, cleanup_entered, cleanup_release = (asyncio.Event() for _ in range(4))
                        original = ValueError(f"{operation}/{stage} failed")
                        stopped = False
                        stop = harness.client.stop
                        async def stop_client():
                            nonlocal stopped
                            stopped = True
                            return await stop()
                        harness.client.stop = stop_client
                        holding = pending = closing = second = None
                        request = JsonRpcClient.request
                        async def fail_request(client, method, params=None, **kwargs):
                            if method in ("session.create", "sessions.fork"):
                                entered.set()
                                try:
                                    await release.wait()
                                    raise original
                                finally:
                                    cleanup_entered.set()
                                    await cleanup_release.wait()
                                    self.assertFalse(stopped, "SDK failure/cancellation cleanup precedes stop")
                            return await request(client, method, params, **kwargs)
                        try:
                            if stage == "store":
                                commit = store.commit_async
                                async def failing_commit(body):
                                    async def fail(tx):
                                        await body(tx)
                                        entered.set()
                                        await release.wait()
                                        raise original
                                    return await commit(fail)
                                store.commit_async = failing_commit
                            with patch.object(JsonRpcClient, "request", fail_request):
                                action = (harness.create_conversation_async() if operation == "create"
                                          else harness.resume_conversation_async(identifier) if operation == "resume"
                                          else harness.fork_async(parent, store.entries(parent.id)[-1]))
                                pending = asyncio.create_task(action)
                                await entered.wait()
                                closing = asyncio.create_task(harness.close_async())
                                second = asyncio.create_task(harness.close_async())
                                drain = await self._start_close_gate(harness, closing)
                                closing.cancel("closer cancelled")
                                if cancel:
                                    pending.cancel("lifecycle cancelled")
                                    if stage == "sdk":
                                        release.set()
                                else:
                                    release.set()
                                if stage == "sdk":
                                    await cleanup_entered.wait()
                                    self.assertFalse(stopped)
                                cleanup_release.set()
                                with self.assertRaises(asyncio.CancelledError if cancel else ValueError) as caught:
                                    await pending
                                if not cancel:
                                    self.assertIs(caught.exception, original)
                                with self.assertRaisesRegex(asyncio.CancelledError, "closer cancelled"):
                                    await closing
                                if cancel and stage == "sdk":
                                    with self.assertRaises(ValueError) as shutdown:
                                        await second
                                    self.assertIs(shutdown.exception, original,
                                                  "shutdown owns the abandoned SDK failure")
                                    await drain
                                else:
                                    await asyncio.gather(second, drain)
                            self.assertTrue(stopped)
                            if stage == "store":
                                self.assertEqual((store.provider.sequence, len(store.conversations)), (before, count))
                            elif operation == "fork":
                                intent = harness.fork_intent_of(store.conversations[-1].id)
                                assert intent is not None
                                self.assertIs(intent.state, ForkIntentState.PENDING,
                                              "failed SDK fork must not be falsely marked completed")
                            self.assertTrue(all(chat._unsubscribe is None for chat in (parent, *harness.conversations)))
                            bytes_after = path.read_bytes()
                            with self.assertRaisesRegex(RuntimeError, "closing"):
                                await harness.resume_conversation_async(identifier)
                            self.assertEqual(path.read_bytes(), bytes_after)
                        finally:
                            release.set()
                            cleanup_release.set()
                            await self._settle_lifecycle_tasks(holding, pending, closing, second)
                            await harness.close_async()
                            store.close()

    async def test_review_accepted_facade_lifecycle_owns_shared_initialization(self):
        from copilot import CopilotClient
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            host = DurableClientBuilder().use_store_options(options).build()
            entered, release = asyncio.Event(), asyncio.Event()
            start = CopilotClient.start
            create = CopilotClient.create_session
            starts = creates = 0
            async def starting(client):
                nonlocal starts
                starts += 1
                entered.set()
                await release.wait()
                return await start(client)
            async def creating(client, **kwargs):
                nonlocal creates
                creates += 1
                return await create(client, **kwargs)
            pending = closing = second = initializer = None
            try:
                with patch.object(CopilotClient, "start", starting), patch.object(CopilotClient, "create_session", creating):
                    pending = asyncio.create_task(host.root_async())
                    await entered.wait()
                    initializer = asyncio.create_task(host.initialize_async())
                    closing = asyncio.create_task(host.close_async())
                    second = asyncio.create_task(host.close_async())
                    marker = asyncio.Event()
                    asyncio.get_running_loop().call_soon(marker.set)
                    await marker.wait()
                    closing.cancel("initializing closer cancelled")
                    assert host.store is not None
                    self.assertFalse(host.store._closed)
                    with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                        DurableStore.create(options)
                    release.set()
                    result = await pending
                    binding = result.binding()
                    assert binding is not None
                    self.assertIsNotNone(binding.session_id)
                    with self.assertRaisesRegex(RuntimeError, "closing"):
                        await initializer
                    with self.assertRaisesRegex(asyncio.CancelledError, "initializing closer cancelled"):
                        await closing
                    await second
                self.assertEqual((starts, creates), (1, 1))
                assert host.store is not None
                self.assertTrue(host.store._closed)
                reopened = DurableStore.create(options)
                self.assertEqual(len(reopened.conversations), 1)
                reopened.close()
            finally:
                release.set()
                await self._settle_lifecycle_tasks(pending, closing, second, initializer)
                await host.close_async()

    async def test_review_cancelled_queued_conversation_does_not_abandon_other_admission(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            held, release, enrolled = asyncio.Event(), asyncio.Event(), asyncio.Queue()
            async def hold(tx):
                held.set()
                await release.wait()
            holding = asyncio.create_task(store.commit_async(hold))
            await held.wait()
            commit = store.commit_async
            async def queued(body):
                enrolled.put_nowait(True)
                return await commit(body)
            store.commit_async = queued
            first = asyncio.create_task(harness.create_conversation_async())
            second = asyncio.create_task(harness.create_conversation_async())
            await enrolled.get()
            await enrolled.get()
            closing = asyncio.create_task(harness.close_async())
            drain = await self._start_close_gate(harness, closing)
            try:
                first.cancel("queued lifecycle cancelled")
                with self.assertRaisesRegex(asyncio.CancelledError, "queued lifecycle cancelled"):
                    await first
                self.assertFalse(closing.done())
                with self.assertRaisesRegex(RuntimeError, "pending work"):
                    harness.close()
                release.set()
                result = await second
                await asyncio.gather(holding, closing, drain)
                self.assertEqual([chat.id for chat in store.conversations], [result.id])
                binding = result.binding()
                assert binding is not None
                self.assertIsNotNone(binding.session_id)
            finally:
                release.set()
                await asyncio.gather(first, second, holding, closing, drain, return_exceptions=True)
                await harness.close_async()
                store.close()

    async def test_review_published_harness_retains_facade_admission_before_attachment(self):
        with tempfile.TemporaryDirectory() as directory:
            host = DurableClientBuilder().use_store_options(
                DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))).build()
            initialize = host._initialize_core_async
            published, release = asyncio.Event(), asyncio.Event()
            async def initializing():
                await initialize()
                published.set()
                await release.wait()
            host._initialize_core_async = initializing
            pending = asyncio.create_task(host.root_async())
            closing = None
            try:
                await published.wait()
                harness = host._harness
                assert harness is not None
                with self.assertRaisesRegex(RuntimeError, "pending work"):
                    harness.close()
                closing = asyncio.create_task(host.close_async())
                marker = asyncio.Event()
                asyncio.get_running_loop().call_soon(marker.set)
                await marker.wait()
                self.assertFalse(closing.done())
                with self.assertRaisesRegex(RuntimeError, "closing"):
                    await harness.create_conversation_async()
                release.set()
                result = await pending
                await closing
                binding = result.binding()
                assert binding is not None
                self.assertIsNotNone(binding.session_id)
                assert host.store is not None
                self.assertTrue(host.store._closed)
            finally:
                release.set()
                await self._settle_lifecycle_tasks(pending, closing)
                await host.close_async()

    async def test_review_draining_parent_finishes_dependent_conversation_lifecycle(self):
        store, registry = DurableStore.create_ephemeral(), ExtensionRegistry()
        harness = await DurableHarness.open_async(store, registry)
        entered, release = asyncio.Event(), asyncio.Event()
        completed = False
        stop = harness.client.stop
        async def stopping():
            self.assertTrue(completed, "parent's dependent conversation must precede SDK release")
            return await stop()
        harness.client.stop = stopping
        async def phase(_input, _checkpoint, runtime):
            nonlocal completed
            entered.set()
            await release.wait()
            child = await harness.create_conversation_async(owner_task_id=runtime.task.id)
            self.assertIsNotNone(child.session_id)
            self.assertIs(await harness.resume_conversation_async(child.id), child)
            completed = True
            return await runtime.commit_async(lambda: TaskCommit.completed(child.id))
        definition = DurableTask.define("conversation-parent", lambda: {}, phase)
        registry.install(Extension("conversation-parent", tasks=[definition]))
        closing = None
        try:
            record = await harness.create_task_async(definition)
            await entered.wait()
            closing = asyncio.create_task(harness.close_async())
            drain = await self._start_close_gate(harness, closing)
            with self.assertRaisesRegex(RuntimeError, "closing"):
                await harness.create_conversation_async()
            release.set()
            await asyncio.gather(closing, drain)
            saved = store.task(record.id)
            assert saved is not None
            self.assertIs(saved.status, TaskStatus.TERMINAL)
            self.assertEqual([chat.owner_task_id for chat in store.conversations], [record.id])
        finally:
            release.set()
            if closing:
                await asyncio.gather(closing, return_exceptions=True)
            await harness.close_async()
            store.close()

    async def test_review_accepted_task_creation_behind_store_gate_belongs_to_shutdown(self):
        for facade, owned in ((False, False), (True, False), (True, True)):
            with self.subTest(facade=facade, owned=owned), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                client = sdk_client(sdk_session())
                registry = ExtensionRegistry()
                host = None
                if facade:
                    builder = DurableClientBuilder().use_client(client).use_registry(registry)
                    if owned:
                        builder.use_store_options(options)
                    else:
                        builder.use_store(DurableStore.create(options))
                    host = builder.configure(lambda o: setattr(o, "owns_client", True)).build()
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, registry)
                assert store is not None and harness is not None
                held, release, enrolled = asyncio.Event(), asyncio.Event(), asyncio.Event()
                phase_entered, phase_release = asyncio.Event(), asyncio.Event()
                released = False
                async def phase(_input, _checkpoint, runtime):
                    phase_entered.set()
                    await phase_release.wait()
                    self.assertFalse(released, "accepted checkpoint must precede resource release")
                    return await runtime.commit_async(lambda: TaskCommit.completed("saved"))
                definition = DurableTask.define("accepted", lambda: {}, phase)
                registry.install(Extension("accepted", tasks=[definition]))
                async def hold(tx):
                    held.set()
                    await release.wait()
                holding = asyncio.create_task(store.commit_async(hold))
                await held.wait()
                commit = store.commit_async
                async def observe_commit(body):
                    enrolled.set()
                    return await commit(body)
                store.commit_async = observe_commit
                creating = asyncio.create_task(harness.create_task_async(definition))
                await enrolled.wait()
                with self.assertRaisesRegex(RuntimeError, "pending work"):
                    harness.close()
                async def stop():
                    nonlocal released
                    assert store is not None
                    released = True
                    self.assertEqual(len(store.tasks), 1, "resource release must include accepted creation")
                    self.assertIs(store.tasks[0].status, TaskStatus.TERMINAL,
                                  "resource release must follow the final checkpoint")
                    outcome = store.tasks[0].outcome
                    assert outcome is not None
                    self.assertEqual(outcome.result, "saved")
                client.stop.side_effect = stop
                closing = asyncio.create_task(host.close_async() if host else harness.close_async())
                drain = await self._start_close_gate(harness, closing)
                try:
                    self.assertFalse(released, "close must retain resources while creation awaits the store gate")
                    self.assertEqual(len(store.tasks), 0, "creation is held at the real transaction gate")
                    with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                        DurableStore.create(options)
                    with self.assertRaisesRegex(RuntimeError, "scheduler|runner"):
                        DurableHarness(client, store, registry)
                    with self.assertRaisesRegex(RuntimeError, "closing"):
                        await harness.create_task_async(definition)
                    release.set()
                    await holding
                    result = (await asyncio.gather(creating, return_exceptions=True))[0]
                    self.assertNotIsInstance(result, BaseException,
                                             "accepted creation must not fail scheduling after its commit")
                    assert not isinstance(result, BaseException)
                    await asyncio.wait_for(phase_entered.wait(), 1)
                    self.assertFalse(released, "close must drain the newly scheduled phase")
                    phase_release.set()
                    record = result
                    await closing
                    terminal = store.task(record.id)
                    assert terminal is not None
                    self.assertIs(terminal.status, TaskStatus.TERMINAL)
                    assert options.path is not None
                    path = Path(options.path)
                    before = path.read_bytes()
                    sequence = store.provider.sequence
                    with self.assertRaisesRegex(RuntimeError, "closing"):
                        await harness.create_task_async(definition)
                    self.assertEqual(path.read_bytes(), before, "post-close creation must not write")
                    self.assertEqual(store.provider.sequence, sequence)
                    if owned:
                        reopened = DurableStore.create(options)
                        recovered = reopened.task(record.id)
                        assert recovered is not None
                        self.assertIs(recovered.status, TaskStatus.TERMINAL)
                        reopened.close()
                    else:
                        with self.assertRaisesRegex(RuntimeError, "writer|already open"):
                            DurableStore.create(options)
                        replacement = DurableHarness(client, store, registry)
                        replacement.close()
                finally:
                    release.set()
                    phase_release.set()
                    await asyncio.gather(holding, creating, closing, drain, return_exceptions=True)
                    print(json.dumps({"facade": facade, "owned": owned, "released": released,
                                      "creation": str(creating.exception()) if creating.done() and not creating.cancelled()
                                      and creating.exception() else "success",
                                      "tasks": [(task.id, task.status.value, task.outcome.result if task.outcome else None)
                                                for task in store.tasks]}))
                    store.close()

    async def test_review_failed_or_cancelled_accepted_creation_releases_its_drain(self):
        for cancel in (False, True):
            with self.subTest(cancel=cancel):
                store = DurableStore.create_ephemeral()
                client = sdk_client(sdk_session())
                harness = DurableHarness(client, store, ExtensionRegistry())
                held, release, enrolled = asyncio.Event(), asyncio.Event(), asyncio.Event()
                original = ValueError("initial checkpoint failed")
                def initial():
                    raise original
                definition = DurableTask.define("fails-before-task", initial, AsyncMock())
                async def hold(tx):
                    held.set()
                    await release.wait()
                holding = asyncio.create_task(store.commit_async(hold))
                await held.wait()
                commit = store.commit_async
                async def observe_commit(body):
                    enrolled.set()
                    return await commit(body)
                store.commit_async = observe_commit
                creating = asyncio.create_task(harness.create_task_async(definition))
                await enrolled.wait()
                closing = asyncio.create_task(harness.close_async())
                drain = await self._start_close_gate(harness, closing)
                try:
                    self.assertFalse(closing.done(), "close must enroll failed/cancelled creations too")
                    if cancel:
                        creating.cancel("cancel accepted creation")
                    release.set()
                    await holding
                    with self.assertRaises(asyncio.CancelledError if cancel else ValueError) as caught:
                        await creating
                    if not cancel:
                        self.assertIs(caught.exception, original)
                    await asyncio.wait_for(asyncio.shield(closing), 1)
                    self.assertEqual(list(store.tasks), [], "failed/cancelled creation must not persist a task")
                    self.assertEqual(store.provider.sequence, 0, "creation rollback must preserve the write sequence")
                    self.assertEqual(list(store.documents), [])
                    client.stop.assert_awaited_once()
                finally:
                    release.set()
                    await asyncio.gather(holding, creating, closing, drain, return_exceptions=True)
                    store.close()

    async def test_review_draining_parent_public_creation_retains_ownership(self):
        store = DurableStore.create_ephemeral()
        registry = ExtensionRegistry()
        harness = DurableHarness(sdk_client(sdk_session()), store, registry)
        entered, release = asyncio.Event(), asyncio.Event()
        async def child_phase(_input, _checkpoint, runtime):
            return await runtime.commit_async(lambda: TaskCommit.completed("child"))
        child = DurableTask.define("public-child", lambda: {}, child_phase)
        async def parent_phase(_input, _checkpoint, runtime):
            entered.set()
            await release.wait()
            record = await harness.create_task_async(child, owner_task_id=runtime.task.id)
            outcome = await harness.wait_for_task_async(record.id)
            self.assertEqual(outcome.result, "child")
            return await runtime.commit_async(lambda: TaskCommit.completed("parent"))
        parent = DurableTask.define("public-parent", lambda: {}, parent_phase)
        registry.install(Extension("public", tasks=[parent, child]))
        record = await harness.create_task_async(parent)
        await entered.wait()
        closing = asyncio.create_task(harness.close_async())
        drain = await self._start_close_gate(harness, closing)
        try:
            with self.assertRaisesRegex(RuntimeError, "closing"):
                await harness.create_task_async(child, owner_task_id=record.id)
            release.set()
            await asyncio.wait_for(asyncio.shield(closing), 1)
            self.assertEqual(len(store.tasks), 2)
            self.assertTrue(all(task.status is TaskStatus.TERMINAL for task in store.tasks))
            self.assertEqual([task.outcome.result for task in store.tasks if task.outcome], ["parent", "child"])
        finally:
            release.set()
            await asyncio.gather(closing, drain, return_exceptions=True)
            store.close()

    async def test_review_invalid_sandbox_leaves_no_poisoned_root(self):
        store = DurableStore.create_ephemeral()
        harness = DurableHarness(sdk_client(sdk_session()), store, ExtensionRegistry())
        try:
            with self.assertRaisesRegex(RuntimeError, "disables the sandbox"):
                await harness.root_async(AgentChoices(sandbox=DurableSandboxPolicy(
                    enabled=False, filesystem=DurableSandboxFilesystem(denied_paths=["/private"]))))
            self.assertEqual(len(store.conversations), 0, "invalid sandbox must not persist a root")
            chat = await harness.root_async(AgentChoices(model="corrected"))
            choices = chat.choices()
            assert choices is not None
            self.assertEqual(choices.model, "corrected")
        finally:
            await harness.close_async()
            store.close()

    async def test_review_draining_parent_can_schedule_dependency_not_external_work(self):
        store = DurableStore.create_ephemeral()
        registry = ExtensionRegistry()
        harness = DurableHarness(sdk_client(sdk_session()), store, registry)
        entered, release = asyncio.Event(), asyncio.Event()
        child_id = None
        async def child_phase(_input, _checkpoint, runtime):
            return await runtime.commit_async(lambda: TaskCommit.completed("child saved"))
        child = DurableTask.define("drain-child", lambda: {}, child_phase)
        async def parent_phase(_input, _checkpoint, runtime):
            nonlocal child_id
            entered.set()
            await release.wait()
            async def commit(tx):
                nonlocal child_id
                child_id = runtime.create_child_task(tx, child).id
                return TaskCommit.waiting({"phase": "done"}, [child_id])
            result = await runtime.commit_async(commit)
            runtime.schedule_task(child_id)
            return result
        async def done(_input, _checkpoint, runtime):
            return await runtime.commit_async(lambda: TaskCommit.completed("parent saved"))
        parent = DurableTask.define_phases("drain-parent", lambda: {"phase": "run"},
                                          {"run": parent_phase, "done": done})
        registry.install(Extension("drain", tasks=[parent, child]))
        record = await harness.create_task_async(parent)
        await entered.wait()
        closing = asyncio.create_task(harness.close_async())
        # Entering the first checkpoint drain proves close has closed admission.
        drain = await self._start_close_gate(harness, closing)
        try:
            with self.assertRaisesRegex(RuntimeError, "closing"):
                await harness.create_task_async(child)
            release.set()
            await asyncio.wait_for(asyncio.shield(closing), 1)
            parent_record = store.task(record.id)
            assert parent_record is not None and parent_record.outcome is not None
            self.assertEqual(parent_record.outcome.result, "parent saved",
                             "drain must not falsely fail the committed parent")
            self.assertIsNotNone(child_id)
            assert child_id is not None
            child_record = store.task(child_id)
            assert child_record is not None
            self.assertIs(child_record.status, TaskStatus.TERMINAL)
        finally:
            release.set()
            await asyncio.gather(closing, drain, return_exceptions=True)
            store.close()

    async def _start_close_gate(self, harness, closing):
        # Starting the shared drain is synchronous up to creating its dedicated core.
        drain = asyncio.create_task(harness._drain_async())
        entered = asyncio.Event()
        asyncio.get_running_loop().call_soon(entered.set)
        await entered.wait()
        self.assertTrue(harness._closed)
        return drain

    async def test_review_close_drains_direct_and_queued_submissions_through_both_hosts(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                session = sdk_session()
                client = sdk_client(session)
                host = None
                if facade:
                    host = (DurableClientBuilder().use_store_options(options).use_client(client)
                            .configure(lambda o: setattr(o, "owns_client", True)).build())
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, ExtensionRegistry())
                assert store is not None and harness is not None
                chat = await harness.root_async()
                async def stop_after_submissions():
                    admission = chat.admission()
                    assert admission is not None
                    self.assertEqual((admission.prompt, admission.state), ("second", "completed"),
                                     "SDK stop must await settled active and queued admissions")
                    self.assertEqual(session.listeners, [])
                client.stop.side_effect = stop_after_submissions
                sent = asyncio.Queue()
                async def send(prompt):
                    sent.put_nowait(prompt)
                    return "sent"
                session.send.side_effect = send
                a = asyncio.create_task(chat.submit_async("first"))
                b = asyncio.create_task(chat.submit_async("second"))
                self.assertEqual(await sent.get(), "first")
                enrolled = asyncio.Event()
                gather = asyncio.gather
                submission_cores = getattr(harness, "_submissions", set())
                def trace_drain(*awaitables, **kwargs):
                    if any(isinstance(item, asyncio.Task) and item in submission_cores for item in awaitables):
                        enrolled.set()
                    return gather(*awaitables, **kwargs)
                tracing = patch("durable.asyncio.gather", side_effect=trace_drain)
                tracing.start()
                close = host.close_async if host else harness.close_async
                closing = asyncio.create_task(close())
                drain = await self._start_close_gate(harness, closing)
                enrollment = asyncio.create_task(enrolled.wait())
                try:
                    await asyncio.wait((enrollment, closing), return_when=asyncio.FIRST_COMPLETED, timeout=1)
                    self.assertTrue(enrolled.is_set(), "shutdown must enroll direct and queued submission cores before cleanup")
                    async with asyncio.timeout(0.1):
                        with self.assertRaisesRegex(RuntimeError, "closing"):
                            await chat.submit_async("external")
                    self.assertFalse(closing.done(), "close must await active admission")
                    session.emit({"type": "session.idle", "data": {}})
                    self.assertEqual(await asyncio.wait_for(sent.get(), 1), "second")
                    self.assertFalse(closing.done(), "close must await queued admission")
                    session.emit({"type": "session.idle", "data": {}})
                    await asyncio.gather(a, b, closing, drain)
                    admission = chat.admission()
                    assert admission is not None
                    self.assertEqual(admission.state, "completed")
                    self.assertEqual(admission.prompt, "second")
                    self.assertEqual(session.listeners, [])
                    client.stop.assert_awaited_once()
                    sequence = store.provider.sequence
                    session.emit({"type": "session.idle", "data": {}})
                    self.assertEqual(store.provider.sequence, sequence)
                    if facade:
                        reopened = DurableStore.create(options)
                        reopened.close()
                finally:
                    enrollment.cancel()
                    for task in (a, b):
                        if not task.done():
                            task.cancel()
                    await asyncio.gather(a, b, closing, drain, enrollment, return_exceptions=True)
                    tracing.stop()
                    store.close()

    async def test_review_idle_before_send_rpc_waits_for_success_or_late_error(self):
        for fail in (False, True):
            with self.subTest(fail=fail):
                store = DurableStore.create_ephemeral()
                session = sdk_session()
                harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
                chat = await harness.root_async()
                entered, release = asyncio.Event(), asyncio.Event()
                original = RuntimeError("late public send rejection")
                async def send(prompt):
                    session.emit({"type": "assistant.message", "id": "answer", "data": {"content": "received"}})
                    session.emit({"type": "session.idle", "data": {}})
                    entered.set()
                    await release.wait()
                    if fail:
                        raise original
                session.send.side_effect = send
                run = asyncio.create_task(chat.submit_async("prompt"))
                await entered.wait()
                admission = chat.admission()
                assert admission is not None
                self.assertEqual(admission.state, "admitted")
                release.set()
                try:
                    if fail:
                        with self.assertRaises(RuntimeError) as raised:
                            await run
                        self.assertIs(raised.exception, original)
                    else:
                        self.assertEqual((await run)["data"]["content"], "received")
                    admission = chat.admission()
                    assert admission is not None
                    self.assertEqual(admission.state, "uncertain" if fail else "completed")
                    self.assertEqual(len(session.listeners), 1)
                finally:
                    release.set()
                    await harness.close_async()
                    store.close()

    async def test_review_cancelled_direct_submission_and_queued_rejection_drain_before_store_release(self):
        for facade in (False, True):
            with self.subTest(facade=facade), tempfile.TemporaryDirectory() as directory:
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                session = sdk_session()
                client = sdk_client(session)
                host = None
                if facade:
                    host = DurableClientBuilder().use_store_options(options).use_client(client).build()
                    await host.initialize_async()
                    store, harness = host.store, host._harness
                else:
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, ExtensionRegistry())
                assert store is not None and harness is not None
                chat = await harness.root_async()
                entered, settled, release = asyncio.Event(), asyncio.Event(), asyncio.Event()
                async def send(prompt):
                    entered.set()
                    try:
                        await asyncio.Event().wait()
                    finally:
                        await release.wait()
                        settled.set()
                session.send.side_effect = send
                first = asyncio.create_task(chat.submit_async("cancelled"))
                second = asyncio.create_task(chat.submit_async("queued"))
                await entered.wait()
                if not facade:
                    with self.assertRaisesRegex(RuntimeError, "pending work"):
                        harness.close()
                closing = asyncio.create_task(host.close_async() if host else harness.close_async())
                drain = await self._start_close_gate(harness, closing)
                first.cancel("submission owner interrupted")
                self.assertFalse(closing.done())
                self.assertFalse(settled.is_set())
                release.set()
                results = await asyncio.gather(first, second, closing, drain, return_exceptions=True)
                self.assertIsInstance(results[0], asyncio.CancelledError)
                self.assertIsInstance(results[1], DurableRecoveryRequiredException)
                self.assertTrue(settled.is_set(), "send cancellation cleanup must settle before shutdown")
                admission = chat.admission()
                assert admission is not None
                self.assertEqual((admission.prompt, admission.state), ("cancelled", "uncertain"))
                self.assertEqual(session.listeners, [])
                if facade:
                    client.stop.assert_not_awaited()
                    reopened = DurableStore.create(options)
                    reopened.close()
                else:
                    client.stop.assert_awaited_once()
                store.close()

    async def test_review_startup_failure_and_cancelled_initializer_preserve_borrowed_store(self):
        for fail in (False, True):
            with self.subTest(fail=fail):
                store = DurableStore.create_ephemeral()
                entered, release = asyncio.Event(), asyncio.Event()
                client = sdk_client(sdk_session())
                original = RuntimeError("startup failure on borrowed store")
                async def start():
                    entered.set()
                    await release.wait()
                    if fail:
                        raise original
                client.start = AsyncMock(side_effect=start)
                host = DurableClientBuilder().use_store(store).build()
                with patch("durable.CopilotClient", return_value=client):
                    initializer = asyncio.create_task(host.initialize_async())
                    await entered.wait()
                    initializer.cancel("initialization waiter interrupted")
                    with self.assertRaisesRegex(asyncio.CancelledError, "initialization waiter interrupted"):
                        await initializer
                    closing = asyncio.create_task(host.close_async())
                    release.set()
                    if fail:
                        with self.assertRaises(RuntimeError) as raised:
                            await closing
                        self.assertIs(raised.exception, original)
                    else:
                        await closing
                    self.assertFalse(store._closed, "borrowed store must not be closed on startup success or failure")
                    client.stop.assert_awaited_once()
                    replacement = DurableHarness(sdk_client(sdk_session()), store, ExtensionRegistry())
                    await replacement.close_async()
                    store.close()

    async def test_review_facade_close_joins_startup_success_failure_and_cancellation(self):
        for fail in (False, True):
            with self.subTest(fail=fail), tempfile.TemporaryDirectory() as directory:
                entered, release = asyncio.Event(), asyncio.Event()
                session = sdk_session()
                client = sdk_client(session)
                original = RuntimeError("public SDK startup failed")
                async def start():
                    entered.set()
                    await release.wait()
                    if fail:
                        raise original
                client.start = AsyncMock(side_effect=start)
                options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
                host = DurableClientBuilder().use_store_options(options).build()
                with patch("durable.CopilotClient", return_value=client):
                    init = asyncio.create_task(host.root_async())
                    await entered.wait()
                    first = asyncio.create_task(host.close_async())
                    entered_close = asyncio.Event()
                    asyncio.get_running_loop().call_soon(entered_close.set)
                    await entered_close.wait()
                    second = asyncio.create_task(host.close_async())
                    first.cancel("closer interrupted")
                    self.assertFalse(first.done(), "cancelled close must still join startup")
                    assert host.store is not None
                    self.assertFalse(host.store._closed, "owned store must remain locked until startup settles")
                    release.set()
                    results = await asyncio.gather(init, first, second, return_exceptions=True)
                    if fail:
                        self.assertIs(results[0], original)
                        self.assertIs(results[2], original, "close must preserve startup failure")
                    else:
                        self.assertIsInstance(results[0], DurableConversation)
                        assert isinstance(results[0], DurableConversation)
                        binding = results[0].binding()
                        assert binding is not None
                        self.assertIsNotNone(binding.session_id)
                        self.assertIsNone(results[2])
                    self.assertIsInstance(results[1], asyncio.CancelledError)
                    client.stop.assert_awaited_once()
                    self.assertEqual(client.create_session.await_count, 0 if fail else 1)
                    self.assertTrue(host.store is None or host.store._closed)
                    reopened = DurableStore.create(options)
                    reopened.close()
                    await host.close_async()

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
