"""Assertion helpers for the four policy-mode examples, mirroring SandboxSupport.cs."""

import json

from durable import Check


def assert_policy(policy, config, working_directory, label):
    # Inspect the official SDK's serialized wire object, not another durable policy.
    wire = json.loads(json.dumps(config.to_dict()))
    Check.that(wire.get("enabled") is True, f"{label}: the sandbox is enabled")
    for attribute, field in [
        ("add_current_working_directory", "addCurrentWorkingDirectory"),
        ("allow_bypass", "allowBypass"), ("allow_dev_tool_access", "allowDevToolAccess"),
        ("sandbox_mcp_servers", "sandboxMcpServers"), ("sandbox_lsp_servers", "sandboxLspServers"),
    ]:
        Check.equal(getattr(policy, attribute), wire.get(field), f"{label}: {field} maps")
    auth = wire.get("auth") or {}
    for field in ("gh", "git"):
        Check.equal(getattr(policy.auth, field) if policy.auth else None, auth.get(field),
                    f"{label}: Auth.{field} maps")
    user = wire.get("userPolicy") or {}
    Check.equal(policy.keychain_access, (user.get("seatbelt") or {}).get("keychainAccess"),
                f"{label}: Seatbelt.KeychainAccess maps")
    Check.that("experimental" not in user, f"{label}: no deprecated experimental policy is written")
    filesystem = user.get("filesystem")
    Check.that(isinstance(filesystem, dict), f"{label}: the filesystem policy is mapped")
    Check.equal(policy.filesystem.clear_policy_on_exit, filesystem.get("clearPolicyOnExit"),
                f"{label}: ClearPolicyOnExit maps")
    for attribute, field in [
        ("denied_paths", "deniedPaths"), ("readonly_paths", "readonlyPaths"),
        ("readwrite_paths", "readwritePaths"),
    ]:
        expected = [path.replace("{workspace}", working_directory)
                    for path in getattr(policy.filesystem, attribute) or []]
        actual = filesystem.get(field) or []
        Check.sequence(expected, actual, f"{label}: {field} maps with the token expanded")
        Check.that(all("{workspace}" not in path for path in actual),
                   f"{label}: no {field} path still contains the workspace token")
    env_vars = (wire.get("credentials") or {}).get("envVars") or {}
    expected_env = policy.credentials.env_vars if policy.credentials else None
    Check.equal(len(expected_env or {}), len(env_vars), f"{label}: every injected credential maps")
    for name, hosts in (expected_env or {}).items():
        Check.sequence(hosts, env_vars[name]["injectHosts"], f"{label}: {name} inject hosts map")
        Check.sequence(["injectHosts"], list(env_vars[name]), f"{label}: {name} carries no credential value")
    if policy.network is not None:
        network = user.get("network")
        Check.that(isinstance(network, dict), f"{label}: network policy maps")
        Check.equal(policy.network.allow_outbound, network.get("allowOutbound"),
                    f"{label}: AllowOutbound maps")
        Check.equal(policy.network.allow_local_network, network.get("allowLocalNetwork"),
                    f"{label}: AllowLocalNetwork maps")
        Check.sequence(policy.network.allowed_hosts or [], network.get("allowedHosts") or [],
                       f"{label}: AllowedHosts maps")
        Check.sequence(policy.network.blocked_hosts or [], network.get("blockedHosts") or [],
                       f"{label}: BlockedHosts maps")
        proxy = policy.network.proxy
        if proxy is not None and proxy.url:
            for attribute, field in [("url", "url"), ("username", "username"), ("password", "password")]:
                Check.equal(getattr(proxy, attribute), network["proxy"].get(field),
                            f"{label}: proxy {field} maps")
        else:
            Check.that(network.get("proxy") is None, f"{label}: absent proxy stays absent")
    return wire


def decision_data(events):
    data = [event.data.to_dict() for event in events]
    Check.that(all(isinstance(item.get("kind"), str) for item in data),
               "every observed sandbox decision names its kind")
    return data


def engaged(decisions):
    enforcement = next((item for item in decisions if item["kind"] == "enforcement_state"), None)
    return bool(enforcement and enforcement.get("outcome") == "engaged"
                and enforcement.get("backend") not in (None, "none", "unsupported"))


def print_report(decisions):
    enforcement = next((item for item in decisions if item["kind"] == "enforcement_state"), None)
    resolved = next((item for item in decisions if item["kind"] == "policy_resolved"), None)
    report = enforcement or resolved or {}
    mode = "Enforced" if engaged(decisions) else "Replayed"
    print(f"run mode: {mode} (backend: {report.get('backend', 'unsupported')}, "
          f"platform: {report.get('platform', 'other')}, outcome: {report.get('outcome', 'inactive')})")
