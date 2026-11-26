#!/usr/bin/env python3
"""Exercise UIA MCP tools against a disposable, real WPF fixture."""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import importlib.util
import json
import os
from pathlib import Path
import sys
from threading import Timer
import time

sys.dont_write_bytecode = True
MODULE = Path(__file__).resolve().parents[1] / "scripts" / "smoke_test.py"
SPEC = importlib.util.spec_from_file_location("wpf_smoke", MODULE)
smoke = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(smoke)


def payload(response):
    if "error" in response:
        return {"ok": False, "error": json.dumps(response["error"], ensure_ascii=False)}
    result = response["result"]
    if "structuredContent" in result:
        return result["structuredContent"]
    for block in result.get("content", []):
        if block.get("type") == "text":
            try:
                return json.loads(block["text"])
            except json.JSONDecodeError:
                return {"ok": not result.get("isError", False), "error": block["text"]}
    raise AssertionError("No JSON tool response")


def unwrap(value):
    return value.get("result", value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    options = parser.parse_args()
    if os.name != "nt":
        parser.error("Windows is required")
    server = options.server.resolve(strict=True)
    fixture = options.fixture.resolve(strict=True)
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if (output / "integration-report.json").exists():
        parser.error("Choose a new evidence directory; existing evidence is preserved")
    before = smoke.windows_processes()
    fixture_path = smoke.normalize_path(fixture)
    if any(record["path"] == fixture_path for record in before.values()):
        parser.error("An existing fixture process is open; this scenario requires a fresh disposable instance")
    report = {"status": "FAIL", "assertions": [], "cleanup": [], "fixture": str(fixture), "server": str(server)}
    client = None
    owned_pid = None

    def check(name, condition, details=None):
        report["assertions"].append({"name": name, "status": "PASS" if condition else "FAIL", "details": details})
        if not condition:
            raise AssertionError(name + (": " + str(details) if details else ""))

    def call(tool_name, **arguments):
        return unwrap(client.tool(tool_name, arguments))

    def state(automation_id):
        return call("get_element_state", automationId=automation_id)

    def wait_for_modal_transition(automation_id, **locator):
        deadline = time.monotonic() + 2
        while True:
            try:
                return call("wait_for_element", automationId=automation_id, timeoutMs=2000, **locator)
            except RuntimeError as error:
                if "No enabled visible window" not in str(error) or time.monotonic() >= deadline:
                    raise
                time.sleep(0.05)

    def value(automation_id):
        return state(automation_id)["element"]["Value"]

    def wait_value(automation_id, expected, timeout=3):
        deadline = time.monotonic() + timeout
        observed = None
        while time.monotonic() < deadline:
            observed = value(automation_id)
            if observed == expected:
                return observed
            time.sleep(0.05)
        raise AssertionError(f"{automation_id}: expected {expected!r}, observed {observed!r}")

    def rejected(tool_name, contains, **arguments):
        response = client.request("tools/call", {"name": tool_name, "arguments": arguments}, timeout=20)
        data = payload(response)
        error = str(data.get("error", ""))
        check(tool_name + " rejects " + contains, data.get("ok") is False and contains.casefold() in error.casefold(), error)
        return data

    try:
        client = smoke.StdioClient(server, output)
        initialized = client.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                                    "clientInfo": {"name": "wpf-uia-integration", "version": "1.0"}})
        check("MCP initialize", "result" in initialized)
        client.notify("notifications/initialized")
        listed = client.request("tools/list")
        names = {entry["name"] for entry in listed["result"]["tools"]}
        check("six core tools", {"launch_or_attach", "get_ui_tree", "click_element", "set_text",
                                  "wait_for_element", "get_element_state"}.issubset(names))
        check("window wait tool exposed", "wait_for_window" in names)
        rejected("wait_for_window", "No live attachment", title="WPF UIA integration fixture")
        launch = call("launch_or_attach", exePath=str(fixture), processName=fixture.stem)
        report["launch"] = launch
        owned_pid = launch["processId"]
        check("fresh fixture launch", launch["startedByServer"] is True)
        check("correct fixture main window", launch["window"]["automationId"] == "Fixture.Main")
        window = call("wait_for_window", title="WPF UIA integration fixture", timeoutMs=1000)
        check("native window wait returns attached PID and exact HWND without a tree",
              window["processId"] == owned_pid and window["window"] ==
              {"handle": launch["window"]["handle"], "title": "WPF UIA integration fixture"}
              and window["readiness"] == "native-visible-enabled" and "nodes" not in window)
        rejected("wait_for_window", "exact window title", title=" ")
        rejected("wait_for_window", "1-30000", title="WPF UIA integration fixture", timeoutMs=0)
        missing = rejected("wait_for_window", "TimeoutException", title="WPF UIA integration", timeoutMs=200)
        check("partial title does not match and timeout is bounded", 190 <= missing["elapsedMs"] < 1500)
        attached = call("launch_or_attach", exePath=str(fixture), processName=fixture.stem)
        check("attach reuses exact PID", attached["processId"] == owned_pid)
        attached_with_extension = call("launch_or_attach", exePath=str(fixture), processName=fixture.name)
        check("attach accepts dotted process name with exe suffix", attached_with_extension["processId"] == owned_pid)
        instances = [record for record in smoke.windows_processes().values() if record["path"] == fixture_path]
        check("attach did not duplicate process", len(instances) == 1)

        tree = call("get_ui_tree", maxDepth=4, interactiveOnly=True)
        report["initialTree"] = tree
        check("stable tree needs one attempt", tree["stability"] ==
              {"attempts": 1, "noEnabledWindow": 0, "elementUnavailable": 0, "rootTimeouts": 0})
        check("main tree contains actionable controls", any(node["AutomationId"] == "Fixture.Invoke" for node in tree["nodes"]))
        check("default tree preserves named custom Invoke control without ID",
              any(node["Name"] == "Custom action" and node["ControlType"] == "Custom" and not node["AutomationId"]
                  for node in tree["nodes"]))
        check("password never leaked in tree", "fixture-secret-never-return" not in json.dumps(tree))
        check("tree keeps exact ValuePattern text",
              any(node["AutomationId"] == "Fixture.Editor" and node["Value"] == "initial" for node in tree["nodes"]))
        check("tree keeps exact RangeValuePattern value",
              any(node["AutomationId"] == "Fixture.Range" and node["Value"] == "42" for node in tree["nodes"]))
        waited = call("wait_for_element", automationId="Fixture.Invoke", timeoutMs=1000)
        check("wait returns enabled actual element", waited["IsEnabled"] is True)

        check("counter starts at zero", value("Fixture.Count") == "0")
        action = call("click_element", automationId="Fixture.Invoke", controlType="Button")
        check("button uses InvokePattern", action["pattern"] == "Invoke")
        wait_value("Fixture.Count", "1")
        check("Invoke changes actual UI", True)
        custom_action = call("click_element", name="Custom action", controlType="Custom")
        wait_value("Fixture.Count", "2")
        check("custom peer supports Name and ControlType Invoke", custom_action["pattern"] == "Invoke")

        check("toggle starts Off", state("Fixture.Toggle")["toggleState"] == "Off")
        action = call("click_element", automationId="Fixture.Toggle")
        check("checkbox uses TogglePattern", action["pattern"] == "Toggle")
        check("toggle becomes On", state("Fixture.Toggle")["toggleState"] == "On")
        call("click_element", automationId="Fixture.Toggle")
        check("toggle reverses to Off", state("Fixture.Toggle")["toggleState"] == "Off")

        grid = state("Fixture.Grid")["grid"]
        check("grid returns actual row and column totals", grid["rows"] == 12 and grid["columns"] == 2, grid)
        check("grid caps cells and marks truncation", len(grid["cells"]) <= 20 and grid["truncated"] is True)
        check("grid returns readable cell values", any(cell.get("state", {}).get("Value") == "R01" for cell in grid["cells"]), grid)

        action = call("click_element", automationId="Fixture.Tab.Other")
        check("tab uses SelectionItemPattern", action["pattern"] == "SelectionItem")
        check("tab selection changes", state("Fixture.Tab.Other")["isSelected"] is True and state("Fixture.Tab.Data")["isSelected"] is False)
        call("click_element", automationId="Fixture.Tab.Data")

        call("set_text", automationId="Fixture.Editor", text="UIA value 写入", pressEnter=False)
        wait_value("Fixture.Editor", "UIA value 写入")
        check("ValuePattern writes exact Unicode text", True)
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.SetForegroundWindow.argtypes = [wintypes.HWND]
        user32.SetForegroundWindow.restype = wintypes.BOOL
        check("fixture can be focused for optional Enter", bool(user32.SetForegroundWindow(launch["window"]["handle"])))
        entered = call("set_text", automationId="Fixture.Editor", text="commit", pressEnter=True)
        wait_value("Fixture.EnterState", "entered:commit")
        check("optional Enter reaches exact editor", entered["enterSent"] is True)

        rejected("set_text", "writable ValuePattern", automationId="Fixture.ReadOnly", text="must-not-change")
        check("readonly value unchanged", value("Fixture.ReadOnly") == "read-only")
        rejected("click_element", "Ambiguous", name="Duplicate", controlType="Button")
        rejected("click_element", "disabled", automationId="Fixture.Disabled")
        rejected("click_element", "No actionable UIA pattern", automationId="Fixture.Decoration")
        rejected("click_element", "Supply AutomationId", controlType="Button")
        rejected("get_element_state", "Invalid ControlType", automationId="Fixture.Invoke", controlType="invalid")
        rejected("wait_for_element", "Timeout", automationId="Fixture.Missing", timeoutMs=250)
        rejected("wait_for_element", "Timeout", automationId="Fixture.Disabled", timeoutMs=250)
        check("password state is redacted", state("Fixture.Password")["element"]["Value"] == "[redacted]")

        call("click_element", automationId="Fixture.OpenModal")
        wait_for_modal_transition("Fixture.Dialog.Close")
        modal = call("get_ui_tree", maxDepth=4, interactiveOnly=True)
        check("active tree follows modal", modal["window"]["automationId"] == "Fixture.Dialog")
        rejected("get_element_state", "not found", automationId="Fixture.Invoke")
        call("click_element", automationId="Fixture.Dialog.Close")
        wait_for_modal_transition("Fixture.Invoke")
        check("active scope returns to main after modal close", call("get_ui_tree")["window"]["automationId"] == "Fixture.Main")

        call("click_element", automationId="Fixture.OpenNativeModal")
        native_wait = call("wait_for_window", title="UIA native modal fixture", timeoutMs=2000)
        rejected("wait_for_window", "TimeoutException", title="WPF UIA integration fixture", timeoutMs=100)
        wait_for_modal_transition("2", controlType="Button")
        native_modal = call("get_ui_tree", maxDepth=5, interactiveOnly=False)
        check("window wait handles native modal with no AutomationId", native_wait["window"]["handle"] == native_modal["window"]["handle"])
        check("native dialog root can omit AutomationId",
              native_modal["window"]["title"] == "UIA native modal fixture"
              and not native_modal["window"]["automationId"])
        check("native dialog tree retains prompt text and its OK button",
              any(node["Name"] == "Native dialog body" for node in native_modal["nodes"])
              and any(node["AutomationId"] == "2" and node["ControlType"] == "Button"
                      for node in native_modal["nodes"]))
        check("native dialog can be focused", bool(user32.SetForegroundWindow(native_modal["window"]["handle"])))
        call("click_element", automationId="2", controlType="Button")
        call("wait_for_window", title="WPF UIA integration fixture", timeoutMs=2000)
        wait_for_modal_transition("Fixture.Invoke")

        main_hwnd = launch["window"]["handle"]
        user32.EnableWindow.argtypes = (wintypes.HWND, wintypes.BOOL)
        user32.IsWindowEnabled.argtypes = (wintypes.HWND,)
        user32.EnableWindow(main_hwnd, False)
        restore_wait_window = Timer(0.3, user32.EnableWindow, args=(main_hwnd, True))
        try:
            restore_wait_window.start()
            waited_window = call("wait_for_window", title="WPF UIA integration fixture", timeoutMs=1500)
            check("native wait polls disabled window until enabled",
                  waited_window["window"]["handle"] == main_hwnd and waited_window["polls"] > 1)
        finally:
            restore_wait_window.join()
            user32.EnableWindow(main_hwnd, True)
        user32.EnableWindow(main_hwnd, False)
        restore_window = Timer(0.45, user32.EnableWindow, args=(main_hwnd, True))
        try:
            check("controlled transition starts with a disabled fixture window", not user32.IsWindowEnabled(main_hwnd))
            restore_window.start()
            recovered = call("get_ui_tree")
            check("one tree request recovers when the window is re-enabled",
                  recovered["window"]["handle"] == main_hwnd and recovered["stability"]["noEnabledWindow"] > 0
                  and recovered["stability"]["attempts"] == recovered["stability"]["noEnabledWindow"] + 1,
                  recovered["stability"])
        finally:
            if restore_window.ident is not None:
                restore_window.join()
            user32.EnableWindow(main_hwnd, True)

        user32.EnableWindow(main_hwnd, False)
        try:
            exhausted = rejected("get_ui_tree", "TreeTransitionException")
            check("exhausted tree reports attempts without a stale snapshot",
                  "result" not in exhausted and exhausted["stability"]["attempts"] ==
                  exhausted["stability"]["noEnabledWindow"] > 1, exhausted["stability"])
        finally:
            user32.EnableWindow(main_hwnd, True)
        check("new request succeeds after exhaustion", call("get_ui_tree")["window"]["handle"] == main_hwnd)

        rejected("get_ui_tree", "1-12", maxDepth=0)
        rejected("get_ui_tree", "1-12", maxDepth=13)
        shallow = call("get_ui_tree", maxDepth=1)
        check("depth-limited tree marks truncation", shallow["truncated"] is True)
        call("click_element", automationId="Fixture.Tab.Bulk")
        bulk = call("get_ui_tree", maxDepth=12, interactiveOnly=True)
        report["bulkTree"] = bulk
        compact_chars = len(json.dumps(bulk, ensure_ascii=False, separators=(",", ":")))
        check("tree obeys node and response bounds", len(bulk["nodes"]) <= 120 and compact_chars <= 17000,
              {"nodes": len(bulk["nodes"]), "characters": compact_chars})
        check("dense UI tree reports truncation", bulk["truncated"] is True)
        check("tree clips long accessible names", all(len(node["Name"] or "") <= 161 for node in bulk["nodes"]))
        report["status"] = "PASS"
    except Exception as exc:
        report["failure"] = f"{type(exc).__name__}: {exc}"
    finally:
        if client:
            try:
                after = smoke.windows_processes()
                descendants = {client.process.pid}
                for _ in range(len(after)):
                    child_ids = {pid for pid, record in after.items() if record["parent"] in descendants}
                    if child_ids.issubset(descendants):
                        break
                    descendants |= child_ids
                for pid, record in after.items():
                    is_new = pid not in before or before[pid]["created"] != record["created"]
                    if record["path"] == fixture_path and is_new and record["created"] and (pid == owned_pid or pid in descendants):
                        report["cleanup"].append(smoke.terminate_verified_process(record))
                client.close()
                report["cleanup"].append({"process": "mcp_server", "exitCode": client.process.returncode})
                remaining = smoke.windows_processes()
                report["cleanupVerified"] = not any(item["path"] == fixture_path for item in remaining.values())
                if not report["cleanupVerified"]:
                    report["status"] = "FAIL"
            except Exception as exc:
                report["cleanup"].append({"error": str(exc)})
                report["status"] = "FAIL"
                if client.process.poll() is None:
                    client.process.kill()
                    client.process.wait(timeout=5)
                client.stderr_file.close()
            (output / "integration-protocol.json").write_text(json.dumps(client.events, ensure_ascii=False, indent=2), encoding="utf-8")
            report["timings"] = [{"method": event["request"]["method"],
                                  "tool": event["request"].get("params", {}).get("name"),
                                  "wallElapsedMs": event["wallElapsedMs"]}
                                 for event in client.events if "request" in event]
        (output / "integration-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": report["status"], "passed": sum(item["status"] == "PASS" for item in report["assertions"]),
                      "failure": report.get("failure"), "report": str(output / "integration-report.json")}, ensure_ascii=False))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
