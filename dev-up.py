"""根目录转发 shim：dev-up 脚本本体已归位到 `Tools/Dev/dev-up.py`（2026-10-02）。

保留 `python dev-up.py ...` 这一历史入口，避免同步数十处文档引用；
仓库根识别由脚本本体的标志文件向上查找完成，因此从根目录调用仍然正确。
"""

import runpy
import sys
from pathlib import Path

_TARGET = Path(__file__).resolve().parent / "Tools" / "Dev" / "dev-up.py"
sys.argv[0] = str(_TARGET)
runpy.run_path(str(_TARGET), run_name="__main__")