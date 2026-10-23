# 打一个 MSI 安装包。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File toolkit\installer\build-installer.ps1
#   powershell ... -File build-installer.ps1 -SkipPublish     # 复用现有 dist，不重新发布
#   powershell ... -File build-installer.ps1 -TestInstall      # 打完自动静默安装/卸载验一遍
#
# 产物：toolkit\installer\out\AgentToolkit-<版本>-x64.msi

[CmdletBinding()]
param(
  [string]$Version = '2.0.0',
  [string]$Stage = "$env:TEMP\agent-toolkit-stage",
  [string]$OutDir = '',
  [switch]$SkipPublish,
  [switch]$TestInstall
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot 在 param() 的默认值里取不到（会变成空串，路径会落到盘根），在这里补
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
$Repo    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent   # …\破甲
$Toolkit = Join-Path $Repo 'toolkit'
$Dotnet  = 'C:\Program Files\dotnet\dotnet.exe'
$Wix     = "$env:USERPROFILE\.dotnet\tools\wix.exe"
$UiDist  = Join-Path $Toolkit 'ui\dist'
$CliDist = Join-Path $Toolkit 'backend\dist'

function Step($m) { Write-Host "`n== $m ==" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  ✔ $m" -ForegroundColor Green }
function Info($m) { Write-Host "  · $m" -ForegroundColor Gray }

if (-not (Test-Path $Wix)) { throw "找不到 wix：$Wix（先 dotnet tool install --global wix）" }

# ---------------------------------------------------------------- 1. 发布
if (-not $SkipPublish) {
  Step "重新发布（Release，自包含）"
  & $Dotnet publish (Join-Path $Toolkit 'ui\AgentToolkit.Ui.csproj') -c Release -o $UiDist --nologo -v q | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "界面发布失败" }
  Ok "界面 → $UiDist"
  & $Dotnet publish (Join-Path $Toolkit 'backend\AgentToolkit.Engine.Cli\AgentToolkit.Engine.Cli.csproj') -c Release -o $CliDist --nologo -v q | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "引擎发布失败" }
  Ok "引擎 → $CliDist"
}

foreach ($p in @((Join-Path $UiDist 'JailbreakTool.exe'), (Join-Path $CliDist 'engine.exe'))) {
  if (-not (Test-Path $p)) { throw "缺发布产物：$p" }
}

# ---------------------------------------------------------------- 2. 暂存到 ASCII 路径
# MSI 构建对源路径里的中文不友好，统一搬到 %TEMP% 下的纯 ASCII 目录
Step "暂存到 $Stage"
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$Stage\app", "$Stage\cli", "$Stage\docs" | Out-Null

# /XF *.pdb：调试符号不进安装包
& robocopy $UiDist "$Stage\app" /E /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 界面失败，退出码 $LASTEXITCODE" }
Copy-Item (Join-Path $CliDist 'engine.exe') "$Stage\cli\engine.exe" -Force

$docMap = @{
  'README.md'       = 'README.md'
  'docs\dsh.md'     = 'dsh.md'
  'docs\zcode.md'   = 'zcode.md'
  'docs\opencode.md'= 'opencode.md'
  'docs\probes.md'  = 'probes.md'
  'docs\verification.md' = 'verification.md'
}
foreach ($k in $docMap.Keys) {
  $src = Join-Path $Toolkit $k
  if (Test-Path $src) { Copy-Item $src (Join-Path "$Stage\docs" $docMap[$k]) -Force }
}
# 工具脚本：界面交互回归（UIA 驱动，只点导航，不点执行）
New-Item -ItemType Directory -Force -Path "$Stage\docs\tools" | Out-Null
foreach ($t in @('ui-regression.ps1')) {
  $src = Join-Path $Toolkit "tools\$t"
  if (Test-Path $src) { Copy-Item $src (Join-Path "$Stage\docs\tools" $t) -Force }
}
$appCount = (Get-ChildItem "$Stage\app" -Recurse -File).Count
$appSize  = (Get-ChildItem "$Stage\app" -Recurse -File | Measure-Object Length -Sum).Sum
Ok "app $appCount 个文件 / $([math]::Round($appSize/1MB,1)) MB（已排除 pdb）"
Ok "cli engine.exe $([math]::Round((Get-Item "$Stage\cli\engine.exe").Length/1MB,1)) MB"
Ok "docs $((Get-ChildItem "$Stage\docs" -Recurse -File).Count) 个文件（含 tools\）"

# 命令行快捷方式的落地脚本：用 .cmd 包装，快捷方式直接指它。
# 不用 "[SystemFolder]cmd.exe + 一堆带引号的参数"——那条路实测解析成了 SysWOW64\cmd.exe，
# 而且嵌套引号在 cmd /k 里必崩。
#
# 三条硬要求（都是实测踩出来的）：
#   1) 换行必须 CRLF。LF-only 的 .cmd 会被 cmd.exe 误解析：整行被拆成单词逐个当命令执行，
#      cd /d "%~dp0" 也不生效（实测 [CWD] 还停在调用者的目录）。
#   2) **内容必须纯 ASCII**。中文 banner 试过 GBK（代码页 936，且文件字节确实是对的
#      c6 c6 bc d7 = 破甲），但回显链路上仍出现 U+FFFD——批处理解析 + 控制台代码页这一层
#      跨环境不可靠。纯 ASCII 就没有这一层风险，而且能机械断言。
#      （engine.exe 自己的中文输出不受影响：那是 .NET 写控制台，走的是控制台编码。）
#   3) 简短：banner 之外不要有别的逻辑。
$cmdLines = @(
  '@echo off',
  'title Jailbreak Tool - CLI engine',
  'cd /d "%~dp0"',
  'echo.',
  'echo   Jailbreak Tool - CLI engine   (engine.exe --help for all commands)',
  'echo.',
  'echo   engine.exe targets                        list detected clients',
  'echo   engine.exe --target opencode status       layered status of a target',
  'echo   engine.exe --target opencode preview      show planned changes (read-only)',
  'echo   engine.exe --target opencode apply        write (backup first)',
  'echo   engine.exe --target opencode revert       restore from backup',
  'echo.',
  'echo   data + backups: %LOCALAPPDATA%\AgentToolkit\',
  'echo.',
  'cmd /k',
  ''
)
$cmdBody = ($cmdLines -join "`r`n")
$cmdPath = "$Stage\app\Jailbreak-CLI.cmd"
[System.IO.File]::WriteAllText($cmdPath, $cmdBody, [System.Text.ASCIIEncoding]::new())
$cmdBytes = [System.IO.File]::ReadAllBytes($cmdPath)
$crlf = 0; for ($i = 0; $i -lt $cmdBytes.Length - 1; $i++) { if ($cmdBytes[$i] -eq 13 -and $cmdBytes[$i+1] -eq 10) { $crlf++ } }
$lfOnly = ($cmdBytes | Where-Object { $_ -eq 10 }).Count - $crlf
$nonAscii = ($cmdBytes | Where-Object { $_ -gt 127 }).Count
Ok "命令行包装脚本 Jailbreak-CLI.cmd（ASCII + CRLF，$($cmdBytes.Length) B，CRLF $crlf / 裸 LF $lfOnly / 非 ASCII $nonAscii）"
if ($lfOnly -ne 0) { throw "包装脚本里还有裸 LF，cmd.exe 会误解析" }
if ($nonAscii -ne 0) { throw "包装脚本里有非 ASCII 字节，回显会在某些代码页下坏掉" }

# ---------------------------------------------------------------- 2.5 生成组件表
# 为什么不用 <Files Include="**">：那是「一个文件一个组件、以文件为 KeyPath」，
# 而本安装包是 perUser（装进用户配置目录 LOCALAPPDATA）。ICE38 规定：
# 装到用户配置目录的组件必须以 HKCU 注册表键为 KeyPath，不能用文件。
# 通配元素给不了这个，所以逐文件生成组件：<File/> + <RegistryValue Root="HKCU" KeyPath="yes"/>。
# 组件 Id 取相对路径的 sha256 前 16 位——稳定，升级时不会因改名而错乱。
Step "生成组件表"
function Get-Id16($group, $rel) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  # 哈希输入带上组名：不同组里同名的文件（app\README.md 与 docs\README.md）否则会撞 Id
  return (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("$group/$rel")) | ForEach-Object { $_.ToString('x2') }) -join '').Substring(0, 16)
}
function Get-GuidOf($id16) {
  $gb = New-Object byte[] 16
  for ($i = 0; $i -lt 16; $i++) { $gb[$i] = [Convert]::ToByte(($id16 + $id16).Substring($i * 2, 2), 16) }
  return ([Guid]::new($gb)).ToString().ToUpper()
}
function Esc($s) { return $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('"', '&quot;') }

# 逐文件生成组件（见上方注释），并且**保留目录结构**：
# <File Source="...\en-us\x.mui"> 只会把文件放进组件所在目录、丢掉源路径里的子目录，
# 于是所有同名 .mui 会被拍平到一层互相覆盖（ICE30 就是这么抓出来的）。
# 所以先把目录树建出来，再让每个组件指向自己那一层。
function New-Components($wxsPath, $groupPrefix, $rootDirId, $root, $files) {
  $dirs = @{}          # 相对目录 -> 目录 Id
  $dirs[''] = $rootDirId
  foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\')
    # 每一级祖先都要登记：只含子目录、不直接含文件的中间层否则不会被 emit，
    # 引用它的组件就会报 WIX0094「identifier Directory:d_… could not be found」
    $sub = Split-Path $rel -Parent
    while ($sub) {
      if (-not $dirs.ContainsKey($sub)) { $dirs[$sub] = "d_$(Get-Id16 $groupPrefix $sub)" }
      $sub = Split-Path $sub -Parent
    }
  }

  $sb = [System.Text.StringBuilder]::new()
  # 目录树：必须**真正嵌套**。只缩进不嵌套的话，Microsoft.UI.Xaml\Assets 会和根下的 Assets
  # 变成同级兄弟，两个同名目录在同一个父目录下 → 文件装错位置（实测踩过：2 个 Assets 文件跑到根下）。
  [void]$sb.AppendLine('  <Fragment>')
  [void]$sb.AppendLine("    <DirectoryRef Id=`"$rootDirId`">")
  function Emit-SubDirs($sb, $dirs, $parentRel, $indent) {
    $children = $dirs.Keys | Where-Object { $_ -and ((Split-Path $_ -Parent) -eq $parentRel) } | Sort-Object
    foreach ($c in $children) {
      [void]$sb.AppendLine("$indent<Directory Id=`"$($dirs[$c])`" Name=`"$(Esc (Split-Path $c -Leaf))`">")
      Emit-SubDirs $sb $dirs $c "$indent  "
      [void]$sb.AppendLine("$indent</Directory>")
    }
  }
  Emit-SubDirs $sb $dirs '' '      '
  [void]$sb.AppendLine('    </DirectoryRef>')
  [void]$sb.AppendLine('  </Fragment>')

  # 组件：每个目录的**第一个**组件负责登记 RemoveFolder（卸载时删掉这一层，ICE64）
  # RemoveFolder 不能挂在 Directory 下，只能放进组件里
  [void]$sb.AppendLine('  <Fragment>')
  [void]$sb.AppendLine("    <ComponentGroup Id=`"$groupPrefix`">")
  $n = 0
  $didRm = @{}
  foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\')
    $sub = Split-Path $rel -Parent
    $id16 = Get-Id16 $groupPrefix $rel
    $guid = Get-GuidOf $id16
    $where = if ($sub) { $dirs[$sub] } else { $rootDirId }
    [void]$sb.AppendLine("      <Component Id=`"c_$id16`" Guid=`"{$guid}`" Directory=`"$where`">")
    [void]$sb.AppendLine("        <File Id=`"f_$id16`" Source=`"$(Esc $f.FullName)`" />")
    if (-not $didRm.ContainsKey($where)) {
      $didRm[$where] = $true
      [void]$sb.AppendLine("        <RemoveFolder Id=`"rf_${groupPrefix}_$($where -replace '[^A-Za-z0-9]','_')`" On=`"uninstall`" />")
    }
    [void]$sb.AppendLine("        <RegistryValue Root=`"HKCU`" Key=`"Software\AgentToolkit\Components`" Name=`"c_$id16`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine('      </Component>')
    $n++
  }
  [void]$sb.AppendLine('    </ComponentGroup>')
  [void]$sb.AppendLine('  </Fragment>')
  Add-Content -LiteralPath $wxsPath -Value $sb.ToString() -Encoding UTF8
  return [pscustomobject]@{ Files = $n; Dirs = ($dirs.Keys | Where-Object { $_ }).Count }
}

$genWxs = Join-Path $Stage 'components.wxs'
Set-Content -LiteralPath $genWxs -Encoding UTF8 -Value @(
  '<?xml version="1.0" encoding="utf-8"?>',
  '<!-- 由 build-installer.ps1 生成，勿手改。WiX 的 include 文件根元素必须是 Include。 -->',
  '<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$r1 = New-Components $genWxs 'GuiFiles' 'INSTALLFOLDER' "$Stage\app" (Get-ChildItem "$Stage\app" -Recurse -File)
$r2 = New-Components $genWxs 'CliFiles' 'INSTALLFOLDER' "$Stage\cli" (Get-ChildItem "$Stage\cli" -Recurse -File)
$r3 = New-Components $genWxs 'DocFiles' 'DocsFolder'    "$Stage\docs" (Get-ChildItem "$Stage\docs" -Recurse -File)
Add-Content -LiteralPath $genWxs -Value '</Include>' -Encoding UTF8
Ok "组件：界面 $($r1.Files)（含子目录 $($r1.Dirs) 层）+ 引擎 $($r2.Files) + 文档 $($r3.Files) = $($r1.Files+$r2.Files+$r3.Files) 个（每个带 HKCU KeyPath）"

# ---------------------------------------------------------------- 3. 构建 MSI
Step "构建 MSI"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$msi = Join-Path $OutDir "JailbreakTool-$Version-x64.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }

# 图标：源图 png → 多尺寸 ico（Pillow 生成，见 ui\app.ico / app-logo.png）
$icon = Join-Path $Toolkit 'ui\app.ico'
$logo = Join-Path $Toolkit 'ui\app-logo.png'
if (-not (Test-Path $icon)) { throw "缺图标：$icon（用 Pillow 从源图转多尺寸 ico）" }
if (-not (Test-Path $logo)) { throw "缺引导程序 logo：$logo（64x64 png，与图标同源）" }
Ok "图标 $icon（$((Get-Item $icon).Length) B）+ logo $logo（$((Get-Item $logo).Length) B）"

& $Wix build (Join-Path $PSScriptRoot 'AgentToolkit.wxs') `
  -arch x64 `
  -d "Version=$Version" `
  -d "StageDir=$Stage" `
  -d "IconPath=$icon" `
  -o $msi 2>&1 | ForEach-Object { "  $_" }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msi)) { throw "wix build 失败" }
Ok "MSI: $msi（$([math]::Round((Get-Item $msi).Length/1MB,1)) MB，sha256 $((Get-FileHash $msi -Algorithm SHA256).Hash.Substring(0,16))…）"

# ---------------------------------------------------------------- 3.5 单文件 setup.exe（Burn）
# MSI 双击也能装，但 .msi 容易被邮件网关/下载器/组策略拦；一个 setup.exe 更省事。
# 装的东西完全一样：exe 只是外壳，真正的安装逻辑还在 MSI 里。
Step "构建单文件 setup.exe（Burn 引导程序）"
& $Wix extension add -g 'WixToolset.BootstrapperApplications.wixext/5.0.2' 2>&1 | Out-Null
$setup = Join-Path $OutDir "JailbreakTool-Setup-$Version.exe"
if (Test-Path $setup) { Remove-Item $setup -Force }
& $Wix build (Join-Path $PSScriptRoot 'Bundle.wxs') `
  -ext WixToolset.BootstrapperApplications.wixext `
  -d "Version=$Version" `
  -d "MsiPath=$msi" `
  -d "IconPath=$icon" `
  -d "LogoPath=$logo" `
  -o $setup 2>&1 | ForEach-Object { "  $_" }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setup)) { throw "bundle 构建失败" }
Ok "setup.exe: $setup（$([math]::Round((Get-Item $setup).Length/1MB,1)) MB，sha256 $((Get-FileHash $setup -Algorithm SHA256).Hash.Substring(0,16))…）"

# ---------------------------------------------------------------- 4. 可选：真装一遍
if ($TestInstall) {
  Step "静默安装 → 核对 → 卸载"
  $install = Join-Path $env:LOCALAPPDATA 'Programs\JailbreakTool'
  $log = Join-Path $env:TEMP 'Jailbreak-msi-install.log'
  $p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart /l*v `"$log`"" -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "安装失败，退出码 $($p.ExitCode)，日志 $log" }
  Ok "安装完成（退出码 0）"

  $checks = @(
    @{ P = "$install\JailbreakTool.exe";      N = '图形界面' },
    @{ P = "$install\engine.exe";             N = '命令行引擎' },
    @{ P = "$install\Jailbreak-CLI.cmd";      N = '命令行包装脚本' },
    @{ P = "$install\docs\README.md";         N = 'README' },
    @{ P = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Jailbreak Tool\Jailbreak Tool.lnk"; N = '开始菜单：图形界面' },
    @{ P = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Jailbreak Tool\Jailbreak CLI.lnk"; N = '开始菜单：命令行' },
    @{ P = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Jailbreak Tool\Jailbreak 文档.lnk"; N = '开始菜单：文档' }
  )
  foreach ($c in $checks) {
    if (Test-Path -LiteralPath $c.P) { Ok "$($c.N) 已安装" } else { Write-Host "  ✘ $($c.N) 缺失：$($c.P)" -ForegroundColor Red }
  }

  # 图标有没有真的嵌进 exe（Explorer/开始菜单/任务栏都读这一份）
  try {
    Add-Type -AssemblyName System.Drawing
    $ic = [System.Drawing.Icon]::ExtractAssociatedIcon("$install\JailbreakTool.exe")
    if ($ic) { Ok "JailbreakTool.exe 内嵌图标 $($ic.Width)x$($ic.Height)" ; $ic.Dispose() }
    else { Write-Host "  ✘ exe 里没有图标" -ForegroundColor Red }
  } catch { Write-Host "  ! 图标检查失败：$($_.Exception.Message)" -ForegroundColor Yellow }

  # 装完能不能真跑
  $targets = & "$install\engine.exe" targets 2>&1
  if ($LASTEXITCODE -eq 0) { Ok "安装后的引擎可运行：" ; $targets | ForEach-Object { "      $_" } }
  else { Write-Host "  ✘ 安装后的引擎跑不起来" -ForegroundColor Red }

  $p = Start-Process msiexec.exe -ArgumentList "/x `"$msi`" /qn /norestart" -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "卸载失败，退出码 $($p.ExitCode)" }
  Start-Sleep -Seconds 2
  $left = Get-ChildItem $install -Recurse -File -ErrorAction SilentlyContinue
  if ($null -eq $left -or $left.Count -eq 0) { Ok "卸载干净（安装目录已移除）" }
  else { Write-Host "  ! 卸载后还剩 $($left.Count) 个文件" -ForegroundColor Yellow }
  if (Test-Path -LiteralPath "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Jailbreak Tool") {
    Write-Host "  ! 开始菜单目录还在" -ForegroundColor Yellow
  } else { Ok "开始菜单项已移除" }
  if (Test-Path "$env:LOCALAPPDATA\AgentToolkit") { Ok "用户数据目录保留（卸载不动注入记录与备份）" }
}

Step "完成"
Write-Host "  $setup"
Write-Host "  $msi"
