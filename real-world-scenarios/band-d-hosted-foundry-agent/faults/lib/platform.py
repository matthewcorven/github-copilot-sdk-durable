#!/usr/bin/env python3
"""The four platform behaviours D4 has to account for, in one place.

D1 answered these four from a live deployment and recorded each answer as *documented*,
*observed*, or both (`../evidence/platform-behaviour.md`). D4 does not provision anything,
so it cannot observe them again — and the rule this file exists to keep is that a
behaviour is never upgraded to "reproduced" because a local analogue looked similar.

Every result carries all four, each with one of exactly two statuses:

    documented, not exercised here      the answer comes from the documentation and D1's
                                        captures; nothing in this run reached Foundry
    reproduced here                     this run caused the behaviour and observed it

`reproduced here` is only legal for a fault whose injection reached the platform. No fault
in this directory does that, so `lib/result.py` refuses the status unless the fault script
also records a live call count — which it cannot, because there is no deployment.
"""

from __future__ import annotations

BEHAVIOURS = [
    {
        "id": "session-persistence",
        "question": "How long does a hosted agent session persist?",
        "documented": (
            "30 days, sliding on activity. The session persists the sandbox ($HOME, uploaded files), "
            "not the conversation: continuing a conversation needs previous_response_id or a "
            "conversation id."
        ),
        "source": "learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions; "
        "D1 evidence/session-resume/*.json (expires_at - created_at = 30 days, sliding)",
        "status": "documented, not exercised here",
    },
    {
        "id": "idle-timeout",
        "question": "What ends a session's idle lifetime, and what happens to its state?",
        "documented": (
            "900 s (15 min) by default, configurable 120 s-14400 s per agent version. On timeout the "
            "platform deprovisions compute and saves the session state until the session is referenced "
            "again."
        ),
        "source": "learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions; "
        "D1 evidence/platform-behaviour.md section 2 (default applied, eviction not waited out)",
        "status": "documented, not exercised here",
    },
    {
        "id": "foreground-cancelled",
        "question": "What happens to a foreground response when the driving client disappears mid-run?",
        "documented": (
            "Foreground responses stay tied to the client connection: the response is resolved "
            "cancelled, background=false, and the in-flight work is not preserved for a later lifetime."
        ),
        "source": "learn.microsoft.com/azure/foundry/agents/concepts/long-running-agent-resilience; "
        "D1 evidence/platform-behaviour.md section 3 (status cancelled, background false)",
        "status": "documented, not exercised here",
    },
    {
        "id": "no-replay",
        "question": "Does the platform replay the interrupted response?",
        "documented": (
            "No for foreground. Only stored background responses with resilient_background opted in are "
            "re-entered, and re-entry starts at the handler's beginning - not deterministic replay, no "
            "restored local variables or call stack."
        ),
        "source": "learn.microsoft.com/azure/foundry/agents/how-to/recover-long-running-work; "
        "D1 evidence/platform-behaviour.md section 4",
        "status": "documented, not exercised here",
    },
]

BY_ID = {item["id"]: item for item in BEHAVIOURS}


def block(*, touched: list[str], observed: str, note: str) -> dict:
    """The `platform` block every result carries.

    ``touched`` names the behaviours this fault speaks to. A touched behaviour keeps its
    documented answer and gains this run's *local* observation — it does not become
    "reproduced here", because nothing here is Foundry.
    """
    unknown = [item for item in touched if item not in BY_ID]
    if unknown:
        raise ValueError(f"unknown platform behaviour id(s): {', '.join(unknown)}")

    behaviours = []
    for item in BEHAVIOURS:
        entry = dict(item)
        entry["spoken_to_by_this_fault"] = item["id"] in touched
        behaviours.append(entry)

    return {
        "live_foundry_called": False,
        "what_the_platform_did": observed,
        "what_the_platform_is_not_represented_by": note,
        "behaviours": behaviours,
    }
