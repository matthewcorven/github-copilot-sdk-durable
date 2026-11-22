"""09 — Registry reload. Installing a name replaces its code without reconfiguration."""
from example import d, run_example
from check import Check
from tools import file_tool


async def main(harness, store, registry):
    def load_files(description):
        return d.Extension("files", tools=[file_tool("read", "Read a file"), file_tool("grep", description)])

    registry.install(load_files("Search files"))
    root = await harness.root_async(d.AgentChoices(permissions=["custom-tool:read", "custom-tool:grep"]))
    print(f"effective model: {(await root.wait_for_effective_model_async()).model or '(not reported)'}")
    registry.install(load_files("Search files, faster"))
    registry.install(d.Extension("audit", tools=[file_tool("read", "Read a file (audited)")]))
    print(f"with audit: {[(t.name, t.description) for t in root.agent().tools]}")
    registry.uninstall("audit")
    Check.equal(1, sum(e.name == "files" for e in registry.extensions), "the reload replaced the extension rather than adding one")
    Check.equal("Search files, faster", next(t for t in registry.find("files").tools if t.name == "grep").description, "the registry holds the new code")
    Check.equal("Search files, faster", next(t for t in root.agent().tools if t.name == "grep").description, "the conversation resolves the reloaded code")
    registry.uninstall("audit")
    Check.equal(None, registry.find("audit"), "the uninstalled extension is gone from the registry")
    Check.equal("Read a file", next(t for t in root.agent().tools if t.name == "read").description, "the conversation falls back to the remaining code")


if __name__ == "__main__":
    run_example(main)
