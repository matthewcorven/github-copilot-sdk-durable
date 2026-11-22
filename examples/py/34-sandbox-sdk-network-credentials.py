"""Network/auth/credential policy wire proof; --live proves masked shell observations."""

import asyncio
import hashlib
import json
import os
import secrets
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableHarnessOptions, DurableSandboxAuth,
    DurableSandboxCredentials, DurableSandboxFilesystem, DurableSandboxNetwork,
    DurableSandboxPolicy, DurableSandboxProxy, DurableStore, ExtensionRegistry,
)
from _sandbox_policy_22_35 import assert_policy, decision_data, engaged, print_report

TOKEN_NAME = "DURABLE_SANDBOX_TOKEN"


def network_denials(decisions):
    return [data for data in decisions if data["kind"] == "access_denied"
            and data.get("denialClass") in ("network_outbound", "network_host", "network_local")]


async def main() -> None:
    live = "--live" in sys.argv[1:]
    if any(argument != "--live" for argument in sys.argv[1:]):
        raise ValueError("only --live is supported")
    original = os.environ.get(TOKEN_NAME)
    real_value = "dsk_" + secrets.token_hex(24)
    real_digest = hashlib.sha256(real_value.encode()).hexdigest()
    os.environ[TOKEN_NAME] = real_value
    try:
        with TemporaryDirectory(prefix="durable-sandbox-") as workspace:
            script = Path(workspace, "observe-env.sh")
            report = Path(workspace, "env-report.txt")
            script.write_text("""#!/bin/sh
value="${DURABLE_SANDBOX_TOKEN-}"
if [ -z "$value" ]; then printf 'verdict=empty\\n' > "$1"; exit 0; fi
if command -v sha256sum >/dev/null 2>&1; then
  digest=$(printf '%s' "$value" | sha256sum | cut -d' ' -f1)
elif command -v shasum >/dev/null 2>&1; then
  digest=$(printf '%s' "$value" | shasum -a 256 | cut -d' ' -f1)
elif command -v openssl >/dev/null 2>&1; then
  digest=$(printf '%s' "$value" | openssl dgst -sha256 -r | cut -d' ' -f1)
else
  printf 'no SHA256 tool available\\n' >&2; exit 2
fi
printf 'verdict=observed\\nsha256=%s\\nlength=%s\\n' "$digest" "${#value}" > "$1"
""")
            def policy(permissive):
                return DurableSandboxPolicy(
                    enabled=True, add_current_working_directory=False, allow_bypass=False,
                    allow_dev_tool_access=permissive, sandbox_mcp_servers=not permissive,
                    sandbox_lsp_servers=True, trust_working_directory=True, keychain_access=False,
                    auth=DurableSandboxAuth(gh=True, git=permissive),
                    credentials=DurableSandboxCredentials(env_vars={TOKEN_NAME: ["api.github.com"]}),
                    filesystem=DurableSandboxFilesystem(readwrite_paths=["{workspace}"]),
                    network=DurableSandboxNetwork(
                        allow_outbound=permissive, allow_local_network=permissive,
                        allowed_hosts=["api.github.com"] if permissive else [],
                        blocked_hosts=["example.com"] if permissive else ["example.com", "*.example.com"],
                        proxy=None if permissive else DurableSandboxProxy(
                            url="http://127.0.0.1:8899", username="sandbox", password=secrets.token_hex(12)),
                    ),
                )
            policies = [("locked", policy(False)), ("permissive", policy(True))]
            for label, requested in policies:
                wire = assert_policy(requested, DurableHarness.build_sandbox_config(requested, workspace),
                                     workspace, f"the {label} network policy")
                Check.that(real_value not in json.dumps(wire),
                           f"{label}: the credential value is absent from the SDK wire")
                print(f"{label}: network, auth, credential names/inject hosts, proxy, MCP and LSP policy mapped")
            with DurableStore.create_ephemeral() as store:
                harness = await DurableHarness.open_async(store, ExtensionRegistry(),
                    DurableHarnessOptions(enable_file_hooks=False, tolerate_host_hook_failures=True))
                try:
                    conversations = []
                    for label, requested in policies:
                        choices = AgentChoices(working_directory=workspace, sandbox=requested,
                                               permissions=["shell", "read"], model="gpt-5.6-luna")
                        config = await harness.build_session_config(choices)
                        assert_policy(requested, config["sandbox_config"], workspace, f"the {label} session policy")
                        conversations.append((label, requested, await harness.create_conversation_async(choices)))
                    effective = await conversations[-1][2].wait_for_effective_model_async()
                    print(f"effective model: {effective.model or '(not reported)'}")
                    if live:
                        for label, requested, conversation in conversations:
                            report.unlink(missing_ok=True)
                            await conversation.submit_async(
                                f"Use the shell tool to run exactly this command, then report its output: sh '{script}' '{report}'")
                            Check.that(report.is_file(), f"{label}: the shell wrote the credential observation")
                            verdict = dict(line.split("=", 1) for line in report.read_text().splitlines() if "=" in line)
                            Check.that(verdict.get("verdict") != "empty", f"{label}: the masked variable is present inside the sandbox (not empty)")
                            length = int(verdict.get("length", "0"))
                            digest = verdict.get("sha256", "")
                            Check.that(length > 0, f"{label}: the value the sandboxed shell sees is not empty")
                            Check.that(len(digest) == 64 and all(char in "0123456789abcdefABCDEF" for char in digest),
                                       f"{label}: the shell reported a SHA256 digest, not an unavailable-tool sentinel")
                            Check.that(digest.lower() != real_digest, f"{label}: the value the sandboxed shell sees is not the real credential value")
                            print(f"{label}: credential masking held — the sandboxed shell saw a {length}-character value whose digest does not match the generated one")
                    else:
                        print("LIVE: both nonempty masked-credential digest/length observations are unproven; the recording has no tool call.")

                    for label, requested, conversation in conversations:
                        decisions = decision_data(conversation.sandbox_decisions)
                        print_report(decisions)
                        if live and engaged(decisions):
                            if label == "permissive":
                                mark = len(conversation.sandbox_decisions)
                                await conversation.submit_async(
                                    "Use the shell tool to run exactly this command, then report its output: "
                                    "curl -sS -m 8 -o /dev/null -w '%{http_code}' 'https://api.github.com/' || true")
                                Check.equal([], network_denials(decision_data(conversation.sandbox_decisions[mark:])),
                                            "the allowed host is not denied")
                            mark = len(conversation.sandbox_decisions)
                            await conversation.submit_async(
                                "Use the shell tool to run exactly this command, then report its output: "
                                "curl -sS -m 8 -o /dev/null -w '%{http_code}' 'https://example.com/' || true")
                            blocked = network_denials(decision_data(conversation.sandbox_decisions[mark:]))
                            print(f"{label}: blocked host denied" if blocked else
                                  f"{label}: blocked-host denial assertion skipped (backend reported no network denial)")
                        else:
                            print(f"LIVE: {label} network tool observations are unproven without real calls and backend enforcement.")
                        resolved = [data for data in decision_data(conversation.sandbox_decisions)
                                    if data["kind"] == "policy_resolved"]
                        if not resolved:
                            print(f"{label}: the runtime reported no resolved policy — skipping its assertions")
                        for data in resolved:
                            for field, expected in [
                                ("allowOutbound", requested.network.allow_outbound),
                                ("allowLocalNetwork", requested.network.allow_local_network),
                                ("ghAuth", requested.auth.gh), ("gitAuth", requested.auth.git),
                            ]:
                                if field in data:
                                    Check.equal(expected, data[field], f"{label}: the resolved policy reports the requested {field}")
                            Check.that(data.get("proxyMode") in ("none", "loopback", "external"),
                                       f"{label}: the resolved ProxyMode is one of none/loopback/external")
                    print(f"assertion set: {'policy + live observations' if live else 'policy only'}")
                    print("note: the generated credential is never printed or stored; the shell reports only a digest and length.")
                finally:
                    await harness.close_async()
    finally:
        if original is None:
            os.environ.pop(TOKEN_NAME, None)
        else:
            os.environ[TOKEN_NAME] = original


if __name__ == "__main__":
    asyncio.run(main())
