"""Network-free tests for the Discord webhook publisher's local behavior."""

import argparse
import unittest
from types import SimpleNamespace

import discord_webhook


class TestWebhookArguments(unittest.TestCase):
    def test_limit_accepts_discord_safe_range(self):
        self.assertEqual(discord_webhook._limit_value("1"), 1)
        self.assertEqual(discord_webhook._limit_value("10"), 10)

    def test_limit_rejects_values_outside_one_message(self):
        with self.assertRaises(argparse.ArgumentTypeError):
            discord_webhook._limit_value("0")
        with self.assertRaises(argparse.ArgumentTypeError):
            discord_webhook._limit_value("11")

    def test_negative_interval_is_rejected(self):
        with self.assertRaises(argparse.ArgumentTypeError):
            discord_webhook._non_negative_float("-1")

    def test_zero_request_rate_is_rejected(self):
        with self.assertRaises(argparse.ArgumentTypeError):
            discord_webhook._positive_float("0")


class TestWebhookRowSelection(unittest.TestCase):
    def test_selection_applies_filters_then_ranking(self):
        args = SimpleNamespace(
            trace_rate=0.0,
            channel_scope="Online + Offline",
            vault="All",
            min_roi=None,
            max_cost=None,
            min_reward=None,
            green_only=False,
            rank_by="Best Overall",
        )
        snapshot = SimpleNamespace()
        original_compute = discord_webhook.analysis.compute_all_rows
        original_passes = discord_webhook.analysis.passes_filters
        original_rank = discord_webhook.analysis.rank_rows
        try:
            discord_webhook.analysis.compute_all_rows = lambda *_: ["keep", "drop"]
            discord_webhook.analysis.passes_filters = lambda row, *_args, **_kwargs: row == "keep"
            discord_webhook.analysis.rank_rows = lambda rows, *_: list(reversed(rows))
            self.assertEqual(discord_webhook.select_rows(snapshot, args), ["keep"])
        finally:
            discord_webhook.analysis.compute_all_rows = original_compute
            discord_webhook.analysis.passes_filters = original_passes
            discord_webhook.analysis.rank_rows = original_rank


if __name__ == "__main__":
    unittest.main()

