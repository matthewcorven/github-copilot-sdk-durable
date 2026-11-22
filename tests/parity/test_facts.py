import asyncio
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import (
    AgentChoices, DocumentDefinition, DocumentHistory, ForkPolicy, DurableConversation, DurableHarness, DurableStore,
    DurableStoreOptions, DurableTask, Extension, ExtensionRegistry, TaskCommit, TaskStatus,
)


class ParityFacts(unittest.IsolatedAsyncioTestCase):
    async def test_transaction_rollback_leaves_no_partial_entry_or_document(self):
        store = DurableStore.create_ephemeral()
        doc = DocumentDefinition("parity.notes", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, lambda: [])
        try:
            async def create(tx):
                return tx.create_conversation()
            conversation = await store.commit_async(create)

            async def write(tx):
                draft = tx.doc(doc, conversation.id)
                draft.value = ["before"]
                entry = tx.append_entry(conversation.id, "note")
                draft.save(entry)
                return entry
            entry = await store.commit_async(write)

            async def interrupted(tx):
                tx.create_conversation()
                draft = tx.doc(doc, conversation.id)
                draft.value = ["partial"]
                draft.save(tx.append_entry(conversation.id, "partial"))
                tx.doc(DocumentDefinition("parity.partial", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, lambda: []), conversation.id).save(
                    tx.append_entry(conversation.id, "other"))
                raise RuntimeError("interrupted transaction")
            with self.assertRaisesRegex(RuntimeError, "interrupted transaction"):
                await store.commit_async(interrupted)
            self.assertEqual(store.entries(conversation.id), [entry], "rollback leaked a partial entry")
            self.assertEqual(store.document(doc.kind, conversation.id).latest, ["before"])
            self.assertEqual(len(store.documents), 1, "rollback leaked a new document")
            self.assertEqual(len(store.conversations), 1, "rollback leaked a conversation")
        finally:
            store.close()

    async def test_fork_inherits_state_independent_edit_and_exact_filtered_transcript(self):
        store = DurableStore.create_ephemeral()
        harness = DurableHarness(None, store, ExtensionRegistry())
        doc = DocumentDefinition("parity.notes", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, lambda: [])
        try:
            async def create(tx):
                return tx.create_conversation()
            record = await store.commit_async(create)
            parent = harness.find_conversation(record.id)
            await parent.configure_async(AgentChoices(permissions=["read:/before"]))

            async def write(chat, value):
                async def body(tx):
                    entry = tx.append_entry(chat.id, "note")
                    draft = tx.doc(doc, chat.id)
                    draft.value = [value]
                    draft.save(entry)
                    return entry
                return await chat.commit_async(body)
            first = await write(parent, "v1")
            second = await write(parent, "v2")

            async def fork_store(tx):
                fork = tx.create_conversation(parent_conversation_id=parent.id, fork_entry_id=first)
                store.provider.copy_entries(parent.id, fork.id, first)
                for document in list(store.documents):
                    if document.owner_id == parent.id:
                        store.provider.copy_document(document, fork.id, first)
                return fork
            fork_record = await store.commit_async(fork_store)
            fork = DurableConversation(store, fork_record)
            self.assertEqual(fork.snapshot(doc), ["v1"])
            self.assertEqual(fork.entries(100), [first], "inherited bookkeeping or later entry leaked")
            self.assertEqual(len(fork.entries(100)), 1)
            own = await write(fork, "v1-forked")
            await fork.configure_async(AgentChoices(permissions=["read:/fork"]))
            self.assertEqual(fork.entries(100), [own, first])
            self.assertEqual(len(fork.entries(100)), 2, "transcript must contain inherited + own only")
            self.assertNotIn(second, fork.entries(100))
            self.assertEqual(parent.entries(100), [second, first])
            self.assertEqual(parent.snapshot(doc), ["v2"])
            self.assertEqual(fork.snapshot(doc), ["v1-forked"])
            self.assertEqual(parent.choices().permissions, ["read:/before"])
            self.assertEqual(fork.choices().permissions, ["read:/fork"])
        finally:
            harness.close()
            store.close()

    async def test_recovery_twice_executes_memoized_step_once_after_actual_process_crashes(self):
        with tempfile.TemporaryDirectory(prefix="parity-recovery-") as directory:
            file = Path(directory) / "store.jsonl"
            effects = Path(directory) / "effects.txt"
            for mode, status in [("create", 73), ("interrupt", 73), ("complete", 0)]:
                result = await asyncio.to_thread(subprocess.run,
                    [sys.executable, str(Path(__file__).with_name("recovery.py")),
                     str(file), str(effects), mode], capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, status, f"{mode}: {result.stdout}\n{result.stderr}")
                self.assertEqual(effects.read_text(), "charged\n", f"{mode} repeated the side effect")
            store = DurableStore.create(DurableStoreOptions(path=str(file)))
            try:
                self.assertEqual(len(store.tasks), 1)
                task = store.tasks[0]
                self.assertEqual(task.status, TaskStatus.TERMINAL)
                self.assertEqual(task.outcome.status, "completed")
                self.assertEqual(task.outcome.result, "receipt")
                self.assertIs(await store.memo_async(task.id, "charged"), True)
                self.assertEqual(task.restarts, 2)
            finally:
                store.close()

    async def test_abort_cascade_persists_exact_terminal_states_for_parent_and_two_children(self):
        with tempfile.TemporaryDirectory(prefix="parity-abort-") as directory:
            options = DurableStoreOptions(path=str(Path(directory) / "store.jsonl"))
            store = DurableStore.create(options)
            registry = ExtensionRegistry()
            aborted = []

            async def run(_input, _checkpoint, runtime):
                return await runtime.commit_async(lambda: TaskCommit.completed(None))

            async def abort(_input, _checkpoint, runtime):
                aborted.append(runtime.task.id)
                return await runtime.commit_async(lambda: TaskCommit.aborted())
            definition = DurableTask.define("parity.work", lambda: {"phase": "run"}, run, abort)
            registry.install(Extension("parity", tasks=[definition]))
            harness = DurableHarness(None, store, registry)
            try:
                async def create(tx):
                    parent = tx.create_task(definition)
                    return (parent, tx.create_task(definition, owner_task_id=parent.id),
                            tx.create_task(definition, owner_task_id=parent.id))
                parent, child1, child2 = await store.commit_async(create)
                await harness.abort_task_async(parent.id)
                await harness.abort_task_async(parent.id)
                self.assertEqual(aborted, [child1.id, child2.id, parent.id])
                harness.close()
                store.close()
                store = DurableStore.create(options)
                self.assertEqual(len(store.tasks), 3)
                self.assertEqual(len([t for t in store.tasks if t.owner_task_id == parent.id]), 2)
                for task in store.tasks:
                    self.assertEqual(task.status, TaskStatus.TERMINAL, f"{task.id} still live")
                    self.assertEqual(task.outcome.status, "aborted", f"{task.id} not aborted")
            finally:
                harness.close()
                store.close()


if __name__ == "__main__":
    unittest.main()
