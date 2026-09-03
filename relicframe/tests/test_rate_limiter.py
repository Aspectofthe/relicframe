"""
test_rate_limiter.py
Tests for rate_limiter.py. Uses real asyncio timing (small intervals) to
verify actual behavior, not just code inspection - same principle as this
project's test_wfm_api.py concurrency test for the old sync limiter.

Run with:
    python -m unittest test_rate_limiter.py -v
"""

import asyncio
import time
import unittest

from rate_limiter import AsyncRateLimiter


class TestConcurrencyCeiling(unittest.IsolatedAsyncioTestCase):
    async def test_never_exceeds_max_concurrent(self):
        limiter = AsyncRateLimiter(max_concurrent=3, max_per_second=1000.0)
        current = 0
        peak = 0
        lock = asyncio.Lock()

        async def worker():
            nonlocal current, peak
            async with limiter:
                async with lock:
                    current += 1
                    peak = max(peak, current)
                await asyncio.sleep(0.02)
                async with lock:
                    current -= 1

        await asyncio.gather(*(worker() for _ in range(10)))
        self.assertLessEqual(peak, 3)
        self.assertGreater(peak, 0)


class TestRateCeiling(unittest.IsolatedAsyncioTestCase):
    async def test_starts_are_spaced_by_min_interval(self):
        # High concurrency ceiling so only the RATE ceiling is under test.
        limiter = AsyncRateLimiter(max_concurrent=50, max_per_second=20.0)  # 0.05s min interval
        starts = []

        async def worker():
            async with limiter:
                starts.append(time.monotonic())

        await asyncio.gather(*(worker() for _ in range(8)))
        starts.sort()
        for earlier, later in zip(starts, starts[1:]):
            gap = later - earlier
            self.assertGreaterEqual(gap, 0.05 - 0.01, "requests started faster than the rate ceiling allows")

    async def test_does_not_block_other_coroutines_while_waiting(self):
        # A blocking time.sleep() inside the limiter would starve this
        # canary coroutine of any chance to run during the wait. With a
        # cooperative asyncio.sleep(), the canary should still get to
        # increment regularly even while several workers are queued on
        # the rate ceiling.
        limiter = AsyncRateLimiter(max_concurrent=10, max_per_second=5.0)  # 0.2s min interval
        canary_ticks = 0

        async def canary():
            nonlocal canary_ticks
            for _ in range(20):
                await asyncio.sleep(0.01)
                canary_ticks += 1

        async def worker():
            async with limiter:
                pass

        await asyncio.gather(canary(), *(worker() for _ in range(5)))
        self.assertGreater(canary_ticks, 10, "canary was starved - limiter may be blocking the event loop")


class TestInputValidation(unittest.TestCase):
    def test_rejects_non_positive_concurrency(self):
        with self.assertRaises(ValueError):
            AsyncRateLimiter(max_concurrent=0, max_per_second=1.0)

    def test_rejects_non_positive_rate(self):
        with self.assertRaises(ValueError):
            AsyncRateLimiter(max_concurrent=5, max_per_second=0.0)


if __name__ == "__main__":
    unittest.main()

