$tmp = "D:\ai\deskbar\.tmp"
$appdata = "D:\ai\deskbar\.appdata"
$localappdata = "D:\ai\deskbar\.localappdata"
foreach ($d in @($tmp, $appdata, $localappdata, "D:\ai\deskbar\.dotnet_home")) {
    if (!(Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}
$env:TEMP = $tmp
$env:TMP = $tmp
$env:APPDATA = $appdata
$env:LOCALAPPDATA = $localappdata
$env:USERPROFILE = "D:\ai\deskbar"
$env:DOTNET_CLI_HOME = "D:\ai\deskbar\.dotnet_home"
$env:DOTNET_ROOT = "D:\ai\deskbar\.dotnet"
$env:NUGET_PACKAGES = "D:\ai\deskbar\.dotnet_home\.nuget\packages"
$env:PATH = "D:\ai\deskbar\.dotnet;" + $env:PATH

$out = "D:\ai\deskbar\out_publish"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

& "D:\ai\deskbar\.dotnet\dotnet.exe" publish src/DeskBar/DeskBar.csproj -c Release -r win-x64 --self-contained false -o $out
