import asyncio
from contextvars import ContextVar

v: ContextVar[str | None] = ContextVar("v", default=None)
token_holder = {}

class Entry:
    async def __aenter__(self):
        token_holder["t"] = v.set("held")
        return self
    async def __aexit__(self, *a):
        v.reset(token_holder["t"])
        return False

async def body():
    print("  inside body:", v.get())
    await asyncio.sleep(0)
    print("  inside body after await:", v.get())

async def main():
    print("outer before:", v.get())
    async with Entry():
        await body()
    print("outer after:", v.get())

asyncio.run(main())
