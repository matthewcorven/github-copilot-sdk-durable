import assert from "node:assert/strict";
import { test } from "node:test";
import { loadManifest, normaliseOutput } from "../../examples/run_all.mjs";

test("normalization changes only volatile identity/path/time, not meaningful output", () => {
  const text = "c17 e32 t4 s0123456789abcdef0123456789abcdef\r\n"
    + "01234567-89ab-cdef-0123-456789abcdef 2026-11-01T14:30:00.123-05:00\n"
    + "/private/var/folders/ab/123/T/work/file.txt\n"
    + "C:\\Users\\tester\\AppData\\Local\\Temp\\work\\file.txt\n"
    + "Pending True count: 2 receipt: visa3\n";
  assert.equal(normaliseOutput(text),
    "<id> <id> <id> <session>\n<guid> <timestamp>\n<temp>\n<temp>\nPending True count: 2 receipt: visa3\n");
  assert.notEqual(normaliseOutput("count: 3\n"), normaliseOutput("count: 2\n"));
  assert.notEqual(normaliseOutput("Pending\n"), normaliseOutput("Terminal\n"));
});

test("global manifest selects the exact C# sign-in-free set including conditional 35", () => {
  const manifest = loadManifest();
  assert.equal(manifest.examples.length, 36);
  assert.deepEqual(manifest.examples.filter(entry => entry.deterministic).map(entry => entry.id),
    ["00", "01", "03", "04", "05", "06", "09", "11", "12", "14", "17", "18", "23", "32", "35"]);
  assert.deepEqual(manifest.examples.filter(entry => entry.expectedOutput).map(entry => entry.id),
    ["00", "01", "03", "04"]);
  assert.equal(manifest.examples[35].live, true);
  assert.equal(manifest.examples[35].liveReason, null);
  assert.equal(manifest.examples[35].languages.ts.sandboxMode, "supported");
  assert.equal(manifest.examples[35].languages.py.sandboxMode, "supported");
  for (const entry of manifest.examples.filter(entry => !entry.deterministic)) assert(entry.liveReason);
});
