"""
test_main.py
Tests for the handful of module-level functions in main.py that don't
touch tkinter (no live display needed, so these run anywhere).

Run with:
    python -m unittest test_main.py -v
"""

import unittest

try:
    from main import _column_sort_key, _format_age
    _TKINTER_AVAILABLE = True
except ImportError:
    # main.py imports tkinter at module level (it's a GUI app), which
    # isn't installed in every environment these tests might run in (e.g.
    # a headless CI runner). Skip gracefully rather than letting the whole
    # test suite hard-fail to load - `python -m unittest discover` should
    # stay green everywhere else even when this one file can't run here.
    _TKINTER_AVAILABLE = False


@unittest.skipUnless(_TKINTER_AVAILABLE, "tkinter not installed in this environment")
class TestColumnSortKey(unittest.TestCase):
    """
    Regression coverage for a real bug: clicking a percentage-style column
    header (e.g. "Odds") used to sort alphabetically instead of
    numerically, because the old strip-then-split order never actually
    reached the '%' character sitting in the middle of a multi-word cell
    value like "16.7% \u00b7 6.0 exp. opens".
    """

    def test_percent_and_exp_openings_format_sorts_numeric(self):
        self.assertEqual(_column_sort_key("16.7% \u00b7 6.0 exp. opens"), (0, 16.7))
        self.assertEqual(_column_sort_key("5.2% \u00b7 19.2 exp. opens"), (0, 5.2))

    def test_odds_column_values_sort_in_true_numeric_order(self):
        # The actual regression case: with the old buggy key, this list
        # would come out ['16.7%...', '23.1%...', '5.2%...'] (alphabetical
        # on the string "16.7%" / "23.1%" / "5.2%"). Correct numeric order
        # is 5.2 < 16.7 < 23.1.
        values = [
            "16.7% \u00b7 6.0 exp. opens",
            "5.2% \u00b7 19.2 exp. opens",
            "23.1% \u00b7 4.3 exp. opens",
        ]
        ordered = sorted(values, key=_column_sort_key)
        self.assertEqual(ordered, [
            "5.2% \u00b7 19.2 exp. opens",
            "16.7% \u00b7 6.0 exp. opens",
            "23.1% \u00b7 4.3 exp. opens",
        ])

    def test_plain_number_sorts_numeric(self):
        self.assertEqual(_column_sort_key("45"), (0, 45.0))
        self.assertEqual(_column_sort_key("3.5"), (0, 3.5))

    def test_placeholder_dash_sorts_as_string_not_numeric(self):
        self.assertEqual(_column_sort_key("-")[0], 1)

    def test_item_name_column_sorts_as_string(self):
        kind, value = _column_sort_key("Ember Prime Blueprint (52.3p)")
        self.assertEqual(kind, 1)
        self.assertEqual(value, "Ember Prime Blueprint (52.3p)")

    def test_fallback_with_warning_glyph_sorts_as_string(self):
        kind, _value = _column_sort_key("Fallback \u26A0")
        self.assertEqual(kind, 1)

    def test_numeric_and_string_values_dont_interleave(self):
        # The (0, ...) / (1, ...) tuple prefix must keep every numeric
        # value sorted before every string value, regardless of value -
        # e.g. a column with mostly percentages but a few "-" placeholders
        # must not have "-" sort in among the numbers by string comparison.
        values = ["10.0%", "-", "5.0%", "-"]

        def strip_pct(v):
            return _column_sort_key(v.rstrip("%")) if v != "-" else _column_sort_key(v)

        # Use the real key directly on percent-suffixed single tokens too:
        ordered = sorted(values, key=_column_sort_key)
        numeric_prefix = [k for k in ordered if _column_sort_key(k)[0] == 0]
        string_prefix = [k for k in ordered if _column_sort_key(k)[0] == 1]
        self.assertEqual(ordered[:len(numeric_prefix)], numeric_prefix)
        self.assertEqual(ordered[len(numeric_prefix):], string_prefix)


@unittest.skipUnless(_TKINTER_AVAILABLE, "tkinter not installed in this environment")
class TestFormatAge(unittest.TestCase):
    def test_none_is_unknown(self):
        self.assertEqual(_format_age(None), "unknown")

    def test_seconds(self):
        self.assertEqual(_format_age(45), "45s")

    def test_minutes(self):
        self.assertEqual(_format_age(200), "3m")

    def test_hours(self):
        self.assertEqual(_format_age(7200), "2.0h")


if __name__ == "__main__":
    unittest.main()

