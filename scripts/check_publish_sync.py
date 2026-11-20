"""核对 DeskBar 的三个程序集副本是否真的同步。

用途：部署后确认「跑起来的是我刚改的代码」。
业务逻辑全在 DeskBar.dll 里（DeskBar.exe 只是个壳，哈希恒为同一个值），
所以不看 exe，只比 dll。

判定规则：
- bin/Release 与 out_publish 的 md5 **可以不同** —— publish 会重新编译一次；
- out_publish 与部署目录的 md5 **必须相同** —— 这才是「部署成功了」。
  曾经 deploy.sh 的 cp 失败只打印一行 FAIL 就继续，带着旧 dll 启动，
  表现为「改了却没生效」；这个脚本就是用来抓那种情况的。

用法：
  python scripts/check_publish_sync.py            # 只比对
"""

import hashlib
import os
import sys

COPIES = [
    ("bin/Release", "src/DeskBar/bin/Release/net8.0-windows10.0.19041.0/DeskBar.dll"),
    ("out_publish", "out_publish/DeskBar.dll"),
    ("deployed", "D:/app/DeskBar/DeskBar.dll"),
]


def digest(path):
    try:
        with open(path, "rb") as fh:
            data = fh.read()
    except OSError as exc:
        return None, None, str(exc)
    return hashlib.md5(data).hexdigest(), len(data), None


def main():
    results = {}

    for label, path in COPIES:
        md5, size, err = digest(path)
        if err:
            print("%-12s %-8s %s" % (label, "MISSING", path))
            results[label] = None
            continue
        print("%-12s %s  %8d bytes" % (label, md5[:16], size))
        results[label] = md5

    published = results.get("out_publish")
    deployed = results.get("deployed")

    if published is None or deployed is None:
        print("\nRESULT: FAIL - out_publish or deployed copy is missing")
        return 1

    if published != deployed:
        print("\nRESULT: FAIL - deployed copy is stale (out_publish=%s deployed=%s)"
              % (published[:16], deployed[:16]))
        return 1

    print("\nRESULT: OK - deployed copy matches out_publish")
    return 0


if __name__ == "__main__":
    sys.exit(main())
