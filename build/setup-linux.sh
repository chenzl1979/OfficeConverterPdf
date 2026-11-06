#!/usr/bin/env bash
# ============================================================
# setup-linux.sh —— 把已下载的 LibreOffice 包解包为免 root 的免安装目录
# 适用：UOS/深度、银河麒麟、openEuler、CentOS 等 Linux 与国产系统
#
# 原理：官方 tar.gz 里是 DEBS/*.deb 或 RPMS/*.rpm，逐个解出文件到用户目录，
#       不写系统目录、不需要 root；删除目录即"卸载"。
#       需要 root 的替代方案（apt/dnf 安装）见 README「安装 LibreOffice」。
#
# 用法：
#   ./build/setup-linux.sh                                # 自动识别包格式与架构，解包已下载的包
#   ./build/setup-linux.sh -k deb -a arm64                # 显式指定：UOS/麒麟 ARM64（飞腾/鲲鹏）
#   ./build/setup-linux.sh -f /path/LibreOffice_26.8.2_Linux_aarch64_deb.tar.gz
#
# 结果布局：engines/libreoffice/linux-<deb|rpm>-<x64|arm64>/program/soffice
#           —— DocumentConverter 会自动找到它（SofficeLocator 会向上查找仓库里的 engines 目录）。
# ============================================================
set -euo pipefail

# 包格式：deb 或 rpm（-k 覆盖；默认按本机工具自动判断）
kind=""
# 架构：x64 或 arm64（-a 覆盖；默认按 uname -m 判断）
arch=""
# 显式指定的 tar.gz 包路径（-f）
tarball=""
# 脚本所在目录（build/），用它定位仓库根
script_dir="$(cd "$(dirname "$0")" && pwd)"
# 仓库根目录
repo_root="$(dirname "$script_dir")"
# 包下载目录
download_dir="$repo_root/engines/downloads"

# ───────────────────────── 解析参数 ─────────────────────────
while getopts "k:a:f:h" opt; do
  case "$opt" in
    k) kind="$OPTARG" ;;
    a) arch="$OPTARG" ;;
    f) tarball="$OPTARG" ;;
    h) grep '^#   ' "$0" | sed 's/^#   //'; exit 0 ;;
    *) echo "未知参数，-h 查看用法"; exit 1 ;;
  esac
done

# ───────────────────────── 自动识别格式与架构 ─────────────────────────
# 包格式：本机有 dpkg-deb 视为 deb 系，有 rpm2cpio 视为 rpm 系
if [ -z "$kind" ]; then
  if command -v dpkg-deb >/dev/null 2>&1; then kind="deb"
  elif command -v rpm2cpio >/dev/null 2>&1; then kind="rpm"
  else echo "错误：本机既没有 dpkg-deb 也没有 rpm2cpio，无法解包。请用 -k 指定格式，或改用发行版包管理器安装 LibreOffice（见 README）。"; exit 1
  fi
fi
# 架构：x86_64 → x64；aarch64/arm64 → arm64（飞腾、鲲鹏）
if [ -z "$arch" ]; then
  case "$(uname -m)" in
    x86_64) arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *) echo "错误：无法识别架构 $(uname -m)（loongarch 等请用 -a 指定并确认有对应包）。"; exit 1 ;;
  esac
fi
# 目标目录，传值示例：engines/libreoffice/linux-deb-arm64
target="$repo_root/engines/libreoffice/linux-$kind-$arch"

# ───────────────────────── 已解包过：直接返回 ─────────────────────────
if [ -x "$target/program/soffice" ]; then
  echo "✔ LibreOffice 已就绪：$target/program/soffice"
  exit 0
fi

# ───────────────────────── 定位 tar.gz ─────────────────────────
if [ -z "$tarball" ]; then
  # 按格式与架构匹配已下载的包，取最新一个
  tarball="$(ls -1 "$download_dir"/LibreOffice_*_Linux_*_"$kind".tar.gz 2>/dev/null | sort | tail -n 1 || true)"
  if [ -z "$tarball" ]; then
    echo "错误：$download_dir 里没有 ${kind} 格式的包。"
    echo "请先在有网络的机器上运行：pwsh build/fetch-engines.ps1 -Only linux-$kind-$arch"
    echo "然后把 engines/downloads 里的包拷到本机同位置，再运行本脚本。"
    exit 1
  fi
fi
if [ ! -f "$tarball" ]; then
  echo "错误：找不到包文件：$tarball"; exit 1
fi
echo "使用安装包：$tarball"

# ───────────────────────── 解包（免 root） ─────────────────────────
# 暂存与解出目录
staging="$(mktemp -d "$repo_root/engines/.linux-staging-XXXXXX")"
work="$staging/work"
mkdir -p "$work"
trap 'rm -rf "$staging"' EXIT

echo "解压 tar.gz …"
tar -xzf "$tarball" -C "$staging"

if [ "$kind" = "deb" ]; then
  # deb 系：逐个解出（dpkg-deb -x 不需要 root）
  echo "解出 deb 包 …"
  find "$staging" -name '*.deb' -print0 | while IFS= read -r -d '' f; do
    dpkg-deb -x "$f" "$work"
  done
else
  # rpm 系：rpm2cpio + cpio 解出（不需要 root；cpio 缺失时给出提示）
  command -v cpio >/dev/null 2>&1 || { echo "错误：缺少 cpio，请先安装（openEuler/麒麟：sudo dnf install cpio）。"; exit 1; }
  echo "解出 rpm 包 …"
  find "$staging" -name '*.rpm' -print0 | while IFS= read -r -d '' f; do
    rpm2cpio "$f" | cpio -idmD "$work" --quiet
  done
fi

# ───────────────────────── 归一化布局 ─────────────────────────
# 解出后 LibreOffice 根在 <work>/opt/libreoffice<版本>/；找到 program/soffice 即定位根目录
soffice_found="$(find "$work" -type f -path '*/program/soffice' | head -n 1 || true)"
if [ -z "$soffice_found" ]; then
  echo "错误：解包完成但没找到 program/soffice，包内容与预期不符。"; exit 1
fi
# LibreOffice 根目录（program 的上一级）
lo_root="$(dirname "$(dirname "$soffice_found")")"

# 整体移动到目标位置（同盘 mv 瞬时完成）
mkdir -p "$(dirname "$target")"
mv "$lo_root" "$target"

# 防御性补执行权限（个别解包工具会丢权限位）
chmod +x "$target/program/soffice" 2>/dev/null || true
chmod +x "$target/program/soffice.bin" 2>/dev/null || true

echo "✔ 解包完成：$target/program/soffice"
echo "DocumentConverter 将自动找到它（无需配置）。可运行演示验证："
echo "  cd src/LibreOfficeDemo && dotnet run -- /path/to/文档.docx pdf"
echo "提示：若启动报缺系统库（libcairo、libXinerama 等），用发行版包管理器补装，或改用 apt/dnf 方式安装 LibreOffice。"
