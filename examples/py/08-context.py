"""08 — Context. LIVE: the recorded runtime cannot fork sessions."""
from copilot import SessionEventType
from example import run_example
from check import Check


async def main(harness, store, registry):
    transcript = await harness.create_conversation_async()
    print(f"effective model: {(await transcript.wait_for_effective_model_async()).model or '(not reported)'}")

    async def write(tx):
        tx.append_entry(transcript.id, "message", "read a and b")
        tx.append_entry(transcript.id, "message", "I crashed")
        tx.append_entry(transcript.id, "message", "reading")
        tx.append_entry(transcript.id, "message", "a and b look fine")
        tx.append_entry(transcript.id, "note", "display only")

    await transcript.commit_async(write)
    reply = await transcript.submit_async("Say exactly: CONTEXT")
    print(f"answer: {reply.data.content if reply else '(no answer)'}")
    first = transcript.entries(10)[-1]
    fork = await harness.fork_async(transcript, first)
    Check.equal("CONTEXT", reply.data.content if reply else None, "the model answered as asked")
    Check.that(len(transcript.entries(10)) >= 1, "the conversation stored at least one entry")
    events = await transcript.context_async()
    Check.that(any(e.type == SessionEventType.USER_MESSAGE for e in events), "the model context holds the user message")
    Check.that(any(e.type == SessionEventType.ASSISTANT_MESSAGE for e in events), "the model context holds the assistant message")
    Check.that(any(e.type == SessionEventType.SYSTEM_MESSAGE for e in events), "the model context holds the system message")
    fork_entries = fork.entries(10)
    Check.that(len(fork_entries) <= len(transcript.entries(10)), "the fork holds no more entries than the parent")
    Check.that(first in fork_entries, "the fork inherited the entry it was taken at")
    Check.equal(transcript.id, fork.record.parent_conversation_id, "the fork records its parent")


if __name__ == "__main__":
    run_example(main)
