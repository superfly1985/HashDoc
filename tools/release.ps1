# 识档 HashDoc 发布脚本：生成 dist\HashDoc_v<版本>.zip
# 只打包程序文件（exe / config / SQLite 驱动 / x86 / x64），不打包 data 目录与数据库，
# 因此用户解压覆盖程序目录即可完成升级，档案数据不受影响。
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # tools\ -> 项目根目录
Set-Location $root

# ---- 1. 版本号（唯一的版本来源：src\HashDoc.csproj） ----
$csproj = Join-Path $root 'src\HashDoc.csproj'
$ver = ([regex]'(?m)<Version>([^<]+)</Version>').Match((Get-Content $csproj -Raw)).Groups[1].Value
if ([string]::IsNullOrWhiteSpace($ver)) { throw "无法从 $csproj 读取 <Version>" }
Write-Host "[HashDoc] 发布版本：$ver"

# ---- 2. 程序在运行就先停下：exe 被占用会复制失败 ----
if (Get-Process -Name 'HashDoc' -ErrorAction SilentlyContinue) {
    throw "检测到 HashDoc.exe 正在运行，请先关闭程序再发布。"
}

# ---- 3. 构建 ----
if (-not $SkipBuild) {
    & dotnet build 'src\HashDoc.csproj' -c Release -o out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "构建失败，发布终止。" }
}

# ---- 4. 组装发布目录（白名单复制） ----
$stageName = "HashDoc_v$ver"
$stage = Join-Path $root "dist\$stageName"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$files = @('HashDoc.exe', 'HashDoc.exe.config', 'System.Data.SQLite.dll')
foreach ($f in $files) {
    $src = Join-Path $root "out\$f"
    if (-not (Test-Path $src)) { throw "缺少发布所需文件：out\$f" }
    Copy-Item $src -Destination $stage -Force
}
foreach ($d in @('x86', 'x64')) {
    $src = Join-Path $root "out\$d"
    if (-not (Test-Path $src)) { throw "缺少发布所需目录：out\$d" }
    Copy-Item $src -Destination (Join-Path $stage $d) -Recurse -Force
}

# ---- 5. 包内说明 ----
$notes = @"
识档 HashDoc v$ver —— 升级 / 安装说明

一、已装过的电脑（升级）
  1. 先关闭正在运行的「识档」。
  2. 把本压缩包解压，全部文件覆盖到原来的程序目录。
  3. 双击 HashDoc.exe 即可。档案数据在 data 目录里，覆盖不会动它。
  4. 首次运行如果提示"档案库已升级"，那是一次性结构升级，照提示走即可，
     旧库会自动备份到 data\backup\ 里。

二、全新的电脑（首次安装）
  1. 把整个文件夹拷到任意可写目录（不要放 Program Files）。
  2. 双击 HashDoc.exe，第一次运行会自动生成 data\HashDoc.db。
  3. 到【设置】里添加归档目录；供应商清单可以用【从文件清单导入供应商…】
     一次性灌进去（清单就是 Excel 另存的 CSV），程序本身不带任何清单文件。
  4. 开始入库：供应商、供应商零件号、VW客户号、ident内部件号都支持打字联想。
  5. 存量目录第一次扫描后会有大量"未入库"，【补入库】里可以先把供应商留空
     直接入库，事后在列表勾「只看待补」再逐批补填。
  6. 打开软件会自动检查一遍归档目录，发现"直接拷进去、还没登记"的文件时，
     工具条上会亮起橙色按钮「发现 N 个新文件 → 补入库」，点一下就能批量登记。
  7. BMG 号是可选填的单值字段；供应商、文档类型、BMG 号任一为空，
     列表最左会标「待补」，鼠标停上去能看到缺的是哪一项。
  8. PDF 现在可以直接在右侧预览：选中一行就出图，预览区底部有
     「上一页 / 下一页」和缩放（适应窗口、100%、150%、200%），
     200% 以上超出部分用滚动条看。PDF 渲染用的是 Windows 10/11 自带的
     组件，安装包没有因此变大；万一某台电脑没有该组件，预览区会提示
     "PDF 预览不可用"，其余功能不受影响，仍可点【打开文件】外开。

三、核对版本
  HashDoc.exe --dbinfo   会打印：程序版本、数据库位置、库结构版本、档案条数。
  主窗口标题栏和【设置】页也能看到版本号。

四、注意
  - 程序目录必须可写；不可写时会退到 %APPDATA%\HashDoc 存库并提示。
  - 升级不要手工删除或替换 data 目录，那里面是全部档案数据。
"@
$notesPath = Join-Path $stage '升级说明.txt'
# UTF-8 带 BOM，记事本打开不乱码
[System.IO.File]::WriteAllText($notesPath, ($notes -replace "`r?`n", "`r`n"), (New-Object System.Text.UTF8Encoding $true))

# ---- 6. 打包 ----
$zip = Join-Path $root "dist\$stageName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

# ---- 7. 自检：包里绝不能有数据库 ----
$leak = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in '.db', '.log' }
if ($leak) { throw ("发布包里出现了不该有的文件：" + ($leak.FullName -join ', ')) }

$sizeMB = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host ""
Write-Host "[HashDoc] 发布包已生成：dist\$stageName.zip（$sizeMB MB）"
Write-Host "  包含：HashDoc.exe / HashDoc.exe.config / System.Data.SQLite.dll / x86 / x64 / 升级说明.txt"
Write-Host "  未包含：data 目录、HashDoc.db、日志（升级时不会碰用户数据）"
