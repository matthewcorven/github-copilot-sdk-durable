// Assertion helpers for the four policy-mode examples, mirroring SandboxSupport.cs.
import assert from "node:assert/strict";
import {
  type DurableSandboxPolicy, type SandboxConfig, type SessionEvent,
} from "../../lib/ts/durable.ts";

export function assertPolicy(policy: DurableSandboxPolicy, config: SandboxConfig, cwd: string, label: string) {
  // Inspect the official SDK's serialized wire object, not another durable policy.
  const wire: SandboxConfig = JSON.parse(JSON.stringify(config));
  assert.equal(wire.enabled, true, `${label}: the sandbox is enabled`);
  for (const field of [
    "addCurrentWorkingDirectory", "allowBypass", "allowDevToolAccess",
    "sandboxMcpServers", "sandboxLspServers",
  ] as const) assert.equal(wire[field], policy[field], `${label}: ${field} maps`);
  assert.equal(wire.auth?.gh, policy.auth?.gh, `${label}: Auth.Gh maps`);
  assert.equal(wire.auth?.git, policy.auth?.git, `${label}: Auth.Git maps`);
  assert.equal(wire.userPolicy?.seatbelt?.keychainAccess, policy.keychainAccess, `${label}: Seatbelt.KeychainAccess maps`);
  assert.equal(wire.userPolicy?.experimental, undefined, `${label}: no deprecated experimental policy is written`);
  const filesystem = wire.userPolicy?.filesystem;
  assert.ok(filesystem, `${label}: the filesystem policy is mapped`);
  assert.equal(filesystem.clearPolicyOnExit, policy.filesystem?.clearPolicyOnExit, `${label}: ClearPolicyOnExit maps`);
  for (const field of ["deniedPaths", "readonlyPaths", "readwritePaths"] as const) {
    const expected = (policy.filesystem?.[field] ?? []).map((path) => path.replaceAll("{workspace}", cwd));
    const actual: string[] = filesystem[field] ?? [];
    assert.deepEqual(actual, expected, `${label}: ${field} maps with the token expanded`);
    assert.ok(actual.every((path) => !path.includes("{workspace}")), `${label}: no ${field} path still contains the workspace token`);
  }
  const env = wire.credentials?.envVars ?? {};
  const expectedEnv = policy.credentials?.envVars ?? {};
  assert.equal(Object.keys(env).length, Object.keys(expectedEnv).length, `${label}: every injected credential maps`);
  for (const [name, hosts] of Object.entries(expectedEnv)) {
    assert.deepEqual(env[name]?.injectHosts, hosts, `${label}: ${name} inject hosts map`);
    assert.deepEqual(Object.keys(env[name] ?? {}), ["injectHosts"], `${label}: ${name} carries no credential value`);
  }
  if (policy.network) {
    const network = wire.userPolicy?.network;
    assert.ok(network, `${label}: network policy maps`);
    assert.equal(network.allowOutbound, policy.network.allowOutbound, `${label}: AllowOutbound maps`);
    assert.equal(network.allowLocalNetwork, policy.network.allowLocalNetwork, `${label}: AllowLocalNetwork maps`);
    assert.deepEqual(network.allowedHosts ?? [], policy.network.allowedHosts ?? [], `${label}: AllowedHosts maps`);
    assert.deepEqual(network.blockedHosts ?? [], policy.network.blockedHosts ?? [], `${label}: BlockedHosts maps`);
    if (policy.network.proxy?.url) {
      for (const field of ["url", "username", "password"] as const) {
        assert.equal(network.proxy?.[field], policy.network.proxy[field], `${label}: proxy ${field} maps`);
      }
    } else assert.equal(network.proxy, undefined, `${label}: absent proxy stays absent`);
  }
  return wire;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
export function decisionData(events: readonly SessionEvent[]): Record<string, unknown>[] {
  return events.map((event) => {
    const data: unknown = JSON.parse(JSON.stringify(event.data));
    assert.ok(isRecord(data) && typeof data.kind === "string", "every observed sandbox decision names its kind");
    return data;
  });
}
export function engaged(decisions: readonly Record<string, unknown>[]): boolean {
  const enforcement = decisions.find((data) => data.kind === "enforcement_state");
  return !!enforcement && enforcement.outcome === "engaged" &&
    typeof enforcement.backend === "string" && !["none", "unsupported"].includes(enforcement.backend);
}
export function printReport(decisions: readonly Record<string, unknown>[]) {
  const report = decisions.find((data) => data.kind === "enforcement_state") ??
    decisions.find((data) => data.kind === "policy_resolved");
  console.log(`run mode: ${engaged(decisions) ? "Enforced" : "Replayed"} (backend: ${report?.backend ?? "unsupported"}, platform: ${report?.platform ?? "other"}, outcome: ${report?.outcome ?? "inactive"})`);
}
