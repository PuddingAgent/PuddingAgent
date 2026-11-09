"""Cold-start the Windows Console Core built by test-foundation-prototype.ps1."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.error import URLError
from urllib.request import build_opener, ProxyHandler


def smoke(executable: Path, run: Path, verify=None, configure=None) -> bool:
    run.mkdir()  # A prior process's log or database cannot satisfy this run.
    data = run / "data"
    system_keys = {
        "SYSTEMROOT", "WINDIR", "PATH", "COMSPEC", "PATHEXT", "PROGRAMFILES",
        "PROGRAMFILES(X86)", "PROGRAMW6432", "DOTNET_ROOT", "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS", "SYSTEMDRIVE",
    }
    env = {k: v for k, v in os.environ.items() if k.upper() in system_keys}
    for name, path in {
        "PUDDING_DATA_ROOT": data, "PUDDING_DESKTOP_HOME": run / "desktop",
        "USERPROFILE": run / "profile", "APPDATA": run / "roaming",
        "LOCALAPPDATA": run / "local", "TEMP": run / "temp", "TMP": run / "temp",
    }.items():
        path.mkdir(exist_ok=True)
        env[name] = str(path)

    if configure is not None:
        configure(data)
    env["ASPNETCORE_URLS"] = "http://127.0.0.1:0"
    args = [str(executable), "--data-root", str(data), "--urls", env["ASPNETCORE_URLS"]]
    http = build_opener(ProxyHandler({}))
    stdout = run / "stdout.log"
    result = {"executable": str(executable), "dataRoot": str(data), "passed": False}
    with stdout.open("wb") as out, (run / "stderr.log").open("wb") as err:
        process = subprocess.Popen(args, cwd=executable.parent, env=env, stdin=subprocess.DEVNULL,
                                   stdout=out, stderr=err, creationflags=subprocess.CREATE_NO_WINDOW)
        result["pid"] = process.pid
        try:
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline and process.poll() is None:
                if "address" not in result:
                    bound = re.search(rb"\[Startup\] Server bound addresses: \[(http://127\.0\.0\.1:[1-9]\d*)\]", stdout.read_bytes())
                    if bound:
                        result["address"] = bound.group(1).decode("ascii")
                if "address" in result:
                    try:
                        for endpoint in ("ready", "live"):
                            with http.open(result["address"] + "/health/" + endpoint, timeout=2) as response:
                                result[endpoint] = {"http": response.status, "status": json.load(response)["status"]}
                        result["passed"] = (
                            result["ready"] == {"http": 200, "status": "ready"}
                            and result["live"] == {"http": 200, "status": "alive"}
                            and (data / "databases" / "pudding_platform.db").is_file()
                            and process.poll() is None
                        )
                        if result["passed"]:
                            break
                    except (URLError, OSError, ValueError, KeyError):
                        pass  # Kestrel can bind before initialization finishes.
                time.sleep(0.5)
            if result["passed"] and verify is not None:
                result["passed"] = False
                verify(result["address"], process.pid, data)
                result["passed"] = True
        finally:
            result["exitBeforeCleanup"] = process.poll()
            if result["exitBeforeCleanup"] is not None:
                result["passed"] = False
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=15)
            result["stopped"] = True
            (run / "health.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))
    return result["passed"]


if __name__ == "__main__":
    sys.exit(0 if smoke(Path(sys.argv[1]).resolve(strict=True), Path(sys.argv[2]).resolve()) else 1)
