"""Deterministic policy/observation proof; enforcement requires an engaged backend.

No successful tool call is required for the recorded-runtime path. The host's
decline branch is proven only if the runtime actually offers an escalation.
"""

import asyncio
import sys
from dataclasses import replace
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableHarnessOptions, DurableSandboxFilesystem,
    DurableSandboxPolicy, DurableStore, ExtensionRegistry,
)
from copilot.generated.rpc import (
    PermissionDecisionApproveOnce, PermissionDecisionReject, SandboxConfig,
)
from _sandbox_policy_22_35 import assert_policy, decision_data, engaged, print_report


def effective_keychain(config):
    user = config.to_dict().get("userPolicy") or {}
    top = user.get("seatbelt")
    return top.get("keychainAccess") if top is not None else (
        (user.get("experimental") or {}).get("seatbelt") or {}).get("keychainAccess")


async def main() -> None:
    # --live cannot force backend engagement or a bypass prompt.
    if any(argument != "--live" for argument in sys.argv[1:]):
        raise ValueError("only --live is supported")
    with TemporaryDirectory(prefix="durable-sandbox-") as workspace:
        for name in ("writable", "readonly", "secret"):
            Path(workspace, name).mkdir()
        Path(workspace, "readonly", "pinned.txt").write_text("read-only content")
        denied = Path(workspace, "secret", "leak.txt")
        denied.write_text("do not read this")
        policy = DurableSandboxPolicy(
            enabled=True, add_current_working_directory=False, allow_bypass=False,
            allow_dev_tool_access=False, trust_working_directory=True, keychain_access=True,
            filesystem=DurableSandboxFilesystem(
                readwrite_paths=["{workspace}/writable"], readonly_paths=["{workspace}/readonly"],
                denied_paths=["{workspace}/secret"],
            ),
        )
        config = DurableHarness.build_sandbox_config(policy, workspace)
        wire = assert_policy(policy, config, workspace, "the observability policy")
        print("resolved filesystem policy:")
        for field, label in [("readwritePaths", "readwrite"), ("readonlyPaths", "readonly"), ("deniedPaths", "denied")]:
            print(f"  {label}: [{', '.join(wire['userPolicy']['filesystem'][field])}]")
        print(f"  allowBypass: {policy.allow_bypass}, keychainAccess: {policy.keychain_access}")

        legacy_policy = DurableSandboxPolicy(enabled=True, filesystem=policy.filesystem)
        legacy = DurableHarness.build_sandbox_config(legacy_policy, workspace).to_dict()
        Check.that("seatbelt" not in legacy["userPolicy"], "a policy without KeychainAccess leaves the top-level seatbelt block unset")
        Check.that("experimental" not in legacy["userPolicy"], "the durable layer never writes the deprecated experimental.seatbelt block")
        legacy["userPolicy"]["experimental"] = {"seatbelt": {"keychainAccess": True}}
        Check.equal(True, effective_keychain(SandboxConfig.from_dict(legacy)),
                    "the deprecated location is consulted only when the top-level seatbelt block is absent")
        conflicting = config.to_dict()
        conflicting["userPolicy"]["experimental"] = {"seatbelt": {"keychainAccess": False}}
        Check.equal(True, conflicting["userPolicy"]["seatbelt"]["keychainAccess"],
                    "the top-level seatbelt block carries the durable policy's keychain decision")
        Check.equal(True, effective_keychain(SandboxConfig.from_dict(conflicting)),
                    "the legacy location is not consulted while the top-level seatbelt block is present")

        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, ExtensionRegistry(),
                DurableHarnessOptions(enable_file_hooks=False, tolerate_host_hook_failures=True))
            try:
                choices = AgentChoices(working_directory=workspace, sandbox=policy, permissions=["shell"])
                session_config = await harness.build_session_config(choices)
                Check.equal("user", session_config["trust_working_directory"],
                            "TrustWorkingDirectory = true maps to WorkingDirectoryTrust.User")
                assert_policy(policy, session_config["sandbox_config"], workspace, "the session observability policy")
                trust_config = await harness.build_session_config(AgentChoices(
                    working_directory=workspace, permissions=["shell"],
                    sandbox=DurableSandboxPolicy(enabled=True, trust_working_directory=False),
                ))
                Check.equal("session", trust_config["trust_working_directory"],
                            "TrustWorkingDirectory = false maps to WorkingDirectoryTrust.Session")
                conversation = await harness.create_conversation_async(choices)
                Check.that(conversation.session is not None, "the session was created with TrustWorkingDirectory set")
                effective = await conversation.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                await conversation.submit_async(
                    f"Use the shell tool to run exactly this command, then report its output: printf 'leaked' > '{denied}'")
                control = Path(workspace, "writable", "control.txt")
                await conversation.submit_async(
                    f"Use the shell tool to run exactly this command, then report its output: printf 'control' > '{control}'")
                context = await conversation.context_async()
                decisions = decision_data(conversation.sandbox_decisions)
                print()
                print_report(decisions)
                print(f"observed decision kinds, in order ({len(decisions)}):")
                for data in decisions:
                    print(f"  {data['kind']}")
                print()
                if engaged(decisions):
                    for kind in ("policy_resolved", "spawn_completed", "enforcement_state"):
                        Check.that(any(data["kind"] == kind for data in decisions), f"the runtime reported {kind}")
                    denial = next((data for data in decisions if data["kind"] == "access_denied"), None)
                    Check.that(denial is not None and isinstance(denial.get("toolCallId"), str),
                               "the denial names the tool call it refused")
                    tool_id = denial["toolCallId"]
                    permissions = [event.data.to_dict() for event in context
                                   if event.type.value == "permission.completed"
                                   and event.data.to_dict().get("toolCallId") == tool_id]
                    Check.that(len(permissions) > 0, "the blocked shell call produced a permission completion")
                    Check.that(all("denied" not in item["result"]["kind"].lower() for item in permissions),
                               "the permission result is not a denial")
                    Check.that(any(data["kind"] == "access_denied" and
                                   data.get("denialClass") in ("filesystem_read", "filesystem_write")
                                   for data in decisions),
                               "the blocked write reports a filesystem denial")
                    Check.equal("filesystem", denial.get("control"), "the denial is a filesystem denial")
                    Check.equal("shell", denial.get("enforcementPoint"), "the denial came from the shell enforcement point")
                    Check.equal("denied", denial.get("outcome"), "the denial reports the denied outcome")
                    tool = next((event.data.to_dict() for event in context
                                 if event.type.value == "tool.execution_complete"
                                 and event.data.to_dict().get("toolCallId") == tool_id), None)
                    Check.that(tool is not None, "the blocked call has a completion event")
                    text = "\n".join(filter(None, [
                        (tool.get("result") or {}).get("content"),
                        (tool.get("result") or {}).get("detailedContent"),
                        (tool.get("error") or {}).get("message"),
                    ]))
                    Check.that(any(marker in text.lower() for marker in
                                   ("denied", "not permitted", "permission", "sandbox", "refus", "blocked")),
                               "the tool output shows the refusal")
                    Check.equal("do not read this", denied.read_text(), "the denied path still holds its original content")
                    Check.equal("control", control.read_text(), "the control write into the readwrite path succeeded")
                    if tool.get("sandboxed") is not None:
                        Check.that(tool["sandboxed"], "the blocked shell call ran sandboxed")
                    if sys.platform == "darwin":
                        resolved = next(data for data in decisions if data["kind"] == "policy_resolved")
                        Check.that(resolved.get("keychainAccess"), "macOS granted the keychain access the policy requested")
                else:
                    print("no sandbox backend engaged on this host: the policy was replayed, not enforced")
                    print("the three-part proof needs a backend that actually applies the policy, so it is skipped")
                Check.equal(0, sum(data["kind"] == "bypass_decided" for data in decisions),
                            "a session with AllowBypass = false recorded no bypass decision")
            finally:
                await harness.close_async()

        counters = {"host": 0, "escalations": 0, "declines": 0}
        inspected = []
        async def permission_handler(request, invocation):
            counters["host"] += 1
            prompt = invocation.get("prompt_request")
            data = (prompt or request).to_dict()
            escalation = bool(data.get("requestSandboxBypass") or data.get("requestSandboxPermissive")
                              or data.get("sandboxPathGrant"))
            inspected.append(data)
            print(f"  host handler saw request #{counters['host']}: kind={data.get('kind')}, "
                  f"bypass={data.get('requestSandboxBypass', False)}, "
                  f"permissive={data.get('requestSandboxPermissive', False)}, "
                  f"pathGrant={data.get('sandboxPathGrant')}")
            if escalation:
                counters["escalations"] += 1
                counters["declines"] += 1
                return PermissionDecisionReject(feedback="declined by the host: commands must stay inside the sandbox")
            return PermissionDecisionApproveOnce()

        bypass_policy = replace(policy, allow_bypass=True)
        Check.that(bypass_policy.allow_bypass, "the second policy asks for the bypass capability")
        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, ExtensionRegistry(), DurableHarnessOptions(
                enable_file_hooks=False, tolerate_host_hook_failures=True, permission_handler=permission_handler))
            try:
                conversation = await harness.create_conversation_async(AgentChoices(
                    working_directory=workspace, sandbox=bypass_policy, permissions=["shell"]))
                effective = await conversation.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                command = f"printf 'leaked' > '{Path(workspace, 'secret', 'bypass.txt')}'"
                await conversation.submit_async(
                    f"Use the shell tool to run exactly this command, then report its output: {command}. "
                    "If the sandbox refuses the write, request the sandbox bypass for the same command and report what the host decides.")
                await conversation.submit_async(
                    f'Use the shell tool with mode="async" and detach=true to start this command in the background, then report what happened: {command}')
                bypass_decisions = decision_data(conversation.sandbox_decisions)
                print()
                print(f"second session, AllowBypass = true: {len(bypass_decisions)} decision(s)")
                bypasses = [data for data in bypass_decisions if data["kind"] == "bypass_decided"]
                if counters["escalations"] or bypasses:
                    Check.equal(1, counters["declines"], "exactly one host-handler invocation declined the bypass")
                    Check.that(counters["host"] >= counters["declines"], "every declined bypass came from an inspected prompt")
                    Check.equal(1, len(bypasses), "the declined bypass is recorded as one bypass_decided event")
                    Check.equal("declined", bypasses[0].get("outcome"), "the bypass decision records the host's decline")
                    Check.equal("model_requested", bypasses[0].get("source"), "the bypass was requested by the model")
                else:
                    Check.equal(0, counters["declines"], "no bypass was declined because none was offered")
                    Check.equal(0, len(bypasses), "no bypass_decided event was reported because none was offered")
                    print("notice: the runtime offered no sandbox-bypass escalation for this session, so the "
                          "decline path was not exercised. The host handler was installed and the policy set "
                          "AllowBypass = true; the runtime decides whether to surface the prompt, and the SDK "
                          "exposes no call that forces it. The handler's decline branch is therefore unproven here.")
            finally:
                await harness.close_async()
        print()
        print(f"assertion set: {'policy + enforcement' if engaged(decisions) else 'policy only'}")
        print(f"host-handler prompts inspected: {len(inspected)}, escalation prompts seen: {counters['escalations']}, "
              f"bypasses declined: {counters['declines']}")


if __name__ == "__main__":
    asyncio.run(main())
