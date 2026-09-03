"""Serialize high-memory phases while Discord and normal requests stay responsive."""
import asyncio
from contextlib import asynccontextmanager
import gc
import weakref
from memory_usage import log_memory

_locks = weakref.WeakKeyDictionary()


@asynccontextmanager
async def heavy_operation(label):
    loop = asyncio.get_running_loop()
    lock = _locks.setdefault(loop, asyncio.Lock())
    async with lock:
        log_memory(f"{label} begin")
        try:
            yield
        finally:
            gc.collect()
            log_memory(f"{label} end")
