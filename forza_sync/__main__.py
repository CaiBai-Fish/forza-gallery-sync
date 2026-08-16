"""入口，兼容两种运行方式：

    python -m forza_sync <命令>   （标准包方式）
    python forza_sync <命令>      （直接运行目录脚本）
"""

import os
import sys


def _ensure_package_importable() -> None:
    """以目录方式直接运行时（python forza_sync），把包父目录加入 sys.path。

    Python 直接运行目录时，sys.path[0] 是包目录本身而非其父目录，
    因此需要手动加入父目录才能 `import forza_sync`。
    """
    if __package__ not in (None, ""):
        return  # -m 方式：父目录已在 sys.path
    parent = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if parent not in sys.path:
        sys.path.insert(0, parent)


_ensure_package_importable()

from forza_sync.cli import main

if __name__ == "__main__":
    sys.exit(main())
