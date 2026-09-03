"""Check the host launcher without loading credentials or connecting to Discord."""

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class HostingEntrypointTests(unittest.TestCase):
    def test_launcher_sets_app_directory_and_resolves_sibling_imports(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            app = root / "relicframe"
            app.mkdir()
            shutil.copyfile(ROOT / "main.py", root / "main.py")
            (app / "hosting_probe.py").write_text("VALUE = 'import-ok'\n", encoding="utf-8")
            (app / "bot.py").write_text(
                "import json, os, sys\n"
                "from hosting_probe import VALUE\n"
                "print(json.dumps([os.getcwd(), __name__, VALUE, sys.argv[1:]]))\n",
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(root / "main.py"), "--probe"],
                cwd=tempfile.gettempdir(), capture_output=True, text=True, check=True,
            )
            self.assertEqual(json.loads(result.stdout), [str(app), "__main__", "import-ok", ["--probe"]])

    def test_import_does_not_start_bot(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            shutil.copyfile(ROOT / "main.py", root / "main.py")
            # No relicframe folder: importing must neither chdir nor execute bot.py.
            result = subprocess.run(
                [sys.executable, "-c", "import main; print('safe')"],
                cwd=root, capture_output=True, text=True, check=True,
            )
            self.assertEqual(result.stdout.strip(), "safe")


if __name__ == "__main__":
    unittest.main()
