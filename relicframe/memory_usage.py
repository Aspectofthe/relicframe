"""Dependency-free process/container memory diagnostics; never log payloads."""
import os
from pathlib import Path


def memory_stats():
    stats = {}
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in (
                    "peak", "rss", "peak_pool_paged", "pool_paged", "peak_pool_nonpaged",
                    "pool_nonpaged", "pagefile", "peak_pagefile")]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        if psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
            stats.update(rss_mb=counters.rss / 1048576, peak_mb=counters.peak / 1048576)
    else:
        try:
            for line in Path("/proc/self/status").read_text().splitlines():
                if line.startswith(("VmRSS:", "VmHWM:")):
                    stats["rss_mb" if line.startswith("VmRSS") else "peak_mb"] = int(line.split()[1]) / 1024
        except OSError:
            pass
        for key, paths in {
            "container_mb": ("/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory/memory.usage_in_bytes"),
            "limit_mb": ("/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"),
        }.items():
            for path in paths:
                try:
                    value = int(Path(path).read_text().strip())
                    if value < 1 << 60:
                        stats[key] = value / 1048576
                    break
                except (OSError, ValueError):
                    continue
    return {key: round(value, 1) for key, value in stats.items()}


def log_memory(stage):
    stats = memory_stats()
    print(f"[memory] {stage}: " + " ".join(f"{key}={value}" for key, value in stats.items()), flush=True)
    return stats


async def monitor_memory():
    import asyncio
    while True:
        stats = log_memory("runtime")
        used = stats.get("container_mb", stats.get("rss_mb", 0))
        limit = stats.get("limit_mb", 256)
        if used > limit * 0.8:
            print("[memory] WARNING: above 80% of memory budget; capture these readings for diagnosis.", flush=True)
        await asyncio.sleep(60)
