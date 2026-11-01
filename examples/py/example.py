"""Standalone example lifecycle, with no replacement durable implementation."""

import asyncio
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
import durable as d  # noqa: E402


def run_example(body):
    async def run():
        store = d.DurableStore.create_ephemeral()
        registry = d.ExtensionRegistry()
        try:
            fixture = os.environ.get("DURABLE_EXAMPLE_REPLAY_FIXTURE")
            if fixture and not os.environ.get("DURABLE_RUNTIME_PATH"):
                raise RuntimeError("A replay fixture requires an explicit DURABLE_RUNTIME_PATH")
            options = d.DurableHarnessOptions(runtime_arguments=["--fixture", fixture] if fixture else [])
            harness = await d.DurableHarness.open_async(store, registry, options)
            try:
                await body(harness, store, registry)
            finally:
                await harness.close_async()
        finally:
            store.close()

    asyncio.run(run())
