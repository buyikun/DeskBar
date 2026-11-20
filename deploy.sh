#!/bin/bash
# 部署 DeskBar 到 D:\app\DeskBar 并启动。
#
# 启动必须走 WMI(Win32_Process::Create)，不能用 Start-Process / cmd start：
# 从 DSH(或任何被 Job Object 托管的宿主)启动的子进程会继承该 Job，
# 而这类 Job 通常带 KILL_ON_JOB_CLOSE —— 宿主一退出，整条进程链被系统连带杀掉。
# 实测：deploy.sh 用 Start-Process 拉起的 DeskBar，在 DSH 关闭瞬间被强杀
#（日志在无 "DeskBar exiting" 的情况下突然中断，即未走 OnExit 的强杀）。
# WMI 创建出的进程由 WmiPrvSE.exe 派生，不在当前进程的 Job 里，因此能独立存活。
#
# 复制阶段任何一个文件失败都直接中止：以前只打印一行 FAIL 就继续，
# 结果可能带着旧 DeskBar.dll 启动，表现为「改了却没生效」，极难排查。

set -u

# 停止旧实例必须用 PowerShell：MSYS 的 taskkill 传 //F //IM 会被判为非法参数，
# 一行都不生效（历史遗留写法，别再改回去）。
powershell.exe -NoProfile -Command "Get-Process DeskBar -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
sleep 1

# 进程必须真的退干净，否则 dll 被占用、cp 会失败
remain=$(powershell.exe -NoProfile -Command "(Get-Process DeskBar -ErrorAction SilentlyContinue | Measure-Object).Count" | tr -d '\r')
if [ "$remain" != "0" ]; then
  echo "ABORT: $remain DeskBar process(es) still running"
  exit 1
fi

rm -f /d/app/DeskBar/logs/* 2>/dev/null

cnt=0
for f in /d/ai/deskbar/out_publish/*; do
  b=$(basename "$f")
  [ "$b" = "ExplorerTAP.dll" ] && continue
  if cp -f "$f" /d/app/DeskBar/ 2>/dev/null; then
    cnt=$((cnt+1))
  else
    echo "ABORT: failed to copy $b"
    exit 1
  fi
done

# 复制完逐个核对大小，避免"复制成功但内容没落地"
mismatch=0
for f in /d/ai/deskbar/out_publish/*; do
  b=$(basename "$f")
  [ "$b" = "ExplorerTAP.dll" ] && continue
  s=$(stat -c %s "$f")
  d=$(stat -c %s "/d/app/DeskBar/$b" 2>/dev/null || echo -1)
  if [ "$s" != "$d" ]; then
    echo "MISMATCH $b src=$s dst=$d"
    mismatch=$((mismatch+1))
  fi
done
if [ "$mismatch" != "0" ]; then
  echo "ABORT: $mismatch file(s) did not land correctly"
  exit 1
fi

echo "copied=$cnt verified=yes"

powershell.exe -NoProfile -Command "\$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='D:\\app\\DeskBar\\DeskBar.exe'}; Start-Sleep -Seconds 3; if (\$r.ReturnValue -eq 0) { \$p = Get-Process -Id \$r.ProcessId -ErrorAction SilentlyContinue; if (\$p) { Write-Host \"PID=\$(\$p.Id) WS=\$([math]::Round(\$p.WorkingSet64/1MB,1))MB (detached via WMI)\" } else { Write-Host 'STARTED THEN EXITED' } } else { Write-Host \"WMICREATE FAILED rv=\$(\$r.ReturnValue)\" }"
