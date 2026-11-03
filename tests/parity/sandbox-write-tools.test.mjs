import assert from "node:assert/strict";
import * as fs from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = fs.readFileSync(new URL("../../examples/ts/28-sandbox-per-conversation.ts", import.meta.url), "utf8");

function writeTool(root, text) {
  const start = text.indexOf("const write: DurableTool = {");
  const end = text.indexOf("const registry =", start);
  assert.ok(start >= 0 && end > start, "load the actual per-conversation write handler");
  const bySession = new Map([["alice", { snapshot: () => ({ path: root }) }]]);
  return runInNewContext(`${stripTypeScriptTypes(text.slice(start, end))}\nwrite;`,
    { ...fs, ...path, bySession, sandboxDoc: {} });
}

async function exercise(text = source, mode = "all") {
  const parent = fs.mkdtempSync(path.join(fs.realpathSync(tmpdir()), "durable-write-regression-"));
  const root = path.join(parent, "workspace");
  const outside = path.join(parent, "workspace-sibling");
  const alias = path.join(parent, "alias");
  fs.mkdirSync(root);
  fs.mkdirSync(outside);
  fs.mkdirSync(path.join(root, "inside-directory"));
  fs.writeFileSync(path.join(root, "existing.txt"), "inside");
  fs.writeFileSync(path.join(outside, "existing.txt"), "outside");
  fs.symlinkSync(root, alias, "dir");
  fs.symlinkSync(outside, path.join(root, "escape"), "dir");
  fs.symlinkSync(path.join(outside, "existing.txt"), path.join(root, "outside-file"));
  fs.symlinkSync(path.join(outside, "absent.txt"), path.join(root, "dangling-file"));
  fs.symlinkSync(path.join(root, "existing.txt"), path.join(root, "inside-file"));
  fs.symlinkSync(path.join(root, "inside-directory"), path.join(root, "inside-parent"), "dir");
  try {
    for (const sandbox of [root, alias]) {
      const tool = writeTool(sandbox, text);
      const write = target => tool.handler({ path: target, content: "updated" }, { sessionId: "alice" });
      if (mode !== "negative") {
        for (const [target, destination] of [
          ["existing.txt", path.join(root, "existing.txt")],
          ["inside-file", path.join(root, "existing.txt")],
          ["inside-parent/new/deep.txt", path.join(root, "inside-directory/new/deep.txt")],
          ["new/directory/note.txt", path.join(root, "new/directory/note.txt")],
          ["..notes.txt", path.join(root, "..notes.txt")],
          [path.join(root, "absolute.txt"), path.join(root, "absolute.txt")],
          [path.join(alias, "aliased.txt"), path.join(root, "aliased.txt")],
        ]) {
          assert.match(await write(target), /^wrote /, `valid write: ${target}`);
          assert.equal(fs.readFileSync(destination, "utf8"), "updated", `actual inside effect: ${target}`);
        }
        for (const invalid of [null, {}, { path: "note.txt", content: 1 }])
          await assert.rejects(tool.handler(invalid, { sessionId: "alice" }), /needs path and content strings/);
      }
      if (mode !== "positive") {
        for (const target of [
          "escape/existing.txt", "escape/new/deep.txt", "outside-file", "dangling-file",
          "../workspace-sibling/existing.txt", path.join(outside, "existing.txt"),
          path.join(outside, "new/deep.txt"),
        ]) {
          await assert.rejects(write(target), /must remain inside|ENOENT/, `outside write rejected: ${target}`);
          assert.equal(fs.readFileSync(path.join(outside, "existing.txt"), "utf8"), "outside");
          assert.equal(fs.existsSync(path.join(outside, "new")), false, "no outside directory was created");
          assert.equal(fs.existsSync(path.join(outside, "absent.txt")), false, "no dangling outside target was created");
        }
      }
    }
  } finally {
    fs.rmSync(parent, { recursive: true, force: true });
  }
}

test("TS28 actual write handler supports canonical/aliased roots, inside links and new files/directories", async () => {
  await exercise(source, "positive");
});

test("TS28 actual write handler rejects outside targets/parents and sibling prefixes without outside effects", async () => {
  await exercise(source, "negative");
});

test("TS28 actual-source canonical-root, canonical-parent and boundary mutants fail protected assertions", async () => {
  for (const [before, after, mode] of [
    ["realpathSync(sandbox.path)", "resolve(sandbox.path)", "positive"],
    ["realpathSync(existing)", "existing", "negative"],
    ['local === "" || local === ".." || local.startsWith(`..${sep}`) || isAbsolute(local)',
      "!full.startsWith(base)", "negative"],
  ]) {
    assert.ok(source.includes(before), `actual mutation anchor: ${before}`);
    await assert.rejects(exercise(source.replace(before, after), mode));
  }
});
