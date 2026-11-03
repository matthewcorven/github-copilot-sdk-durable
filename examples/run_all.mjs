import { readFileSync, existsSync, readdirSync, realpathSync } from "node:fs";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { isDeepStrictEqual } from "node:util";

export const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const referenceIds = ["00", "01", "03", "04"];
const readJson = path => JSON.parse(readFileSync(join(root, path), "utf8"));
const sha256 = path => createHash("sha256").update(readFileSync(join(root, path))).digest("hex");

export function normaliseOutput(text) {
  return text.replaceAll("\r\n", "\n")
    .replace(/\/(?:private\/)?var\/folders\/[A-Za-z0-9_.+/-]+|\/tmp\/[A-Za-z0-9_.+/-]+/g, "<temp>")
    .replace(/[A-Za-z]:[\\/](?:Users[\\/][^\s\\/]+[\\/]AppData[\\/]Local[\\/]Temp|Windows[\\/]Temp)[\\/][^\s]+/g, "<temp>")
    .replace(/s(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})/g, "<session>")
    .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32}/g, "<guid>")
    .replace(/[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+(?:Z|[+-][0-9]{2}:[0-9]{2})/g, "<timestamp>")
    .replace(/[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?/g, "<timestamp>")
    .replace(/\b[cet][0-9]+\b/g, "<id>");
}

export function loadManifest() {
  const manifest = readJson("examples/parity.json");
  const text = readFileSync(join(root, "scripts/validate.sh"), "utf8");
  const liveBlock = text.match(/LIVE_EXAMPLES=\((.*?)\)/s)?.[1];
  const deterministicBlock = text.match(/DETERMINISTIC_EXAMPLES=\((.*?)\)/s)?.[1];
  const reasonsBlock = text.match(/deterministic_exclusion_reason\(\) \{(.*?)\n\}/s)?.[1];
  if (!liveBlock || !deterministicBlock || !reasonsBlock) throw new Error("Cannot read exact C# selection");
  const live = new Set(liveBlock.trim().split(/\s+/));
  const deterministic = new Set(deterministicBlock.trim().split(/\s+/));
  const reasons = Object.fromEntries([...reasonsBlock.matchAll(/(\d\d-[\w-]+)\) echo "([^"]+)" ;;/g)]
    .map(match => [match[1], match[2]]));
  const sources = readdirSync(join(root, "examples")).filter(path => /^[0-3][0-9]-.*\.cs$/.test(path)
    && Number(path.slice(0, 2)) <= 35).sort();
  const ids = Array.from({ length: 36 }, (_, n) => String(n).padStart(2, "0"));
  const sourceIds = sources.map(source => source.slice(0, 2));
  const manifestIds = manifest.examples.map(entry => entry.id);
  if (manifest.schemaVersion !== 1 || manifest.reference !== "scripts/validate.sh" || !isDeepStrictEqual(manifest.scope, [0, 35])
      || !isDeepStrictEqual(sourceIds, ids) || !isDeepStrictEqual(manifestIds, ids)) {
    const missing = ids.filter(id => !sourceIds.includes(id) || !manifestIds.includes(id));
    const duplicated = ids.filter(id => sourceIds.filter(value => value === id).length > 1
      || manifestIds.filter(value => value === id).length > 1);
    throw new Error(`Inventory must contain all 36 C# examples exactly once, 00..35; missing example id(s): ${missing.join(", ") || "none"}; duplicated example id(s): ${duplicated.join(", ") || "none"}`);
  }
  const declarations = {};
  for (const language of ["ts", "py"]) {
    declarations[language] = new Map();
    for (const range of ["00-10", "11-21", "22-35"]) {
      const path = `examples/${language}/parity-${range}.json`;
      const declaration = readJson(path);
      const [first, last] = range.split("-").map(Number);
      if (declaration.schemaVersion !== 1 || !isDeepStrictEqual(declaration.range, [first, last])) {
        throw new Error(`${path}: unsupported range schema/version`);
      }
      for (const evidence of declaration.examples) {
        if (!ids.slice(first, last + 1).includes(evidence.id) || declarations[language].has(evidence.id)) {
          throw new Error(`${path}: invalid or duplicate id ${evidence.id}`);
        }
        declarations[language].set(evidence.id, { evidence, path });
      }
    }
    const undeclared = ids.filter(id => !declarations[language].has(id));
    if (undeclared.length) throw new Error(`${language}: missing range inventory for example id(s) ${undeclared.join(", ")}`);
  }
  for (const [index, entry] of manifest.examples.entries()) {
    const source = sources[index];
    const name = source.slice(0, -3);
    const selected = deterministic.has(name);
    if (entry.source !== source || entry.live !== live.has(entry.id)
        || entry.deterministic !== selected || entry.liveReason !== (reasons[name] ?? null)
        || (!selected && !entry.liveReason)) throw new Error(`${entry.id}: C# selection/reason mismatch`);
    const expectedOutput = referenceIds.includes(entry.id) ? `examples/expected/${name}.txt` : null;
    if (entry.expectedOutput !== expectedOutput) {
      throw new Error(`${entry.id}: missing reference output inventory (expected ${expectedOutput ?? "none"})`);
    }
    for (const language of ["ts", "py"]) {
      const row = entry.languages[language];
      const declaration = declarations[language].get(entry.id);
      const path = `examples/${language}/${name}.${language}`;
      if (row.path !== path || !existsSync(join(root, path))) {
        throw new Error(`Missing example ${entry.id} for language ${language}: ${path}`);
      }
      if (!["ported", "pending-native-api"].includes(row.status)
          || !["supported", "unsupported", "not-applicable"].includes(row.sandboxMode)
          || (row.status === "pending-native-api" && (typeof row.evidence.pendingReason !== "string" || !row.evidence.pendingReason.trim()))
          || (row.sandboxMode === "unsupported" && (typeof row.evidence.sandboxReason !== "string" || !row.evidence.sandboxReason.trim()))
          || ("timeoutSeconds" in row.evidence && (!Number.isSafeInteger(row.evidence.timeoutSeconds) || row.evidence.timeoutSeconds <= 0))
          || row.rangeManifest !== declaration.path
          || row.status !== declaration.evidence.status || row.sandboxMode !== declaration.evidence.sandboxMode
          || declaration.evidence.source !== source
          || ![path, path.split("/").at(-1)].includes(declaration.evidence.path)
          || !isDeepStrictEqual(row.evidence, declaration.evidence)) {
        throw new Error(`${entry.id}/${language}: global/range status or evidence mismatch`);
      }
    }
  }
  const provenance = readJson("examples/expected/provenance.json");
  if (!isDeepStrictEqual(Object.keys(provenance.outputs).sort(), [...referenceIds].sort())
      || provenance.fixtureSha256 !== sha256("tools/ReplayRuntime/fixtures/basic.json")) {
    throw new Error("Four actual C# reference outputs and the captured fixture are required");
  }
  for (const entry of manifest.examples.filter(entry => entry.expectedOutput)) {
    const output = readFileSync(join(root, entry.expectedOutput), "utf8");
    const proof = provenance.outputs[entry.id];
    if (!output || proof.expectedSha256 !== sha256(entry.expectedOutput)
        || proof.sourceSha256 !== sha256(`examples/${entry.source}`)) {
      throw new Error(`Missing or changed actual C# reference output/source: ${entry.expectedOutput}`);
    }
  }
  return manifest;
}

export function main(argv = process.argv.slice(2)) {
  try {
    const allowed = new Set(["--deterministic", "--all-available", "--list"]);
    for (const arg of argv) if (arg.startsWith("-") && !allowed.has(arg)) throw new Error(`Unknown option ${arg}`);
    if (argv.includes("--deterministic") && argv.includes("--all-available")) throw new Error("Choose one mode");
    const deterministic = argv.includes("--deterministic");
    const ids = argv.filter(arg => !arg.startsWith("-"));
    const manifest = loadManifest();
    if (new Set(ids).size !== ids.length || ids.some(id => !manifest.examples.some(entry => entry.id === id))) {
      throw new Error(`Invalid or duplicate requested IDs: ${ids}`);
    }
    if (deterministic && manifest.examples.some(entry => ids.includes(entry.id) && !entry.deterministic)) {
      throw new Error("Requested IDs include an example excluded from deterministic mode");
    }
    const chosen = [];
    for (const entry of manifest.examples) {
      if (deterministic && !entry.deterministic) {
        console.log(`EXCLUDE ${entry.id} ${entry.source}: ${entry.liveReason}`);
        continue;
      }
      if (ids.length && !ids.includes(entry.id)) continue;
      chosen.push(entry);
      console.log(`SELECT ${entry.id} ${entry.languages.ts.path} [${entry.languages.ts.status}]${entry.live && !deterministic ? " LIVE" : ""}`);
    }
    if (argv.includes("--list")) return 0;
    const env = { ...process.env };
    if (deterministic) {
      const runtime = join(root, "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime");
      if (!existsSync(runtime)) throw new Error(`Build the actual ReplayRuntime first: ${runtime}`);
      env.DURABLE_RUNTIME_PATH = runtime;
      env.DURABLE_EXAMPLE_FIXTURE = join(root, "tools/ReplayRuntime/fixtures/basic.json");
      delete env.DURABLE_EXAMPLE_REPLAY_FIXTURE;
      console.log("Replay sessions generated by the runtime are synthesised, NOT recorded transcripts or LIVE/model proof.");
    } else if (env.DURABLE_RUNTIME_PATH || env.DURABLE_EXAMPLE_FIXTURE || env.DURABLE_EXAMPLE_REPLAY_FIXTURE) {
      throw new Error("All-available requires the native runtime; remove replay/runtime override variables");
    }
    const loader = join(root, "lib/ts/node_modules/tsx/dist/loader.mjs");
    if (!existsSync(loader)) throw new Error("Restore declared tooling with npm --prefix lib/ts ci");
    let failures = 0, compared = 0;
    for (const entry of chosen) {
      const language = entry.languages.ts;
      try {
        if (language.status !== "ported") throw new Error(`pending-native-api: ${language.evidence.pendingReason}`);
        const args = ["--import", pathToFileURL(loader).href, join(root, language.path)];
        if (!deterministic && language.sandboxMode === "supported") args.push("--live");
        const result = spawnSync(process.execPath, args, { cwd: root, env, encoding: "utf8",
          timeout: (language.evidence.timeoutSeconds ?? 180) * 1000, maxBuffer: 16 * 1024 * 1024 });
        process.stdout.write(result.stdout ?? "");
        process.stderr.write(result.stderr ?? "");
        if (result.error) throw result.error;
        if (result.status !== 0) throw new Error(`example exit code ${result.status}, signal ${result.signal}`);
        if (entry.expectedOutput && deterministic) {
          const expected = readFileSync(join(root, entry.expectedOutput), "utf8");
          const actual = normaliseOutput(result.stdout);
          if (actual !== expected) throw new Error(`actual C# output mismatch (${entry.expectedOutput})\nEXPECTED:\n${expected}ACTUAL:\n${actual}`);
          compared++;
        }
        console.log(`PASS ${entry.id}`);
      } catch (error) {
        console.error(`FAIL ${entry.id}: ${error.message}`);
        failures++;
      }
    }
    const required = deterministic ? chosen.filter(entry => entry.expectedOutput).length : 0;
    if (compared !== required) {
      console.error(`FAIL output comparisons: expected ${required}, passed ${compared}`);
      failures++;
    }
    console.log(`TypeScript: ${chosen.length} selected, ${failures} failures, ${compared} actual C# output comparisons`);
    return failures ? 1 : 0;
  } catch (error) {
    console.error(`FAIL parity contract: ${error.message}`);
    return 1;
  }
}

if (process.argv[1] && existsSync(process.argv[1])
    && import.meta.url === pathToFileURL(realpathSync(process.argv[1])).href) {
  process.exitCode = main();
}
