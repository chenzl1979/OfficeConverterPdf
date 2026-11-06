# ============================================================
# fetch-engines.ps1 —— 预置 LibreOffice 包下载脚本
#
# 功能：
#   1. 读取 build/engines.json 里钉死的版本与镜像列表；
#   2. 按平台下载（默认全部，可用 -Only 指定），国内清华 TUNA 镜像优先、官方 TDF 兜底；
#   3. 已有且哈希一致就跳过；已钉哈希的包下载后强校验，不匹配即删除报错；
#   4. sha256 为 null 的包首次下载后计算哈希并回填 engines.json（请人工核对来源后作为固定校验值）。
#
# 用法（在仓库任意目录下执行）：
#   powershell -ExecutionPolicy Bypass -File build\fetch-engines.ps1              # 下载全部 5 个包（约 1.24 GB）
#   powershell -ExecutionPolicy Bypass -File build\fetch-engines.ps1 -Only win-x64              # 只下 Windows 包
#   powershell -ExecutionPolicy Bypass -File build\fetch-engines.ps1 -Only linux-deb-arm64      # 只下 UOS/麒麟 ARM64 包
#   powershell -ExecutionPolicy Bypass -File build\fetch-engines.ps1 -List                      # 只列出清单，不下载
#
# 下载落盘位置：engines\downloads\<文件名>（该目录不入版本库）。
# 离线交付：在任一台能上网的机器跑本脚本，然后把 engines\downloads 里的包拷到目标机器同位置即可。
# ============================================================

param(
    # 只下载指定平台，可传多个。传值示例：-Only win-x64；-Only linux-deb-arm64,linux-rpm-arm64；不传则下载全部
    [string[]]$Only,
    # 只打印清单与状态，不实际下载
    [switch]$List
)

# 关闭 IWR 进度条（否则下载大文件时控制台刷屏且极慢）
$ProgressPreference = 'SilentlyContinue'

# 仓库根目录 = 本脚本所在目录的上一级
$repoRoot = Split-Path -Parent $PSScriptRoot
# 清单文件路径
$manifestPath = Join-Path $repoRoot 'build\engines.json'
# 包落盘目录
$downloadDir = Join-Path $repoRoot 'engines\downloads'

if (-not (Test-Path $manifestPath)) {
    Write-Error "找不到清单文件：$manifestPath"
    exit 1
}
# 读清单（显式按 UTF-8 读，兼容 Windows PowerShell 5.1 对无 BOM 文件的默认编码）
$manifest = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json
Write-Host "LibreOffice 版本：$($manifest.libreofficeVersion)"
Write-Host "包落盘目录：$downloadDir"

# 需要新增哈希回填的标记
$manifestDirty = $false

foreach ($property in $manifest.packages.PSObject.Properties) {
    # 平台键名，传值示例：win-x64
    $key = $property.Name
    # 该平台的包信息对象
    $package = $property.Value
    # 指定了 -Only 时跳过未点名的平台
    if ($Only -and ($Only -notcontains $key)) { continue }

    # 本地包文件路径
    $localFile = Join-Path $downloadDir $package.file
    # 现有文件的哈希（不存在则为 $null）
    $existingHash = $null
    if (Test-Path $localFile) {
        $existingHash = (Get-FileHash -Path $localFile -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    # ---- 仅列出：打印当前状态 ----
    if ($List) {
        # 状态描述：已下载且哈希一致 / 已下载但待固定哈希 / 未下载
        $state = if ($existingHash -and $package.sha256 -and ($existingHash -eq $package.sha256)) { '已下载（哈希校验通过）' }
                 elseif ($existingHash) { '已下载（待回填/核对哈希）' }
                 else { '未下载' }
        $sizeMb = [math]::Round($package.sizeBytes / 1MB)
        Write-Host ("  [{0}] {1}  约 {2} MB  sha256={3}" -f $state, $package.file, $sizeMb, ($(if ($package.sha256) { $package.sha256.Substring(0, 16) + '…' } else { '未固定' })))
        continue
    }

    # ---- 已有且哈希一致：跳过 ----
    if ($existingHash -and $package.sha256 -and ($existingHash -eq $package.sha256)) {
        Write-Host "✔ [$key] 已存在且校验通过，跳过：$package.file"
        continue
    }

    # ---- 下载（按镜像顺序尝试） ----
    New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
    $downloaded = $false
    foreach ($mirror in $package.mirrors) {
        # 当前尝试的镜像主机名（仅用于展示）
        $host_ = ([uri]$mirror).Host
        Write-Host "↓ [$key] 从 $host_ 下载：$($package.file) …"
        try {
            # 优先用系统自带 curl.exe（支持断点与更好的代理行为），失败回退 Invoke-WebRequest
            $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
            if ($curl) {
                & curl.exe -L --fail --connect-timeout 20 --retry 2 -o $localFile $mirror
                if ($LASTEXITCODE -ne 0) { throw "curl 退出码 $LASTEXITCODE" }
            }
            else {
                Invoke-WebRequest -Uri $mirror -OutFile $localFile -UseBasicParsing
            }
            $downloaded = $true
            break
        }
        catch {
            Write-Warning "  该镜像失败：$($_.Exception.Message)，尝试下一个…"
            if (Test-Path $localFile) { Remove-Item $localFile -Force -ErrorAction SilentlyContinue }
        }
    }
    if (-not $downloaded) {
        Write-Error "✘ [$key] 所有镜像均失败：$package.file"
        exit 1
    }

    # ---- 哈希校验 / 回填 ----
    $actualHash = (Get-FileHash -Path $localFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($package.sha256) {
        # 已钉哈希：强校验，不匹配删除重下
        if ($actualHash -ne $package.sha256) {
            Remove-Item $localFile -Force
            Write-Error "✘ [$key] SHA-256 不匹配（期望 $package.sha256，实际 $actualHash），已删除损坏文件。请检查网络是否被劫持。"
            exit 1
        }
        Write-Host "✔ [$key] 下载完成，SHA-256 校验通过"
    }
    else {
        # 未钉哈希：回填清单并提醒人工核对
        $package.sha256 = $actualHash
        $manifestDirty = $true
        Write-Host "✔ [$key] 下载完成。SHA-256（已回填 engines.json，请核对来源后固定）：`n    $actualHash"
    }
}

# 把回填的哈希写回清单（保留原文件其余内容）
if ($manifestDirty) {
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8
    Write-Host "已把新计算的 SHA-256 写回 $manifestPath（请核对后提交固定）。"
}
Write-Host "完成。下一步：Windows 运行 build\setup-windows.ps1 解包；Linux 运行 build/setup-linux.sh 解包。"
