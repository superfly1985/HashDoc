$ErrorActionPreference = 'Stop'
Add-Type -Path 'D:\01.AwesomeProject\72.HashDoc\out\System.Data.SQLite.dll'
Add-Type -AssemblyName System.Drawing

$out = 'D:\01.AwesomeProject\72.HashDoc\out'

function New-TestImage($path, $rgb, $text) {
  $bmp = New-Object System.Drawing.Bitmap(800, 500)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb($rgb[0], $rgb[1], $rgb[2]))
  $font = New-Object System.Drawing.Font('Microsoft YaHei', 40)
  $g.DrawString($text, $font, [System.Drawing.Brushes]::White, 50, 190)
  $g.DrawRectangle([System.Drawing.Pens]::White, 30, 170, 740, 120)
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

$sup1 = Join-Path $out 'archive\测试供应商'
$sup2 = Join-Path $out 'archive\泰科'
$sub  = Join-Path $sup1 '子目录 PPAP'
New-Item -ItemType Directory -Force -Path $sup1, $sup2, $sub | Out-Null
New-TestImage (Join-Path $sup1 '图号001 安装图.png') @(31,78,121)  '图号001 安装图'
New-TestImage (Join-Path $sub  '图号002 焊接图.png') @(84,130,53)  '图号002 焊接图'
New-TestImage (Join-Path $sup2 '图号003 外观图.png') @(150,60,40)  '图号003 外观图'
Set-Content -Path (Join-Path $sup1 '报告004.pdf') -Value '%PDF-1.4 demo' -Encoding Ascii

$conn = New-Object System.Data.SQLite.SQLiteConnection ('Data Source=' + (Join-Path $out 'HashDoc.db') + ';Version=3')
$conn.Open()
$cmd = $conn.CreateCommand()
function Exec($sql) { $cmd.CommandText = $sql; $cmd.ExecuteNonQuery() | Out-Null }

Exec "DELETE FROM FileInfo; DELETE FROM FileCode; DELETE FROM SupplierDict;"
Exec "INSERT INTO SupplierDict VALUES('测试供应商','演示数据');"
Exec "INSERT INTO SupplierDict VALUES('泰科','演示数据');"
Exec "INSERT OR REPLACE INTO Config VALUES('RootDir','D:\01.AwesomeProject\72.HashDoc\out\archive');"

function AddFile($path, $sup, $doctype) {
  $fi = Get-Item $path
  $hash = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLower()
  $mt = $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
  $it = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
  $p = $fi.FullName.Replace("'", "''")
  $n = $fi.Name.Replace("'", "''")
  $cmd.CommandText = "INSERT INTO FileInfo(FileHash,CurrentPath,FileName,FileSize,FileMTime,Supplier,DocType,Status,ImportTime) VALUES('$hash','$p','$n',$($fi.Length),'$mt','$sup','$doctype',0,'$it')"
  $cmd.ExecuteNonQuery() | Out-Null
}

AddFile (Join-Path $sup1 '图号001 安装图.png') '测试供应商' 'PPAP'
AddFile (Join-Path $sub  '图号002 焊接图.png') '测试供应商' 'PPAP'
AddFile (Join-Path $sup2 '图号003 外观图.png') '泰科' 'BMG'
AddFile (Join-Path $sup1 '报告004.pdf') '测试供应商' ''

$h1 = (Get-FileHash -Algorithm SHA256 (Join-Path $sup1 '图号001 安装图.png')).Hash.ToLower()
$h2 = (Get-FileHash -Algorithm SHA256 (Join-Path $sup1 '报告004.pdf')).Hash.ToLower()
$cmd.CommandText = "INSERT INTO FileCode VALUES('$h1','SupplierPartNo','P-10001'),('$h1','VWNo','5ND010001'),('$h1','IdentNo','ID-9001'),('$h2','SupplierPartNo','P-10002')"
$cmd.ExecuteNonQuery() | Out-Null
$conn.Close()
Write-Host 'SEED OK'
