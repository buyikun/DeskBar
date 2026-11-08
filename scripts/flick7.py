# -*- coding: utf-8 -*-
"""只测『描边/边缘』是否消失，避开数值文字刷新造成的假阳性。"""
import ctypes, time, sys
from PIL import ImageStat, ImageGrab
u = ctypes.windll.user32
label = sys.argv[1] if len(sys.argv) > 1 else 'mon'
if label == 'mon':
    box = (10, 1392, 244, 1440); edge = (0, 8, 4, 40)
else:
    box = (2084, 1392, 2312, 1440); edge = (0, 8, 4, 40)
series = []
for i in range(50):
    im = ImageGrab.grab(bbox=box).convert('L').crop(edge)
    series.append(ImageStat.Stat(im).mean[0])
    if i == 10:
        u.SetCursorPos(2315, 1424); u.mouse_event(0x0002,0,0,0,0); u.mouse_event(0x0004,0,0,0,0)
    time.sleep(0.03)
base = series[0]
worst = max(range(len(series)), key=lambda i: abs(series[i]-base))
print('%s edge-mean base=%.1f worst=%.1f @frame%d  min=%.1f max=%.1f  spread=%.1f'
      % (label, base, series[worst], worst, min(series), max(series), max(series)-min(series)))
print('  series:', [round(v) for v in series])
