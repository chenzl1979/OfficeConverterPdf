# ============================================================
# setup-windows.ps1 —— 把已下载的 LibreOffice MSI 解包为免安装目录
#
# 原理：msiexec /a（管理解包）只把文件解出来，不写注册表、不需要管理员权限、不留系统残留；
#       解包出来的 soffice.exe 直接可用，删掉目录即"卸载"。这正是 Filee 源项目安装 LibreOffice 的方式。
#
# 用法（在仓库任意目录下执行）：
#   powershell -ExecutionPolicy Bypass -File build\setup-windows.ps1              # 自动找 engines\downloads 里的 MSI
#   powershell -ExecutionPolicy Bypass -File build\setup-windows.ps1 -Test        # 解包后顺带跑一次 --version 自检
#   powershell -ExecutionPolicy Bypass -File build\setup-windows.ps1 -MsiPath "D:\pkg\LibreOffice_26.8.2_Win_x86-64.msi"
#
# 结果布局：engines\libreoffice\win-x64\program\soffice.exe
#           —— DocumentConverter 会自动找到它（SofficeLocator 会向上查找仓库里的 engines 目录），无需任何配置。
# ============================================================

param(
    # MSI 安装包路径；不传则自动在 engines\downloads 里找 LibreOffice_*_Win_x86-64.msi。
    # 传值示例：-MsiPath "D:\pkg\LibreOffice_26.8.2_Win_x86-64.msi"
    [string]$MsiPath,
    # 解包后运行一次 soffice --version 自检（首次运行要几秒）
    [switch]$Test
)

# 仓库根目录 = 本脚本所在目录的上一级
$repoRoot = Split-Path -Parent $PSScriptRoot
# 最终解包目录（DocumentConverter 会自动探测的位置）
$target = Join-Path $repoRoot 'engines\libreoffice\win-x64'
# 最终的 soffice 路径
$soffice = Join-Path $target 'program\soffice.exe'

# ---- 已经解包过：直接成功返回 ----
if (Test-Path $soffice) {
    Write-Host "✔ LibreOffice 已就绪：$soffice"
    if ($Test) { & $soffice --version }
    exit 0
}

# ---- 定位 MSI ----
if (-not $MsiPath) {
    # 自动查找：engines\downloads 下的 Win MSI
    $found = Get-ChildItem -Path (Join-Path $repoRoot 'engines\downloads') -Filter 'LibreOffice_*_Win_x86-64.msi' -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending | Select-Object -First 1
    if (-not $found) {
        Write-Error "engines\downloads 里没有 Windows MSI。请先运行 build\fetch-engines.ps1 -Only win-x64 下载，或用 -MsiPath 指定。"
        exit 1
    }
    $MsiPath = $found.FullName
}
if (-not (Test-Path $MsiPath)) {
    Write-Error "找不到 MSI：$MsiPath"
    exit 1
}
Write-Host "使用安装包：$MsiPath"

# ---- 管理解包（msiexec /a，免管理员）到临时暂存目录 ----
$staging = Join-Path $repoRoot ("engines\.win-staging-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Write-Host "解包中（约 1-3 分钟，无任何弹窗，不需要管理员权限）…"

# TARGETDIR 末尾必须带反斜杠（MSI 的老规矩，少了会把最后一段目录名吃掉）
$targetDirArg = 'TARGETDIR=' + $staging.TrimEnd('\') + '\'
# 启动 msiexec 并等待结束；/qn 全程无界面
$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/a', "`"$MsiPath`"", $targetDirArg, '/qn' -Wait -PassThru
# 0 = 成功；3010 = 成功（要求重启，管理解包不会出现，兼容处理）
if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Write-Error "msiexec /a 解包失败，退出码：$($process.ExitCode)"
    exit 1
}

# ---- 归一化布局：MSI 内部目录层级不确定，递归找到 soffice.exe，取它所在"program"的上一级作为 LibreOffice 根 ----
$foundSoffice = Get-ChildItem -Path $staging -Recurse -Filter 'soffice.exe' | Select-Object -First 1
if (-not $foundSoffice) {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Write-Error "解包完成但没找到 soffice.exe（MSI 内容与预期不符）。"
    exit 1
}
# program 目录（soffice.exe 所在目录）
$programDir = $foundSoffice.Directory.FullName
# LibreOffice 根目录（program 的上一级）
$loRoot = Split-Path -Parent $programDir

# 把 LibreOffice 根整体移动到目标位置（同盘移动是瞬时的）
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
Move-Item -Path $loRoot -Destination $target
# 清理暂存目录的其余外壳（如 "Program Files" 空壳）
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

# ---- 最终校验 ----
if (-not (Test-Path $soffice)) {
    Write-Error "移动完成但 $soffice 不存在，请检查。"
    exit 1
}
Write-Host "✔ 解包完成：$soffice"
Write-Host "DocumentConverter 将自动找到它（无需配置）。可运行演示验证：dotnet run --project src\LibreOfficeDemo -- <文档> pdf"
if ($Test) { & $soffice --version }
