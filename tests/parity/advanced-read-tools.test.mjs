import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

function readTool(name, directory, mutation) {
  let source = readFileSync(new URL(`../../examples/ts/${name}.ts`, import.meta.url), "utf8");
  if (mutation) {
    assert.ok(source.includes(mutation[0]), "mutation targets the actual production handler");
    source = source.replace(...mutation);
  }
  const start = source.indexOf("const read: DurableTool = {");
  const end = source.indexOf("const registry =", start);
  assert.ok(start >= 0 && end > start, "extract the actual standalone read tool");
  return runInNewContext(`${stripTypeScriptTypes(source.slice(start, end))}\nread;`, {
    directory, worktree: directory, existsSync, readFileSync, join, resolve,
  });
}

async function exercise(name, filename, mutation) {
  const directory = mkdtempSync(join(tmpdir(), "durable-read-regression-"));
  try {
    const contents = `actual ${name} file\n`;
    writeFileSync(join(directory, filename), contents);
    const tool = readTool(name, directory, mutation);
    assert.equal(tool.name, "read_file");
    assert.equal(tool.overridesBuiltInTool, true);
    assert.equal(await tool.handler({ path: filename }), contents, "relative existing file is read");
    assert.equal(await tool.handler({ path: resolve(directory, filename) }), contents,
      `${name}: absolute existing file is read without duplicating the working directory`);
    const missing = await tool.handler({ path: resolve(directory, "missing.ts") });
    assert.match(missing, /no such file|not found/);
    for (const invalid of [null, {}, { path: 1 }, { path: null }])
      await assert.rejects(tool.handler(invalid), /read_file path must be a string/);
  } finally {
    rmSync(directory, { recursive: true });
  }
}

for (const [name, filename, base] of [
  ["26-plan-mode", "server.ts", "directory"],
  ["27-reviewer", "user.ts", "worktree"],
]) {
  test(`${name} actual read_file handles relative/absolute files and rejects invalid arguments`, async () => {
    await exercise(name, filename);
  });

  test(`${name} actual handler join regression is rejected by the absolute-file assertion`, async () => {
    await assert.rejects(exercise(name, filename, [
      `resolve(${base}, path)`, `join(${base}, path)`,
    ]), /absolute existing file is read without duplicating the working directory/);
  });
}
