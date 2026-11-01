"""06 — Configuration. Each configure replaces choices rather than merging."""
from example import d, run_example
from check import Check
from tools import file_tool


async def main(harness, store, registry):
    files = d.Extension("files", tools=[file_tool("read", "Read a file"), file_tool("write", "Write a file")])
    registry.install(files)
    registry.install(d.Extension("search", tools=[file_tool("grep", "Search files")]))
    settings = {"retry": {"maxRetries": 5}, "toolExecution": "sequential"}
    root = await harness.root_async()
    print(f"effective model: {(await root.wait_for_effective_model_async()).model or '(not reported)'}")

    def tools():
        return [tool.name for tool in root.agent().tools]

    print(f"default tools: {tools()}")
    await root.configure_async(d.AgentChoices(
        model="gpt-6-luna", reasoning_effort="high", tools=["write", "read"],
        permissions=["custom-tool:write", "custom-tool:read"],
    ))
    stored_model = root.choices().model
    stored_effort = root.choices().reasoning_effort
    stored_tools = root.choices().tools
    agent = root.agent()
    await root.configure_async(d.AgentChoices(
        model="gpt-6-luna", reasoning_effort="high", extensions=["files"], tools=["write", "read"],
        permissions=["custom-tool:write", "custom-tool:read"],
    ))
    without_search = tools()
    await root.configure_async(d.AgentChoices(
        model="gpt-6-luna", extensions=["files", "search"],
        permissions=["custom-tool:read", "custom-tool:write", "custom-tool:grep"],
    ))
    registry.uninstall("files")
    files_uninstalled = tools()
    registry.install(files)
    files_reinstalled = tools()
    settings["retry"]["maxRetries"] = 10
    Check.equal("gpt-6-luna", stored_model, "the stored model is the one that was configured")
    Check.equal("high", stored_effort, "the stored reasoning effort is the one that was configured")
    Check.sequence(["write", "read"], stored_tools, "the stored tool names are the ones that were configured")
    Check.sequence(["write", "read"], [t.name for t in agent.tools], "the resolved tools follow the stored names")
    Check.equal(None, root.choices().reasoning_effort, "a later configure() clears the fields it omits")
    Check.equal(None, root.choices().tools, "a later configure() clears the tools it omits")
    Check.that("grep" not in without_search, "the deselected extension's tools are gone")
    Check.that("read" in without_search, "the still-selected extension keeps its tools")
    Check.that("files" in root.choices().extensions, "the uninstalled extension is still selected by name")
    Check.that("read" not in files_uninstalled, "an uninstalled extension contributes no tools")
    Check.that("grep" in files_uninstalled, "the other selected extension still contributes its tools")
    Check.that("read" in files_reinstalled, "reinstalling the extension brings its tools back")
    Check.equal(10, settings["retry"]["maxRetries"], "a settings change takes effect immediately")
    Check.that(not hasattr(root.choices(), "retry"), "settings are not part of the stored choices")


if __name__ == "__main__":
    run_example(main)
