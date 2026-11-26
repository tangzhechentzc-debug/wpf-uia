#!/usr/bin/env python3
"""Verify keyboard diagnostics with a disposable WPF receiver through MCP stdio."""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import multiprocessing
import os
from pathlib import Path
import subprocess
import sys
import time

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import smoke_test as smoke


EDITOR = "Fixture.Keyboard.Editor"
SUBMISSION = "Fixture.Keyboard.Submission"


def foreground():
    user = ctypes.WinDLL("user32", use_last_error=True)
    user.GetForegroundWindow.restype = wintypes.HWND
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    handle = user.GetForegroundWindow()
    pid = wintypes.DWORD()
    if not handle or not user.GetWindowThreadProcessId(handle, ctypes.byref(pid)):
        return None
    return {"window": handle, "pid": pid.value}


def wait_for(predicate, message, seconds=5):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if value := predicate():
            return value
        time.sleep(0.025)
    raise AssertionError(message)


def receiver_events(path):
    if not path.is_file():
        return []
    contents = path.read_text(encoding="utf-8")
    # A writer may still be appending the last record.
    return [json.loads(line) for line in contents.splitlines(keepends=True) if line.endswith("\n")]


def remaining_identities(identities, seconds=2):
    deadline = time.monotonic() + seconds
    while True:
        snapshot = smoke.windows_processes()
        remaining = [(pid, created) for pid, created in identities if snapshot.get(pid, {}).get("created") == created]
        if not remaining or time.monotonic() >= deadline:
            return remaining
        time.sleep(0.05)


def tool_payload(client, name, **arguments):
    response = client.request("tools/call", {"name": name, "arguments": arguments}, timeout=20)
    if "error" in response:
        raise AssertionError(response["error"])
    result = response["result"]
    if "structuredContent" in result:
        return result["structuredContent"]
    blocks = [block["text"] for block in result.get("content", []) if block["type"] == "text"]
    return json.loads("\n".join(blocks))


def save_evidence_screenshot(hwnd, output):
    capture = multiprocessing.Process(target=smoke.save_window_image, args=(hwnd, output))
    capture.start()
    capture.join(8)
    if capture.is_alive():
        capture.terminate()
        capture.join(2)
        return "capture timed out"
    return "captured" if capture.exitcode == 0 else f"capture failed ({capture.exitcode})"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--variant", choices=("production", "throw", "timeout", "pipe"), default="production")
    parser.add_argument("--activation-timeout", type=float, default=5,
                        help="Seconds to wait for the test-owned window to become foreground")
    options = parser.parse_args()
    if os.name != "nt":
        parser.error("Windows is required")
    server = options.server.resolve(strict=True)
    fixture = options.fixture.resolve(strict=True)
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture_path = smoke.normalize_path(fixture)
    server_path = smoke.normalize_path(server)
    before = smoke.windows_processes()
    if any(record["path"] == fixture_path for record in before.values()):
        parser.error("Existing Fixture instance uses this executable path")

    receiver = output / "fixture.jsonl"
    blocker = output / "blocker.jsonl"
    report = {"status": "FAIL", "variant": options.variant, "assertions": [], "cases": {}, "cleanup": [],
              "screenshots": {}, "versions": {str(path): hashlib.sha256(path.read_bytes()).hexdigest()
                  for exe in (server, fixture) for path in (exe, exe.with_suffix(".dll"))}}
    client = None
    owned = {}

    def check(name, condition, details=None):
        report["assertions"].append({"name": name, "status": "PASS" if condition else "FAIL", "details": details})
        if not condition:
            raise AssertionError(f"{name}: {details}")

    def ok(name, **arguments):
        response = tool_payload(client, name, **arguments)
        if response["ok"] is not True:
            raise AssertionError(response)
        return response["result"]

    def read_state():
        return {key: ok("get_element_state", automationId=locator)["element"]["Value"]
                for key, locator in (("editor", EDITOR), ("submission", SUBMISSION))}

    def case(name, **data):
        report["cases"][name] = data

    def save_screenshot(name, hwnd):
        report["screenshots"][name] = save_evidence_screenshot(hwnd, output / (name + ".bmp"))

    def capture_owned():
        snapshot = smoke.windows_processes()
        descendants = {client.process.pid}
        while True:
            more = {pid for pid, record in snapshot.items() if record["parent"] in descendants}
            if more.issubset(descendants):
                break
            descendants |= more
        for pid, record in snapshot.items():
            identity = (pid, record["created"])
            if (record["created"] and record["path"] in (fixture_path, server_path)
                    and pid != client.process.pid and identity != (pid, before.get(pid, {}).get("created"))
                    and (pid in descendants or identity in client.descendant_identities)):
                owned[identity] = record
        return snapshot

    try:
        client = smoke.StdioClient(server, output)
        initialized = client.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                     "clientInfo": {"name": "keyboard-diagnostics-test", "version": "1.0"}})
        check("MCP initialized", "result" in initialized)
        client.notify("notifications/initialized")
        launch = ok("launch_or_attach", exePath=str(fixture), args=subprocess.list2cmdline(["--keyboard-evidence", str(receiver)]))
        pid, hwnd = launch["processId"], launch["window"]["handle"]
        report["launch"] = launch
        snapshot = capture_owned()
        worker = snapshot[snapshot[pid]["parent"]]
        check("fresh Fixture and MCP worker identified by PID, executable path and creation time",
              launch["startedByServer"] and launch["window"]["automationId"] == "Fixture.Keyboard.Main"
              and snapshot[pid]["path"] == fixture_path and worker["path"] == server_path
              and (pid, snapshot[pid]["created"]) in owned and (worker["pid"], worker["created"]) in owned)
        wait_for(lambda: any(event["eventName"] == "ready" for event in receiver_events(receiver)), "Fixture not ready")
        expected_foreground = {"window": hwnd, "pid": pid}
        report["foregroundBefore"] = foreground()
        if report["foregroundBefore"] != expected_foreground:
            user = ctypes.WinDLL("user32", use_last_error=True)
            user.SetForegroundWindow.argtypes = [wintypes.HWND]
            user.SetForegroundWindow.restype = wintypes.BOOL
            report["fixtureActivationRequested"] = bool(user.SetForegroundWindow(hwnd))
        try:
            wait_for(lambda: foreground() == expected_foreground, "Fixture is not foreground", options.activation_timeout)
        finally:
            report["foregroundAfter"] = foreground()

        if options.variant == "production":
            baseline = read_state()
            check("A starts with a different control focused", "wrong_focus" in [event["eventName"] for event in receiver_events(receiver)])
            a = tool_payload(client, "set_text", automationId=EDITOR, text="fixture-commit-a", pressEnter=True)
            case("A", response=a, before=baseline)
            check("A operation succeeds", a["ok"] is True and a["result"]["enterSent"] is True, a)
            keyboard = a["result"]["keyboard"]
            check("A text written and Enter API completed", keyboard["textWriteStatus"] == "written"
                  and keyboard["sendStatus"] == "sent" and keyboard["diagnosticsAvailable"] is True
                  and keyboard["keys"] == ["Enter"] and keyboard["operation"] == "set_text")
            checks = keyboard["checks"]
            check("A foreground, PID and target control checks passed",
                  all(checks[field]["status"] == "passed" for field in ("foregroundWindow", "foregroundPid", "uiaFocus", "focusPid", "targetControl"))
                  and checks["documentScope"]["status"] == "not_applicable"
                  and checks["foregroundWindow"]["actual"] == hwnd and checks["foregroundPid"]["actual"] == pid
                  and checks["focusAutomationId"]["actual"] == EDITOR and checks["focusControlType"]["actual"] == "Edit", checks)
            wait_for(lambda: sum(event["eventName"] == "submitted" for event in receiver_events(receiver)) == 1,
                     "A Fixture never received and submitted Enter")
            events_a = receiver_events(receiver)
            names_a = [event["eventName"] for event in events_a]
            a_state = read_state()
            report["cases"]["A"].update(after=a_state, receiverEvents=events_a)
            check("A Fixture observed target focus, one Enter and actual submission",
                  names_a.count("enter_received") == 1 and names_a.index("target_focus") < names_a.index("value_written") < names_a.index("enter_received")
                  and a_state == {"editor": "fixture-commit-a", "submission": "fixture-commit-a"}, a_state)
            save_screenshot("fixture-after-success", hwnd)

            ok("click_element", automationId="Fixture.Keyboard.Arm")
            armed = wait_for(lambda: next((event for event in receiver_events(receiver) if event["eventName"] == "armed"), None),
                             "Fixture did not arm the deterministic focus transfer")
            check("C independent arming evidence shows wrong editor focused", armed["detail"]["wrongHasKeyboardFocus"])
            start_c = len(receiver_events(receiver))
            cd = tool_payload(client, "set_text", automationId=EDITOR, text="fixture-write-no-enter-cd", pressEnter=True)
            cd_state = read_state()
            cd_events = receiver_events(receiver)[start_c:]
            case("C", response=cd, receiverEvents=cd_events)
            case("D", state=cd_state, submissionBefore=a_state["submission"])
            keyboard = cd["keyboard"]
            checks = keyboard["checks"]
            check("C final focus evidence identifies target mismatch in the same window", cd["ok"] is False
                  and keyboard["checkPhase"] == "final_check" and keyboard["blockingCheck"] == "targetControl"
                  and checks["foregroundWindow"]["status"] == "passed" and checks["focusPid"]["status"] == "passed"
                  and checks["focusAutomationId"]["actual"] == "Fixture.Keyboard.Wrong"
                  and checks["targetControl"]["status"] == "failed" and checks["targetControl"]["reason"] == "target_control_mismatch", cd)
            check("C Fixture proved active target focus before synchronous transfer and independently observed no Enter",
                  [event["eventName"] for event in cd_events] == ["target_focus", "value_written", "wrong_focus", "focus_transferred"]
                  and cd_events[1]["detail"]["targetHasKeyboardFocus"] and cd_events[3]["detail"]["wrongHasKeyboardFocus"], cd_events)
            check("D write success is independent of blocked Enter", keyboard["textWriteStatus"] == "written"
                  and keyboard["sendStatus"] == "not_sent" and cd_state["editor"] == "fixture-write-no-enter-cd", cd_state)
            check("D submission stayed at the prior value", cd_state["submission"] == a_state["submission"], cd_state)
            save_screenshot("fixture-after-wrong-focus", hwnd)

            ok("click_element", automationId="Fixture.Keyboard.OpenBlocker")
            blocked_by = wait_for(lambda: next((event["detail"] for event in receiver_events(blocker) if event["eventName"] == "ready"), None),
                                  "Separate Fixture window missing")
            blocker_foreground = {key: blocked_by[key] for key in ("window", "pid")}
            blocked_snapshot = capture_owned()
            block_process = blocked_snapshot[blocked_by["pid"]]
            check("B blocker is a distinct, test-owned Fixture child", block_process["parent"] == pid
                  and blocked_by["pid"] != pid and blocked_by["window"] != hwnd
                  and (blocked_by["pid"], block_process["created"]) in owned, blocked_by)
            try:
                wait_for(lambda: foreground() == blocker_foreground, "Blocker failed to take foreground", options.activation_timeout)
            finally:
                report["foregroundForB"] = foreground()
            start_b = len(receiver_events(receiver))
            b = tool_payload(client, "set_text", automationId=EDITOR, text="fixture-must-not-write-b", pressEnter=True)
            b_state = read_state()
            b_events = receiver_events(receiver)[start_b:]
            blocker_events = receiver_events(blocker)
            case("B", response=b, after=b_state, receiverEvents=b_events, blockerEvents=blocker_events)
            keyboard = b["keyboard"]
            checks = keyboard["checks"]
            check("B diagnostic identifies different foreground HWND and PID", b["ok"] is False
                  and checks["foregroundWindow"]["reason"] == "foreground_window_mismatch"
                  and checks["foregroundWindow"]["actual"] == blocked_by["window"]
                  and checks["foregroundPid"]["reason"] == "foreground_pid_mismatch"
                  and checks["foregroundPid"]["actual"] == blocked_by["pid"], b)
            check("B sender and both receivers show no write, Enter or submit", keyboard["textWriteStatus"] == "not_started"
                  and keyboard["sendStatus"] == "not_sent" and b_state == cd_state
                  and not any(event["eventName"] in ("enter_received", "value_written", "submitted") for event in b_events + blocker_events),
                  {"target": b_events, "blocker": blocker_events})
            save_screenshot("foreground-blocker", blocked_by["window"])
        else:
            token = "fixture-dispatched-" + options.variant
            started = time.monotonic()
            response = tool_payload(client, "set_text", automationId=EDITOR, text=token, pressEnter=True)
            elapsed = time.monotonic() - started
            case(options.variant, response=response, seconds=round(elapsed, 3), oldWorker=worker)
            keyboard = response["keyboard"]
            check("Fault response reports unknown send status", response["ok"] is False and keyboard["sendStatus"] == "unknown", response)
            if options.variant in ("timeout", "pipe"):
                reason = "worker_timeout" if options.variant == "timeout" else "worker_pipe_failure"
                check("Lost worker response identifies diagnostics missing", keyboard["diagnosticsAvailable"] is False
                      and keyboard["failureReason"] == keyboard["diagnosticsMissingReason"] == reason
                      and keyboard["textWriteStatus"] == "unknown" and not keyboard.get("checks"), keyboard)
                check("Old worker exited", not remaining_identities([(worker["pid"], worker["created"])]))
                if options.variant == "timeout":
                    check("Outer worker limit reached", 9.8 <= elapsed < 15.0, elapsed)
                reattach = ok("launch_or_attach", exePath=str(fixture))
                check("Read-only recovery reattaches to the same living Fixture", reattach["processId"] == pid and not reattach["startedByServer"], reattach)
                capture_owned()
            else:
                check("Send exception keeps completed write and focus diagnostics", keyboard["diagnosticsAvailable"] is True
                      and keyboard["textWriteStatus"] == "written" and keyboard["failureReason"] == "send_exception", keyboard)
            after = read_state()
            events = receiver_events(receiver)
            report["cases"][options.variant].update(after=after, receiverEvents=events)
            check("Independent receiver saw exactly one Enter and one actual submission without replay",
                  [event["eventName"] for event in events].count("enter_received") == 1
                  and [event["eventName"] for event in events].count("submitted") == 1
                  and after == {"editor": token, "submission": token}, after)
            save_screenshot("fixture-after-" + options.variant, hwnd)
        report["status"] = "PASS"
    except Exception as error:
        report["failure"] = f"{type(error).__name__}: {error}"
    finally:
        if client:
            try:
                snapshot = capture_owned()
                for (pid, created), record in owned.items():
                    if record["path"] == fixture_path and snapshot.get(pid, {}).get("created") == created:
                        report["cleanup"].append(smoke.terminate_verified_process(record))
                client.close()
                report["cleanup"].append({"process": "mcp_server", "exitCode": client.process.returncode})
                for identity in remaining_identities(owned):
                    try:
                        report["cleanup"].append(smoke.terminate_verified_process(owned[identity]))
                    except PermissionError:
                        if remaining_identities([identity]):
                            raise
                        report["cleanup"].append({"pid": identity[0], "status": "exited_during_cleanup"})
                report["cleanupVerified"] = not remaining_identities(owned)
                if not report["cleanupVerified"]:
                    report["status"] = "FAIL"
            except Exception as error:
                report["cleanup"].append({"error": str(error)})
                report["status"] = "FAIL"
                if client.process.poll() is None:
                    client.close()
            (output / "protocol.json").write_text(json.dumps(client.events, ensure_ascii=False, indent=2), encoding="utf-8")
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": report["status"], "failure": report.get("failure"), "report": str(output / "report.json")}, ensure_ascii=False))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
