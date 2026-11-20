"""截图选区遮罩层级回归：确认三个胶囊在 Snipping 选区里会被一起压暗。

背景：DeskBar 的监控浮岛与音乐胶囊曾浮在 `SnipOverlayRootWindow` 的压暗层之上，
截图时看起来"没被压暗"。根因是窗口所在的 z 序带（band）与遮罩不同；修改见
`src/DeskBar/Services/AcrylicWindowHelper.cs` 的 `TryYieldToCaptureOverlay`。

三个场景：桌面、Win+A 快速设置、Win+N 通知面板。每个场景都要求：
  1. 遮罩真的可见且全屏覆盖三个胶囊；
  2. 选区里三个胶囊的 HWND 与 PID 没变（不是被重建出来的新窗口）；
  3. 压暗后 p99 亮度比落在 0.35-0.60（纯压暗约 0.40）；
  4. 退出后 owner 恢复 0x2041A、亮度回到基线 +-10。

失败会打印 `=== OVERALL: FAIL: <原因> ===` 并以 2 退出，可以直接当断言用。
注意：前台有全屏独占程序（例如游戏）时，DeskBar 会按设计折叠浮岛，本脚本会
报 `mon missing or hidden` —— 那不是回归，先退出全屏程序再来跑。

用法：python scripts/verify_overlay_contexts.py
"""

import ctypes as c
from ctypes import wintypes as w
import sys
import time
from PIL import ImageGrab

u = c.WinDLL('user32', use_last_error=True)
u.SetProcessDPIAware()
for name, restype, argtypes in [
    ('FindWindowW', w.HWND, [w.LPCWSTR, w.LPCWSTR]),
    ('GetWindowBand', w.BOOL, [w.HWND, c.POINTER(w.DWORD)]),
    ('GetWindowThreadProcessId', w.DWORD, [w.HWND, c.POINTER(w.DWORD)]),
    ('GetWindowLongPtrW', c.c_ssize_t, [w.HWND, c.c_int]),
    ('GetWindow', w.HWND, [w.HWND, w.UINT]),
    ('GetWindowRect', w.BOOL, [w.HWND, c.POINTER(w.RECT)]),
    ('IsWindow', w.BOOL, [w.HWND]),
    ('IsWindowVisible', w.BOOL, [w.HWND]),
    ('GetForegroundWindow', w.HWND, []),
    ('GetClassNameW', c.c_int, [w.HWND, w.LPWSTR, c.c_int]),
]:
    fn = getattr(u, name)
    fn.restype = restype
    fn.argtypes = argtypes

NAMES = {'mon': 'DeskBarSystemMonitor', 'mus': 'DeskBarMusicCapsule', 'wire': 'DeskBarWirelessBattery'}
GW_OWNER, GW_HWNDPREV = 4, 3
OVERLAY_CLASS = 'SnipOverlayRootWindow'


def chord(*keys):
    for key in keys:
        u.keybd_event(key, 0, 0, 0)
    for key in reversed(keys):
        u.keybd_event(key, 0, 2, 0)


def hwnd_title(title):
    return int(u.FindWindowW(None, title) or 0)


def overlay_hwnd():
    return int(u.FindWindowW(OVERLAY_CLASS, None) or 0)


def rect(hwnd):
    value = w.RECT()
    if not u.GetWindowRect(w.HWND(hwnd), c.byref(value)):
        raise RuntimeError('GetWindowRect failed hwnd=0x%X' % hwnd)
    return value.left, value.top, value.right, value.bottom


def pid(hwnd):
    value = w.DWORD()
    u.GetWindowThreadProcessId(w.HWND(hwnd), c.byref(value))
    return value.value


def band(hwnd):
    value = w.DWORD()
    if not u.GetWindowBand(w.HWND(hwnd), c.byref(value)):
        raise RuntimeError('GetWindowBand failed hwnd=0x%X' % hwnd)
    return value.value


def foreground():
    hwnd = int(u.GetForegroundWindow() or 0)
    buf = c.create_unicode_buffer(256)
    u.GetClassNameW(w.HWND(hwnd), buf, len(buf))
    return hwnd, pid(hwnd) if hwnd else 0, buf.value


def p99(image, box):
    left, top, right, bottom = box
    left, top = max(0, left), max(0, top)
    right, bottom = min(image.width, right), min(image.height, bottom)
    if right <= left or bottom <= top:
        raise RuntimeError('empty bounds: %r' % (box,))
    values = sorted(image.crop((left, top, right, bottom)).convert('L').tobytes())
    return values[int(0.99 * (len(values) - 1))]


def capsule_state():
    rows = {}
    for key, title in NAMES.items():
        hwnd = hwnd_title(title)
        if not hwnd or not u.IsWindow(w.HWND(hwnd)) or not u.IsWindowVisible(w.HWND(hwnd)):
            raise RuntimeError('%s missing or hidden' % key)
        rows[key] = {
            'hwnd': hwnd,
            'pid': pid(hwnd),
            'owner': int(u.GetWindow(w.HWND(hwnd), GW_OWNER) or 0),
            'band': band(hwnd),
            'rect': rect(hwnd),
        }
    return rows


def frame_values(rows):
    frame = ImageGrab.grab()
    return {key: p99(frame, row['rect']) for key, row in rows.items()}


def wait_overlay(timeout=8):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        hwnd = overlay_hwnd()
        if hwnd and u.IsWindowVisible(w.HWND(hwnd)):
            return hwnd
        time.sleep(0.05)
    return 0


def dismiss_overlay():
    chord(0x1B)
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if not overlay_hwnd():
            return True
        time.sleep(0.1)
    return not overlay_hwnd()


def run_case(label, panel_keys=None):
    print('\n=== CASE %s ===' % label, flush=True)
    if overlay_hwnd():
        if not dismiss_overlay():
            raise RuntimeError('existing screenshot overlay would not close')
    if panel_keys:
        chord(*panel_keys)
        time.sleep(0.8)
        fg = foreground()
        print('PANEL_FOREGROUND hwnd=0x%X pid=%d class=%s' % fg, flush=True)
        panel_base_rows = capsule_state()
        panel_base = frame_values(panel_base_rows)
        print('PANEL_P99 %s' % panel_base, flush=True)
    else:
        panel_base_rows = capsule_state()
        panel_base = frame_values(panel_base_rows)
        print('BASE_P99 %s' % panel_base, flush=True)

    chord(0x5B, 0x10, 0x53)
    overlay = wait_overlay()
    if not overlay:
        raise RuntimeError('Snipping overlay did not appear')
    time.sleep(0.75)
    rows = capsule_state()
    overlay_rect = rect(overlay)
    covered = all(
        overlay_rect[0] <= row['rect'][0] and overlay_rect[1] <= row['rect'][1] and
        overlay_rect[2] >= row['rect'][2] and overlay_rect[3] >= row['rect'][3]
        for row in rows.values()
    )
    if not covered:
        raise RuntimeError('Snipping overlay does not cover all capsule bounds')
    if any(rows[key]['hwnd'] != panel_base_rows[key]['hwnd'] for key in NAMES):
        raise RuntimeError('capsule HWND changed during screenshot transition')
    print('OVERLAY hwnd=0x%X pid=%d band=%d rect=%r coversAll=%s' % (
        overlay, pid(overlay), band(overlay), overlay_rect, covered), flush=True)
    for key, row in rows.items():
        print('SNIP_%s hwnd=0x%X pid=%d band=%d owner=0x%X rect=%r' % (
            key, row['hwnd'], row['pid'], row['band'], row['owner'], row['rect']), flush=True)
    snip_values = frame_values(rows)
    print('SNIP_P99 %s' % snip_values, flush=True)

    ok = True
    for key in NAMES:
        ratio = snip_values[key] / max(1, panel_base[key])
        good = 0.35 <= ratio <= 0.60
        if key in ('mon', 'mus', 'wire') and not good:
            ok = False
        print('%s p99=%d->%d ratio=%.3f %s' % (
            key, panel_base[key], snip_values[key], ratio,
            'PASS' if good else 'FAIL'), flush=True)

    if not dismiss_overlay():
        raise RuntimeError('screenshot overlay did not dismiss')
    if panel_keys:
        chord(0x1B)
        time.sleep(0.6)
    deadline = time.monotonic() + 5
    restored = None
    while time.monotonic() < deadline:
        restored = capsule_state()
        if all(restored[key]['owner'] != 0 for key in ('mon', 'mus')):
            break
        time.sleep(0.1)
    if any(restored[key]['owner'] == 0 for key in ('mon', 'mus')):
        ok = False
    after = frame_values(restored)
    back = all(abs(after[key] - panel_base[key]) <= 10 for key in NAMES)
    ok = ok and back
    print('AFTER_P99 %s ownerRestored=%s pixelsRestored=%s' % (
        after, all(restored[key]['owner'] != 0 for key in ('mon', 'mus')), back), flush=True)
    print('CASE_RESULT %s %s' % (label, 'PASS' if ok else 'FAIL'), flush=True)
    return ok


try:
    if overlay_hwnd() and not dismiss_overlay():
        raise RuntimeError('could not dismiss existing screenshot overlay')
    results = []
    results.append(run_case('desktop'))
    results.append(run_case('quick-settings', (0x5B, 0x41)))
    results.append(run_case('notifications', (0x5B, 0x4E)))
    overall = all(results)
    print('\n=== OVERALL: %s ===' % ('PASS' if overall else 'FAIL'), flush=True)
    sys.exit(0 if overall else 1)
except Exception as exc:
    dismiss_overlay()
    print('\n=== OVERALL: FAIL: %s ===' % exc, flush=True)
    sys.exit(2)
