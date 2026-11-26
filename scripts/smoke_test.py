#!/usr/bin/env python3
"""Run a read-only MCP/UIA smoke scenario and retain protocol/timing evidence."""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid


def normalize_path(value):
    return os.path.normcase(os.path.realpath(value))


def windows_processes():
    """Get executable paths and creation times, without command-line secrets."""
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)

    class Entry(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                    ("pid", wintypes.DWORD), ("heap", ctypes.c_size_t),
                    ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                    ("parent", wintypes.DWORD), ("priority", wintypes.LONG),
                    ("flags", wintypes.DWORD), ("exe", wintypes.WCHAR * 260)]

    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    result = {}
    try:
        entry = Entry()
        entry.dwSize = ctypes.sizeof(entry)
        ok = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            record = {"pid": entry.pid, "parent": entry.parent, "path": None, "created": None}
            handle = kernel.OpenProcess(0x1000, False, entry.pid)
            if handle:
                try:
                    size = wintypes.DWORD(32768)
                    path = ctypes.create_unicode_buffer(size.value)
                    if kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(size)):
                        record["path"] = normalize_path(path.value)
                    times = [wintypes.FILETIME() for _ in range(4)]
                    if kernel.GetProcessTimes(handle, *(ctypes.byref(x) for x in times)):
                        record["created"] = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
                finally:
                    kernel.CloseHandle(handle)
            result[entry.pid] = record
            ok = kernel.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    return result


def terminate_verified_process(record):
    """Recheck identity on the same handle used to terminate (PID reuse safe)."""
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000 | 0x100000 | 1, False, record["pid"])
    if not handle:
        return {"pid": record["pid"], "status": "already_exited_or_inaccessible"}
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *(ctypes.byref(x) for x in times)):
            raise ctypes.WinError(ctypes.get_last_error())
        created = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        if created != record["created"]:
            return {"pid": record["pid"], "status": "identity_changed_not_terminated"}
        if not kernel.TerminateProcess(handle, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        wait = kernel.WaitForSingleObject(handle, 5000)
        return {"pid": record["pid"], "status": "terminated" if wait == 0 else "termination_not_confirmed"}
    finally:
        kernel.CloseHandle(handle)


class StdioClient:
    def __init__(self, executable, output):
        self.stderr_file = (output / "server.stderr.log").open("w", encoding="utf-8")
        self.process = subprocess.Popen([str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.stderr_file, text=True, encoding="utf-8", bufsize=1,
                                        creationflags=subprocess.CREATE_NO_WINDOW)
        self.responses = queue.Queue()
        self.events = []
        self.request_id = 0
        self.descendant_identities = set()
        self.monitor_stop = threading.Event()
        self.monitor = threading.Thread(target=self._monitor_descendants, daemon=True)
        self.monitor.start()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _monitor_descendants(self):
        # Retain ownership evidence if a UIA worker times out and leaves the app
        # alive after its parent process exits. Never use process names for this.
        while not self.monitor_stop.is_set() and self.process.poll() is None:
            try:
                snapshot = windows_processes()
                descendants = {self.process.pid}
                for _ in range(len(snapshot)):
                    children = {pid for pid, item in snapshot.items() if item["parent"] in descendants}
                    if children.issubset(descendants):
                        break
                    descendants |= children
                for pid in descendants:
                    record = snapshot.get(pid)
                    if record and record["created"]:
                        self.descendant_identities.add((pid, record["created"]))
            except OSError:
                pass
            self.monitor_stop.wait(0.5)

    def _read(self):
        try:
            for line in self.process.stdout:
                try:
                    self.responses.put(json.loads(line))
                except json.JSONDecodeError:
                    self.responses.put({"protocolError": "Non-JSON stdout", "line": line[:2000]})
        finally:
            self.responses.put({"closed": True})

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.process.stdin.flush()

    def request(self, method, params=None, timeout=45):
        self.request_id += 1
        request = {"jsonrpc": "2.0", "id": self.request_id, "method": method}
        if params is not None:
            request["params"] = params
        started = time.perf_counter()
        self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        event = {"request": request}
        try:
            while True:
                left = timeout - (time.perf_counter() - started)
                if left <= 0:
                    raise TimeoutError(f"{method} exceeded {timeout}s protocol deadline")
                try:
                    response = self.responses.get(timeout=left)
                except queue.Empty as exc:
                    raise TimeoutError(f"{method} exceeded {timeout}s protocol deadline") from exc
                if response.get("closed") or response.get("protocolError"):
                    raise RuntimeError(str(response))
                if response.get("id") == self.request_id:
                    event["response"] = response
                    return response
                self.events.append({"notification": response})
        except Exception as exc:
            event["exception"] = f"{type(exc).__name__}: {exc}"
            raise
        finally:
            event["wallElapsedMs"] = round((time.perf_counter() - started) * 1000, 3)
            self.events.append(event)

    def tool(self, name, arguments=None, timeout=45):
        response = self.request("tools/call", {"name": name, "arguments": arguments or {}}, timeout)
        if "error" in response:
            raise RuntimeError(json.dumps(response["error"], ensure_ascii=False))
        result = response["result"]
        payload = result.get("structuredContent")
        if payload is None:
            content = [item["text"] for item in result.get("content", []) if item.get("type") == "text"]
            joined = "\n".join(content)
            try:
                payload = json.loads(joined)
            except json.JSONDecodeError:
                payload = {"text": joined}
        if result.get("isError"):
            raise RuntimeError(json.dumps(payload, ensure_ascii=False))
        if isinstance(payload, dict) and (payload.get("success") is False or payload.get("ok") is False):
            raise RuntimeError(json.dumps(payload, ensure_ascii=False))
        return payload

    def close(self):
        try:
            if self.process.stdin:
                self.process.stdin.close()
            self.process.wait(timeout=5)
        except (subprocess.TimeoutExpired, BrokenPipeError):
            self.process.kill()
            self.process.wait(timeout=5)
        finally:
            self.monitor_stop.set()
            self.monitor.join(timeout=2)
            self.stderr_file.close()


def field(value, name, default=None):
    if not isinstance(value, dict):
        return default
    return next((v for k, v in value.items() if k.casefold() == name.casefold()), default)


def all_objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from all_objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from all_objects(child)


def save_window_image(handle, destination):
    """Capture one window as evidence. No screenshot is used for UI location."""
    user = ctypes.WinDLL("user32", use_last_error=True)
    gdi = ctypes.WinDLL("gdi32", use_last_error=True)
    user.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user.GetWindowDC.argtypes = [wintypes.HWND]
    user.GetWindowDC.restype = wintypes.HDC
    user.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
    user.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
    gdi.CreateCompatibleDC.argtypes = [wintypes.HDC]
    gdi.CreateCompatibleDC.restype = wintypes.HDC
    gdi.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
    gdi.CreateCompatibleBitmap.restype = wintypes.HBITMAP
    gdi.SelectObject.argtypes = [wintypes.HDC, wintypes.HANDLE]
    gdi.SelectObject.restype = wintypes.HANDLE
    gdi.DeleteObject.argtypes = [wintypes.HANDLE]
    gdi.DeleteDC.argtypes = [wintypes.HDC]
    gdi.GetDIBits.argtypes = [wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
                            ctypes.c_void_p, ctypes.c_void_p, wintypes.UINT]
    rect = wintypes.RECT()
    if not user.GetWindowRect(handle, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    width, height = rect.right - rect.left, rect.bottom - rect.top
    if width <= 0 or height <= 0 or width * height > 30_000_000:
        raise ValueError("Window has invalid screenshot dimensions")
    screen = user.GetWindowDC(handle)
    memory = gdi.CreateCompatibleDC(screen)
    bitmap = gdi.CreateCompatibleBitmap(screen, width, height)
    old = gdi.SelectObject(memory, bitmap)
    try:
        if not user.PrintWindow(handle, memory, 2):
            raise RuntimeError("PrintWindow failed; screenshot unavailable")
        import struct
        info = ctypes.create_string_buffer(struct.pack("<IiiHHIIiiII", 40, width, -height,
                                                       1, 32, 0, width * height * 4, 0, 0, 0, 0))
        pixels = ctypes.create_string_buffer(width * height * 4)
        gdi.SelectObject(memory, old)
        if not gdi.GetDIBits(memory, bitmap, 0, height, pixels, info, 0):
            raise RuntimeError("GetDIBits failed")
        header = struct.pack("<2sIHHI", b"BM", 54 + len(pixels), 0, 0, 54)
        destination.write_bytes(header + info.raw[:40] + pixels.raw)
    finally:
        gdi.SelectObject(memory, old)
        gdi.DeleteObject(bitmap)
        gdi.DeleteDC(memory)
        user.ReleaseDC(handle, screen)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--args", default="")
    parser.add_argument("--process-name")
    parser.add_argument("--expected-window-title", help="Required title substring for main-window acceptance")
    parser.add_argument("--expected-main-id", help="Required main-window AutomationId")
    parser.add_argument("--skip-screenshot", action="store_true")
    options = parser.parse_args()
    if os.name != "nt":
        parser.error("This smoke scenario requires Windows")
    options.server = options.server.resolve(strict=True)
    options.app = options.app.resolve(strict=True)
    options.output.mkdir(parents=True, exist_ok=True)
    if (options.output / "report.json").exists():
        parser.error("Output already contains evidence; choose a new output directory")
    report = {"status": "BLOCKED", "application": str(options.app), "server": str(options.server),
              "assertions": [], "cleanup": [], "limits": [], "mainWindowVerified": False}
    before = windows_processes()
    app_path = normalize_path(options.app)
    report["preexistingTargetProcessIds"] = [pid for pid, record in before.items()
                                              if record["path"] == app_path]
    client = None
    launched_pid = None
    launched_owned = False
    try:
        client = StdioClient(options.server, options.output)
        initialize = client.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                                   "clientInfo": {"name": "wpf-uia-smoke", "version": "1.0"}})
        if "error" in initialize:
            raise RuntimeError(str(initialize["error"]))
        client.notify("notifications/initialized")
        tool_list = client.request("tools/list")
        expected = {"launch_or_attach", "get_ui_tree", "click_element", "set_text",
                    "wait_for_element", "get_element_state"}
        actual = {tool["name"] for tool in tool_list.get("result", {}).get("tools", [])}
        if not expected.issubset(actual):
            raise AssertionError(f"Missing tools: {sorted(expected - actual)}")
        report["assertions"].append({"name": "six_core_tools_registered", "status": "PASS"})
        launch = client.tool("launch_or_attach", {"exePath": str(options.app), "args": options.args,
                                                 "processName": options.process_name or options.app.stem}, 60)
        report["launch"] = launch
        for obj in all_objects(launch):
            if field(obj, "processId") is not None:
                launched_pid = int(field(obj, "processId"))
                launched_owned = field(obj, "startedByServer", False) is True
                break
        tree = client.tool("get_ui_tree", {"maxDepth": 4, "interactiveOnly": True})
        report["tree"] = tree
        (options.output / "ui-tree.json").write_text(json.dumps(tree, ensure_ascii=False, indent=2), encoding="utf-8")
        objects = list(all_objects(tree))
        controls = [obj for obj in objects if field(obj, "controlType") is not None]
        if not controls:
            raise AssertionError("UIA tree contains no controls")
        report["assertions"].append({"name": "nonempty_uia_tree", "status": "PASS", "count": len(controls)})
        windows = [obj for obj in controls if str(field(obj, "controlType")).lower() == "window"]
        windows.extend(obj for obj in all_objects(launch) if field(obj, "title") is not None)
        if options.expected_window_title or options.expected_main_id:
            report["mainWindowVerified"] = any(
                (not options.expected_window_title or options.expected_window_title in
                 str(field(obj, "name", field(obj, "title", "")))) and
                (not options.expected_main_id or field(obj, "automationId") == options.expected_main_id)
                for obj in windows)
        if not report["mainWindowVerified"]:
            report["limits"].append("Main window not positively identified; a startup/error dialog is not accepted as the business main window.")
        identities = [(field(obj, "automationId", ""), field(obj, "name", "")) for obj in controls]
        candidates = [obj for obj in controls if field(obj, "isEnabled", False) and
                      (field(obj, "automationId") or field(obj, "name")) and
                      identities.count((field(obj, "automationId", ""), field(obj, "name", ""))) == 1]
        if not candidates:
            raise AssertionError("No unique enabled control available for a read-only query")
        candidate = next((obj for obj in candidates if field(obj, "automationId")), candidates[0])
        locator = {key: field(candidate, key) for key in ("automationId", "name") if field(candidate, key)}
        report["locator"] = locator
        report["wait"] = client.tool("wait_for_element", {**locator, "timeoutMs": 5000})
        report["state"] = client.tool("get_element_state", locator)
        report["assertions"].append({"name": "real_control_wait_and_state", "status": "PASS"})
        missing = "__wpf_smoke_missing_" + uuid.uuid4().hex
        response = client.request("tools/call", {"name": "wait_for_element",
                                  "arguments": {"automationId": missing, "timeoutMs": 350}}, timeout=15)
        result = response.get("result", {})
        payload_text = json.dumps(result, ensure_ascii=False).lower()
        clear_timeout = any(term in payload_text for term in ("timeout", "timed out", "超时"))
        embedded = []
        for item in result.get("content", []):
            if item.get("type") == "text":
                try:
                    embedded.append(json.loads(item["text"]))
                except json.JSONDecodeError:
                    pass
        error_returned = ("error" in response or result.get("isError") or
                          any(isinstance(obj, dict) and (obj.get("ok") is False or obj.get("success") is False)
                              for obj in embedded))
        if not error_returned or not clear_timeout:
            raise AssertionError("Missing element did not produce an explicit timeout error")
        report["assertions"].append({"name": "missing_element_timeout", "status": "PASS"})
        if not options.skip_screenshot:
            handle = next((field(obj, key) for obj in all_objects(launch)
                           for key in ("windowHandle", "mainWindowHandle", "handle") if field(obj, key)), None)
            if handle:
                try:
                    handle = int(handle, 0) if isinstance(handle, str) else int(handle)
                    # PrintWindow can block on an unresponsive provider; isolate it.
                    import multiprocessing
                    capture = multiprocessing.Process(target=save_window_image,
                                                      args=(handle, options.output / "window.bmp"))
                    capture.start()
                    capture.join(8)
                    if capture.is_alive():
                        capture.terminate()
                        capture.join(2)
                        raise TimeoutError("Window screenshot exceeded 8s")
                    if capture.exitcode != 0:
                        raise RuntimeError(f"Window screenshot exited with code {capture.exitcode}")
                    report["screenshot"] = "window.bmp"
                except Exception as exc:
                    report["limits"].append(f"Screenshot unavailable: {exc}")
            else:
                report["limits"].append("Launch result has no window handle; screenshot unavailable.")
        report["status"] = "PASS" if report["mainWindowVerified"] else "PARTIAL"
    except Exception as exc:
        report["failure"] = f"{type(exc).__name__}: {exc}"
        report["status"] = "FAIL" if isinstance(exc, AssertionError) else "BLOCKED"
    finally:
        if client is not None:
            try:
                after = windows_processes()
                descendants = {client.process.pid}
                for _ in range(len(after)):
                    more = {pid for pid, item in after.items() if item["parent"] in descendants}
                    if more.issubset(descendants):
                        break
                    descendants |= more
                cleanup_targets = []
                for pid, record in after.items():
                    new_identity = pid not in before or before[pid]["created"] != record["created"]
                    owned = (pid in descendants or (launched_owned and pid == launched_pid) or
                             (pid, record["created"]) in client.descendant_identities)
                    if record["path"] == app_path and record["created"] and new_identity and owned:
                        cleanup_targets.append(record)
                        report["cleanup"].append(terminate_verified_process(record))
                client.close()
                report["cleanup"].append({"process": "mcp_server", "status": "exited", "exitCode": client.process.returncode})
                remaining = windows_processes()
                survivors = [record["pid"] for record in cleanup_targets
                             if record["pid"] in remaining and
                             remaining[record["pid"]]["created"] == record["created"]]
                report["cleanupVerified"] = not survivors
                if survivors:
                    report["cleanup"].append({"status": "ERROR", "remainingOwnedProcessIds": survivors})
                    report["status"] = "PARTIAL" if report["status"] == "PASS" else report["status"]
            except Exception as exc:
                report["cleanup"].append({"status": "ERROR", "error": str(exc)})
                if client.process.poll() is None:
                    client.process.kill()
                    client.process.wait(timeout=5)
                client.monitor_stop.set()
                client.monitor.join(timeout=2)
                client.stderr_file.close()
                report["status"] = "PARTIAL" if report["status"] == "PASS" else report["status"]
            (options.output / "protocol.json").write_text(json.dumps(client.events, ensure_ascii=False, indent=2), encoding="utf-8")
            report["timings"] = [{"method": event["request"]["method"],
                                  "tool": event["request"].get("params", {}).get("name"),
                                  "wallElapsedMs": event["wallElapsedMs"]}
                                 for event in client.events if "request" in event]
        (options.output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": report["status"], "report": str(options.output / "report.json"),
                      "failure": report.get("failure"), "timings": report.get("timings", [])}, ensure_ascii=False))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
