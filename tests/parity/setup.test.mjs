import assert from "node:assert/strict";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";
import { test } from "node:test";

const root = new URL("../../", import.meta.url);
const metadata = JSON.parse(readFileSync(new URL("examples/ts/parity-00-10.json", root), "utf8"));
const commands = metadata.setupCommands ?? [metadata.setupCommand];

function cleanSetup(setup) {
  const directory = mkdtempSync(join(tmpdir(), "durable-clean-setup-"));
  const run = (command) => spawnSync(command[0], command.slice(1), {
    cwd: directory, encoding: "utf8", timeout: 120000,
    env: { ...process.env, NODE_PATH: "", NODE_OPTIONS: "" },
  });
  try {
    for (const prefix of ["lib/ts", "examples/ts"]) {
      mkdirSync(join(directory, prefix), { recursive: true });
      for (const file of ["package.json", "package-lock.json", "tsconfig.json"])
        copyFileSync(new URL(`${prefix}/${file}`, root), join(directory, prefix, file));
    }
    copyFileSync(new URL("lib/ts/durable.ts", root), join(directory, "lib/ts/durable.ts"));
    for (const helper of ["check.ts", "example.ts", "tools.ts"])
      copyFileSync(new URL(`examples/ts/${helper}`, root), join(directory, "examples/ts", helper));
    for (const entry of metadata.examples)
      copyFileSync(new URL(`examples/ts/${entry.path}`, root), join(directory, "examples/ts", entry.path));
    for (const command of setup) {
      const result = run(command);
      assert.equal(result.status, 0, `declared setup ${command.join(" ")}: ${result.stdout}\n${result.stderr}`);
    }
    const probe = run([process.execPath, "--experimental-transform-types", "--input-type=module", "-e", `
      import assert from "node:assert/strict";
      import { createRequire } from "node:module";
      import { DurableStore } from "./lib/ts/durable.ts";
      const require = createRequire(new URL("./lib/ts/durable.ts", import.meta.url));
      assert.ok(require.resolve("@github/copilot-sdk").includes("/lib/ts/node_modules/"));
      assert.ok(require.resolve("@types/fs-ext/package.json").includes("/lib/ts/node_modules/"));
      const store = DurableStore.createEphemeral();
      assert.equal(store.conversations.length, 0);
      store.dispose();
      console.log("library-relative SDK and fs-ext declarations resolve; actual store imported");
    `]);
    assert.equal(probe.status, 0, `actual library dependency resolution: ${probe.stdout}\n${probe.stderr}`);
    assert.match(probe.stdout, /library-relative SDK and fs-ext declarations resolve/);
    const typecheck = run(["npm", "--prefix", "examples/ts", "run", "typecheck"]);
    assert.equal(typecheck.status, 0, `clean range 00-10 typecheck: ${typecheck.stdout}\n${typecheck.stderr}`);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test("declared TS00-10 setup supports actual library-relative imports and clean typechecking", () => {
  cleanSetup(commands);
});

test("removing the actual library install makes clean dependency resolution RED", () => {
  assert.ok(commands.some(command => command.join(" ") === "npm --prefix lib/ts ci"));
  assert.throws(() => cleanSetup(commands.filter(command => !command.includes("lib/ts"))),
    /actual library dependency resolution:[\s\S]*ERR_MODULE_NOT_FOUND[\s\S]*@github\/copilot-sdk/);
});
