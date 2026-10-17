# 界面交互回归：用 UI Automation 驱动发布版界面，逐页验证真实引擎数据是否渲染出来。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File toolkit\tools\ui-regression.ps1
#   powershell ... -File ui-regression.ps1 -KeepOpen        # 跑完不关窗口
#
# 产出：docs\ui-regression\*.png（每页截图）+ 控制台逐页断言结果。
# 只读语义：本脚本只点击导航与目标选择器，不点"执行注入/还原"。

[CmdletBinding()]
param(
  [string]$Exe = 'D:\破甲\toolkit\ui\dist\JailbreakTool.exe',
  [string]$OutDir = 'D:\破甲\toolkit\docs\ui-regression',
  [int]$WaitMs = 3000,
  [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace UiReg -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:Fail = @()
$script:Pass = 0

function Info($m) { Write-Host "  · $m" -ForegroundColor Gray }
function Ok($m)   { $script:Pass++; Write-Host "  ✔ $m" -ForegroundColor Green }
function Bad($m)  { $script:Fail += $m; Write-Host "  ✘ $m" -ForegroundColor Red }

function Get-ByType($root, $type) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  return $root.FindAll($TS::Descendants, $c)
}

function Get-ByName($root, $name) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  return $root.FindFirst($TS::Descendants, $c)
}

function Get-Texts($root, $limit = 400) {
  $out = New-Object System.Collections.ArrayList
  foreach ($t in (Get-ByType $root ([System.Windows.Automation.ControlType]::Text))) {
    try { $n = $t.Current.Name; if ($n) { [void]$out.Add($n) } } catch { }
    if ($out.Count -ge $limit) { break }
  }
  return $out
}

function Shot($hwnd, $file) {
  [void][UiReg.Native]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 400
  $r = New-Object UiReg.Native+RECT
  [void][UiReg.Native]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  if ($w -le 0 -or $h -le 0) { return $null }
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $g.Dispose()
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  return $file
}

function Select-Nav($root, $name) {
  $item = Get-ByName $root $name
  if (-not $item) { return $false }
  try {
    $p = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $p.Select()
  } catch {
    try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { return $false }
  }
  Start-Sleep -Milliseconds $WaitMs
  return $true
}

# ---------------------------------------------------------------- 启动
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-Process -Name 'AgentToolkit.Ui' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); Start-Sleep -Milliseconds 500 }

Write-Host "启动 $Exe" -ForegroundColor Cyan
$proc = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
  Start-Sleep -Milliseconds 500
  $proc.Refresh()
  if ($proc.HasExited) { Write-Host "进程已退出，exit=$($proc.ExitCode)" -ForegroundColor Red; exit 1 }
  if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "拿不到主窗口句柄" -ForegroundColor Red; exit 1 }
Info "主窗口句柄 $hwnd，标题 '$($proc.MainWindowTitle)'"
[void][UiReg.Native]::ShowWindow($hwnd, 3)   # maximize，保证截图完整
Start-Sleep -Seconds 3

$root = $AE::FromHandle($hwnd)

# ---------------------------------------------------------------- 目标选择器
Write-Host "`n== 目标选择器 ==" -ForegroundColor Cyan
$combo = (Get-ByType $root ([System.Windows.Automation.ControlType]::ComboBox)) | Select-Object -First 1
if (-not $combo) { Bad "找不到目标选择器（ComboBox）" }
else {
  Ok "目标选择器存在"
  try {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 800
    $items = Get-ByType $root ([System.Windows.Automation.ControlType]::ListItem)
    $names = @($items | ForEach-Object { $_.Current.Name })
    Info "下拉项：$($names -join ' | ')"
    $hasDsh = $names -match 'DeepSeek Harness'
    $hasZcode = $names -match 'ZCode'
    $hasOc = $names -match 'opencode'
    $hasAg = $names -match 'Antigravity'
    if ($hasDsh) { Ok "列出 DeepSeek Harness" } else { Bad "下拉里没有 DeepSeek Harness" }
    if ($hasZcode) { Ok "列出 ZCode" } else { Bad "下拉里没有 ZCode" }
    if ($hasOc) { Ok "列出 opencode" } else { Bad "下拉里没有 opencode" }
    if ($hasAg) { Ok "列出 Google Antigravity" } else { Bad "下拉里没有 Google Antigravity" }
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
  } catch { Bad "展开目标选择器失败：$($_.Exception.Message)" }
}

# ---------------------------------------------------------------- 逐页
$pages = @(
  @{ Nav = '状态';        Must = @('系统提示词层', '上下文载荷层'); Shot = 'status' },
  @{ Nav = '探测与诊断';  Must = @('探测');                         Shot = 'locate' },
  @{ Nav = '注入';        Must = @('预览');                         Shot = 'inject' },
  @{ Nav = '还原';        Must = @('还原');                         Shot = 'revert' },
  @{ Nav = '日志查看器';  Must = @('会话列表');                     Shot = 'logs' },
  @{ Nav = '差异对照';    Must = @('差异');                         Shot = 'diff' },
  @{ Nav = '注入文本';    Must = @('文本');                         Shot = 'policy' },
  @{ Nav = '验收台账';    Must = @('探针');                         Shot = 'probes' },
  @{ Nav = '关于';        Must = @('边界');                         Shot = 'about' }
)

Write-Host "`n== 逐页导航与内容断言 ==" -ForegroundColor Cyan
foreach ($p in $pages) {
  Write-Host "`n[$($p.Nav)]" -ForegroundColor White
  if (-not (Select-Nav $root $p.Nav)) { Bad "点不到导航项「$($p.Nav)」"; continue }
  $texts = Get-Texts $root
  $joined = ($texts -join "`n")
  if ($texts.Count -eq 0) { Bad "「$($p.Nav)」页面上没有任何文本元素（可能渲染失败）"; continue }
  Ok "页面渲染出 $($texts.Count) 个文本元素"

  foreach ($m in $p.Must) {
    if ($joined -like "*$m*") { Ok "含关键内容「$m」" } else { Bad "「$($p.Nav)」缺少关键内容「$m」" }
  }
  if ($joined -match '加载中|正在加载') { Bad "「$($p.Nav)」仍停在加载态" }
  if ($joined -match '操作未完成|引擎返回未知错误|未处理的异常') { Bad "「$($p.Nav)」出现错误态" }
  $shotFile = Join-Path $OutDir "$($p.Shot).png"
  if (Shot $hwnd $shotFile) { Ok "截图 $(Split-Path $shotFile -Leaf)" } else { Bad "「$($p.Nav)」截图失败" }
}

# ---------------------------------------------------------------- 真实数据断言（状态页）
Write-Host "`n== 状态页的真实引擎数据 ==" -ForegroundColor Cyan
[void](Select-Nav $root '状态')
$s = (Get-Texts $root) -join "`n"
if ($s -match '44\.0\.0') { Ok "显示 DSH 版本 44.0.0" } else { Bad "状态页没显示 44.0.0" }
if ($s -match '已注入') { Ok "显示「已注入」" } else { Bad "状态页没显示「已注入」" }
if ($s -match '备份干净') { Ok "显示「备份干净」" } else { Bad "状态页没显示备份健康度" }
if ($s -match '运行中') { Ok "显示客户端进程状态" } else { Bad "状态页没显示进程状态" }

# ---------------------------------------------------------------- 切目标
Write-Host "`n== 切换到 ZCode ==" -ForegroundColor Cyan
$combo = (Get-ByType $root ([System.Windows.Automation.ControlType]::ComboBox)) | Select-Object -First 1
if ($combo) {
  try {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 800
    $zc = (Get-ByType $root ([System.Windows.Automation.ControlType]::ListItem)) |
          Where-Object { $_.Current.Name -match 'ZCode' } | Select-Object -First 1
    if ($zc) {
      try { $zc.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
      catch { $zc.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
      Start-Sleep -Milliseconds ($WaitMs + 2000)
      $z = (Get-Texts $root) -join "`n"
      if ($z -match '3\.14\.4') { Ok "切到 ZCode 后状态页显示 3.14.4" } else { Bad "切到 ZCode 后没看到 3.14.4" }
      if ($z -match '宿主补丁层|zcode\.cjs') { Ok "显示 ZCode 的宿主补丁层" } else { Bad "切到 ZCode 后没看到补丁层" }
      [void](Shot $hwnd (Join-Path $OutDir 'status-zcode.png'))
      Ok "截图 status-zcode.png"
    } else { Bad "下拉里点不到 ZCode" }
  } catch { Bad "切换目标失败：$($_.Exception.Message)" }
}

function Click-Button($root, $name) {
  $btn = Get-ByName $root $name
  if (-not $btn) { return $false }
  # Expander 头暴露的是 ExpandCollapse/Toggle，普通按钮才是 Invoke —— 依次试
  try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds ($WaitMs + 1000); return $true } catch { }
  try { $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds ($WaitMs + 1000); return $true } catch { }
  try { $btn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds ($WaitMs + 1000); return $true } catch { }
  try { $btn.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds ($WaitMs + 1000); return $true } catch { }
  return $false
}

# ---------------------------------------------------------------- 真实客户端文件的只读证明
$watch = @()
foreach ($p in @(
  "$env:USERPROFILE\.dsh\profiles\desktop\cordis.patch.yml",
  "$env:USERPROFILE\.dsh\AGENTS.md",
  "$env:USERPROFILE\.zcode\AGENTS.md",
  'E:\APP\zcode\resources\glm\zcode.cjs')) {
  if (Test-Path -LiteralPath $p) {
    $watch += [pscustomobject]@{ Path = $p; Before = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
  }
}
Write-Host "`n== 只读证明：盯住 $($watch.Count) 个真实客户端文件 ==" -ForegroundColor Cyan

# 按钮断言针对 DSH（台账、备份、预览数据都在它身上）；ZCode 的差异要算 14.8 MB bundle，慢得多
Write-Host "`n== 切回 DeepSeek Harness 再做按钮断言 ==" -ForegroundColor Cyan
$combo = (Get-ByType $root ([System.Windows.Automation.ControlType]::ComboBox)) | Select-Object -First 1
if ($combo) {
  try {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 800
    $dshItem = (Get-ByType $root ([System.Windows.Automation.ControlType]::ListItem)) |
               Where-Object { $_.Current.Name -match 'DeepSeek Harness' } | Select-Object -First 1
    if ($dshItem) {
      try { $dshItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
      catch { $dshItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
      Start-Sleep -Milliseconds ($WaitMs + 2000)
      Ok "已切回 DeepSeek Harness"
    } else { Bad "切不回 DeepSeek Harness" }
  } catch { Bad "切回目标失败：$($_.Exception.Message)" }
}

# ---------------------------------------------------------------- 只读按钮交互
Write-Host "`n== 只读按钮交互（真走引擎，但不写客户端） ==" -ForegroundColor Cyan

[void](Select-Nav $root '状态')
if (Click-Button $root '跑自检') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match '系统提示词|标记命中') { Ok "状态页「跑自检」执行后有结果" } else { Bad "状态页跑自检后没有结果" }
} else { Bad "点不到「跑自检」" }

[void](Select-Nav $root '探测与诊断')
if (Click-Button $root '开始探测 / 重试') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match 'E:\\APP\\(DSH|zcode)') { Ok "探测页重新探测后拿到安装根" } else { Bad "探测后没看到安装根" }
} else { Bad "点不到「开始探测 / 重试」" }

[void](Select-Nav $root '注入')
if (Click-Button $root '重新预览') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match '跳过|将写入|无需写入') { Ok "注入页预览出计划变更（跳过/将写入）" } else { Bad "预览后没看到计划变更" }
  if ($t -match 'cordis\.patch\.yml|AGENTS\.md') { Ok "预览列出了目标文件" } else { Bad "预览没列出目标文件" }
} else { Bad "点不到「重新预览」" }

[void](Select-Nav $root '还原')
if (Click-Button $root '试运行预览') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match '回读|哈希|试运行|尚未') { Ok "还原页试运行有回读/哈希证据区" } else { Bad "还原页试运行没有证据区" }
} else { Bad "点不到「试运行预览」" }

[void](Select-Nav $root '差异对照')
if (Click-Button $root '刷新差异') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match '备份|当前') { Ok "差异页刷新后标出前后来源" } else { Bad "差异页没标出来源" }
} else { Bad "点不到「刷新差异」" }

[void](Select-Nav $root '日志查看器')
if (Click-Button $root '刷新会话') { Ok "日志页刷新会话" } else { Bad "点不到「刷新会话」" }
# "已导出文件" 是默认折叠的 Expander，里面的按钮在展开前不在 UIA 树里
if (Click-Button $root '已导出文件') { Ok "展开「已导出文件」区" } else { Bad "点不到「已导出文件」" }
if (Click-Button $root '导出会话原文') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match '刚导出会话原文|\.jsonl') { Ok "日志页导出会话原文成功" } else { Bad "导出后没看到路径" }
  [void](Shot $hwnd (Join-Path $OutDir 'logs-export.png'))
} else { Bad "点不到「导出会话原文」" }

[void](Select-Nav $root '注入文本')
if (Click-Button $root '刷新文本') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match 'persona|agents|KB') { Ok "注入文本页刷新后有内容" } else { Bad "注入文本页刷新后没有内容" }
} else { Bad "点不到「刷新文本」" }

[void](Select-Nav $root '验收台账')
if (Click-Button $root '刷新台账') {
  $t = (Get-Texts $root) -join "`n"
  if ($t -match 'Broken|破了|没破|半破') { Ok "验收台账页显示历史判定" } else { Bad "台账页没显示历史判定" }
} else { Bad "点不到「刷新台账」" }

# ---------------------------------------------------------------- 只读证明核对
Write-Host "`n== 只读证明核对 ==" -ForegroundColor Cyan
foreach ($w in $watch) {
  $after = (Get-FileHash -LiteralPath $w.Path -Algorithm SHA256).Hash
  if ($after -eq $w.Before) { Ok "$(Split-Path $w.Path -Leaf) 未被改动" }
  else { Bad "$(Split-Path $w.Path -Leaf) 被改动了！" }
}

# ---------------------------------------------------------------- 收尾
Write-Host "`n== 汇总 ==" -ForegroundColor Cyan
Write-Host "通过 $script:Pass 项，失败 $($script:Fail.Count) 项" -ForegroundColor $(if ($script:Fail.Count) { 'Red' } else { 'Green' })
foreach ($f in $script:Fail) { Write-Host "  - $f" -ForegroundColor Red }
Write-Host "截图目录：$OutDir"

if (-not $KeepOpen) {
  try { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 } catch { }
  Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
  Info "已关闭测试窗口"
}
exit $(if ($script:Fail.Count) { 1 } else { 0 })
