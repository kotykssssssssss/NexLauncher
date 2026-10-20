"""Linux/WSLg-only launcher smoke. Uses only Python stdlib and xwininfo/xprop.
Never launches Minecraft, edits the real user profile, or interacts with other windows.
"""
import argparse
import ctypes
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import time


def close_window(window_id):
    class Data(ctypes.Union):
        _fields_ = [("b", ctypes.c_char * 20), ("s", ctypes.c_short * 10), ("l", ctypes.c_long * 5)]

    class ClientMessage(ctypes.Structure):
        _fields_ = [("type", ctypes.c_int), ("serial", ctypes.c_ulong), ("send_event", ctypes.c_int),
                    ("display", ctypes.c_void_p), ("window", ctypes.c_ulong), ("message_type", ctypes.c_ulong),
                    ("format", ctypes.c_int), ("data", Data)]

    class Event(ctypes.Union):
        _fields_ = [("client", ClientMessage), ("pad", ctypes.c_long * 24)]

    x11 = ctypes.CDLL("libX11.so.6")
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = ctypes.c_void_p
    x11.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
    x11.XInternAtom.restype = ctypes.c_ulong
    x11.XSendEvent.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_long, ctypes.POINTER(Event)]
    x11.XFlush.argtypes = [ctypes.c_void_p]
    x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
    display = x11.XOpenDisplay(None)
    if not display:
        raise RuntimeError("X11 display unavailable")
    try:
        event = Event()
        event.client.type = 33
        event.client.display = display
        event.client.window = window_id
        event.client.message_type = x11.XInternAtom(display, b"WM_PROTOCOLS", 0)
        event.client.format = 32
        event.client.data.l[0] = x11.XInternAtom(display, b"WM_DELETE_WINDOW", 0)
        if not x11.XSendEvent(display, window_id, 0, 0, ctypes.byref(event)):
            raise RuntimeError("Close request failed")
        x11.XFlush(display)
    finally:
        x11.XCloseDisplay(display)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", required=True)
    parser.add_argument("--report-directory", required=True)
    args = parser.parse_args()
    if os.uname().sysname != "Linux":
        raise RuntimeError("Run on Linux, optionally inside existing WSLg")
    for tool in ("xwininfo", "xprop"):
        if not shutil.which(tool):
            raise RuntimeError(f"Required tool unavailable: {tool}")
    output = Path(args.report_directory).resolve()
    output.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix="nexlauncher-smoke-", dir="/tmp"))
    process = None
    try:
        with tarfile.open(Path(args.archive).resolve(), "r:gz") as archive:
            archive.extractall(temporary, filter="data")
        application = temporary / "NexLauncher"
        environment = {k: v for k, v in os.environ.items() if not re.match(r"^(DOTNET_|CORECLR_|COMPlus_|NUGET_)", k)}
        environment.update({"XDG_DATA_HOME": str(temporary / "profile"), "XDG_CACHE_HOME": str(temporary / "cache"),
                            "XDG_CONFIG_HOME": str(temporary / "config"), "DOTNET_ROOT": str(temporary / "no-runtime"),
                            "DOTNET_MULTILEVEL_LOOKUP": "0", "PATH": "/usr/bin:/bin"})
        with (output / "launcher.log").open("w") as log:
            process = subprocess.Popen([str(application / "NexLauncher")], cwd=temporary, env=environment,
                                       stdout=log, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + 35
            window_id = None
            while time.monotonic() < deadline and process.poll() is None:
                tree = subprocess.run(["xwininfo", "-root", "-tree"], capture_output=True, text=True, timeout=3).stdout
                for match in re.finditer(r'(0x[0-9a-fA-F]+)\s+"NexLauncher"', tree):
                    candidate = match.group(1)
                    prop = subprocess.run(["xprop", "-id", candidate, "_NET_WM_PID"], capture_output=True, text=True, timeout=3).stdout
                    if re.search(r"=\s*" + str(process.pid) + r"\s*$", prop):
                        window_id = int(candidate, 16)
                        break
                if window_id is not None:
                    break
                time.sleep(0.2)
            if window_id is None:
                raise RuntimeError(f"Own launcher window did not appear; exit={process.poll()}; see launcher.log")
            time.sleep(3)
            if process.poll() is not None:
                raise RuntimeError("Launcher exited during initialization")
            maps = Path(f"/proc/{process.pid}/maps").read_text()
            for native in ("libcoreclr.so", "libSkiaSharp.so"):
                matching = [line for line in maps.splitlines() if native in line]
                if not matching or any(str(application) not in line for line in matching):
                    raise RuntimeError(f"Native dependency not loaded from own self-contained payload: {native}")
            close_window(window_id)
            exit_code = process.wait(timeout=12)
            if exit_code != 0:
                raise RuntimeError(f"Launcher close returned {exit_code}")
            result = {"os": "Linux", "architecture": os.uname().machine, "wslg": bool(os.environ.get("WSL_DISTRO_NAME")),
                      "sdkAvailable": bool(shutil.which("dotnet")), "isolatedUserData": True,
                      "ownWindowFound": True, "selfContainedNativeLibraries": True, "normalExitCode": exit_code,
                      "minecraftStarted": False, "microsoftLoginTested": False, "keyringTested": False}
            (output / "smoke-result.json").write_text(json.dumps(result, indent=2) + "\n")
            print(json.dumps(result))
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        # Only this freshly generated direct child of /tmp is ever removed.
        if temporary.parent == Path("/tmp") and temporary.name.startswith("nexlauncher-smoke-"):
            shutil.rmtree(temporary)


if __name__ == "__main__":
    main()
