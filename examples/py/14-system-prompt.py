"""14 - System prompt. Configuration isolation and per-request prompt rendering."""
import asyncio
import os
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import AgentChoices, Check, DurableHarness, DurableHarnessOptions, DurableStore, Extension, ExtensionRegistry, PromptSection


async def main():
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()

    async def preamble(input):
        return "You are a coding agent."

    async def cwd(input):
        return input.working_directory

    async def agents_md(input):
        return "Run npm run check after changes."

    registry.install(Extension("coding", sections=[PromptSection("preamble", preamble, tag=False), PromptSection("cwd", cwd)]))
    registry.install(Extension("agents-md", sections=[PromptSection("agents_md", agents_md)]))
    with TemporaryDirectory(prefix="durable-repo-") as repo:
        fixture = os.environ.get("DURABLE_EXAMPLE_FIXTURE")
        harness = await DurableHarness.open_async(store, registry, DurableHarnessOptions(
            runtime_arguments=["--fixture", fixture] if fixture else []))
        try:
            root = await harness.root_async(AgentChoices(extensions=["coding", "agents-md"], working_directory=repo))
            report = root.effective_model
            print(f"effective model: {report.model or '(not reported)'}")
            subagent = await harness.create_conversation_async(AgentChoices(
                extensions=["coding"], working_directory=repo, instructions="Only read; never edit files."))
            root_agent = root.agent()
            root_instructions = root_agent.instructions
            subagent_instructions = subagent.agent().instructions
            root_prompt = await harness.build_system_message(
                root_agent, [s for e in root_agent.extensions for s in e.sections], root)
            child_agent = subagent.agent()
            child_prompt = await harness.build_system_message(
                child_agent, [s for e in child_agent.extensions for s in e.sections], subagent)
            print(f"root system prompt:\n{root_prompt}\nsubagent system prompt:\n{child_prompt}")
            packages = str(Path(repo) / "packages")
            Path(packages).mkdir()
            await root.configure_async(AgentChoices(extensions=["coding", "agents-md"], working_directory=packages))
            Check.equal(packages, root.agent().working_directory, "the root's working directory changed")
            Check.that(subagent.agent().working_directory != packages, "the subagent's working directory is unchanged")
            Check.equal(subagent_instructions, subagent.agent().instructions, "the subagent's instructions are unchanged")
            Check.equal(root_instructions, root.agent().instructions, "the root's instructions are unchanged")
            Check.that(len(root.agent().extensions) >= 2, "the root selects both extensions")
            Check.that(repo in root_prompt, "the root prompt renders its working directory")
            Check.that("Run npm run check after changes." in root_prompt, "the root renders AGENTS.md")
            Check.that("Run npm run check after changes." not in child_prompt, "the child deselects AGENTS.md")
            Check.that("Only read; never edit files." in child_prompt, "the child renders its own instructions")
        finally:
            await harness.close_async()
            store.close()


if __name__ == "__main__":
    asyncio.run(main())
