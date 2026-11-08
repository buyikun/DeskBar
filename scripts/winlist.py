import ctypes, ctypes.wintypes as w, sys
u = ctypes.windll.user32
EnumWindows = u.EnumWindows
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
rows = []
def cb(h, l):
    pid = w.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    r = w.RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    vis = bool(u.IsWindowVisible(h))
    ln = u.GetWindowTextLengthW(h)
    buf = ctypes.create_unicode_buffer(ln+2)
    u.GetWindowTextW(h, buf, ln+2)
    cn = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, cn, 256)
    rows.append((pid.value, h, vis, (r.left,r.top,r.right,r.bottom), cn.value, buf.value))
    return True
EnumWindows(CB(cb), 0)
targets = [int(x) for x in sys.argv[1:]] if len(sys.argv)>1 else None
for pid,h,vis,rc,cls,title in rows:
    if targets and pid not in targets: continue
    print(f"pid={pid} hwnd={h} vis={vis} rect={rc} cls={cls} title={title[:40]!r}")
print(f"-- total top-level windows: {len(rows)}")
