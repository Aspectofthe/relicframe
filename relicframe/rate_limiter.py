from __future__ import annotations

import asyncio
import time


class AsyncRateLimiter:
    """Async global scheduler enforcing request rate and concurrency."""

    def __init__(self, max_concurrent: int = 15, max_per_second: float = 5.0):
        if max_concurrent < 1:
            raise ValueError("max_concurrent must be at least 1")
        if max_per_second <= 0:
            raise ValueError("max_per_second must be positive")
        self.max_concurrent = max_concurrent
        self.max_per_second = max_per_second
        self._min_interval = 1.0 / max_per_second
        self._semaphore = asyncio.Semaphore(max_concurrent)
        self._schedule_lock = asyncio.Lock()
        self._last_start = 0.0
        self._blocked_until = 0.0

    async def __aenter__(self):
        await self._semaphore.acquire()
        try:
            async with self._schedule_lock:
                now = time.monotonic()
                wait = max(
                    self._last_start + self._min_interval - now,
                    self._blocked_until - now,
                )
                if wait > 0:
                    await asyncio.sleep(wait)
                self._last_start = time.monotonic()
        except BaseException:
            self._semaphore.release()
            raise
        return self

    async def pause(self, seconds: float) -> None:
        """Temporarily stop new request starts after a server 429."""
        if seconds <= 0:
            return
        async with self._schedule_lock:
            self._blocked_until = max(
                self._blocked_until, time.monotonic() + seconds
            )

    async def __aexit__(self, exc_type, exc, tb):
        self._semaphore.release()
        return False

