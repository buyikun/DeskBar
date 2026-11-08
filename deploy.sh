#!/bin/bash
# 部署 DeskBar 到 D:\app\DeskBar 并启动。
#
# 启动必须走 WMI(Win32_Process::Create)，不能用 Start-Process / cmd start：
# 从 DSH(或任何被 Job Object 托管的宿主)启动的子进程会继承该 Job，
# 而这类 Job 通常带 KILL_ON_JOB_CLOSE —— 宿主一退出，整条进程链被系统连带杀掉。
# 实测：deploy.sh 用 Start-Process 拉起的 DeskBar，在 DSH 关闭瞬间被强杀
#（日志在无 "DeskBar exiting" 的情况下突然中断，即未走 OnExit 的强杀）。
# WMI 创建出的进程由 WmiPrvSE.exe 派生，不在当前进程的 Job 里，因此能独立存活。

taskkill //F //IM DeskBar.exe 2>/dev/null; sleep 1
rm -f /d/app/DeskBar/logs/* 2>/dev/null
cnt=0
for f in /d/ai/deskbar/out_publish/*; do
  b=$(basename "$f")
  [ "$b" = "ExplorerTAP.dll" ] && continue
  cp -f "$f" /d/app/DeskBar/ 2>/dev/null && cnt=$((cnt+1)) || echo "FAIL $b"
done
echo "copied=$cnt"

powershell.exe -NoProfile -Command "\$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='D:\\app\\DeskBar\\DeskBar.exe'}; Start-Sleep -Seconds 3; if (\$r.ReturnValue -eq 0) { \$p = Get-Process -Id \$r.ProcessId -ErrorAction SilentlyContinue; if (\$p) { Write-Host \"PID=\$(\$p.Id) WS=\$([math]::Round(\$p.WorkingSet64/1MB,1))MB (detached via WMI)\" } else { Write-Host 'STARTED THEN EXITED' } } else { Write-Host \"WMICREATE FAILED rv=\$(\$r.ReturnValue)\" }"
