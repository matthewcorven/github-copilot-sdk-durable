"""Native SDK tools used by the configuration and registry examples."""
from copilot import define_tool
from pydantic import BaseModel, Field


class FileArguments(BaseModel):
    path: str = Field(description="File path")


class SearchArguments(BaseModel):
    pattern: str = Field(description="Pattern")


def file_tool(name, description):
    argument = "pattern" if name == "grep" else "path"
    return define_tool(
        name,
        description=description,
        params_type=SearchArguments if name == "grep" else FileArguments,
        overrides_built_in_tool=True,
        handler=lambda params, invocation: f"{name} {getattr(params, argument)}",
    )
