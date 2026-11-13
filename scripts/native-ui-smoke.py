# /// script
# requires-python = ">=3.11"
# dependencies = ["pywinauto==0.6.9", "Pillow==13.0.0"]
# ///
"""Настоящее GPUI-окно и изолированный профиль.

На пользовательском рабочем столе отправляются только сообщения своему HWND.
Реальное наведение разрешено только на проверенной GitHub-hosted CI VM.
Подключённая Windows Sandbox заблокирована из-за синхронизации курсора RDP.

uv run --cache-dir artifacts/uv-cache scripts/native-ui-smoke.py
  --exe artifacts/rust-target/debug/netlights.exe --output artifacts/native-ui-new
Дополнительно: --measure-seconds 3600 --enable-country-for-measurement.
Вывод допускается только в новых подпапках artifacts или TestResults.
DPI и параметры Windows пользователя не меняются. Только в проверенной
изолированной VM тест временно включает анимации и восстанавливает их в finally.
"""
import argparse
import csv
import ctypes as ct
from ctypes import wintypes as wt
import datetime as dt
import hashlib
import ipaddress
import json
import math
import os
import pathlib
import queue
import re
import sysconfig
import subprocess
import stat
import sys
import threading
import time

PERIODS = [(1, "1 ч"), (3, "3 ч"), (6, "6 ч"), (12, "12 ч"), (24, "24 ч"),
           (72, "3 д"), (120, "5 д"), (168, "7 д")]
PAGES = ["Состояние", "Локации", "Диагностика", "Параметры", "О программе"]
DEBUG = "Debug: источники определения"
WM_CLOSE, WM_MOUSEMOVE, WM_MOUSEWHEEL = 0x0010, 0x0200, 0x020A
NO_CONSOLE = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def rectangle_inside(rect, bounds):
    a,b,c,d=bounds
    return a<=rect[0]<rect[2]<=c and b<=rect[1]<rect[3]<=d


def navigation_frame_evidence(initial, finish, frames, scale, coverage_instrumented=False):
    assert frames and all(math.isfinite(t) and math.isfinite(x) and t>=0 for t,x in frames), "Invalid navigation samples"
    assert all(a[0]<=b[0] for a,b in zip(frames,frames[1:])), "Navigation timestamps reversed"
    assert abs(frames[-1][1]-finish)<=3*scale, "Indicator didn't reach the selected tab"
    lower,upper=sorted((initial,finish))
    intermediate={round(x) for _,x in frames if lower+3*scale<x<upper-3*scale}
    moved=next((elapsed for elapsed,x in frames if abs(x-initial)>3*scale),None)
    assert moved is not None, "The navigation indicator never moved"
    if not coverage_instrumented:
        assert len(intermediate)>=3, "Insufficient intermediate positions to confirm smooth navigation"
        assert moved<.5, "Navigation response exceeded 500 ms"
    return dict(first_visible_change_ms=round(moved*1000,1),
                distinct_intermediate_positions=len(intermediate),frames=len(frames),
                smoothness="NOT RUN" if coverage_instrumented else "PASS")


def fitted_outer(original, work, width, height):
    x,y,w,h=work
    width,height=min(int(width),w),min(int(height),h)
    if width<=0 or height<=0:
        raise ValueError("Invalid fitted window dimensions")
    return [min(max(original[0],x),x+w-width), min(max(original[1],y),y+h-height),width,height]


def distinct_custom_size(expected, work, scale):
    custom=[max(round(640*scale),expected[0]-70),max(round(480*scale),expected[1]-70)]
    if custom==expected or custom[0]>work[2] or custom[1]>work[3]:
        raise ValueError("Desktop cannot exercise a distinct valid saved size")
    return custom


def visible_endpoint_ids(controls, bounds):
    result=set()
    for item in controls:
        if item["kind"]=="Button" and item["name"]=="Проверить сейчас" and item["visible"] and rectangle_inside(item["rect"],bounds):
            identity=tuple(item["runtime_id"] or ())
            if not identity:
                raise RuntimeError("Endpoint button has no stable UIA identity")
            result.add(identity)
    return result


def stable_endpoint_inventory(controls, toolbar_id, expected=None):
    identities=set()
    targets=toolbar_count=0
    for item in controls:
        if item["kind"]=="Button" and item["name"]=="Проверить сейчас":
            identity=tuple(item["runtime_id"] or ())
            if not identity:
                raise RuntimeError("Endpoint inventory has no stable UIA identity")
            if identity!=toolbar_id:
                targets+=1
                identities.add(identity)
            else:
                toolbar_count+=1
    if toolbar_count!=1 or targets!=7 or len(identities)!=targets or (expected is not None and identities!=expected):
        raise RuntimeError("The seven endpoint identities changed or were not present in the expanded provider")
    return identities


def token_username():
    """Win32 token identity, not an environment variable supplied by a caller."""
    function = ct.WinDLL("advapi32", use_last_error=True).GetUserNameW
    function.argtypes, function.restype = [wt.LPWSTR, ct.POINTER(wt.DWORD)], wt.BOOL
    buffer, size = ct.create_unicode_buffer(32768), wt.DWORD(32768)
    if not function(buffer, ct.byref(size)):
        raise ct.WinError(ct.get_last_error())
    return buffer.value


class IsolatedInput:
    """Pointer injection is allowed only on the disposable hosted CI desktop."""
    def __init__(self, exe, sandbox_file=None, nonce=None, ci=False):
        if bool(sandbox_file) == bool(ci):
            raise ValueError("Выберите ровно один изолированный режим ввода")
        self.ci = ci
        self.assert_identity()
        if ci:
            if nonce:
                raise ValueError("CI-ввод не принимает Sandbox nonce")
            self.evidence = dict(scope="GitHub-hosted Windows VM", guard="actual-token-and-hosted-runner-environment")
            return
        if not nonce or not re.fullmatch(r"[0-9a-fA-F]{32}", nonce):
            raise ValueError("Sandbox nonce должен содержать 32 hex-символа")
        marker = pathlib.Path(sandbox_file)
        if not marker.is_absolute() or marker.stat().st_size > 4096:
            raise ValueError("Sandbox input marker должен быть абсолютным JSON размером до 4096 байт")
        for path in (marker, *marker.parents):
            if path.lstat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError("Sandbox input marker не должен проходить через reparse point")
        raw = marker.read_bytes()
        proof = json.loads(raw.decode("utf-8-sig"))
        runner_hash = hashlib.sha256(pathlib.Path(__file__).resolve().read_bytes()).hexdigest()
        app_hash = Runner.sha(exe)
        if not isinstance(proof, dict) or proof.get("nonce") != nonce or proof.get("purpose") != "netlights-native-ui" or proof.get("mode") != "windows-sandbox":
            raise ValueError("Sandbox input marker не подтверждает этот запуск")
        if not all(isinstance(proof.get(key), str) for key in ("app_sha256", "runner_sha256")) or proof["app_sha256"].lower() != app_hash or proof["runner_sha256"].lower() != runner_hash:
            raise ValueError("Sandbox input marker не соответствует app/runner SHA-256")
        if str(exe).lower() != r"c:\nltools\publish\netlights.exe":
            raise ValueError("Sandbox ввод разрешён только для C:\\NLTools\\publish\\NetLights.exe")
        self.marker_path, self.nonce = marker, nonce
        self.evidence = dict(scope="Windows Sandbox", guard="actual-token-and-nonce-and-exact-app-runner-hashes",
                             nonce=nonce, marker_sha256=hashlib.sha256(raw).hexdigest(),
                             app_sha256=app_hash, runner_sha256=runner_hash)

    def assert_identity(self):
        username = token_username().casefold()
        if self.ci:
            if username != "runneradmin" or os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted" or os.environ.get("CI") != "true":
                raise ValueError("Реальный ввод разрешён только в GitHub-hosted Windows VM под runneradmin")
        elif username != "wdagutilityaccount":
            raise ValueError("Sandbox ввод разрешён только реальному Windows token WDAGUtilityAccount")

    def assert_pointer_input_allowed(self):
        self.assert_identity()
        if not self.ci:
            raise RuntimeError("BLOCKED: Windows Sandbox RDP can move the host cursor; pointer injection is disabled")

    def move(self, api, hwnd, pid, target):
        self.assert_pointer_input_allowed()
        if api.owner(hwnd) != pid or not api.user.IsWindowVisible(hwnd):
            raise RuntimeError("Изолированный ввод требует видимый HWND созданного процесса")
        a, b, c, d = api.client_rect(hwnd)
        if not (a <= target[0] < c and b <= target[1] < d):
            raise RuntimeError("Изолированный ввод вышел за клиентскую область собственного HWND")
        top = api.user.WindowFromPoint(wt.POINT(*target))
        if not top or api.owner(top) != pid:
            raise RuntimeError("Целевую область собственного HWND перекрывает другое окно")
        if not api.user.SetCursorPos(*target):
            raise ct.WinError(ct.get_last_error())
        top = api.user.WindowFromPoint(wt.POINT(*target))
        if not top or api.owner(top) != pid:
            raise RuntimeError("Целевую область собственного HWND перекрывает другое окно")

    def send_mouse_path(self, api, hwnd, pid, target):
        self.assert_pointer_input_allowed()
        if api.owner(hwnd) != pid or not api.user.IsWindowVisible(hwnd):
            raise RuntimeError("SendInput требует видимый HWND созданного процесса")
        a, b, c, d = api.client_rect(hwnd)
        points = [(target[0]+1, target[1]), target]
        for point in points:
            if not (a <= point[0] < c and b <= point[1] < d):
                raise RuntimeError("SendInput вышел за клиентскую область собственного HWND")
            top = api.user.WindowFromPoint(wt.POINT(*point))
            if not top or api.owner(top) != pid:
                raise RuntimeError("Целевую область SendInput перекрывает другое окно")
        activated = bool(api.user.SetForegroundWindow(hwnd))
        left, top, width, height = (api.user.GetSystemMetrics(index) for index in (76, 77, 78, 79))
        if not (0 < width <= 8192 and 0 < height <= 8192):
            raise RuntimeError("Invalid isolated virtual desktop for SendInput")
        class MouseInput(ct.Structure):
            _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                        ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("extra", ct.c_size_t)]
        class Payload(ct.Union):
            _fields_ = [("mouse", MouseInput)]
        class Input(ct.Structure):
            _fields_ = [("type", wt.DWORD), ("payload", Payload)]
        packets = (Input*len(points))()
        for packet, point in zip(packets, points):
            if not (left <= point[0] < left+width and top <= point[1] < top+height):
                raise RuntimeError("SendInput target is outside the isolated virtual desktop")
            packet.type = 0  # INPUT_MOUSE; this helper can't send keys or clicks.
            packet.payload.mouse.dx = ((2*(point[0]-left)+1)*65536)//(2*width)
            packet.payload.mouse.dy = ((2*(point[1]-top)+1)*65536)//(2*height)
            packet.payload.mouse.dwFlags = 0x0001 | 0x2000 | 0x4000 | 0x8000
        send = api.user.SendInput
        send.argtypes, send.restype = [wt.UINT, ct.POINTER(Input), ct.c_int], wt.UINT
        self.assert_identity()
        delivered = send(len(packets), packets, ct.sizeof(Input))
        if delivered != len(packets):
            raise RuntimeError(f"Isolated SendInput delivered {delivered}/{len(packets)} moves; error {ct.get_last_error()}")
        top_window = api.user.WindowFromPoint(wt.POINT(*target))
        if api.owner(hwnd) != pid or not top_window or api.owner(top_window) != pid:
            raise RuntimeError("SendInput target no longer belongs to the created process")
        return dict(method="SendInput-absolute-virtual-desktop-no-coalescing", delivered=delivered,
                    input_size=ct.sizeof(Input), foreground_activation_returned=activated,
                    virtual_desktop=[left, top, width, height])


def stamp(value):
    return value.isoformat().replace("+00:00", "Z")


def wait_for(predicate, timeout=8, description="условие"):
    deadline, last_error = time.monotonic()+timeout, None
    while time.monotonic() < deadline:
        try:
            value = predicate()
            if value:
                return value
        except (OSError, RuntimeError, ValueError) as error:
            last_error = error
        time.sleep(0.08)
    raise TimeoutError(f"Не выполнено за {timeout} с: {description}; {last_error or ''}")


def startup_navigation_label(setting, system_ui_language=None):
    if setting not in (None, "ru", "en", "system"):
        raise ValueError("Saved UI language isn't a supported canonical setting")
    if setting in (None, "system"):
        if system_ui_language is None:
            function = ct.WinDLL("kernel32", use_last_error=True).GetUserDefaultUILanguage
            function.argtypes, function.restype = [], wt.WORD
            system_ui_language = function()
        setting = "ru" if system_ui_language & 0x03ff == 0x0019 else "en"
    return "Состояние" if setting == "ru" else "Status"


def startup_navigation_ready(controls, expected_label):
    if any(row["name"].startswith(("Не удалось запустить Net Lights", "Couldn't start Net Lights"))
           for row in controls):
        raise AssertionError("Получено стартовое окно ошибки, а не интерфейс приложения")
    return any(row["name"] == expected_label and row["kind"] == "TabItem" and row["visible"] for row in controls)


def checked_output(requested, workspace=None):
    workspace = (workspace or pathlib.Path(__file__).resolve().parents[1]).resolve()
    raw = pathlib.Path(os.path.abspath(requested))
    # Проверяется исходное написание, прежде чем resolve скроет junction/symlink.
    for component in (raw, *raw.parents):
        try:
            attributes = component.lstat()
        except FileNotFoundError:
            continue
        if attributes.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError("Папка результатов не должна проходить через reparse point")
    output = raw.resolve()
    roots = [(workspace/name).resolve() for name in ("artifacts", "TestResults")]
    if not any(output != root and output.is_relative_to(root) for root in roots):
        raise ValueError("--output должен быть новым подкаталогом artifacts или TestResults этого репозитория")
    local_data = os.environ.get("LOCALAPPDATA")
    if local_data:
        live = (pathlib.Path(local_data)/"NetLights").resolve()
        if output == live or output.is_relative_to(live):
            raise ValueError("Живой профиль Net Lights и его дочерние папки защищены")
    if output.exists():
        raise ValueError("Папка результатов уже существует; повторное использование запрещено")
    return output


def profile_json_snapshot(profile, relative, kernel=None):
    """Read an owned profile snapshot without blocking its atomic replacement."""
    limits = {"settings.json": 1024*1024, "availability-history.json": 16*1024*1024,
              "location-history.json": 64*1024*1024, "GeoIP/current.json": 4096}
    root = profile if isinstance(profile, pathlib.PurePath) else pathlib.Path(profile)
    if relative not in limits or root.name != "profile" or not root.is_absolute() or not re.fullmatch(r"[A-Za-z]:", root.drive):
        raise ValueError("Snapshot reads require a known file in the owned local profile")
    maximum = limits[relative]
    if kernel is None:
        kernel = ct.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes, kernel.CreateFileW.restype = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ct.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE], wt.HANDLE
        kernel.GetFileSizeEx.argtypes, kernel.GetFileSizeEx.restype = [wt.HANDLE, ct.POINTER(ct.c_longlong)], wt.BOOL
        kernel.ReadFile.argtypes, kernel.ReadFile.restype = [wt.HANDLE, ct.c_void_p, wt.DWORD, ct.POINTER(wt.DWORD), ct.c_void_p], wt.BOOL
        kernel.CloseHandle.argtypes, kernel.CloseHandle.restype = [wt.HANDLE], wt.BOOL
    # GENERIC_READ; SHARE_READ | SHARE_WRITE | SHARE_DELETE; OPEN_EXISTING.
    # Python's normal CRT file open omits SHARE_DELETE and can obstruct rename.
    handle = kernel.CreateFileW(str(root.joinpath(*relative.split("/"))), 0x80000000, 0x7, None, 3, 0x80, None)
    if handle in (None, 0, ct.c_void_p(-1).value):
        raise OSError(0, "The owned profile snapshot could not be opened", None, ct.get_last_error())
    chunks, total = [], 0
    try:
        size = ct.c_longlong()
        if not kernel.GetFileSizeEx(handle, ct.byref(size)):
            raise OSError(0, "The owned profile snapshot size could not be read", None, ct.get_last_error())
        if not 0 <= size.value <= maximum:
            raise ValueError("Profile snapshot exceeds its size limit")
        buffer = ct.create_string_buffer(min(64*1024, maximum+1))
        while True:
            requested, count = min(len(buffer), maximum-total+1), wt.DWORD()
            if not kernel.ReadFile(handle, buffer, requested, ct.byref(count), None):
                raise OSError(0, "The owned profile snapshot could not be read", None, ct.get_last_error())
            if count.value > requested:
                raise RuntimeError("Invalid profile snapshot read count")
            if not count.value:
                break
            total += count.value
            if total > maximum:
                raise ValueError("Profile snapshot grew beyond its size limit")
            chunks.append(buffer[:count.value])
    finally:
        if not kernel.CloseHandle(handle):
            raise OSError(0, "The owned profile snapshot handle could not be closed", None, ct.get_last_error())
    # Close the native handle before parsing, including on invalid JSON/UTF-8.
    return json.loads(b"".join(chunks).decode("utf-8-sig"))


def country_readiness(profile, enabled_after):
    """Возвращает только публичные версии/булевы признаки, никогда сам IP."""
    archive = profile/"GeoIP-Compare/IPLocate-country.csv.zip"
    state = dict(v2fly=False, iplocate=False, public_lookup=False, both_versions_recorded=False)
    try:
        generation = profile_json_snapshot(profile, "GeoIP/current.json")
        digest = generation.get("Hash", generation.get("hash", "")) if isinstance(generation, dict) else ""
        if isinstance(digest, str) and len(digest) == 64 and all(c in "0123456789abcdefABCDEF" for c in digest):
            data = profile/"GeoIP"/f"geoip-{digest.lower()}.dat"
            state["v2fly"] = data.is_file() and 0 < data.stat().st_size <= 40*1024*1024
    except (OSError, TypeError, ValueError):
        pass
    try:
        state["iplocate"] = archive.is_file() and 0 < archive.stat().st_size <= 24*1024*1024
    except OSError:
        pass
    try:
        history = profile_json_snapshot(profile, "location-history.json")
        if not isinstance(history, dict):
            return state
        for observation in reversed(history.get("observations", [])):
            if not isinstance(observation, dict):
                continue
            try:
                observed = dt.datetime.fromisoformat(observation["observedUtc"].replace("Z", "+00:00"))
                if observed < enabled_after or not observation.get("confirmed"):
                    continue
                address = ipaddress.ip_address(observation.get("publicIp", ""))
            except (KeyError, TypeError, ValueError):
                continue
            if not address.is_global:
                continue
            state["public_lookup"] = True
            versions = (observation.get("databaseVersion"), observation.get("iplocateDatabaseVersion"))
            if all(isinstance(v, str) and v and not v.startswith("fixture-") for v in versions):
                state["both_versions_recorded"] = True
                state["local_result_recorded"] = observation.get("localCountry") is not None
                state["iplocate_result_recorded"] = observation.get("iplocateCountry") is not None
                break
    except (OSError, TypeError, ValueError):
        pass
    return state


def maximum_rows(now):
    """Предельная сохранённая история, а не размножение UI-контролов."""
    begin = now-dt.timedelta(days=7)+dt.timedelta(minutes=10)
    total_seconds = (now-begin).total_seconds()
    stays, observations, spans = [], [], []
    for index in range(70_000):
        first = begin+dt.timedelta(seconds=total_seconds*index/70_000)
        next_first = begin+dt.timedelta(seconds=total_seconds*(index+1)/70_000)
        iso = ("DE", "PL", "RU", "DE")[index % 4]
        last = min(next_first, first+dt.timedelta(seconds=1)) if iso == "RU" else next_first
        if index % 17 == 0:
            last -= dt.timedelta(milliseconds=250)
        stays.append(dict(iso=iso, startedUtc=stamp(first), endedUtc=stamp(last)))
        observations.append(dict(observedUtc=stamp(first), observedUntilUtc=stamp(last), countryCode=iso,
            onlineCountry=iso, localCountry="FR" if iso == "RU" else iso, iplocateCountry=iso,
            confirmed=True, publicIp=f"198.51.100.{1+index%250}", resolutionSource="country.is",
            databaseVersion="fixture-v2", iplocateDatabaseVersion="fixture-ip"))
    for index in range(5000):
        first = begin+dt.timedelta(seconds=total_seconds*index/5000)
        last = begin+dt.timedelta(seconds=total_seconds*(index+1)/5000)
        for group in ("Ru", "World"):
            spans.append(dict(group=group, state=("Online", "Limited", "Offline", "Online")[index%4],
                paused=index%31 == 0, startedUtc=stamp(first), endedUtc=stamp(last)))
    return stays, observations, spans


def fixture(root, maximum=False, offline=False):
    """Пишется только новый output/profile, реальные данные не читаются."""
    root.mkdir(parents=True, exist_ok=False)
    now = dt.datetime.now(dt.timezone.utc)
    settings = dict(settingsVersion=8, autoStart=False, autoUpdateEnabled=False,
                    geoCountryDetectionEnabled=False, geoCountryIconEnabled=False,
                    locationChartWindowHours=12, emphasizeShortStatuses=True,
                    windowWidth=1450, windowHeight=1300, theme="dark", language="ru")
    # showLocationDebugSources отсутствует: проверяется default=false.
    (root/"settings.json").write_text(json.dumps(settings), encoding="utf-8")
    if offline:
        endpoints = [dict(id=f"{group}-{index}", group=group,
                          uri=f"https://127.0.0.1:1/{group}-{index}",
                          infrastructureId=f"local-{group}-{index}")
                     for group in ("Ru", "World") for index in range(7)]
        (root/"endpoints.json").write_text(json.dumps(endpoints), encoding="utf-8")
    spans, stays, observations = [], [], []
    for day in range(7):
        start = now-dt.timedelta(days=7-day)
        blocks = [(start+dt.timedelta(hours=h), 60) for h in range(24)] if day == 6 else [(start, 1440)]
        for begin, length in blocks:
            if length == 60:
                status = [(0, 24, "Online", False), (24, 24.2, "Limited", False),
                          (24.2, 24.3, "Offline", False), (24.3, 48, "Online", False),
                          (48, 49, "Unknown", False), (49, 52, "Online", False),
                          (52, 53, "Online", True), (53, 54, "Online", False), (56, 60, "Online", False)]
                countries = [(0, 30, "DE"), (30, 30.25, "RU"), (30.25, 54, "PL"), (56, 60, "DE")]
            else:
                status = [(0, 360, "Online", False), (360, 362, "Limited", False),
                          (362, 363, "Offline", False), (363, 700, "Online", False), (720, 1440, "Online", False)]
                countries = [(0, 350, "DE"), (350, 351, "RU"), (351, 700, "PL"), (720, 1440, "DE")]
            for group in ("Ru", "World"):
                for a, b, state, paused in status:
                    if group == "World" and length == 60:
                        a, b = a+1, min(b+1, length)
                        if a >= b:
                            continue
                    spans.append(dict(group=group, state=state, paused=paused,
                                      startedUtc=stamp(begin+dt.timedelta(minutes=a)), endedUtc=stamp(begin+dt.timedelta(minutes=b))))
            for a, b, iso in countries:
                first, last = begin+dt.timedelta(minutes=a), begin+dt.timedelta(minutes=b)
                stays.append(dict(iso=iso, startedUtc=stamp(first), endedUtc=stamp(last)))
                observations.append(dict(observedUtc=stamp(first), observedUntilUtc=stamp(last), countryCode=iso,
                    onlineCountry=iso, localCountry="FR" if iso == "RU" else iso, iplocateCountry=iso, confirmed=True,
                    publicIp=f"198.51.100.{1+len(stays)%250}", resolutionSource="country.is",
                    databaseVersion="fixture-v2", iplocateDatabaseVersion="fixture-ip"))
    if maximum:
        stays, observations, spans = maximum_rows(now)
    (root/"availability-history.json").write_text(json.dumps(dict(spans=spans, lastObservedUtc=stamp(now))), encoding="utf-8")
    (root/"location-history.json").write_text(json.dumps(dict(version=3, stays=stays, observations=observations,
                                                            lastObservedUtc=stamp(now))), encoding="utf-8")
    assert len(stays) >= 28
    return dict(stays=len(stays), observations=len(observations), availability_spans=len(spans),
                oldest_stay_utc=stays[0]["startedUtc"], newest_stay_utc=stays[-1]["endedUtc"],
                synthetic_history=True, maximum_history=maximum, live_country_detection=False,
                offline_availability_endpoints=offline)


class Win32:
    def __init__(self):
        self.user, self.kernel, self.psapi = (ct.WinDLL(name, use_last_error=True) for name in ("user32", "kernel32", "psapi"))
        for name, args, result in [
            ("IsWindow", [wt.HWND], wt.BOOL), ("IsWindowVisible", [wt.HWND], wt.BOOL),
            ("IsIconic", [wt.HWND], wt.BOOL), ("IsZoomed", [wt.HWND], wt.BOOL),
            ("GetForegroundWindow", [], wt.HWND), ("GetDpiForWindow", [wt.HWND], wt.UINT),
            ("SetForegroundWindow", [wt.HWND], wt.BOOL), ("GetSystemMetrics", [ct.c_int], ct.c_int),
            ("GetWindowRect", [wt.HWND, ct.POINTER(wt.RECT)], wt.BOOL),
            ("SetWindowPos", [wt.HWND, wt.HWND, ct.c_int, ct.c_int, ct.c_int, ct.c_int, wt.UINT], wt.BOOL),
            ("MonitorFromWindow", [wt.HWND, wt.DWORD], wt.HANDLE),
            ("GetClientRect", [wt.HWND, ct.POINTER(wt.RECT)], wt.BOOL),
            ("ClientToScreen", [wt.HWND, ct.POINTER(wt.POINT)], wt.BOOL),
            ("ScreenToClient", [wt.HWND, ct.POINTER(wt.POINT)], wt.BOOL),
            ("GetCursorPos", [ct.POINTER(wt.POINT)], wt.BOOL),
            ("SetCursorPos", [ct.c_int, ct.c_int], wt.BOOL),
            ("WindowFromPoint", [wt.POINT], wt.HWND),
            ("GetWindowThreadProcessId", [wt.HWND, ct.POINTER(wt.DWORD)], wt.DWORD),
            ("PostMessageW", [wt.HWND, wt.UINT, ct.c_size_t, ct.c_ssize_t], wt.BOOL),
            ("SendMessageTimeoutW", [wt.HWND, wt.UINT, ct.c_size_t, ct.c_ssize_t,
                                     wt.UINT, wt.UINT, ct.POINTER(ct.c_size_t)], ct.c_ssize_t),
            ("GetAsyncKeyState", [ct.c_int], ct.c_short), ("GetGuiResources", [wt.HANDLE, wt.DWORD], wt.DWORD),
        ]:
            function = getattr(self.user, name)
            function.argtypes, function.restype = args, result
        self.kernel.OpenProcess.argtypes, self.kernel.OpenProcess.restype = [wt.DWORD, wt.BOOL, wt.DWORD], wt.HANDLE
        self.kernel.CloseHandle.argtypes, self.kernel.CloseHandle.restype = [wt.HANDLE], wt.BOOL
        self.kernel.GetProcessTimes.argtypes = [wt.HANDLE]+[ct.POINTER(wt.FILETIME)]*4
        self.kernel.GetProcessTimes.restype = wt.BOOL
        self.kernel.GetProcessHandleCount.argtypes, self.kernel.GetProcessHandleCount.restype = [wt.HANDLE, ct.POINTER(wt.DWORD)], wt.BOOL
        self.kernel.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ct.POINTER(wt.DWORD)]
        self.kernel.QueryFullProcessImageNameW.restype = wt.BOOL
        self.psapi.GetProcessMemoryInfo.argtypes, self.psapi.GetProcessMemoryInfo.restype = [wt.HANDLE, ct.c_void_p, wt.DWORD], wt.BOOL

    def outer(self, hwnd):
        rectangle = wt.RECT()
        if not self.user.GetWindowRect(hwnd, ct.byref(rectangle)):
            raise ct.WinError(ct.get_last_error())
        return [rectangle.left, rectangle.top, rectangle.right-rectangle.left, rectangle.bottom-rectangle.top]

    def work_area(self, hwnd):
        class MonitorInfo(ct.Structure):
            _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT), ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]
        information = MonitorInfo()
        information.cbSize = ct.sizeof(information)
        function = self.user.GetMonitorInfoW
        function.argtypes, function.restype = [wt.HANDLE, ct.POINTER(MonitorInfo)], wt.BOOL
        monitor = self.user.MonitorFromWindow(hwnd, 2)
        if not monitor or not function(monitor, ct.byref(information)):
            raise ct.WinError(ct.get_last_error())
        r = information.rcWork
        return [r.left, r.top, r.right-r.left, r.bottom-r.top]

    def owner(self, hwnd):
        value = wt.DWORD()
        if not self.user.GetWindowThreadProcessId(hwnd, ct.byref(value)):
            raise ct.WinError(ct.get_last_error())
        return value.value

    def visible_owned_window(self, pid, title):
        callback_type = ct.WINFUNCTYPE(wt.BOOL, wt.HWND, ct.c_ssize_t)
        function = self.user.EnumWindows
        function.argtypes, function.restype = [callback_type, ct.c_ssize_t], wt.BOOL
        get_text = self.user.GetWindowTextW
        get_text.argtypes, get_text.restype = [wt.HWND, wt.LPWSTR, ct.c_int], ct.c_int
        matches = []

        @callback_type
        def visit(hwnd, _):
            try:
                if self.owner(hwnd) == pid and self.user.IsWindowVisible(hwnd):
                    buffer = ct.create_unicode_buffer(257)
                    get_text(hwnd, buffer, len(buffer))
                    if buffer.value == title:
                        matches.append(int(hwnd))
            except OSError:
                pass  # A window can be destroyed during enumeration.
            return len(matches) < 2

        function(visit, 0)
        if len(matches) > 1:
            raise RuntimeError("Несколько собственных видимых окон имеют один заголовок")
        return matches[0] if matches else None

    def client_rect(self, hwnd):
        rectangle, point = wt.RECT(), wt.POINT()
        if not self.user.GetClientRect(hwnd, ct.byref(rectangle)) or not self.user.ClientToScreen(hwnd, ct.byref(point)):
            raise ct.WinError(ct.get_last_error())
        return point.x, point.y, point.x+rectangle.right, point.y+rectangle.bottom

    def post(self, hwnd, message, wp=0, lp=0):
        if not self.user.PostMessageW(hwnd, message, wp, lp):
            raise ct.WinError(ct.get_last_error())

    def send(self, hwnd, message, wp=0, lp=0):
        result = ct.c_size_t()
        if not self.user.SendMessageTimeoutW(hwnd, message, wp, lp, 3, 1000, ct.byref(result)):
            raise ct.WinError(ct.get_last_error())
        return ct.c_ssize_t(result.value).value

    def hover(self, hwnd, point):
        local = wt.POINT(*point)
        if not self.user.ScreenToClient(hwnd, ct.byref(local)):
            raise ct.WinError(ct.get_last_error())
        self.post(hwnd, WM_MOUSEMOVE, 0, (local.x & 0xffff) | ((local.y & 0xffff) << 16))

    def wheel(self, hwnd, point, delta):
        # GPUI читает реальные модификаторы. Пользовательские клавиши не отпускаются.
        wait_for(lambda: not any(self.user.GetAsyncKeyState(key) & 0x8000
                                for key in (0x10, 0x11, 0x12, 0x5B, 0x5C)), 3, "нет удерживаемых модификаторов")
        self.hover(hwnd, point)
        self.post(hwnd, WM_MOUSEWHEEL, (delta & 0xffff) << 16, (point[0] & 0xffff) | ((point[1] & 0xffff) << 16))

    def click(self, hwnd, point):
        local = wt.POINT(*point)
        if not self.user.ScreenToClient(hwnd, ct.byref(local)):
            raise ct.WinError(ct.get_last_error())
        position = (local.x & 0xffff) | ((local.y & 0xffff) << 16)
        self.hover(hwnd, point)
        self.post(hwnd, 0x0201, 1, position)
        self.post(hwnd, 0x0202, 0, position)


class NavigationFrames:
    """Fast foreground ROI capture; only the isolated app's visible tab row."""
    def __init__(self,api,policy,hwnd,pid,band):
        self.api,self.policy,self.hwnd,self.pid,self.band=api,policy,hwnd,pid,band
        self.screen=self.memory=self.bitmap=self.original=None
        self.gdi=ct.WinDLL("gdi32",use_last_error=True)
        for name,args,result in [
            ("CreateCompatibleDC",[wt.HDC],wt.HDC),
            ("CreateCompatibleBitmap",[wt.HDC,ct.c_int,ct.c_int],wt.HBITMAP),
            ("SelectObject",[wt.HDC,wt.HANDLE],wt.HANDLE),
            ("BitBlt",[wt.HDC,ct.c_int,ct.c_int,ct.c_int,ct.c_int,wt.HDC,ct.c_int,ct.c_int,wt.DWORD],wt.BOOL),
            ("GetDIBits",[wt.HDC,wt.HBITMAP,wt.UINT,wt.UINT,ct.c_void_p,ct.c_void_p,wt.UINT],ct.c_int),
            ("DeleteObject",[wt.HANDLE],wt.BOOL),("DeleteDC",[wt.HDC],wt.BOOL)]:
            function=getattr(self.gdi,name)
            function.argtypes,function.restype=args,result
        api.user.GetDC.argtypes,api.user.GetDC.restype=[wt.HWND],wt.HDC
        api.user.ReleaseDC.argtypes,api.user.ReleaseDC.restype=[wt.HWND,wt.HDC],ct.c_int

    def guard(self):
        self.policy.assert_identity()
        assert self.api.owner(self.hwnd)==self.pid and self.api.user.GetForegroundWindow()==self.hwnd
        x,y,width,height=self.api.outer(self.hwnd)
        left,top,right,bottom=self.band
        assert x<=left<right<=x+width and y<=top<bottom<=y+height
        assert 0<right-left<=8192 and 0<bottom-top<=256
        for point in ((left,top),(right-1,top),(left,bottom-1),(right-1,bottom-1)):
            assert self.api.owner(self.api.user.WindowFromPoint(wt.POINT(*point)))==self.pid

    def __enter__(self):
        self.guard()
        left,top,right,bottom=self.band
        self.width,self.height=right-left,bottom-top
        try:
            self.screen=self.api.user.GetDC(None)
            self.memory=self.gdi.CreateCompatibleDC(self.screen)
            self.bitmap=self.gdi.CreateCompatibleBitmap(self.screen,self.width,self.height)
            if not self.screen or not self.memory or not self.bitmap:
                raise ct.WinError(ct.get_last_error())
            self.original=self.gdi.SelectObject(self.memory,self.bitmap)
            self.buffer=ct.create_string_buffer(self.width*self.height*4)
            class Header(ct.Structure):
                _fields_=[("size",wt.DWORD),("width",ct.c_int32),("height",ct.c_int32),
                    ("planes",wt.WORD),("bits",wt.WORD),("compression",wt.DWORD),("image_size",wt.DWORD),
                    ("x",ct.c_int32),("y",ct.c_int32),("used",wt.DWORD),("important",wt.DWORD)]
            self.header=Header(ct.sizeof(Header),self.width,-self.height,1,32,0,len(self.buffer),0,0,0,0)
            return self
        except BaseException:
            self.__exit__(None,None,None)
            raise

    def capture(self):
        from PIL import Image
        self.guard()
        left,top,_,_=self.band
        if not self.gdi.BitBlt(self.memory,0,0,self.width,self.height,self.screen,left,top,0x40CC0020):
            raise ct.WinError(ct.get_last_error())
        self.gdi.SelectObject(self.memory,self.original)
        try:
            if self.gdi.GetDIBits(self.screen,self.bitmap,0,self.height,self.buffer,ct.byref(self.header),0)!=self.height:
                raise ct.WinError(ct.get_last_error())
        finally:
            self.gdi.SelectObject(self.memory,self.bitmap)
        return Image.frombuffer("RGB",(self.width,self.height),self.buffer,"raw","BGRX",0,1).copy()

    def __exit__(self,*_):
        if self.memory and self.original:
            self.gdi.SelectObject(self.memory,self.original)
        if self.bitmap:
            self.gdi.DeleteObject(self.bitmap)
        if self.memory:
            self.gdi.DeleteDC(self.memory)
        if self.screen:
            self.api.user.ReleaseDC(None,self.screen)


def set_dpi_awareness():
    function = ct.WinDLL("user32", use_last_error=True).SetProcessDpiAwarenessContext
    function.argtypes, function.restype = [ct.c_void_p], wt.BOOL
    function(ct.c_void_p(-4))  # PER_MONITOR_AWARE_V2, только собственный процесс.


def capture_native(hwnd, expected_pid, destination):
    """Отдельный helper ограничивает зависший PrintWindow временем жизни процесса."""
    from PIL import Image, ImageStat, ImageGrab
    api = Win32()
    if api.owner(hwnd) != expected_pid:
        raise RuntimeError("Снимок не принадлежит созданному процессу")
    user, gdi = api.user, ct.WinDLL("gdi32", use_last_error=True)
    user.GetWindowDC.argtypes, user.GetWindowDC.restype = [wt.HWND], wt.HDC
    user.ReleaseDC.argtypes, user.ReleaseDC.restype = [wt.HWND, wt.HDC], ct.c_int
    user.PrintWindow.argtypes, user.PrintWindow.restype = [wt.HWND, wt.HDC, wt.UINT], wt.BOOL
    for name, args, result in [("CreateCompatibleDC", [wt.HDC], wt.HDC),
        ("CreateCompatibleBitmap", [wt.HDC, ct.c_int, ct.c_int], wt.HBITMAP),
        ("SelectObject", [wt.HDC, wt.HANDLE], wt.HANDLE), ("DeleteObject", [wt.HANDLE], wt.BOOL),
        ("DeleteDC", [wt.HDC], wt.BOOL),
        ("GetDIBits", [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ct.c_void_p, ct.c_void_p, wt.UINT], ct.c_int)]:
        function = getattr(gdi, name)
        function.argtypes, function.restype = args, result
    rectangle = wt.RECT()
    if not api.user.IsWindowVisible(hwnd) or not user.GetWindowRect(hwnd, ct.byref(rectangle)):
        raise RuntimeError("Нельзя снять невидимое или отсутствующее окно")
    width, height = rectangle.right-rectangle.left, rectangle.bottom-rectangle.top
    if not 0 < width <= 8192 or not 0 < height <= 8192 or width*height > 32_000_000:
        raise RuntimeError("Размер реального HWND вне границ снимка")
    source = user.GetWindowDC(hwnd)
    memory, bitmap = gdi.CreateCompatibleDC(source), gdi.CreateCompatibleBitmap(source, width, height)
    previous, image, method = None, None, "PrintWindow"
    try:
        if not source or not memory or not bitmap:
            raise ct.WinError(ct.get_last_error())
        previous = gdi.SelectObject(memory, bitmap)
        rendered = user.PrintWindow(hwnd, memory, 2)
        gdi.SelectObject(memory, previous)
        previous = None
        class Header(ct.Structure):
            _fields_ = [("size", wt.DWORD), ("width", ct.c_int32), ("height", ct.c_int32),
                ("planes", wt.WORD), ("bits", wt.WORD), ("compression", wt.DWORD), ("image_size", wt.DWORD),
                ("x", ct.c_int32), ("y", ct.c_int32), ("used", wt.DWORD), ("important", wt.DWORD)]
        header = Header(ct.sizeof(Header), width, -height, 1, 32, 0, width*height*4, 0, 0, 0, 0)
        buffer = ct.create_string_buffer(width*height*4)
        if rendered and gdi.GetDIBits(source, bitmap, 0, height, buffer, ct.byref(header), 0) == height:
            image = Image.frombuffer("RGB", (width, height), buffer, "raw", "BGRX", 0, 1).copy()
        def nonempty(frame):
            if frame is None:
                return False
            crop = frame.crop((min(250, width//3), min(90, height//4), width-15, height-15))
            return max(ImageStat.Stat(crop).var) > 1
        if not nonempty(image):
            if api.user.GetForegroundWindow() != hwnd:
                raise RuntimeError("PrintWindow не снял GPU-клиент, своё окно не foreground; чужой экран не используется")
            image = ImageGrab.grab(bbox=(rectangle.left, rectangle.top, rectangle.right, rectangle.bottom), all_screens=True)
            method = "desktop_foreground"
            if not nonempty(image):
                raise RuntimeError("Реальное окно не дало непустого GPU-снимка")
        if api.owner(hwnd) != expected_pid:
            raise RuntimeError("Владелец HWND изменился во время снимка")
        image.save(destination)
        print(json.dumps(dict(method=method, hwnd=hwnd, width=width, height=height, dpi=api.user.GetDpiForWindow(hwnd))))
    finally:
        if previous:
            gdi.SelectObject(memory, previous)
        if bitmap:
            gdi.DeleteObject(bitmap)
        if memory:
            gdi.DeleteDC(memory)
        if source:
            user.ReleaseDC(hwnd, source)


def capture_sandbox_desktop(exe, marker, nonce, destination):
    """Only a verified disposable WDAG desktop, never the user's desktop."""
    policy = IsolatedInput(exe, marker, nonce)
    policy.assert_identity()
    destination = checked_output(destination)
    from PIL import ImageGrab
    function = ct.WinDLL("user32", use_last_error=True).GetSystemMetrics
    function.argtypes, function.restype = [ct.c_int], ct.c_int
    left, top, width, height = (function(index) for index in (76, 77, 78, 79))
    if not (0 < width <= 8192 and 0 < height <= 8192 and width*height <= 32_000_000):
        raise RuntimeError("Guest desktop dimensions exceed capture limits")
    picture = ImageGrab.grab(bbox=(left, top, left+width, top+height), all_screens=True)
    if picture.size != (width, height):
        raise RuntimeError("Guest desktop capture dimensions differ")
    with destination.open("xb") as output:
        picture.save(output, format="PNG")
    print(json.dumps(dict(method="ImageGrab-isolated-guest-desktop", width=width, height=height,
                          input_scope=policy.evidence["scope"], nonce=nonce, physical_input=False)))


class Automation:
    """Один UIA/COM владелец; зависший provider не порождает бесконечные потоки."""
    def __init__(self):
        self.requests = queue.Queue(maxsize=1)
        self.interactions = []
        self.connection_diagnostics = {}
        self.thread = threading.Thread(target=self._worker, daemon=True)
        self.thread.start()
        self.hwnd = None

    def connect(self, pid):
        self.hwnd = self.call("connect", pid, timeout=30)

    def call(self, command, *arguments, timeout=8):
        deadline = time.monotonic()+timeout
        for attempt in range(3):
            remaining = deadline-time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"UIA provider не ответил: {command}")
            reply = queue.Queue(maxsize=1)
            self.requests.put_nowait((command, arguments, reply))
            try:
                result = reply.get(timeout=remaining)
            except queue.Empty:
                raise TimeoutError(f"UIA provider не ответил: {command}") from None
            if not isinstance(result, BaseException):
                return result
            # A live refresh can remove a descendant during enumeration. Only
            # repeat read-only queries, under the original shared deadline.
            if (command in ("inventory", "exists", "rect", "value", "selected") and attempt < 2
                    and getattr(result, "uia_hresult", None) == 0x80040201):
                self.interactions.append(dict(method="UIA-read-retry", command=command, attempt=attempt+1))
                continue
            raise RuntimeError(f"UIA {command}: {type(result).__name__}: {result}") from result

    def _worker(self):
        ole, com_initialized, initialization_error = None, False, None
        app = window = specification = control = info = None
        controls = []
        walker = child_element = None
        try:
            try:
                ole = ct.OleDLL("ole32")
                ole.CoInitializeEx(None, 0)  # MTA, также для потоков после перезапуска.
                com_initialized = True
                from pywinauto import Application
                from pywinauto.uia_defines import IUIA, NoPatternInterfaceError
            except BaseException as error:
                # Keep only text: import/COM tracebacks may retain MTA-owned
                # objects. Still drain commands so connect fails promptly and
                # the caller can stop and join this owner normally.
                initialization_error = f"{type(error).__name__}: {str(error)[:4096]}"
            while True:
                command, arguments, reply = self.requests.get()
                try:
                    if command == "stop":
                        reply.put(None)
                        return
                    if command == "interactions":
                        value = self.interactions.copy()
                    elif command == "connection-diagnostics":
                        value = self.connection_diagnostics.copy()
                    elif initialization_error is not None:
                        raise RuntimeError(f"UIA initialization failed: {initialization_error}")
                    elif command == "connect":
                        pid = arguments[0]
                        api = Win32()
                        hwnd = wait_for(lambda: api.visible_owned_window(pid, "Net Lights"), 15,
                                        "Win32 HWND с точным заголовком, видимостью и собственным PID")
                        self.connection_diagnostics = dict(method="Win32-owned-HWND-to-UIA.ElementFromHandle",
                                                           expected_pid=pid, native_hwnd=hwnd)
                        app = Application(backend="uia").connect(process=arguments[0], timeout=10)
                        specification = app.window(handle=hwnd)
                        window = specification.wrapper_object()
                        self.connection_diagnostics["root"] = dict(
                            name=window.element_info.name[:256], process_id=window.element_info.process_id,
                            control_type=window.element_info.control_type,
                            is_offscreen=bool(window.element_info.element.CurrentIsOffscreen))
                        specification.wait("visible", timeout=15)
                        # Сохраняем конкретный HWND/UIA element. Popup/tooltip
                        # своего процесса может позже иметь такой же заголовок.
                        window = specification.wrapper_object()
                        if int(window.handle) != hwnd or api.owner(hwnd) != pid:
                            raise RuntimeError("UIA подключилась к чужому или заменённому HWND")
                        value = int(window.handle)
                    elif window is not None and api.owner(hwnd) != pid:
                        raise RuntimeError("UIA HWND больше не принадлежит проверенному процессу")
                    elif command == "inventory":
                        value = []
                        for control in window.descendants():
                            info, rectangle = control.element_info, control.rectangle()
                            value.append(dict(name=info.name, kind=info.control_type, id=info.automation_id,
                                runtime_id=[int(part) for part in info.element.GetRuntimeId()] if info.control_type=="Button" else None,
                                rect=[rectangle.left, rectangle.top, rectangle.right, rectangle.bottom], visible=control.is_visible()))
                    else:
                        name = arguments[0]
                        controls = [c for c in window.descendants() if c.element_info.name == name]
                        if command == "exists":
                            value = any(c.is_visible() for c in controls)
                        else:
                            if not controls:
                                raise RuntimeError(f"Контрол не найден: {name}")
                            control = next((c for c in controls if c.is_visible()), controls[0])
                            if command == "invoke":
                                if control.element_info.control_type == "TabItem":
                                    control.iface_selection_item.Select()
                                    value = "UIA.SelectionItem.Select"
                                else:
                                    try:
                                        control.iface_invoke.Invoke()
                                        value = "UIA.Invoke"
                                    except NoPatternInterfaceError:
                                        try:
                                            control.iface_toggle.Toggle()
                                            value = "UIA.Toggle"
                                        except NoPatternInterfaceError:
                                            r = control.rectangle()
                                            api = Win32()
                                            assert api.owner(window.handle) == pid
                                            api.click(window.handle, ((r.left+r.right)//2, (r.top+r.bottom)//2))
                                            value = "PostMessageClick"
                                self.interactions.append(dict(name=name, method=value, pid=pid))
                            elif command == "rect":
                                r = control.rectangle()
                                value = [r.left, r.top, r.right, r.bottom]
                            elif command == "value":
                                try:
                                    value = control.iface_value.CurrentValue
                                except Exception:
                                    value = control.window_text()
                            elif command == "selected":
                                value = bool(control.iface_selection_item.CurrentIsSelected)
                            else:
                                raise RuntimeError(f"Неизвестная UIA команда: {command}")
                    reply.put(value)
                except BaseException as error:
                    if command == "connect":
                        self.connection_diagnostics["error"] = f"{type(error).__name__}: {str(error)[:1024]}"
                        if window is not None:
                            try:
                                element = window.element_info.element
                                self.connection_diagnostics["root"] = dict(name=(element.CurrentName or "")[:256],
                                    process_id=int(element.CurrentProcessId), control_type=int(element.CurrentControlType),
                                    is_offscreen=bool(element.CurrentIsOffscreen))
                                children = []
                                self.connection_diagnostics["children"] = children
                                walker = IUIA().iuia.RawViewWalker
                                child_element = walker.GetFirstChildElement(element)
                                until = time.monotonic()+1
                                while child_element and len(children) < 8 and time.monotonic() < until:
                                    children.append(dict(name=(child_element.CurrentName or "")[:256],
                                        process_id=int(child_element.CurrentProcessId), control_type=int(child_element.CurrentControlType),
                                        is_offscreen=bool(child_element.CurrentIsOffscreen)))
                                    child_element = walker.GetNextSiblingElement(child_element)
                            except Exception as diagnostic_error:
                                self.connection_diagnostics["property_error"] = str(diagnostic_error)[:256]
                            finally:
                                element = walker = child_element = None
                    # Vendor tracebacks can retain COM wrappers owned by this MTA.
                    failure = RuntimeError(f"{type(error).__name__}: {str(error)[:4096]}")
                    hresult = getattr(error, "hresult", None)
                    if isinstance(hresult, int):
                        failure.uia_hresult = hresult & 0xffffffff
                    reply.put(failure)
        finally:
            app = window = specification = control = info = None
            controls = []
            walker = child_element = None
            if com_initialized:
                ole.CoUninitialize()

    def close(self):
        if self.thread.is_alive():
            self.call("stop", timeout=2)
            self.thread.join(timeout=2)
            if self.thread.is_alive():
                raise TimeoutError("UIA owner не завершил освобождение provider")


class MemoryCounters(ct.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD)] + [(name, ct.c_size_t) for name in
        ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
         "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]


class ResourceProcess:
    def __init__(self, api, process, exe):
        self.api, self.process = api, process
        self.handle = api.kernel.OpenProcess(0x0400 | 0x0010, False, process.pid)
        if not self.handle:
            raise ct.WinError(ct.get_last_error())
        name, length = ct.create_unicode_buffer(32768), wt.DWORD(32768)
        if not api.kernel.QueryFullProcessImageNameW(self.handle, 0, name, ct.byref(length)):
            self.close()
            raise ct.WinError(ct.get_last_error())
        if pathlib.Path(name.value).resolve() != exe:
            self.close()
            raise RuntimeError("Счётчики относятся не к созданному exe")
        self.previous = None

    def sample(self):
        if self.process.poll() is not None:
            return None
        memory = MemoryCounters()
        memory.cb = ct.sizeof(memory)
        if not self.api.psapi.GetProcessMemoryInfo(self.handle, ct.byref(memory), memory.cb):
            raise ct.WinError(ct.get_last_error())
        values = [wt.FILETIME() for _ in range(4)]
        if not self.api.kernel.GetProcessTimes(self.handle, *(ct.byref(v) for v in values)):
            raise ct.WinError(ct.get_last_error())
        ticks = sum((v.dwHighDateTime << 32) | v.dwLowDateTime for v in values[2:])
        now, cpu = time.monotonic(), ""
        if self.previous:
            elapsed, previous = self.previous
            cpu = max(0., 100.*(ticks-previous)/10_000_000/max(0.000001, now-elapsed)/max(1, os.cpu_count() or 1))
        self.previous = now, ticks
        handles = wt.DWORD()
        if not self.api.kernel.GetProcessHandleCount(self.handle, ct.byref(handles)):
            raise ct.WinError(ct.get_last_error())
        return dict(pid=self.process.pid, private_bytes=memory.PrivateUsage, working_set=memory.WorkingSetSize,
            handles=handles.value, gdi=self.api.user.GetGuiResources(self.handle, 0), user=self.api.user.GetGuiResources(self.handle, 1), cpu_percent_all_logical=cpu)

    def close(self):
        if self.handle:
            self.api.kernel.CloseHandle(self.handle)
            self.handle = None


class ResourceLog:
    def __init__(self, api, exe, path):
        self.api, self.exe, self.current, self.phase = api, exe, None, "startup"
        self.lock, self.stop = threading.Lock(), threading.Event()
        self.errors, self.samples = [], 0
        self.file = path.open("w", encoding="utf-8", newline="")
        self.writer = csv.DictWriter(self.file, fieldnames=["utc", "phase", "pid", "private_bytes", "working_set", "handles", "gdi", "user", "cpu_percent_all_logical"])
        self.writer.writeheader()
        self.thread = threading.Thread(target=self._worker, daemon=True)
        self.thread.start()

    def attach(self, process):
        with self.lock:
            if self.current:
                self.current.close()
            self.current = ResourceProcess(self.api, process, self.exe) if process else None

    def _worker(self):
        while not self.stop.wait(1):
            with self.lock:
                try:
                    sample = self.current.sample() if self.current else None
                    if sample:
                        self.writer.writerow(dict(utc=stamp(dt.datetime.now(dt.timezone.utc)), phase=self.phase, **sample))
                        self.file.flush()
                        self.samples += 1
                except OSError as error:
                    self.errors.append(str(error))

    def close(self):
        self.stop.set()
        self.thread.join(timeout=2)
        if self.thread.is_alive():
            raise RuntimeError("Resource sampler не завершился в пределах timeout")
        self.attach(None)
        self.file.close()


def normalize_resources(source, destination, pid, started, ended, requested_seconds):
    """Сохраняет реальные времена и все samples непрерывной финальной фазы."""
    with source.open(encoding="utf-8", newline="") as stream:
        rows = list(csv.DictReader(stream))
    positions = [index for index, row in enumerate(rows) if row["phase"].startswith("resource-geo-")]
    selected = rows[positions[0]:positions[-1]+1] if positions else []
    for row in selected:
        if int(row["pid"]) != pid or not row["phase"].startswith("resource-geo-enabled-"):
            raise ValueError("Ресурсная фаза сменила PID/режим; смешанные прогоны запрещены")
    if len(selected) < max(2, int(requested_seconds/1.25)):
        raise ValueError("Недостаточно секундных samples финальной ресурсной фазы")
    times = [dt.datetime.fromisoformat(row["utc"].replace("Z", "+00:00")) for row in selected]
    if (times[-1]-times[0]).total_seconds() < max(0., requested_seconds-5):
        raise ValueError("CSV не покрывает запрошенную продолжительность ресурсной фазы")
    if not 0 <= (times[0]-started).total_seconds() <= 5 or not 0 <= (ended-times[-1]).total_seconds() <= 5:
        raise ValueError("Начало или конец ресурсного измерения потеряны")
    for previous, current in zip(times, times[1:]):
        if not 0 < (current-previous).total_seconds() <= 5:
            raise ValueError("Ресурсный CSV содержит возврат часов или пропуск samples > 5 с")
    with destination.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["utc", "pid", "privateBytes", "workingSet", "handles", "gdi", "userObjects", "cpuPercentAllLogical", "phase"])
        writer.writeheader()
        for row in selected:
            for key in ("private_bytes", "working_set", "handles", "gdi", "user"):
                value = float(row[key])
                if not math.isfinite(value) or value < 0:
                    raise ValueError("Недопустимый ресурсный счётчик")
            writer.writerow(dict(utc=row["utc"], pid=row["pid"], privateBytes=row["private_bytes"],
                workingSet=row["working_set"], handles=row["handles"], gdi=row["gdi"], userObjects=row["user"],
                cpuPercentAllLogical=row["cpu_percent_all_logical"], phase=row["phase"]))
    return dict(pid=pid, samples=len(selected), elapsed_seconds=(times[-1]-times[0]).total_seconds(),
                maximum_gap_seconds=max((b-a).total_seconds() for a, b in zip(times, times[1:])),
                source_timestamps_preserved=True)


class Runner:
    def __init__(self, exe, output, measure_seconds, expected_sha=None, enable_country=False, maximum=False, functional_evidence=False, input_policy=None, coverage_instrumented=False):
        if not __debug__:
            raise RuntimeError("Проверки требуют включённых assert: уберите -O и PYTHONOPTIMIZE")
        if functional_evidence and (measure_seconds or enable_country):
            raise ValueError("Функциональный offline UI-режим не допускает ресурсный прогон или публичное Geo-определение")
        self.exe, self.output, self.profile = exe, output, output/"profile"
        self.api, self.process, self.uia = Win32(), None, None
        self.measure_seconds, self.results, self.captures = measure_seconds, [], []
        self.functional_evidence=functional_evidence
        self.coverage_instrumented=coverage_instrumented
        self.enable_country, self.country_enabled, self.measurement_blocked = enable_country, False, False
        self.input_policy = input_policy
        self.private_captures = []
        digest = self.sha(exe)
        if expected_sha and digest != expected_sha.lower():
            raise RuntimeError("SHA-256 exe не соответствует проверяемой сборке")
        windows = sys.getwindowsversion()
        self.meta = dict(fixture=fixture(self.profile, maximum, functional_evidence or input_policy is not None), exe=str(exe), exe_sha256=digest,
            exe_mtime_utc=stamp(dt.datetime.fromtimestamp(exe.stat().st_mtime, dt.timezone.utc)),
            windows_build=windows.build, windows_product_type=windows.product_type,
            measured_seconds=measure_seconds, dpi_changed=False, physical_input=input_policy is not None,
            assertions_enabled=__debug__)
        self.meta["evidenceLevel"] = "functional" if functional_evidence else "Windows 11 native visual"
        self.meta["visualAcceptance"] = "NOT RUN" if functional_evidence else "requires screenshot review"
        self.meta["coverage_instrumented"] = coverage_instrumented
        self.meta["navigation_smoothness"] = "NOT RUN" if coverage_instrumented else "PENDING"
        self.meta["country_measurement_requested"] = enable_country
        self.meta["locale_required"] = (pathlib.Path(__file__).resolve().parents[1]/"native/core/src/locale.rs").is_file()
        self.meta["input_isolation"] = input_policy.evidence if input_policy else dict(scope="host-owned HWND messages", guard="no-real-pointer-input")
        if not functional_evidence and (windows.build < 22000 or windows.product_type != 1):
            raise RuntimeError("Визуальная приёмка требует Windows 11")
        self.resources = ResourceLog(self.api, exe, output/"resources.csv")

    @staticmethod
    def sha(path):
        digest = hashlib.sha256()
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024*1024), b""):
                digest.update(block)
        return digest.hexdigest()

    def setting(self, key):
        return profile_json_snapshot(self.profile, "settings.json").get(key)

    def history_counts(self):
        history = profile_json_snapshot(self.profile, "location-history.json")
        counts = dict(stays=len(history["stays"]), observations=len(history["observations"]))
        assert counts["stays"] == self.meta["fixture"]["stays"], "Перезапуск потерял или продублировал страны"
        assert counts["observations"] == self.meta["fixture"]["observations"], "Перезапуск потерял или продублировал наблюдения"
        return counts

    def check(self, name, function):
        started = time.monotonic()
        self.resources.phase = name
        print(f"RUN {name}", flush=True)
        try:
            detail = function()
            self.results.append(dict(check=name, result="PASS", seconds=time.monotonic()-started, detail=detail))
            print(f"PASS {name}", flush=True)
        except BaseException as error:
            self.results.append(dict(check=name, result="FAIL", seconds=time.monotonic()-started, error=str(error)))
            try:
                self.capture("failed-"+name,private=self.country_enabled)
            except BaseException as capture_error:
                self.meta.setdefault("failure_capture_errors",[]).append(str(capture_error)[:1024])
            raise

    def start(self):
        assert self.process is None
        with (self.output/f"process-{len(self.results)}.log").open("ab") as log:
            self.process = subprocess.Popen([str(self.exe), "--profile-directory", str(self.profile), "--show-local", "--skip-automatic-updates"], stdout=log, stderr=log, creationflags=NO_CONSOLE)
        self.resources.attach(self.process)
        self.meta.setdefault("pids", []).append(self.process.pid)
        if self.uia is None:
            self.uia = Automation()
        try:
            self.uia.connect(self.process.pid)
        except Exception as original_error:
            try:
                self.meta["uia_connection"] = self.uia.call("connection-diagnostics", timeout=2)
            except Exception as diagnostic_error:
                self.meta.setdefault("startup_diagnostic_errors", []).append(str(diagnostic_error)[:1024])
            try:
                self.startup_diagnostics(original_error)
            except Exception as diagnostic_error:
                self.meta.setdefault("startup_diagnostic_errors", []).append(str(diagnostic_error)[:1024])
            raise
        self.meta.setdefault("uia_connections", []).append(self.uia.call("connection-diagnostics"))
        assert self.api.owner(self.uia.hwnd) == self.process.pid
        self.meta["dpi"] = self.api.user.GetDpiForWindow(self.uia.hwnd)
        work=self.api.work_area(self.uia.hwnd)
        required_height=1000 if self.functional_evidence else 1300
        if work[2]<1450 or work[3]<required_height:
            raise RuntimeError("Actual work area cannot exercise the required functional/visual UI matrix")
        self.meta.setdefault("viewports",[]).append(dict(pid=self.process.pid,work_area=work,
            outer=self.api.outer(self.uia.hwnd),compact_functional=self.functional_evidence and work[3]<1300))
        expected_navigation = startup_navigation_label(self.setting("language"))
        self.meta.setdefault("startup_navigation", []).append(dict(pid=self.process.pid,
            saved_language=self.setting("language"), expected_label=expected_navigation))
        def ready():
            if self.process.poll() is not None:
                raise RuntimeError("Процесс завершился до готовности главного окна")
            controls = self.uia.call("inventory")
            try:
                return startup_navigation_ready(controls, expected_navigation)
            except AssertionError:
                self.capture("startup-error")
                raise
        wait_for(ready, 12, "главная навигация отрисована, не только окно с совпадающим заголовком")
        time.sleep(0.2)

    def startup_diagnostics(self, original_error):
        """Bounded evidence on connect failure; never changes the original FAIL."""
        deadline = time.monotonic()+15
        owned_pid = self.process.pid
        detail = dict(pid=owned_pid, process_exit_code=self.process.poll(),
                      original_failure=str(original_error)[:1024], windows=[], capture_errors=[])
        self.meta.setdefault("startup_failures", []).append(detail)
        callback_type = ct.WINFUNCTYPE(wt.BOOL, wt.HWND, ct.c_ssize_t)
        enum = self.api.user.EnumWindows
        enum.argtypes, enum.restype = [callback_type, ct.c_ssize_t], wt.BOOL
        text = self.api.user.GetWindowTextW
        text.argtypes, text.restype = [wt.HWND, wt.LPWSTR, ct.c_int], ct.c_int
        window_class = self.api.user.GetClassNameW
        window_class.argtypes, window_class.restype = [wt.HWND, wt.LPWSTR, ct.c_int], ct.c_int
        class Placement(ct.Structure):
            _fields_ = [("length", wt.UINT), ("flags", wt.UINT), ("showCmd", wt.UINT),
                        ("min_position", wt.POINT), ("max_position", wt.POINT), ("normal", wt.RECT)]
        placement = self.api.user.GetWindowPlacement
        placement.argtypes, placement.restype = [wt.HWND, ct.POINTER(Placement)], wt.BOOL

        @callback_type
        def collect(hwnd, _):
            try:
                if self.api.owner(hwnd) != owned_pid:
                    return True
                title, kind = ct.create_unicode_buffer(257), ct.create_unicode_buffer(129)
                text(hwnd, title, len(title))
                window_class(hwnd, kind, len(kind))
                record = dict(hwnd=int(hwnd), title=title.value, window_class=kind.value,
                              visible=bool(self.api.user.IsWindowVisible(hwnd)), outer=self.api.outer(hwnd))
                saved = Placement()
                saved.length = ct.sizeof(saved)
                if placement(hwnd, ct.byref(saved)):
                    record["placement"] = dict(showCmd=saved.showCmd, flags=saved.flags,
                        normal=[saved.normal.left, saved.normal.top, saved.normal.right-saved.normal.left,
                                saved.normal.bottom-saved.normal.top])
                detail["windows"].append(record)
            except Exception as error:
                if len(detail["capture_errors"]) < 8:
                    detail["capture_errors"].append(str(error)[:256])
            return len(detail["windows"]) < 8 and time.monotonic() < deadline

        ct.set_last_error(0)
        detail["enumeration_completed"] = bool(enum(collect, 0))
        if not detail["enumeration_completed"]:
            detail["enumeration_last_error"] = ct.get_last_error()
        index = len(self.meta.get("pids", []))
        if self.input_policy and not self.input_policy.ci and time.monotonic() < deadline:
            self.input_policy.assert_identity()
            desktop = self.output/f"startup-guest-desktop-{index}.png"
            try:
                child = subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve()),
                    "--capture-sandbox-desktop", str(self.exe), str(self.input_policy.marker_path),
                    self.input_policy.nonce, str(desktop)], timeout=min(5, max(.1, deadline-time.monotonic())),
                    capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, creationflags=NO_CONSOLE)
                if child.returncode:
                    raise RuntimeError(child.stderr[-1024:])
                detail["guest_desktop"] = dict(file=desktop.name, sha256=self.sha(desktop),
                                                evidence=json.loads(child.stdout.strip().splitlines()[-1]))
            except Exception as error:
                detail["capture_errors"].append(str(error)[:1024])
        captures = 0
        for window in detail["windows"]:
            if not window["visible"] or captures >= 2 or deadline-time.monotonic() < .2:
                continue
            captures += 1
            destination = self.output/f"startup-owned-window-{index}-{captures}.png"
            try:
                child = subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve()), "--capture-hwnd",
                    str(window["hwnd"]), str(owned_pid), str(destination)],
                    timeout=min(5, max(.1, deadline-time.monotonic())), capture_output=True, text=True,
                    encoding="utf-8", errors="replace", check=False, creationflags=NO_CONSOLE)
                if child.returncode:
                    raise RuntimeError(child.stderr[-1024:])
                window["screenshot"] = dict(file=destination.name, sha256=self.sha(destination),
                                              evidence=json.loads(child.stdout.strip().splitlines()[-1]))
            except Exception as error:
                detail["capture_errors"].append(str(error)[:1024])
        with (self.output/f"startup-windows-{index}.json").open("x", encoding="utf-8") as output:
            json.dump(detail, output, ensure_ascii=False, indent=2)

    def stop(self):
        if not self.process:
            return
        automation, self.uia = self.uia, None
        automation_error = None
        if automation:
            try:
                self.meta.setdefault("interactions", []).extend(automation.call("interactions"))
                automation.close()
            except (RuntimeError, TimeoutError, queue.Full) as error:
                automation_error = error
        self.resources.attach(None)
        process, self.process = self.process, None
        if process.poll() is None:
            try:
                helper = subprocess.run([str(self.exe), "--profile-directory", str(self.profile), "--exit-local"], timeout=10, capture_output=True, check=False, creationflags=NO_CONSOLE)
                if helper.returncode:
                    raise RuntimeError(f"exit-local вернул {helper.returncode}")
                process.wait(timeout=15)
            except BaseException:
                if process.poll() is None:
                    process.terminate()  # Сохранённый handle только созданного процесса.
                    process.wait(timeout=5)
                raise
        if process.returncode:
            raise RuntimeError(f"Созданный Net Lights завершился с {process.returncode}")
        if automation_error:
            raise RuntimeError(f"Освобождение UIA перед перезапуском: {automation_error}") from automation_error

    def capture(self, name, evidence=True, private=False):
        assert self.process.poll() is None and self.api.owner(self.uia.hwnd) == self.process.pid
        if self.country_enabled and not private:
            raise RuntimeError("После включения реальной локации снимки могут сохраняться только как measurement-private")
        folder = self.output/"measurement-private" if private else self.output
        folder.mkdir(exist_ok=True)
        destination = folder/f"{name}.png"
        child = subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve()), "--capture-hwnd", str(self.uia.hwnd), str(self.process.pid), str(destination)], timeout=5, capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, creationflags=NO_CONSOLE)
        if child.returncode:
            raise RuntimeError(f"Снимок реального HWND: {child.stderr[-2000:]}")
        if evidence:
            item = json.loads(child.stdout.strip().splitlines()[-1])
            item.update(file=str(destination.relative_to(self.output)), pid=self.process.pid, sha256=self.sha(destination))
            (self.private_captures if private else self.captures).append(item)
        return destination

    def page(self, name):
        self.uia.call("invoke", name)
        time.sleep(0.3)
        if name in ("Локации", "Locations") and self.meta["fixture"]["maximum_history"]:
            self.scroll_rail(False)
        anchor = {"Состояние": "1 ч", "Локации": "1 ч", "Параметры": "Интервал доступности",
                  "Status": "1 h", "Locations": "1 h", "Settings": "Availability interval"}.get(name)
        if anchor:
            for _ in range(25):
                try:
                    r = self.uia.call("rect", anchor)
                    a, b, c, d = self.content_bounds()
                    if a <= r[0] < r[2] <= c and b <= r[1] < r[3] <= d:
                        break
                except RuntimeError:
                    pass
                self.api.wheel(self.uia.hwnd, self.content_point(), 3840)
                time.sleep(0.12)
            else:
                raise RuntimeError("Не удалось вернуть страницу к её верхним элементам")

    def period(self, hours, label):
        self.visible_control(label)
        self.uia.call("invoke", label)
        wait_for(lambda: self.setting("locationChartWindowHours") == hours, description=f"период {label} принят и сохранён")
        time.sleep(0.16)

    def inventory(self, name):
        controls = self.uia.call("inventory")
        (self.output/f"controls-{name}.json").write_text(json.dumps(controls, ensure_ascii=False, indent=2), encoding="utf-8")
        return controls

    def content_point(self):
        a, b, c, d = self.api.client_rect(self.uia.hwnd)
        return int(a+(c-a)*0.72), int(b+(d-b)*0.73)

    def content_bounds(self):
        a,b,c,d=self.api.client_rect(self.uia.hwnd)
        nav="Состояние" if self.uia.call("exists","Состояние") else "Status"
        check="Проверить сейчас" if self.uia.call("exists","Проверить сейчас") else "Check now"
        nav_bottom=self.uia.call("rect",nav)[3]
        commands_bottom=self.uia.call("rect",check)[3]
        margin=round(8*self.api.user.GetDpiForWindow(self.uia.hwnd)/96.)
        return a,max(b,nav_bottom,commands_bottom)+margin,c,d

    def scroll_rail(self, bottom):
        # GPUI Scrollbar обрабатывает клик rail как абсолютный jump.
        # Только клиентские сообщения собственному HWND, без движения курсора.
        # В режиме Scrolling скрытый rail не принимает клики: сначала реальный
        # небольшой scroll раскрывает его и устанавливает MouseDown hitbox.
        self.api.wheel(self.uia.hwnd, self.content_point(), -120 if bottom else 120)
        time.sleep(0.25)
        a, b, c, d = self.api.client_rect(self.uia.hwnd)
        scale = self.api.user.GetDpiForWindow(self.uia.hwnd)/96.
        button = self.uia.call("rect", "Проверить сейчас" if self.uia.call("exists", "Проверить сейчас") else "Check now")
        point = (int(c-5*scale), int(d-3*scale if bottom else button[3]+17*scale))
        self.api.hover(self.uia.hwnd, point)
        time.sleep(0.15)
        self.api.click(self.uia.hwnd, point)
        time.sleep(0.18)

    def visible_control(self, name, extra_below=0):
        for _ in range(35):
            direction=-480
            try:
                r = self.uia.call("rect", name)
                a, b, c, d = self.content_bounds()
                if rectangle_inside([r[0],r[1],r[2],r[3]+extra_below],(a,b,c,d)):
                    return r
                if r[1] < b:
                    direction=480
            except RuntimeError:
                pass
            self.api.wheel(self.uia.hwnd, self.content_point(), direction)
            time.sleep(0.12)
        raise RuntimeError(f"Не удалось прокрутить до контрола: {name}")

    def resize_fitted(self, width, height):
        hwnd=self.uia.hwnd
        assert self.api.owner(hwnd)==self.process.pid
        target=fitted_outer(self.api.outer(hwnd),self.api.work_area(hwnd),width,height)
        if not self.api.user.SetWindowPos(hwnd,None,*target,0x0014):
            raise ct.WinError(ct.get_last_error())
        wait_for(lambda:self.api.outer(hwnd)==target,description="own window fits actual work area")
        self.meta.setdefault("viewport_operations",[]).append(dict(target=target,work_area=self.api.work_area(hwnd)))
        return target

    def history_end(self):
        from PIL import Image
        self.page("Локации")
        self.period(168, "7 д")
        if self.setting("showLocationDebugSources"):
            self.visible_control(DEBUG)
            self.uia.call("invoke", DEBUG)
            wait_for(lambda: self.setting("showLocationDebugSources") is False)
        top = self.capture("locations-history-top")
        if self.meta["fixture"]["maximum_history"]:
            self.scroll_rail(True)
        previous, settled, probes = None, 0, []
        for batch in range(80):
            self.api.wheel(self.uia.hwnd, self.content_point(), -1440)
            time.sleep(0.12)
            image = self.capture("history-scroll-probe", evidence=False)
            with Image.open(image) as frame:
                crop = frame.crop((int(frame.width*0.28), int(frame.height*0.2), frame.width-30, frame.height-25)).convert("RGB")
                digest = hashlib.sha256(crop.tobytes()).hexdigest()
            probes.append(dict(batch=batch, sha256=digest))
            settled = settled+1 if digest == previous else 0
            previous = digest
            if settled >= 2:
                break
        else:
            raise RuntimeError("За 80 ограниченных wheel-пакетов конец истории не найден")
        (self.output/"history-scroll-probes.json").write_text(json.dumps(probes), encoding="utf-8")
        bottom = self.capture("locations-history-bottom")
        if self.sha(top) == self.sha(bottom):
            raise RuntimeError("История не прокрутилась")
        if self.uia.call("exists", "7 д"):
            r = self.uia.call("rect", "7 д")
            a, b, c, d = self.api.client_rect(self.uia.hwnd)
            if b <= r[1] < r[3] <= d:
                raise RuntimeError("Header периода остался на месте: обнаружен вложенный scroll")
        assert self.uia.call("exists", "Состояние"), "Боковая навигация должна оставаться на месте"
        return dict(input_rows=self.meta["fixture"]["stays"], bottom_stable=True,
                    single_scroll_owner_header_disappeared=True, row_text_visual_review="locations-history-bottom.png")

    def debug_persistence(self):
        self.page("Локации")
        self.period(168, "7 д")
        self.visible_control(DEBUG)
        self.uia.call("invoke", DEBUG)
        wait_for(lambda: self.setting("showLocationDebugSources") is True)
        self.capture("locations-debug-on")
        self.stop()
        self.history_counts()
        self.start()
        self.page("Локации")
        assert self.setting("showLocationDebugSources") is True
        self.capture("locations-debug-restarted-on")
        self.visible_control(DEBUG)
        self.uia.call("invoke", DEBUG)
        wait_for(lambda: self.setting("showLocationDebugSources") is False)
        self.stop()
        self.history_counts()
        self.start()
        self.page("Локации")
        assert self.setting("showLocationDebugSources") is False
        self.capture("locations-debug-restarted-off")
        return dict(enabled_survived_restart=True, disabled_survived_restart=True,
                    history_counts=self.history_counts())

    def dropdown_wheel(self):
        self.page("Параметры")
        tested = []
        mapping = [("Интервал доступности", "availabilityPollingSeconds"), ("Интервал страны", "countryPollingSeconds"),
                   ("Порог трафика", "trafficWarningBytesPerHour"), ("Размер букв", "geoCountryLetterSize"), ("Оформление", "theme")]
        if self.meta["locale_required"]:
            mapping.append(("Язык интерфейса", "language"))
        for label, key in mapping:
            r = self.visible_control(label)
            point = ((r[0]+r[2])//2, (r[1]+r[3])//2)
            before, value = self.setting(key), self.uia.call("value", label)
            self.api.wheel(self.uia.hwnd, point, -120)
            time.sleep(0.5)
            assert before == self.setting(key), f"Первый wheel изменил {key}"
            assert self.uia.call("value", label) == value, f"Первый wheel изменил UI значение: {label}"
            self.api.wheel(self.uia.hwnd, point, 120)
            time.sleep(0.5)
            assert before == self.setting(key), f"Колесо изменило {key}"
            assert self.uia.call("value", label) == value, f"Колесо изменило UI значение: {label}"
            tested.append(label)
        self.capture("settings-dropdown-wheel")
        return tested

    def motion_preference(self, value=None):
        assert self.input_policy, "Motion fixture requires the isolated input policy"
        self.input_policy.assert_identity()
        function=self.api.user.SystemParametersInfoW
        function.argtypes,function.restype=[wt.UINT,wt.UINT,ct.c_void_p,wt.UINT],wt.BOOL
        if value is not None and not function(0x1043,0,ct.c_void_p(int(value)),0):
            raise ct.WinError(ct.get_last_error())
        result=wt.BOOL()
        if not function(0x1042,0,ct.byref(result),0):
            raise ct.WinError(ct.get_last_error())
        if value is not None:
            assert bool(result.value)==value, "Guest motion preference didn't change"
        return bool(result.value)

    def navigation_motion(self, tabs):
        self.input_policy.assert_identity()
        hwnd,pid=self.uia.hwnd,self.process.pid
        scale=self.api.user.GetDpiForWindow(hwnd)/96.
        rectangles={name:next(row["rect"] for row in tabs
                             if row["kind"]=="TabItem" and row["name"]==name and row["visible"])
                    for name in PAGES}
        top=min(r[3] for r in rectangles.values())-round(7*scale)
        bottom=max(r[3] for r in rectangles.values())
        left=min(r[0] for r in rectangles.values())
        right=max(r[2] for r in rectangles.values())
        band=(left,top,right,bottom)
        def sample():
            self.input_policy.assert_identity()
            assert self.api.owner(hwnd)==pid and self.api.user.GetForegroundWindow()==hwnd
            x,y,width,height=self.api.outer(hwnd)
            assert x<=left<right<=x+width and y<=top<bottom<=y+height
            image=frames_source.capture()
            positions=[index%image.width+left for index,pixel in enumerate(image.getdata())
                       if max(abs(pixel[i]-channel) for i,channel in enumerate((76,194,255)))<=10]
            assert len(positions)>=round(10*scale), "The visible navigation indicator disappeared"
            return sum(positions)/len(positions),image
        with NavigationFrames(self.api,self.input_policy,hwnd,pid,band) as frames_source:
            return self.navigation_motion_samples(rectangles,sample)

    def navigation_motion_samples(self,rectangles,sample):
        hwnd,pid=self.uia.hwnd,self.process.pid
        scale=self.api.user.GetDpiForWindow(hwnd)/96.
        def click(name):
            r=rectangles[name]
            self.api.click(hwnd,((r[0]+r[2])//2,(r[1]+r[3])//2))
        def center(name):
            r=rectangles[name]
            return (r[0]+r[2])/2
        self.page("Состояние")
        time.sleep(.3)
        results=[]
        for target in ("О программе","Состояние"):
            initial,_=sample()
            started=time.monotonic()
            click(target)
            frames=[]
            while time.monotonic()-started<.8:
                position,image=sample()
                frames.append((time.monotonic()-started,position,image))
                time.sleep(.012)
            finish=center(target)
            (self.output/f"navigation-motion-{len(results)}-trace.json").write_text(
                json.dumps(dict(initial=initial,target=finish,samples=[dict(seconds=t,x=x) for t,x,_ in frames]),indent=2),encoding="utf-8")
            for index,(_,_,image) in enumerate(frames):
                image.save(self.output/f"navigation-motion-{len(results)}-frame-{index:02}.png")
            evidence=navigation_frame_evidence(initial,finish,[(t,x) for t,x,_ in frames],scale,self.coverage_instrumented)
            for label,index in (("first",0),("middle",len(frames)//4),("last",len(frames)-1)):
                destination=self.output/f"navigation-motion-{len(results)}-{label}.png"
                frames[index][2].save(destination)
            results.append(dict(target=target,**evidence))
        for target in ("О программе","Локации","Параметры"):
            click(target)
            time.sleep(.06)
        time.sleep(.35)
        assert self.uia.call("selected","Параметры"), "Rapid navigation lost the final selection"
        position,_=sample()
        assert abs(position-center("Параметры"))<=3*scale, "Interrupted indicator missed the last target"
        self.page("Состояние")
        time.sleep(.3)
        self.meta["navigation_smoothness"] = "NOT RUN" if self.coverage_instrumented else "PASS"
        return dict(switches=results,rapid_reversal=True,foreground_owned_sandbox_only=True,
                    smoothness=self.meta["navigation_smoothness"])

    def fluent_controls(self):
        assert self.input_policy, "Fluent interaction requires the isolated input policy"
        self.input_policy.assert_identity()
        from pywinauto import clipboard
        pid, hwnd = self.process.pid, self.uia.hwnd
        original = self.api.outer(hwnd)
        _,_,original_width,original_height=original
        scale = self.api.user.GetDpiForWindow(hwnd)/96.
        self.page("Состояние")
        tabs = self.uia.call("inventory")
        heights = {}
        for name in PAGES:
            matches=[r for r in tabs if r["kind"]=="TabItem" and r["name"]==name and r["visible"]]
            assert len(matches)==1, f"Expected one accessible navigation tab: {name}"
            r=matches[0]["rect"]
            height=(r[3]-r[1])/scale
            assert abs(height-32)<=1.1, f"Fluent tab height: {name}: {height}"
            heights[name]=height
        for name in ("Проверить сейчас","Пауза"):
            r=next(r["rect"] for r in tabs if r["kind"]=="Button" and r["name"]==name and r["visible"])
            assert abs((r[3]-r[1])/scale-24)<=1.1, f"Fluent compact button: {name}"
        self.capture("fluent-navigation-metrics")
        motion=self.navigation_motion(tabs)
        self.uia.call("invoke","Состояние")
        for name in (*PAGES[1:],PAGES[0]):
            assert self.api.owner(hwnd)==pid
            self.api.post(hwnd,0x0100,0x27,1)
            self.api.post(hwnd,0x0101,0x27,0xc0000001)
            wait_for(lambda name=name:self.uia.call("selected",name),5,f"arrow selected {name}")
        for key,name in ((0x23,"О программе"),(0x24,"Состояние")):
            self.api.post(hwnd,0x0100,key,1)
            self.api.post(hwnd,0x0101,key,0xc0000001)
            wait_for(lambda name=name:self.uia.call("selected",name),5,f"keyboard selected {name}")
        self.page("О программе")
        version=subprocess.run([str(self.exe),"--version"],capture_output=True,text=True,timeout=5,
                               creationflags=NO_CONSOLE,check=True).stdout.strip()
        assert re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?",version), "CLI version is missing"
        assert self.uia.call("exists",version), "About differs from actual executable version"
        self.uia.call("invoke","Скопировать версию")
        wait_for(lambda:clipboard.GetData()==f"Net Lights {version}",5,"About copied actual version")
        self.uia.call("invoke","Источники и лицензии")
        self.visible_control("Файлы лицензий")
        self.capture("fluent-about-licenses")
        try:
            assert self.api.owner(hwnd)==pid
            wide=self.resize_fitted(round(1000*scale),original_height)
            assert wide[2]/scale>924, "Actual desktop cannot exercise the wide settings rail"
            time.sleep(.5)
            self.page("Параметры")
            for anchor,target in (("Приложение","Язык интерфейса"),("Локация","Размер букв"),("Проверки и трафик","Интервал доступности")):
                candidates=[r for r in self.uia.call("inventory") if r["kind"]=="Button" and r["visible"] and
                            r["name"] in (anchor,anchor+", текущий раздел")]
                assert len(candidates)==1, f"Settings anchor missing: {anchor}"
                r=candidates[0]["rect"]
                self.api.click(hwnd,((r[0]+r[2])//2,(r[1]+r[3])//2))
                def reached():
                    a,b,c,d=self.content_bounds()
                    r=self.uia.call("rect",target)
                    return self.uia.call("exists",anchor+", текущий раздел") and a<=r[0]<r[2]<=c and b<=r[1]<r[3]<=d
                wait_for(reached,5,f"Settings anchor scrolled to {anchor}")
            self.capture("fluent-settings-wide")
            self.api.wheel(hwnd,self.content_point(),-3840)
            wait_for(lambda:self.uia.call("exists","Приложение, текущий раздел"),5,"anchor follows manual bottom scroll")
            narrow=self.resize_fitted(round(680*scale),original_height)
            assert narrow[2]/scale<=924, "Narrow settings fixture did not cross the actual breakpoint"
            time.sleep(.5)
            self.page("Параметры")
            assert not any(r["kind"]=="Button" and ", текущий раздел" in r["name"] for r in self.uia.call("inventory")), "Narrow settings rail should be hidden"
            for label in ("Интервал доступности","Интервал страны","Порог трафика","Размер букв","Оформление","Язык интерфейса"):
                self.visible_control(label)
                a,b,c,d=self.content_bounds()
                r=self.uia.call("rect",label)
                assert a<=r[0]<r[2]<=c and b<=r[1]<r[3]<=d, f"Narrow setting clipped: {label}"
            self.capture("fluent-settings-narrow")
        finally:
            assert self.api.owner(hwnd)==pid
            if not self.api.user.SetWindowPos(hwnd,None,*original,0x14):
                raise ct.WinError(ct.get_last_error())
            time.sleep(.5)
            wait_for(lambda:self.api.outer(hwnd)==original,5,"Fluent test restored exact owned window geometry")
            wait_for(lambda:self.setting("windowWidth")==original_width and self.setting("windowHeight")==original_height,5,"Restored window dimensions persisted")
        self.page("Состояние")
        assert self.process.pid==pid and self.process.poll() is None
        return dict(tabs=heights,compact_buttons=24,arrow_home_end=True,about_version=version,clipboard_in_guest=True,settings_anchors=True,narrow=True,navigation_motion=motion)

    def overview_interactions(self):
        from PIL import Image
        self.page("Состояние")
        original = self.setting("locationChartWindowHours")
        self.period(1, "1 ч")
        assert self.input_policy, "Overview interaction requires the isolated real-input policy"
        initial_outer=self.api.outer(self.uia.hwnd)
        safe=(initial_outer[0]+100,initial_outer[1]+100)
        self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,safe)
        self.api.hover(self.uia.hwnd,safe)
        wait_for(lambda:not any(r["kind"]=="Text" and "Провайдер:" in r["name"] and "Мир:" in r["name"] for r in self.uia.call("inventory")),description="подсказка не перекрывает измеряемую полосу")
        def lanes(name):
            scale=self.api.user.GetDpiForWindow(self.uia.hwnd)/96.
            self.visible_control("Провайдер",extra_below=round(300*scale))
            outer=self.api.outer(self.uia.hwnd)
            clear=(outer[0]+100,outer[1]+100)
            self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,clear)
            self.api.hover(self.uia.hwnd,clear)
            time.sleep(.15)
            with Image.open(self.capture(name)).convert("RGB") as picture:
                green = []
                for y in range(120, picture.height-30):
                    xs = [x for x in range(25, picture.width-25) if picture.getpixel((x,y)) == (42,168,122)]
                    if len(xs) > picture.width*.35:
                        green.append((y,min(xs),max(xs)))
                clusters = []
                for item in green:
                    if not clusters or item[0] > clusters[-1][-1][0]+1:
                        clusters.append([])
                    clusters[-1].append(item)
                assert len(clusters) == 2, f"Expected two painted availability lanes: {len(clusters)}"
                row = clusters[0][len(clusters[0])//2]
                palette={(42,168,122),(230,80,96),(233,179,60),(152,165,181),(139,123,193)}
                painted=[x for x in range(25,picture.width-25) if picture.getpixel((x,row[0])) in palette]
                row=(row[0],min(painted),max(painted)+1)
                assert row[2]-row[1]>picture.width*.85, "A popup or clipping obscures the measured lane"
                world_y=clusters[1][len(clusters[1])//2][0]
                red = [x for x in range(row[1],row[2]) if picture.getpixel((x,row[0])) == (230,80,96)
                       and picture.getpixel((x,world_y))==(42,168,122)]
                assert red, "A recorded provider outage with the world online must be painted in the fixture"
                return row,(red[len(red)//2],row[0])
        row,point=lanes("overview-one-hour")
        outer = self.api.outer(self.uia.hwnd)
        target = (outer[0]+point[0],outer[1]+point[1])
        assert self.input_policy, "Overview interaction requires the isolated real-input policy"
        self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,target)
        self.api.hover(self.uia.hwnd,target)
        def readout():
            return next((r["name"] for r in self.uia.call("inventory") if r["kind"] == "Text" and r["visible"] and "Провайдер: Нет ответа" in r["name"] and "Мир: Доступно" in r["name"]), None)
        tooltip = wait_for(readout, description="общая подсказка различает два направления в один момент")
        self.capture("overview-linked-tooltip")
        self.api.click(self.uia.hwnd,target)
        self.visible_control("Выбранное событие")
        self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,(target[0],outer[1]+100))
        history = profile_json_snapshot(self.profile, "availability-history.json")
        local = lambda value: dt.datetime.fromisoformat(value.replace("Z","+00:00")).astimezone().strftime("%d.%m %H:%M:%S")
        expected = next(local(s["startedUtc"])+" – "+local(s["endedUtc"]) for s in history["spans"]
                        if s["group"]=="Ru" and s["state"]=="Offline" and s["endedUtc"]
                        and local(s["startedUtc"])+" – "+local(s["endedUtc"]) in tooltip)
        self.visible_control(expected)
        assert self.uia.call("exists",expected), "Selected event lost its exact saved interval"
        self.visible_control("Диагностика сейчас")
        self.uia.call("invoke","Диагностика сейчас")
        self.visible_control("Текущая диагностика")
        self.capture("overview-current-diagnostics")
        self.visible_control(expected)
        assert self.uia.call("exists",expected), "Current diagnostics changed the historical interval"
        self.capture("overview-selected-outage")
        self.visible_control("Закрыть")
        self.uia.call("invoke","Закрыть")
        zoom_ranges=[]
        now=dt.datetime.now().astimezone()
        def parse_bound(value):
            parsed=dt.datetime.strptime("2024 "+value,"%Y %d.%m %H:%M:%S")
            options=[]
            for offset in (-1,0,1):
                try:
                    options.append(parsed.replace(year=now.year+offset,tzinfo=now.tzinfo))
                except ValueError:
                    pass
            return min(options,key=lambda v:abs((v-now).total_seconds()))
        for a,b in ((.28,.67),(.82,.44)):
            wait_for(lambda:int(time.time())%10<=3,8,"выделение не пересекает обновление живой оси")
            row,point=lanes("overview-drag-forward-ready" if a<b else "overview-drag-reverse-ready")
            outer=self.api.outer(self.uia.hwnd)
            left,right=outer[0]+row[1],outer[0]+row[2]
            y=outer[1]+point[1]
            self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,(outer[0]+100,outer[1]+100))
            self.api.hover(self.uia.hwnd,(outer[0]+100,outer[1]+100))
            axis_names=[r["name"] for r in self.uia.call("inventory") if r["kind"]=="Text" and r["visible"] and re.fullmatch(r"\d{2}\.\d{2} \d{2}:\d{2}:\d{2}",r["name"])]
            assert len(axis_names)==2,f"The common axis must expose both exact recorded bounds: {axis_names}"
            domain_start,domain_end=sorted(map(parse_bound,axis_names))
            assert (domain_end-domain_start).total_seconds()==3600
            start,end = (int(left+(right-left)*a),y),(int(left+(right-left)*b),y)
            self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,start)
            local_point=wt.POINT(*start); self.api.user.ScreenToClient(self.uia.hwnd,ct.byref(local_point))
            self.api.send(self.uia.hwnd,0x0201,1,(local_point.x&0xffff)|((local_point.y&0xffff)<<16))
            for step in range(1,9):
                p=(int(start[0]+(end[0]-start[0])*step/8),y)
                self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,p)
                q=wt.POINT(*p); self.api.user.ScreenToClient(self.uia.hwnd,ct.byref(q))
                self.api.send(self.uia.hwnd,WM_MOUSEMOVE,1,(q.x&0xffff)|((q.y&0xffff)<<16))
            self.api.send(self.uia.hwnd,0x0202,0,(q.x&0xffff)|((q.y&0xffff)<<16))
            wait_for(lambda:self.uia.call("exists","Весь период"), description="настоящее выделение увеличило диапазон")
            assert self.setting("locationChartWindowHours") == 1, "Zoom mustn't modify the persisted period"
            names=[r["name"] for r in self.uia.call("inventory") if r["kind"]=="Text" and r["visible"] and r["name"].startswith("Участок: ")]
            assert len(names)==1, f"Exact zoom bounds missing: {names}"
            bounds=names[0].removeprefix("Участок: ").split(" – ")
            first,last=map(parse_bound,bounds)
            expected_first=domain_start+dt.timedelta(seconds=min(a,b)*3600)
            expected_last=domain_start+dt.timedelta(seconds=max(a,b)*3600)
            assert abs((last-first).total_seconds()-abs(a-b)*3600)<6, f"Wrong zoom duration: {names[0]}"
            assert abs((first-expected_first).total_seconds())<5, f"Wrong zoom start: {names[0]}"
            assert abs((last-expected_last).total_seconds())<5, f"Wrong zoom end: {names[0]}"
            zoom_ranges.append(names[0])
            self.capture("overview-zoom-forward" if a<b else "overview-zoom-reverse")
            self.visible_control("Весь период")
            self.uia.call("invoke","Весь период")
            wait_for(lambda:not self.uia.call("exists","Весь период"), description="вернулась общая временная область")
        self.visible_control("Провайдер")
        controls = self.uia.call("inventory")
        bounds=self.content_bounds()
        toolbar=next(r for r in controls if r["kind"]=="Button" and r["name"]=="Проверить сейчас" and r["visible"] and r["rect"][3]<bounds[1])
        toolbar_id=tuple(toolbar["runtime_id"] or ())
        assert toolbar_id, "Toolbar button has no stable UIA identity"
        expander = next(r for r in controls if r["kind"]=="Button" and r["visible"] and r["name"].endswith("Адреса"))
        self.uia.call("invoke",expander["name"])
        expected_ids=wait_for(lambda:stable_endpoint_inventory(self.uia.call("inventory"),toolbar_id),description="expanded provider exposes exactly seven fixed endpoint identities")
        seen=set()
        for _ in range(35):
            bounds=self.content_bounds()
            controls=self.uia.call("inventory")
            stable_endpoint_inventory(controls,toolbar_id,expected_ids)
            seen.update(visible_endpoint_ids(controls,bounds))
            assert seen<=expected_ids, "An unrelated control was counted as a provider endpoint"
            if seen==expected_ids:
                break
            self.api.wheel(self.uia.hwnd,self.content_point(),-240)
            time.sleep(.15)
        assert seen==expected_ids, f"Not all seven actual endpoint controls were visible: {len(seen)}"
        self.capture("overview-expanded-addresses")
        self.visible_control("Провайдер")
        closer=next(r for r in self.uia.call("inventory") if r["kind"]=="Button" and r["visible"] and "Скрыть адреса" in r["name"])
        self.uia.call("invoke",closer["name"])
        initial=self.api.outer(self.uia.hwnd)
        try:
            narrow=self.resize_fitted(1000,1100)
            self.page("Состояние")
            self.state_heading_counts()
            a,b,c,d=self.api.client_rect(self.uia.hwnd)
            for r in self.uia.call("inventory"):
                if r["visible"] and r["kind"]=="Button" and r["rect"][1]>=b and r["rect"][3]<=d:
                    assert a<=r["rect"][0]<r["rect"][2]<=c, f"Control horizontally clipped: {r['name']}"
            self.capture("overview-narrow")
        finally:
            assert self.api.user.SetWindowPos(self.uia.hwnd,None,*initial,0x0014)
            wait_for(lambda:self.api.outer(self.uia.hwnd)==initial,description="восстановлен размер своего окна")
        self.period(original,next(label for h,label in PERIODS if h==original))
        return dict(common_readout=tooltip, exact_selected_interval=expected, drag_both_directions=True,
                    zoom_preserves_period=True, exact_zoom_ranges=zoom_ranges,current_addresses=7, endpoint_runtime_ids=sorted(seen), narrow_outer=narrow)

    def state_heading_counts(self, english=False):
        names = ("Provider", "World") if english else ("Провайдер", "Мир")
        controls = self.uia.call("inventory")
        counts = {name: sum(row["kind"] == "Text" and row["visible"] and row["name"] == name
                            for row in controls) for name in names}
        assert all(value == 1 for value in counts.values()), f"State headings duplicated or inaccessible: {counts}"
        for hours, label in PERIODS:
            label = (f"{hours} h" if hours <= 24 else f"{hours//24} d") if english else label
            assert sum(row["kind"] == "Button" and row["visible"] and row["name"] == label for row in controls) == 1, f"Period selector duplicated or missing: {label}"
        return counts

    def select_index(self, label, index, key, expected):
        scale=self.api.user.GetDpiForWindow(self.uia.hwnd)/96.
        self.visible_control(label,extra_below=round((index+2)*32*scale))
        self.uia.call("invoke", label)
        def options():
            rows = [row for row in self.uia.call("inventory") if row["kind"] == "ListItem" and row["visible"]]
            return sorted(rows, key=lambda row: (row["rect"][1], row["rect"][0])) if len(rows) > index else None
        rows = wait_for(options, description=f"варианты {label} доступны через UIA")
        rect = rows[index]["rect"]
        a, b, c, d = self.api.client_rect(self.uia.hwnd)
        assert a <= rect[0] < rect[2] <= c and b <= rect[1] < rect[3] <= d
        self.api.click(self.uia.hwnd, ((rect[0]+rect[2])//2, (rect[1]+rect[3])//2))
        wait_for(lambda: self.setting(key) == expected, description=f"{key} принят и сохранён")
        wait_for(lambda: not any(row["kind"] == "ListItem" and row["visible"]
                                for row in self.uia.call("inventory")), description="список выбора закрыт")

    def capture_with_stable_pointer(self,name,target,outer,evidence,input_state):
        shot = self.capture(name)
        after = input_state(target)
        evidence["capture_state"] = after
        if self.input_policy and (tuple(after["cursor"]) != target or
                                  after["foreground_owner"] != self.process.pid or after["outer"] != outer):
            raise RuntimeError("Isolated tooltip capture lost its actual pointer position or window; no cursor retry performed")
        return shot

    def capture_chart_tooltip(self, name, color, minimum_y=150, section=None, section_height=110):
        from PIL import Image
        def cursor():
            position = wt.POINT()
            if not self.api.user.GetCursorPos(ct.byref(position)):
                raise ct.WinError(ct.get_last_error())
            return position.x, position.y

        def bar_point(path):
            with Image.open(path).convert("RGB") as picture:
                lower,upper=(max(0,region[0]),min(picture.height-30,region[1])) if region else (minimum_y,picture.height-30)
                for y in range(lower,upper):
                    xs = [x for x in range(300, picture.width-30) if picture.getpixel((x, y)) == color]
                    if xs:
                        a, b = xs[0], xs[0]+1
                        while b in xs:
                            b += 1
                        x = (a+b-1)//2
                        bottom = y+1
                        while bottom < min(y+30, picture.height) and picture.getpixel((x, bottom)) == color:
                            bottom += 1
                        # One-DIP short-country ticks are legitimate marks too.
                        # Keep the target inside their actual painted extent.
                        return x, y+min(5, (bottom-y-1)//2)
            raise RuntimeError(f"Реальная полоса/линия для {name} не найдена")

        def move(point):
            local = wt.POINT(*point)
            if not self.api.user.ScreenToClient(self.uia.hwnd, ct.byref(local)):
                raise ct.WinError(ct.get_last_error())
            self.api.send(self.uia.hwnd, WM_MOUSEMOVE, 0, (local.x & 0xffff) | ((local.y & 0xffff) << 16))

        def input_state(target):
            foreground = self.api.user.GetForegroundWindow()
            local = wt.POINT(*target)
            if not self.api.user.ScreenToClient(self.uia.hwnd, ct.byref(local)):
                raise ct.WinError(ct.get_last_error())
            return dict(utc=stamp(dt.datetime.now(dt.timezone.utc)), clock_slot=int(time.time())//10,
                        outer=self.api.outer(self.uia.hwnd), client=self.api.client_rect(self.uia.hwnd),
                        target_screen=list(target), target_client=[local.x, local.y], cursor=list(cursor()),
                        dpi=self.api.user.GetDpiForWindow(self.uia.hwnd), foreground_hwnd=int(foreground or 0),
                        foreground_owner=self.api.owner(foreground) if foreground else 0)

        def painted(path):
            with Image.open(path).convert("RGB") as picture:
                return sum(value == (48, 48, 48) for value in picture.getdata())

        evidence = dict(name=name, pid=self.process.pid, physical_input=self.input_policy is not None,
                        own_window_placement=False)
        self.meta.setdefault("tooltip_probes", []).append(evidence)
        region=None
        if section:
            scale=self.api.user.GetDpiForWindow(self.uia.hwnd)/96.
            heading=self.visible_control(section,extra_below=round(section_height*scale))
            outer=self.api.outer(self.uia.hwnd)
            region=(heading[3]-outer[1],heading[3]-outer[1]+round(section_height*scale))
            evidence["section"]=section
            evidence["visible_plot_region"]=list(region)
        if self.input_policy:
            a,b,c,d=self.api.client_rect(self.uia.hwnd)
            clear=(a+min(100,(c-a)//2),b+min(60,(d-b)//2))
            self.input_policy.move(self.api,self.uia.hwnd,self.process.pid,clear)
            move(clear)
            time.sleep(.15)
        point = bar_point(self.capture(name+"-before", evidence=False))
        outer = self.api.outer(self.uia.hwnd)
        target = outer[0]+point[0], outer[1]+point[1]
        evidence.update(png_pixel=list(point), before=input_state(target))
        if self.input_policy:
            # Real input is limited to a verified disposable Sandbox/CI desktop.
            # TrackMouseEvent would reject a fictitious hover outside HWND, and
            # repaint can otherwise dispatch the real cursor's different point.
            self.input_policy.move(self.api, self.uia.hwnd, self.process.pid, target)
        move((target[0]+1, target[1]))
        time.sleep(.08)
        move(target)
        time.sleep(.35)
        if self.input_policy and cursor() != target:
            raise RuntimeError("Isolated tooltip probe lost its actual pointer position")
        if self.input_policy:
            first = self.capture(name+"-setcursorpos")
            evidence["setcursorpos"] = dict(popover_pixels=painted(first), after=input_state(target))
            evidence["sendinput"] = self.input_policy.send_mouse_path(self.api, self.uia.hwnd, self.process.pid, target)
            time.sleep(.35)
        shot = self.capture_with_stable_pointer(name,target,outer,evidence,input_state)
        pixels = painted(shot)
        evidence["final"] = dict(popover_pixels=pixels, after=input_state(target))
        assert pixels > 1000, "Tooltip wasn't painted"
        return point

    def locale_persistence(self):
        watched = ("availabilityPollingSeconds", "countryPollingSeconds", "theme", "trafficWarningBytesPerHour",
                   "geoCountryLetterSize", "geoCountryDetectionEnabled", "geoCountryIconEnabled",
                   "emphasizeShortStatuses", "showLocationDebugSources", "autoStart", "autoUpdateEnabled")
        before = {key: self.setting(key) for key in watched}
        original_period = self.setting("locationChartWindowHours")
        pid = self.process.pid
        self.page("Параметры")
        self.select_index("Язык интерфейса", 2, "language", "en")
        wait_for(lambda: self.uia.call("exists", "Status"), description="English включён без restart")
        assert self.process.pid == pid and self.process.poll() is None
        for page in ("Status", "Locations", "Diagnostics", "Settings", "About"):
            self.page(page)
            if page == "Status":
                self.state_heading_counts(True)
            if page in ("Status", "Locations"):
                for hours, _ in PERIODS:
                    label = f"{hours} h" if hours <= 24 else f"{hours//24} d"
                    self.period(hours, label)
                    self.capture(f"en-{page.lower()}-{hours:03d}h")
                    if hours == 1:
                        self.capture_chart_tooltip("en-availability-tooltip" if page == "Status" else "en-country-tooltip",
                                                  (42,168,122) if page == "Status" else (220,0,0),
                                                  section="Provider" if page=="Status" else "Country history",
                                                  section_height=90 if page=="Status" else 110)
                        if page == "Locations":
                            self.capture_chart_tooltip("en-frequency-tooltip", (76,194,255),section="Country change frequency",section_height=210)
                if page == "Locations":
                    self.period(1, "1 h")
                    self.visible_control("Debug: detection sources")
                    self.uia.call("invoke", "Debug: detection sources")
                    wait_for(lambda:self.setting("showLocationDebugSources") is True)
                    for attempt in range(15):
                        try:
                            self.capture_chart_tooltip("en-source-tooltip", (200,120,36),section="Debug: V2Fly")
                            break
                        except RuntimeError as error:
                            if "не найдена" not in str(error):
                                raise
                            self.api.wheel(self.uia.hwnd, self.content_point(), -360)
                            time.sleep(.2)
                    else:
                        raise RuntimeError("English Debug source plot not reached")
                    self.page("Locations")
                    self.visible_control("Debug: detection sources")
                    self.uia.call("invoke", "Debug: detection sources")
                    wait_for(lambda:self.setting("showLocationDebugSources") is False)
            else:
                self.capture("en-"+page.lower())
        self.page("Settings")
        values = {
            "Availability interval": f"{before['availabilityPollingSeconds']} s" if before['availabilityPollingSeconds'] < 60 else f"{before['availabilityPollingSeconds']//60} min",
            "Country interval": f"{before['countryPollingSeconds']} s" if before['countryPollingSeconds'] < 60 else f"{before['countryPollingSeconds']//60} min",
            "Appearance": {"dark":"Dark", "light":"Light", "system":"System default"}[before['theme']],
            "Letter size": ("Small", "Medium", "Large")[before['geoCountryLetterSize']],
            "Traffic threshold": {None:"After baseline", 0:"After baseline", 67108864:"64 MiB per hour", 268435456:"256 MiB per hour", 1073741824:"1 GiB per hour"}[before['trafficWarningBytesPerHour']],
            "Display language": "English",
        }
        for label, value in values.items():
            self.visible_control(label)
            assert self.uia.call("value", label) == value, f"Translated selector {label} lost its value"
        assert {key:self.setting(key) for key in watched} == before
        self.stop()
        self.history_counts()
        self.start()
        assert self.setting("language") == "en"
        wait_for(lambda: self.uia.call("exists", "Status"), description="English пережил restart")
        self.page("Status")
        self.state_heading_counts(True)
        self.capture("en-state-restarted")
        self.page("Settings")
        self.select_index("Display language", 0, "language", "system")
        english_system = self.api.kernel.GetUserDefaultUILanguage() & 0x03ff != 0x0019
        wait_for(lambda:self.uia.call("exists", "Status" if english_system else "Состояние"), description="system использует реальный UI language Windows")
        self.select_index("Display language" if english_system else "Язык интерфейса", 1, "language", "ru")
        wait_for(lambda:self.uia.call("exists", "Состояние"), description="Русский восстановлен без restart")
        self.page("Состояние")
        self.period(original_period, next(label for hours,label in PERIODS if hours == original_period))
        self.state_heading_counts()
        assert {key:self.setting(key) for key in watched} == before
        self.capture("ru-state-after-language-cycle")
        self.meta["locale_verified"] = True
        return dict(live_switch_same_pid=True, english_restart=True, system_ui_language_observed=True,
                    unrelated_settings_unchanged=True, selectors=values, history_counts=self.history_counts(),
                    tooltip_screenshots_require_visual_review=True)

    def hide_restore(self):
        self.page("Состояние")
        pid, hwnd = self.process.pid, self.uia.hwnd
        self.api.post(hwnd, WM_CLOSE)
        wait_for(lambda: not self.api.user.IsWindowVisible(hwnd), description="X скрыл настоящий HWND")
        assert self.process.poll() is None and self.api.user.IsWindow(hwnd)
        time.sleep(1.2)
        helper = subprocess.run([str(self.exe), "--profile-directory", str(self.profile), "--show-local", "--skip-automatic-updates"], timeout=10, capture_output=True, check=False, creationflags=NO_CONSOLE)
        assert helper.returncode == 0
        wait_for(lambda: self.api.user.IsWindowVisible(hwnd), description="show-local восстановил HWND")
        assert self.process.pid == pid and self.api.owner(hwnd) == pid and self.process.poll() is None
        self.capture("state-restored-before-action")
        self.inventory("state-restored-before-action")
        self.period(3, "3 ч")
        self.capture("state-restored-same-process")
        return dict(pid=pid, hwnd=hwnd, hidden_at_os_level=True, restored=True, fresh_ui_action=True)

    def native_caption(self):
        self.page("Состояние")
        original = self.api.outer(self.uia.hwnd)
        pid, hwnd = self.process.pid, self.uia.hwnd
        hits = []
        shows = []
        self.meta["caption_hit_tests"] = hits

        def hit(point, expected):
            started, attempts, actual, first = time.monotonic(), 0, None, None
            position = (point[0] & 0xffff) | ((point[1] & 0xffff) << 16)

            def frame_ready():
                nonlocal attempts, actual, first
                assert self.process.poll() is None and self.api.owner(hwnd) == pid
                local = wt.POINT(*point)
                if not self.api.user.ScreenToClient(hwnd, ct.byref(local)):
                    raise ct.WinError(ct.get_last_error())
                client_position = (local.x & 0xffff) | ((local.y & 0xffff) << 16)
                # Visible HWND ещё не доказывает готовность первого GPUI-кадра.
                # SDK игнорирует повторный MouseMove без изменения координаты.
                # Моделируется движение на 1px и обратно только внутри своего HWND.
                adjacent_position = ((local.x-1) & 0xffff) | ((local.y & 0xffff) << 16)
                self.api.send(hwnd, WM_MOUSEMOVE, 0, adjacent_position)
                self.api.send(hwnd, WM_MOUSEMOVE, 0, client_position)
                actual = self.api.send(hwnd, 0x0084, 0, position)
                attempts += 1
                if first is None:
                    first = actual
                return actual == expected

            try:
                wait_for(frame_ready, 8, f"первый caption-кадр с WM_NCHITTEST={expected}")
            finally:
                hits.append(dict(expected=expected, actual=actual, first_hit=first, point=point,
                                 attempt_count=attempts, wait_milliseconds=(time.monotonic()-started)*1000,
                                 since_show_milliseconds=(time.monotonic()-shows[-1]["started"])*1000 if shows else None))
            assert actual == expected, f"Caption hit-test {actual} вместо {expected}"
            return position

        def click_caption(expected, index):
            wait_for(lambda: not any(self.api.user.GetAsyncKeyState(key) & 0x8000
                                    for key in (0x10, 0x11, 0x12, 0x5B, 0x5C)), 3,
                     "нет пользовательских модификаторов перед caption-проверкой")
            a, b, c, d = self.api.client_rect(hwnd)
            scale = self.api.user.GetDpiForWindow(hwnd)/96.
            # Официальный TitleBar: 34logical на каждую caption-кнопку.
            point = (int(c-(index+0.5)*34*scale), int(b+20*scale))
            position = hit(point, expected)
            self.api.post(hwnd, 0x00A1, expected, position)  # WM_NCLBUTTONDOWN.
            self.api.post(hwnd, 0x00A2, expected, position)  # WM_NCLBUTTONUP.

        def show_local():
            started = time.monotonic()
            helper = subprocess.run([str(self.exe), "--profile-directory", str(self.profile),
                "--show-local", "--skip-automatic-updates"], timeout=10, capture_output=True,
                check=False, creationflags=NO_CONSOLE)
            assert helper.returncode == 0
            wait_for(lambda: self.api.user.IsWindowVisible(hwnd) and not self.api.user.IsIconic(hwnd),
                     description="show-local восстановил то же caption-окно")
            visible_milliseconds = (time.monotonic()-started)*1000
            wait_for(lambda: self.api.outer(hwnd) == original,
                     description="show-local восстановил физическую геометрию до caption hit-test")
            assert self.process.poll() is None and self.process.pid == pid and self.api.owner(hwnd) == pid
            shows.append(dict(started=started, visible_milliseconds=visible_milliseconds,
                              geometry_ready_milliseconds=(time.monotonic()-started)*1000))

        a, b, c, d = self.api.client_rect(hwnd)
        scale = self.api.user.GetDpiForWindow(hwnd)/96.
        hit((int((a+c)/2), int(b+20*scale)), 2)  # HTCAPTION, без физического drag.
        click_caption(9, 1)  # HTMAXBUTTON.
        wait_for(lambda: self.api.user.IsZoomed(hwnd), description="caption Maximize увеличил собственное окно")
        click_caption(9, 1)
        wait_for(lambda: not self.api.user.IsZoomed(hwnd), description="caption Restore вернул обычное окно")
        wait_for(lambda: self.api.outer(hwnd) == original, description="Restore сохранил физическую геометрию")
        click_caption(8, 2)  # HTMINBUTTON.
        wait_for(lambda: self.api.user.IsIconic(hwnd), description="caption Minimize свернул собственное окно")
        show_local()
        click_caption(20, 0)  # HTCLOSE.
        wait_for(lambda: not self.api.user.IsWindowVisible(hwnd), description="caption Close скрыл окно в трей")
        show_local()
        assert self.api.outer(hwnd) == original
        a, b, c, d = self.api.client_rect(hwnd)
        scale = self.api.user.GetDpiForWindow(hwnd)/96.
        hit((int(c-0.5*34*scale), int(b+20*scale)), 20)
        self.page("Локации")
        self.page("Состояние")
        self.capture("caption-controls-restored")
        return dict(pid=pid, hit_tests=hits, maximize_restore=True, minimize_restore=True,
                    close_hide_restore=True, outer_rect_restored=original,
                    restore_visible_milliseconds=[show["visible_milliseconds"] for show in shows],
                    restore_geometry_ready_milliseconds=[show["geometry_ready_milliseconds"] for show in shows],
                    history_counts=self.history_counts(), physical_drag_performed=False,
                    snap_popup_not_tested=True)

    def geometry(self):
        initial = self.api.outer(self.uia.hwnd)
        work = self.api.work_area(self.uia.hwnd)
        expected = [min(1450, work[2]), min(1300, work[3])]
        assert initial[2:] == expected, f"Неверный стартовый физический размер: {initial[2:]} вместо {expected}"
        scale=self.api.user.GetDpiForWindow(self.uia.hwnd)/96.
        custom=distinct_custom_size(expected,work,scale)
        if not self.api.user.SetWindowPos(self.uia.hwnd, None, initial[0], initial[1], *custom, 0x0014):
            raise ct.WinError(ct.get_last_error())
        wait_for(lambda: self.api.outer(self.uia.hwnd)[2:] == custom,
                 description="собственный HWND принял другой физический размер")
        wait_for(lambda: [self.setting("windowWidth"), self.setting("windowHeight")] == custom,
                 description="пользовательская физическая геометрия сохранена")
        self.page("Параметры")
        self.visible_control("Сбросить размер окна")
        self.uia.call("invoke", "Сбросить размер окна")
        wait_for(lambda: self.api.outer(self.uia.hwnd)[2:] == expected,
                 description="UIA reset применил точный утверждённый outer size")
        wait_for(lambda: [self.setting("windowWidth"), self.setting("windowHeight")] == expected,
                 description="reset сохранил физическую геометрию")
        reset = self.api.outer(self.uia.hwnd)
        self.capture("settings-window-reset")
        self.stop()
        self.start()
        restored = self.api.outer(self.uia.hwnd)
        assert restored == reset, f"Перезапуск изменил физическую геометрию: {reset} -> {restored}"
        return dict(initial_outer=initial, custom_size=custom, reset_outer=reset, restarted_outer=restored,
                    work_area=work, dpi=self.api.user.GetDpiForWindow(self.uia.hwnd), physical_pixels=True)

    def measure(self):
        start, cycle, sample_start = time.monotonic(), 0, self.resources.samples
        self.meta["resource_target_pid"] = self.process.pid
        self.meta["resource_started_utc"] = stamp(dt.datetime.now(dt.timezone.utc))
        visited = set()
        expected = {(page, hours) for page in PAGES[:2] for hours, _ in PERIODS}
        while time.monotonic()-start < self.measure_seconds:
            mode = "geo-enabled" if self.country_enabled else "geo-disabled"
            page_index = cycle%len(PAGES)
            page = PAGES[page_index]
            self.resources.phase = f"resource-{mode}-{page}"
            self.page(page)
            if page_index < 2:
                hours, label = PERIODS[(cycle//len(PAGES))%len(PERIODS)]
                self.period(hours, label)
                visited.add((page, hours))
                self.meta["resource_page_period_coverage"] = [dict(page=page, hours=hours)
                    for page, hours in sorted(visited)]
                self.meta["resource_all_periods_on_both_pages"] = visited == expected
            remaining = min(30., self.measure_seconds-(time.monotonic()-start))
            print(f"RESOURCE elapsed={int(time.monotonic()-start)}s pid={self.process.pid}", flush=True)
            until = time.monotonic()+max(0., remaining)
            while time.monotonic() < until:
                if self.process.poll() is not None:
                    raise RuntimeError("Процесс завершился во время ресурсного прогона")
                time.sleep(max(0., min(1., until-time.monotonic())))
            cycle += 1
        self.meta["resource_ended_utc"] = stamp(dt.datetime.now(dt.timezone.utc))
        self.resources.phase = "resource-complete"
        if self.measure_seconds >= 1800 and visited != expected:
            raise RuntimeError("Длительный ресурсный прогон не прошёл все восемь периодов обеих вкладок, включая 7 д")
        self.page("Диагностика")
        self.visible_control("Сохранить диагностику")
        self.capture("traffic-after-resource-run", private=self.country_enabled)
        return dict(pid=self.process.pid, elapsed_seconds=time.monotonic()-start,
                    samples=self.resources.samples-sample_start,
                    traffic_baseline_evidence="measurement-private/traffic-after-resource-run.png" if self.country_enabled else "traffic-after-resource-run.png",
                    live_country_detection=self.country_enabled)

    def prepare_country_measurement(self):
        self.resources.phase = "geo-loading"
        self.meta["synthetic_screenshots_frozen_before_live_country"] = True
        self.meta["fixture_history_before_measurement"] = self.history_counts()
        self.page("Параметры")
        self.visible_control("Определять страну")
        enabled_after = dt.datetime.now(dt.timezone.utc)
        if not self.setting("geoCountryDetectionEnabled"):
            self.uia.call("invoke", "Определять страну")
        wait_for(lambda: self.setting("geoCountryDetectionEnabled") is True,
                 description="определение страны включено только в изолированном профиле")
        self.country_enabled = True
        started, last_print = time.monotonic(), -30.
        while time.monotonic()-started < 300:
            if self.process.poll() is not None:
                raise RuntimeError("Процесс завершился при загрузке геобаз")
            state = country_readiness(self.profile, enabled_after)
            if all(state[key] for key in ("v2fly", "iplocate", "public_lookup", "both_versions_recorded")):
                self.meta["country_measurement_readiness"] = state
                self.meta["live_ip_data_only_in_ignored_profile"] = True
                return True
            elapsed = time.monotonic()-started
            if elapsed-last_print >= 30:
                print(f"GEO-WARMUP elapsed={int(elapsed)}s v2fly={state['v2fly']} iplocate={state['iplocate']} public_lookup={state['public_lookup']}", flush=True)
                last_print = elapsed
            time.sleep(0.5)
        self.meta["country_measurement_readiness"] = country_readiness(self.profile, enabled_after)
        self.measurement_blocked = True
        self.results.append(dict(check="geo-measurement-preparation", result="NOT RUN",
                                 reason="За 300с не подтверждена готовность обеих баз и нового публичного IP-опроса; IP не выводится"))
        self.results.append(dict(check="resource-measurement", result="NOT RUN", reason="Геобазы/онлайн-наблюдение не готовы; неполный режим не подменяет запрошенный ресурсный прогон"))
        return False

    def default_debug_off(self):
        assert self.setting("showLocationDebugSources") in (None, False), "Debug должен быть выключен"

    def run(self, inventory_only=False):
        original_motion=None
        try:
            if not inventory_only and self.input_policy:
                original_motion=self.motion_preference()
                self.meta["motion_fixture"]=dict(original=original_motion,enabled=True,restored=False)
                self.motion_preference(True)
            self.start()
            self.inventory("initial-state")
            self.capture("state-initial")
            if inventory_only:
                return
            self.check("physical-outer-geometry-reset-and-restart", self.geometry)
            self.check("native-caption-controls-and-hittest", self.native_caption)
            self.check("fluent-tabs-about-and-settings-navigation",self.fluent_controls)
            if self.meta["locale_required"]:
                self.check("state-no-duplicate-provider-world", self.state_heading_counts)
            if not self.meta["fixture"]["maximum_history"]:
                self.check("overview-linked-selection-zoom-and-addresses",self.overview_interactions)
            self.check("default-debug-off", self.default_debug_off)
            for page in PAGES[:2]:
                self.page(page)
                for hours, label in PERIODS:
                    def scenario(hours=hours, label=label, page=page):
                        self.period(hours, label)
                        self.capture(f"{'state' if page == PAGES[0] else 'locations'}-{hours:03d}h")
                    self.check(f"{page}-{hours}h", scenario)
            for page in PAGES[2:]:
                self.page(page)
                slug = {"Диагностика":"diagnostics","Параметры":"settings","О программе":"about"}[page]
                self.capture(slug)
                self.inventory(slug)
            self.check("dropdown-wheel-does-not-change-settings", self.dropdown_wheel)
            self.check("X-hide-and-show-local-same-PID", self.hide_restore)
            self.check("Debug-on-and-off-survive-restart", self.debug_persistence)
            self.check("history-end-and-single-scroll-owner", self.history_end)
            if self.meta["locale_required"]:
                self.check("locale-English-restart-system-Russian", self.locale_persistence)
            if self.measure_seconds:
                ready = self.prepare_country_measurement() if self.enable_country else True
                if ready:
                    self.check("resource-measurement", self.measure)
            else:
                self.results.append(dict(check="resource-measurement", result="NOT RUN", reason="measure-seconds=0; короткий CSV не является длительным soak"))
        finally:
            original_error = sys.exc_info()[1]
            cleanup_errors = []
            if not any(row["check"] == "resource-measurement" for row in self.results):
                self.results.append(dict(check="resource-measurement", result="NOT RUN",
                                         reason="Прогон остановился до ресурсной фазы" if original_error else "Ресурсная фаза не запрашивалась"))
            if original_error and not any(row["result"] == "FAIL" for row in self.results):
                self.results.append(dict(check="runner", result="FAIL", error=str(original_error)))
            try:
                self.stop()
            except BaseException as error:
                cleanup_errors.append(str(error))
            if original_motion is not None:
                try:
                    self.motion_preference(original_motion)
                    self.meta["motion_fixture"]["restored"]=True
                except BaseException as error:
                    cleanup_errors.append("Guest motion restore: "+str(error))
            try:
                if self.uia:
                    try:
                        self.meta["interactions"] = self.uia.call("interactions")
                    except (RuntimeError, TimeoutError, queue.Full):
                        self.meta["interactions_unavailable"] = True
                    self.uia.close()
                self.resources.close()
            except BaseException as error:
                cleanup_errors.append(str(error))
            if cleanup_errors or self.resources.errors:
                self.results.append(dict(check="cleanup-and-resource-counters", result="FAIL", errors=cleanup_errors+self.resources.errors))
            if self.country_enabled and self.meta.get("resource_ended_utc") and not original_error:
                try:
                    self.meta["resource_normalization"] = normalize_resources(self.output/"resources.csv", self.output/"resource-measurement.csv",
                        self.meta["resource_target_pid"], dt.datetime.fromisoformat(self.meta["resource_started_utc"].replace("Z", "+00:00")),
                        dt.datetime.fromisoformat(self.meta["resource_ended_utc"].replace("Z", "+00:00")), self.measure_seconds)
                except (OSError, ValueError, KeyError) as error:
                    cleanup_errors.append(str(error))
                    self.results.append(dict(check="resource-normalization", result="FAIL", error=str(error)))
            data = dict(metadata=self.meta, checks=self.results, screenshots=self.captures,
                        measurement_private_screenshots=self.private_captures,
                        resource_samples=self.resources.samples, resource_errors=self.resources.errors,
                        cleanup_errors=cleanup_errors)
            (self.output/"results.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
            if original_error is None and (cleanup_errors or self.resources.errors):
                raise RuntimeError("Ошибки cleanup/counters записаны в results.json")


def uia_retry_selftest():
    """No COM, windows or physical input; exercise the actual query policy."""
    from unittest.mock import patch

    def run(command, results, expect, calls, timeout=8, clock=None):
        owner = Automation.__new__(Automation)
        owner.interactions = []
        sent = []

        class Requests:
            def put_nowait(self, request):
                sent.append(request[0])
                request[2].put_nowait(results[len(sent)-1])

        owner.requests = Requests()
        try:
            if clock is None:
                result = owner.call(command, timeout=timeout)
            else:
                with patch.object(time, "monotonic", side_effect=clock):
                    result = owner.call(command, timeout=timeout)
        except expect if isinstance(expect, type) else ():
            pass
        else:
            assert result == expect
        assert len(sent) == calls, (command, len(sent), calls)

    removed = RuntimeError("owned descendant removed")
    removed.uia_hresult = 0x80040201
    for command in ("inventory", "exists", "rect", "value", "selected"):
        run(command, [removed, "fresh"], "fresh", 2)
    run("inventory", [removed]*3, RuntimeError, 3)
    run("invoke", [removed], RuntimeError, 1)
    run("inventory", [RuntimeError("unrelated failure")], RuntimeError, 1)
    run("inventory", [removed], TimeoutError, 1, timeout=1, clock=[10, 10, 11.01])
    pointer_capture_selftest()
    adaptive_viewport_selftest()
    profile_snapshot_selftest()
    navigation_motion_selftest()
    policy=IsolatedInput.__new__(IsolatedInput)
    policy.ci=False
    with patch.object(policy,"assert_identity"):
        for operation in (policy.move,policy.send_mouse_path):
            try:
                operation(None,0,0,(0,0))
            except RuntimeError as error:
                assert str(error).startswith("BLOCKED: Windows Sandbox RDP")
            else:
                raise AssertionError("Sandbox pointer injection reached an OS API")
    policy.ci=True
    with patch.object(policy,"assert_identity"):
        policy.assert_pointer_input_allowed()
    with patch.object(policy,"assert_identity",side_effect=ValueError("untrusted identity")):
        try:
            policy.assert_pointer_input_allowed()
        except ValueError:
            pass
        else:
            raise AssertionError("CI pointer policy skipped actual identity verification")
    print("RUN - PASS: bounded read-only UIA retry and four failure controls; no COM or input")


def adaptive_viewport_selftest():
    from types import SimpleNamespace
    from unittest.mock import patch
    bounds=(0,100,1000,900)
    assert rectangle_inside([10,110,100,140],bounds)
    for rect in ([-1,110,100,140],[10,99,100,140],[10,110,1001,140],[10,110,100,901]):
        assert not rectangle_inside(rect,bounds)
    assert fitted_outer([-1500,200,1450,1300],[-1600,0,1600,1160],1000,1100)==[-1500,60,1000,1100]
    assert fitted_outer([100,100,1450,1300],[0,0,1600,1160],1450,1300)==[100,0,1450,1160]
    assert distinct_custom_size([1024,728],[0,0,1024,728],1.)==[954,658]
    try:
        distinct_custom_size([640,480],[0,0,640,480],1.)
    except ValueError:
        pass
    else:
        raise AssertionError("Saved-size test silently reused the reset size")
    owner=Runner.__new__(Runner)
    position=[1100]
    owner.uia=SimpleNamespace(hwnd=1,call=lambda *_:[10,position[0],100,position[0]+32])
    def wheel(*args):
        position[0]+=120 if args[-1]>0 else -120
    owner.api=SimpleNamespace(wheel=wheel)
    owner.content_bounds=lambda:bounds
    owner.content_point=lambda:(500,500)
    with patch.object(time,"sleep"):
        result=owner.visible_control("owned",extra_below=210)
    assert rectangle_inside([*result[:3],result[3]+210],bounds)
    owner.uia.call=lambda *_:[10,110,1001,140]
    with patch.object(time,"sleep"):
        try:
            owner.visible_control("too-wide")
        except RuntimeError:
            pass
        else:
            raise AssertionError("Horizontal clipping was accepted after scrolling")
    def button(identity,rect=(10,110,100,140)):
        return dict(kind="Button",name="Проверить сейчас",visible=True,rect=rect,runtime_id=identity)
    visited=set()
    complete=[button([identity]) for identity in range(1,8)]+[button([99],(10,10,100,40))]
    expected=stable_endpoint_inventory(complete,(99,))
    for identities in ((1,2,3),(3,4,5),(5,6,7)):
        visited.update(visible_endpoint_ids([button([identity]) for identity in identities]+[button([99],(10,10,100,40))],bounds))
    assert visited==expected
    toolbar=button([99],(10,10,100,40))
    for churn in ([button([41]),button([42]),button([43]),toolbar],
                  [button([identity+10]) for identity in range(1,8)]+[toolbar],
                  complete+[button([1])],
                  [button([1]),button([1])]+[button([identity]) for identity in range(2,7)]+[toolbar]):
        try:
            stable_endpoint_inventory(churn,(99,),expected)
        except RuntimeError:
            pass
        else:
            raise AssertionError("Recreated controls were counted as previously unvisited endpoints")
    assert not visible_endpoint_ids([button([8],(10,890,100,920))],bounds)
    try:
        visible_endpoint_ids([button([])],bounds)
    except RuntimeError:
        pass
    else:
        raise AssertionError("Endpoint identity was replaced by its duplicated label")
    print("RUN - PASS: adaptive rectangles, distinct geometry, bounded scrolling and seven independent endpoint identities; no OS calls")


def profile_snapshot_selftest():
    from unittest.mock import patch

    class FileApi:
        def __init__(self, data=b'{"language":"ru"}', fault=None, declared=None, chunk=3):
            self.data, self.fault, self.declared, self.chunk = data, fault, declared, chunk
            self.offset, self.calls = 0, []

        def CreateFileW(self, *arguments):
            self.calls.append(("open", arguments))
            return ct.c_void_p(-1).value if self.fault == "open" else 77

        def GetFileSizeEx(self, handle, size):
            assert handle == 77
            self.calls.append(("size", handle))
            size._obj.value = len(self.data) if self.declared is None else self.declared
            return self.fault != "size"

        def ReadFile(self, handle, buffer, requested, count, overlapped):
            assert handle == 77 and overlapped is None
            self.calls.append(("read", requested))
            if self.fault == "read":
                return False
            part = self.data[self.offset:self.offset+min(requested, self.chunk)]
            self.offset += len(part)
            buffer[:len(part)] = part
            count._obj.value = requested+1 if self.fault == "count" else len(part)
            return True

        def CloseHandle(self, handle):
            assert handle == 77
            self.calls.append(("close", handle))
            return self.fault != "close"

    profile = pathlib.PureWindowsPath("C:/owned/profile")
    api = FileApi(b'\xef\xbb\xbf{"language":"ru","title":"\xd1\x81\xd1\x82\xd0\xb0\xd1\x82\xd1\x83\xd1\x81"}')
    assert profile_json_snapshot(profile, "settings.json", api) == {"language":"ru", "title":"статус"}
    opened = api.calls[0][1]
    assert opened == (str(profile/"settings.json"), 0x80000000, 0x7, None, 3, 0x80, None)
    assert sum(call[0] == "read" for call in api.calls) > 1
    assert [call[0] for call in api.calls].count("close") == 1
    assert api.calls[-1][0] == "close"
    for fault in ("open", "size", "read", "close", "count"):
        api = FileApi(fault=fault)
        with patch.object(ct, "get_last_error", return_value=32, create=True):
            try:
                profile_json_snapshot(profile, "settings.json", api)
            except OSError as error:
                if os.name == "nt":
                    assert error.winerror == 32, "The observer lost the native file error code"
            except RuntimeError:
                assert fault == "count"
            else:
                raise AssertionError("The snapshot reader hid a native file failure")
        assert [call[0] for call in api.calls].count("close") == (0 if fault == "open" else 1)
    for api in (FileApi(declared=1024*1024+1), FileApi(b"x"*4097, declared=1, chunk=4097),
                FileApi(b"\xff"), FileApi(b"not JSON")):
        relative = "GeoIP/current.json" if len(api.data) == 4097 else "settings.json"
        try:
            profile_json_snapshot(profile, relative, api)
        except ValueError:
            pass
        else:
            raise AssertionError("The snapshot reader accepted oversize or invalid JSON")
        assert [call[0] for call in api.calls].count("close") == 1
    for root, relative in ((profile, "../settings.json"), (profile, "unrelated.json"),
                           (pathlib.PureWindowsPath("C:/live/NetLights"), "settings.json"),
                           (pathlib.PureWindowsPath("//offline.invalid/share/profile"), "settings.json")):
        api = FileApi()
        try:
            profile_json_snapshot(root, relative, api)
        except ValueError:
            pass
        else:
            raise AssertionError("The snapshot reader accepted an unrelated profile path")
        assert not api.calls
    print("RUN - PASS: bounded profile snapshots share read/write/delete, reject invalid paths and close handles on every outcome; no OS calls")


def navigation_motion_selftest():
    smooth=[(0.,10.),(.05,30.),(.1,60.),(.15,90.),(.25,110.)]
    sparse=[(0.,10.),(.312,37.),(.437,87.),(.531,110.)]
    late=[(0.,10.),(.51,30.),(.56,60.),(.61,90.),(.7,110.)]
    assert navigation_frame_evidence(10,110,smooth,1)["smoothness"]=="PASS"
    assert navigation_frame_evidence(10,110,sparse,1,True)["smoothness"]=="NOT RUN"
    assert navigation_frame_evidence(10,110,late,1,True)["smoothness"]=="NOT RUN"
    for frames in (sparse,late,[(0,10),(.1,110)]):
        try:
            navigation_frame_evidence(10,110,frames,1)
        except AssertionError:
            pass
        else:
            raise AssertionError("The ordinary executable lost its strict motion gate")
    for instrumented in (False,True):
        for frames in ([],[(0,10),(.2,60)],[(0,10),(.2,10)],[(.2,30),(.1,110)],[(0,float("nan")),(.2,110)]):
            try:
                navigation_frame_evidence(10,110,frames,1,instrumented)
            except AssertionError:
                pass
            else:
                raise AssertionError("Instrumented mode bypassed functional navigation evidence")
    print("RUN - PASS: ordinary motion thresholds and instrumented functional evidence stay separate; no OS calls")


def pointer_capture_selftest():
    from types import SimpleNamespace
    from unittest.mock import patch
    target,outer=(10,20),[0,0,100,100]
    good=dict(cursor=target,foreground_owner=17,outer=outer)
    displaced=dict(good,cursor=(11,20))
    def scenario(states,success,captures,restores,capture_error=False):
        class Shot:
            def __init__(self,path):
                self.path=pathlib.PurePosixPath(path)
        owner=Runner.__new__(Runner)
        owner.output=pathlib.PurePosixPath("/owned")
        owner.process=SimpleNamespace(pid=17)
        owner.uia=SimpleNamespace(hwnd=23)
        owner.api=object()
        owner.captures=[]
        sends=[]
        def send(api,hwnd,pid,point):
            assert api is owner.api and hwnd==23 and pid==17 and point==target
            sends.append(point)
            return dict(restored=True)
        owner.input_policy=SimpleNamespace(send_mouse_path=send)
        calls=[]
        def capture(name):
            calls.append(name)
            if capture_error:
                raise ValueError("Capture failure must not be retried")
            owner.captures.append(dict(file=name+".png"))
            return Shot(owner.output/(name+".png"))
        owner.capture=capture
        evidence={}
        with patch.object(time,"sleep"):
            try:
                shot=owner.capture_with_stable_pointer("probe",target,outer,evidence,lambda _:states.pop(0))
            except (RuntimeError,ValueError):
                assert not success
            else:
                assert success and shot.path==owner.output/"probe.png"
        assert len(calls)==captures and len(sends)==restores
    scenario([good],True,1,0)
    scenario([displaced,good],False,1,0)
    scenario([displaced]*3,False,1,0)
    scenario([dict(good,foreground_owner=18)],False,1,0)
    scenario([dict(good,outer=[1,0,100,100])],False,1,0)
    scenario([],False,1,0,capture_error=True)
    print("RUN - PASS: stable capture, no cursor retries, owner/geometry/capture failure controls; no OS calls")


def seconds(value):
    parsed = int(value)
    if not 0 <= parsed <= 3600:
        raise argparse.ArgumentTypeError("measure-seconds должен быть от 0 до 3600")
    return parsed


def main():
    if not __debug__:
        raise RuntimeError("Проверки требуют включённых assert: уберите -O и PYTHONOPTIMIZE")
    if sys.argv[1:] == ["--self-test-uia"]:
        uia_retry_selftest()
        return
    if sys.argv[1:] == ["--runtime-info"]:
        from importlib.metadata import version
        print(json.dumps(dict(executable=sys.executable, base_executable=getattr(sys, "_base_executable", sys.executable),
                              base_prefix=sys.base_prefix, site_packages=[sysconfig.get_path("purelib")],
                              version=sys.version, dependencies={name:version(name) for name in ("pywinauto", "Pillow")})))
        return
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--inventory-only", action="store_true")
    parser.add_argument("--measure-seconds", default=0, type=seconds, help="0–3600 с; 0 оставляет длительный прогон NOT RUN")
    parser.add_argument("--expected-sha256")
    isolation = parser.add_mutually_exclusive_group()
    isolation.add_argument("--sandbox-input", type=pathlib.Path,
                           help="Реальный ввод только в WDAGUtilityAccount по nonce/SHA marker")
    isolation.add_argument("--isolated-ci-input", action="store_true",
                           help="Реальный ввод только в подтверждённой GitHub-hosted Windows VM")
    parser.add_argument("--sandbox-nonce", help="32 hex-символа, совпадающие с Sandbox marker")
    parser.add_argument("--maximum-history", action="store_true", help="70 000 наблюдений, 70 000 периодов стран и 10 000 интервалов доступности")
    parser.add_argument("--functional-evidence", action="store_true",
                        help="Функциональные native UI-проверки на Windows Server; визуальная приёмка Windows 11 остаётся NOT RUN")
    parser.add_argument("--coverage-instrumented", action="store_true",
                        help="Сборка с покрытием: плавность проверяется отдельно на обычном exe")
    parser.add_argument("--enable-country-for-measurement", action="store_true",
                        help="После synthetic-снимков включить настоящий IP-опрос и обе базы для ресурсного прогона")
    args = parser.parse_args()
    if args.enable_country_for_measurement and (not args.measure_seconds or args.inventory_only):
        parser.error("--enable-country-for-measurement требует --measure-seconds > 0 и полный UI-прогон")
    if args.functional_evidence and (args.measure_seconds or args.enable_country_for_measurement):
        parser.error("--functional-evidence допускает только offline UI-проверки без ресурсного прогона и публичного Geo")
    if args.sandbox_nonce and not args.sandbox_input:
        parser.error("--sandbox-nonce требует --sandbox-input")
    exe = args.exe.resolve(strict=True)
    try:
        input_policy = IsolatedInput(exe, args.sandbox_input, args.sandbox_nonce, args.isolated_ci_input) if args.sandbox_input or args.isolated_ci_input else None
        output = checked_output(args.output)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    output.mkdir(parents=True, exist_ok=False)
    runner = Runner(exe, output, args.measure_seconds, args.expected_sha256, args.enable_country_for_measurement, args.maximum_history, args.functional_evidence, input_policy, args.coverage_instrumented)
    runner.run(args.inventory_only)
    print(json.dumps(dict(result="NOT RUN" if runner.measurement_blocked else "PASS", output=str(output), screenshots=len(runner.captures), resource_seconds=args.measure_seconds), ensure_ascii=False), flush=True)
    if runner.measurement_blocked:
        raise SystemExit(2)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    if sys.argv[1:] not in (["--self-test-uia"], ["--runtime-info"]):
        set_dpi_awareness()
    if len(sys.argv) == 5 and sys.argv[1] == "--capture-hwnd":
        capture_native(int(sys.argv[2]), int(sys.argv[3]), pathlib.Path(sys.argv[4]))
    elif len(sys.argv) == 6 and sys.argv[1] == "--capture-sandbox-desktop":
        capture_sandbox_desktop(pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3]), sys.argv[4], pathlib.Path(sys.argv[5]))
    else:
        main()
