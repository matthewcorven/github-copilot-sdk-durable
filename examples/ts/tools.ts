import { defineTool } from "@github/copilot-sdk";

export function fileTool(name: string, description: string) {
  const argument = name === "grep" ? "pattern" : "path";
  return defineTool(name, {
    description,
    parameters: {
      type: "object",
      properties: { [argument]: { type: "string" } },
      required: [argument],
    },
    overridesBuiltInTool: true,
    handler: args => `${name} ${(args as Record<string, string>)[argument]}`,
  });
}
