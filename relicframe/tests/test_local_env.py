import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from local_env import load_local_env


class TestLocalEnv(unittest.TestCase):
    def test_loads_supported_lines_and_never_overwrites_process_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            path.write_text(
                "# comment\nPLAIN=value\nQUOTED=\"spaced value\"\nexport EXPORTED=yes\n"
                "BAD LINE\n1INVALID=no\nEXISTING=file-value\n",
                encoding="utf-8",
            )
            with patch.dict(os.environ, {"EXISTING": "process-value"}, clear=True):
                loaded = load_local_env(path)
                self.assertEqual(loaded, 3)
                self.assertEqual(os.environ["PLAIN"], "value")
                self.assertEqual(os.environ["QUOTED"], "spaced value")
                self.assertEqual(os.environ["EXPORTED"], "yes")
                self.assertEqual(os.environ["EXISTING"], "process-value")
                self.assertNotIn("1INVALID", os.environ)

    def test_missing_file_is_a_no_op(self):
        self.assertEqual(load_local_env(Path("definitely-missing.env")), 0)


if __name__ == "__main__":
    unittest.main()
