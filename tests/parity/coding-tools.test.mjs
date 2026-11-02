import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { tmpdir } from "node:os";
import { stripTypeScriptTypes } from "node:module";
import { runInNewContext } from "node:vm";
import { test } from "node:test";

const source = fs.readFileSync(new URL("../../examples/ts/16-coding-tools.ts", import.meta.url), "utf8");

function tools(workspace, text = source) {
  const start = text.indexOf("const stringArg =");
  const end = text.indexOf("const shell =");
  assert.ok(start >= 0 && end > start, "load actual read/edit declarations");
  return runInNewContext(stripTypeScriptTypes(text.slice(start, end)) + "\n({ read, edit });",
    { ...fs, ...path, workspace });
}

function containment(text = source) {
  const base = fs.mkdtempSync(path.join(tmpdir(), "durable-ts-path-"));
  try {
    const workspace = path.join(base, "workspace");
    const sibling = path.join(base, "workspace-sibling");
    fs.mkdirSync(workspace);
    fs.mkdirSync(sibling);
    fs.writeFileSync(path.join(workspace, "notes.txt"), "hello world");
    fs.writeFileSync(path.join(workspace, "..notes.txt"), "dot-prefixed");
    fs.writeFileSync(path.join(sibling, "notes.txt"), "outside");
    fs.symlinkSync(workspace, path.join(base, "alias"), "dir");
    fs.symlinkSync(sibling, path.join(workspace, "escape"), "dir");
    fs.symlinkSync(path.join(workspace, "notes.txt"), path.join(workspace, "inside"));
    for (const root of [workspace, path.join(base, "alias")]) {
      const { read, edit } = tools(root, text);
      assert.equal(read.handler({ path: "notes.txt" }), "hello world");
      assert.equal(read.handler({ path: "inside" }), "hello world");
      assert.equal(read.handler({ path: path.join(workspace, "notes.txt") }), "hello world");
      for (const target of ["escape/notes.txt", "../workspace-sibling/notes.txt", path.join(sibling, "notes.txt")]) {
        assert.throws(() => read.handler({ path: target }), /outside the example workspace/, `read rejects ${target}`);
        assert.throws(() => edit.handler({ path: target, oldText: "outside", newText: "corrupted" }),
          /outside the example workspace/, `edit rejects ${target}`);
        assert.equal(fs.readFileSync(path.join(sibling, "notes.txt"), "utf8"), "outside");
      }
      assert.equal(read.handler({ path: "..notes.txt" }), "dot-prefixed");
      edit.handler({ path: "inside", oldText: "world", newText: "durable" });
      assert.equal(fs.readFileSync(path.join(workspace, "notes.txt"), "utf8"), "hello durable");
      fs.writeFileSync(path.join(workspace, "notes.txt"), "hello world");
    }
  } finally {
    fs.rmSync(base, { recursive: true, force: true });
  }
}

test("actual TS16 read/edit handlers canonicalize workspace aliases and reject symlink/sibling escapes", () => {
  containment();
});

test("actual TS16 guard mutations are rejected by containment assertions", () => {
  for (const [before, after] of [
    ["realpathSync(resolve(workspace, name))", "resolve(workspace, name)"],
    ["relative(realpathSync(workspace), path)", "relative(workspace, path)"],
    ['local === ".." || local.startsWith(`..${sep}`) || isAbsolute(local)',
      "!path.startsWith(realpathSync(workspace))"],
  ]) {
    assert.ok(source.includes(before), `mutation anchor: ${before}`);
    assert.throws(() => containment(source.replace(before, after)));
  }
});
