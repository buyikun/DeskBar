#!/bin/bash
taskkill //F //IM DeskBar.exe 2>/dev/null; sleep 1
rm -f /d/app/DeskBar/logs/* 2>/dev/null
cnt=0
for f in /d/ai/deskbar/out_publish/*; do
  b=$(basename "$f")
  [ "$b" = "ExplorerTAP.dll" ] && continue
  cp -f "$f" /d/app/DeskBar/ 2>/dev/null && cnt=$((cnt+1)) || echo "FAIL $b"
done
echo "copied=$cnt"
powershell.exe -NoProfile -Command "Start-Process 'D:\app\DeskBar\DeskBar.exe'; Start-Sleep -Seconds 3; \$p = Get-Process DeskBar -ErrorAction SilentlyContinue; if (\$p) { Write-Host \"PID=\$(\$p.Id) WS=\$([math]::Round(\$p.WorkingSet64/1MB,1))MB\" } else { Write-Host 'NOT RUNNING' }"
