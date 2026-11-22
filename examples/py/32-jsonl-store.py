"""JSONL persistence, reopening, torn-tail recovery, and explicit storage tiers.

Run from any directory: python3 /path/to/examples/py/32-jsonl-store.py
Mirrors examples/32-jsonl-store.cs; no SDK session or sign-in is required.
"""

import asyncio
import json
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    Check,
    DocumentDefinition,
    DocumentHistory,
    DurableStore,
    DurableStoreOptions,
    ForkPolicy,
    Transaction,
)


async def main() -> None:
    notes = DocumentDefinition(
        "example.notes", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF
    )

    with TemporaryDirectory(prefix="durable-example-") as directory:
        store_path = Path(directory) / "state.jsonl"
        with DurableStore.create(DurableStoreOptions(path=str(store_path))) as store:
            Check.that(store.is_persistent, "an explicit path makes the store persistent")

            async def write_notes(tx: Transaction) -> None:
                conversation = tx.create_conversation()
                draft = tx.doc(notes, conversation.id)
                draft.value = {"text": "written before the restart"}
                draft.save(tx.append_entry(conversation.id, "notes.write"))

            await store.commit_async(write_notes)

        Check.that(store_path.is_file(), "the store file exists after the commit")
        lines = [line for line in store_path.read_text().splitlines() if line]
        Check.that(len(lines) > 1, "the store writes one record per line")
        Check.that(
            all(
                line.startswith("{")
                and '"type"' in line
                and isinstance(json.loads(line), dict)
                for line in lines
            ),
            "every line is a JSONL record envelope",
        )

        with DurableStore.create(DurableStoreOptions(path=str(store_path))) as reopened:
            Check.equal(1, len(reopened.documents), "the document was read back")
            document = reopened.documents[0]
            Check.equal(
                "written before the restart",
                document.latest["text"],
                "the reopened store holds the value the first store committed",
            )

        with store_path.open("a") as handle:
            handle.write('{"v":1,"seq":9999,"ty')

        with DurableStore.create(DurableStoreOptions(path=str(store_path))) as recovered:
            Check.equal(1, len(recovered.documents), "the committed document survived")
            Check.equal(
                1,
                len(recovered.documents[0].versions),
                "the committed record survived the torn tail",
            )

        Check.that(
            '"seq":9999' not in store_path.read_text(),
            "the torn tail was truncated away at open",
        )

        # Resolve only: writing the default would leave state in the user's home.
        default_path = Path(DurableStore.default_store_path())
        Check.equal(
            "durable-store.jsonl", default_path.name, "the default file name is stable"
        )
        Check.equal(
            "durable",
            default_path.parent.name,
            "the default file sits in a durable directory",
        )
        Check.that(
            default_path.is_absolute(),
            "the default path is absolute, so it does not depend on the working directory",
        )

        with DurableStore.create_ephemeral() as ephemeral:
            Check.that(not ephemeral.is_persistent, "an ephemeral store persists nothing")

        print(f"explicit store file: {store_path}")
        print(f"default store file:  {default_path}")
        print(
            f"tiers: durable writes {len(lines)} JSONL record(s); ephemeral writes none"
        )


if __name__ == "__main__":
    asyncio.run(main())
