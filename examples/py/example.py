"""Standalone example lifecycle, with no replacement durable implementation."""

import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
import durable as d  # noqa: E402


def run_example(body):
    async def run():
        store = d.DurableStore.create_ephemeral()
        registry = d.ExtensionRegistry()
        harness = await d.DurableHarness.open_async(store, registry)
        try:
            await body(harness, store, registry)
        finally:
            await harness.close_async()
            store.close()

    asyncio.run(run())
