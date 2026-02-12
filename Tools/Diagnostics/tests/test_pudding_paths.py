import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


TOOLS_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIR))

from pudding_paths import resolve_data_paths


class PuddingPathsTests(unittest.TestCase):
    def test_resolve_data_paths_uses_explicit_environment_before_dev_up(self):
        with tempfile.TemporaryDirectory() as env_dir, tempfile.TemporaryDirectory() as dev_dir:
            env_root = Path(env_dir)
            dev_root = Path(dev_dir)
            db_path = env_root / "pudding_platform.db"
            db_path.write_text("", encoding="utf-8")

            with patch.dict(os.environ, {"PUDDING_DATA_ROOT": str(env_root)}, clear=True):
                with patch(
                    "pudding_paths._dev_up_backend_environment",
                    return_value={"PUDDING_DATA_ROOT": str(dev_root)},
                ):
                    paths = resolve_data_paths()
                    resolved_db_path = paths.platform_db_file()

            self.assertEqual(env_root.resolve(), paths.data_root)
            self.assertEqual(db_path.resolve(), resolved_db_path)

    def test_resolve_data_paths_uses_dev_up_environment_when_shell_env_is_absent(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_root = Path(temp_dir)
            db_path = data_root / "pudding_platform.db"
            db_path.write_text("", encoding="utf-8")

            with patch.dict(os.environ, {}, clear=True):
                with patch(
                    "pudding_paths._dev_up_backend_environment",
                    return_value={
                        "PUDDING_DATA_ROOT": str(data_root),
                        "ConnectionStrings__Default": f"Data Source={db_path}",
                    },
                ):
                    paths = resolve_data_paths()
                    resolved_db_path = paths.platform_db_file()

            self.assertEqual(data_root.resolve(), paths.data_root)
            self.assertEqual(db_path.resolve(), resolved_db_path)


if __name__ == "__main__":
    unittest.main()
