# 破甲 · 集成验收脚本（只读 + 沙箱，不写真实客户端）
#
#   pwsh -File toolkit\verify.ps1                # 全部步骤
#   pwsh -File toolkit\verify.ps1 -SkipBuild     # 跳过构建，直接跑检查
#   pwsh -File toolkit\verify.ps1 -Target zcode  # 只验一个目标
#
# 退出码：0 = 全过；1 = 有步骤失败；2 = 环境缺件。
# 设计纪律：默认路径**不写真实客户端**——只跑 status / preview / check / diff / sessions。
# 真实写入（apply / revert）不在这里做，那是需要单独决定并带 dry-run 预览的动作。

[CmdletBinding()]
param(
  [string]$Target = "",
  [switch]$SkipBuild,
  [switch]$SkipSelfTest,
  [string]$Dotnet = "C:\Program Files\dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Sln     = Join-Path $Root "AgentToolkit.slnx"
$Core    = Join-Path $Root "backend\AgentToolkit.Core\AgentToolkit.Core.csproj"
$Cli     = Join-Path $Root "backend\AgentToolkit.Engine.Cli\AgentToolkit.Engine.Cli.csproj"
$SelfTest= Join-Path $Root "backend\AgentToolkit.SelfTest\AgentToolkit.SelfTest.csproj"
$Ui      = Join-Path $Root "ui\AgentToolkit.Ui.csproj"
$Engine  = Join-Path $Root "backend\dist\engine.exe"

$script:Failures = @()
$script:Step = 0

function Section([string]$text) {
  $script:Step++
  Write-Host ""
  Write-Host ("=" * 72) -ForegroundColor DarkGray
  Write-Host ("[{0}] {1}" -f $script:Step, $text) -ForegroundColor Cyan
  Write-Host ("=" * 72) -ForegroundColor DarkGray
}

function Ok([string]$text)   { Write-Host "  ✔ $text" -ForegroundColor Green }
function Bad([string]$text)  { Write-Host "  ✘ $text" -ForegroundColor Red; $script:Failures += $text }
function Info([string]$text) { Write-Host "  · $text" -ForegroundColor Gray }

function Invoke-Step([string]$name, [scriptblock]$body) {
  try { & $body } catch { Bad "$name 抛出异常：$($_.Exception.Message)" }
}

if (-not (Test-Path -LiteralPath $Dotnet)) { Write-Host "找不到 dotnet：$Dotnet" -ForegroundColor Red; exit 2 }
if (-not (Test-Path -LiteralPath $Sln))    { Write-Host "找不到解决方案：$Sln" -ForegroundColor Red; exit 2 }

$targets = if ($Target) { @($Target) } else { @("dsh", "zcode", "opencode", "antigravity") }

# ---------------------------------------------------------------- 1. 构建
if (-not $SkipBuild) {
  Section "构建（Release）"
  Invoke-Step "解决方案构建" {
    $out = & $Dotnet build $Sln -c Release --nologo -v q 2>&1
    $tail = ($out | Select-Object -Last 6) -join "`n"
    if ($LASTEXITCODE -ne 0) { Bad "解决方案构建失败：`n$tail" }
    else { Ok "解决方案构建通过（0 错误）" }
  }

  Invoke-Step "发布单文件 engine.exe" {
    $out = & $Dotnet publish $Cli -c Release -o (Join-Path $Root "backend\dist") --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Engine)) { Bad "engine.exe 发布失败：`n$(($out | Select-Object -Last 6) -join "`n")" }
    else {
      $mb = [math]::Round((Get-Item -LiteralPath $Engine).Length / 1MB, 1)
      Ok "engine.exe 已发布（$mb MB，自包含；不依赖 Node / PowerShell / .NET 运行时）"
    }
  }

  Invoke-Step "构建 WinUI 界面" {
    $out = & $Dotnet build $Ui -c Release -r win-x64 --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { Bad "界面构建失败：`n$(($out | Select-Object -Last 8) -join "`n")" }
    else { Ok "界面构建通过（0 错误）" }
  }
}

if (-not (Test-Path -LiteralPath $Engine)) { Write-Host "engine.exe 不存在，先去掉 -SkipBuild" -ForegroundColor Red; exit 2 }

function Engine([string[]]$EngineArgs) {
  $out = & $Engine @EngineArgs 2>&1
  return @{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}

# ---------------------------------------------------------------- 2. 沙箱回归自检
if (-not $SkipSelfTest) {
  Section "沙箱回归自检（合成 fixture，不碰真实客户端）"
  foreach ($t in $targets) {
    Invoke-Step "自检 $t" {
      $r = & $Dotnet run --project $SelfTest -- --target $t 2>&1
      $text = ($r -join "`n")
      Write-Host $text
      if ($text -match "没有匹配的自检用例") { Info "$t 没有自检用例（尚未实现）" }
      elseif ($text -match "失败 0 个") { Ok "$t 自检通过" }
      else { Bad "$t 自检失败（见上面的输出）" }
    }
  }
}

# ---------------------------------------------------------------- 3. 目标识别
Section "目标识别"
Invoke-Step "targets" {
  $r = Engine @("targets")
  Write-Host $r.Text
  foreach ($t in $targets) { if ($r.Text -notmatch [regex]::Escape($t)) { Bad "目标列表里没有 $t" } else { Ok "识别到目标 $t" } }
}

# ---------------------------------------------------------------- 4. 只读验收
foreach ($t in $targets) {
  Section "目标 $t 只读验收"

  Invoke-Step "$t locate" {
    $r = Engine @("--target", $t, "locate")
    if ($r.Text -match "未找到") { Bad "$t 未定位到安装根" } else { Ok "$t 定位成功" }
    Write-Host $r.Text
  }

  Invoke-Step "$t status" {
    $r = Engine @("--target", $t, "status")
    Write-Host $r.Text
    if ($r.Text -match "备份污染") { Bad "$t 备份被判定为污染——必须走逆向撤销路径，不能用它还原" }
    elseif ($r.Text -match "备份干净") { Ok "$t 备份健康度：干净" }
    elseif ($r.Text -match "备份缺失") { Info "$t 备份缺失（首次注入前属正常）" }
    if ($r.Text -match "已安装但未生效") { Bad "$t 报「已安装但未生效」——按 SPEC §1.5 回到层判定" }
  }

  Invoke-Step "$t preview（只读）" {
    $before = @{}
    $r = Engine @("--target", $t, "preview")
    Write-Host $r.Text
    if ($r.Text -match "可执行：True") { Ok "$t 预览显示可执行（本次不执行）" }
    elseif ($r.Text -match "未找到|适配器未接线") { Bad "$t 预览不可用" }
    else { Info "$t 预览：无可打补丁（可能已注入或锚点失配）" }
  }

  Invoke-Step "$t sessions" {
    $r = Engine @("--target", $t, "sessions")
    Write-Host (($r.Text -split "`n" | Select-Object -First 6) -join "`n")
    if ($r.Text -match "没有会话|未找到") { Info "$t 没有会话日志" } else { Ok "$t 会话日志可读" }
  }

  Invoke-Step "$t check latest（命中数自检）" {
    # 先看盘上是不是注入态：出厂态的目标（没注入过）命中 0 是正确状态，不是失败
    $applied = $false
    try {
      $st = (Engine @("--target", $t, "status", "--json")).Text | ConvertFrom-Json
      $applied = @($st.Layers | Where-Object { $_.State -eq "Applied" }).Count -gt 0
    } catch { $applied = $true }   # 拿不到状态就按原来的严格口径走

    $r = Engine @("--target", $t, "check", "latest", "--json")
    $report = $null
    try { $report = $r.Text | ConvertFrom-Json } catch { }
    if ($null -eq $report) { Bad "$t check 没有返回可解析的报告" }
    else {
      Info ("会话 {0} · 全文 {1} 字符 · 系统层 {2} · 载荷层 {3}" -f `
        $report.SessionId, $report.TotalChars, `
        $(if ($report.SystemPromptLayerPresent) { "命中" } else { "命中 0" }), `
        $(if ($report.ContextPayloadLayerPresent) { "命中" } else { "命中 0" }))
      foreach ($h in $report.Hits) { if ($h.Count -gt 0) { Info ("  {0,4}  {1,-18} {2}" -f $h.Count, $h.Layer, $h.Marker) } }
      Write-Host ("  " + $report.Verdict)
      if (-not $applied) {
        Info "$t 是出厂态（没注入过）：命中 0 属正确状态，跳过命中数断言"
      }
      elseif ($report.TotalChars -eq 0) { Bad "$t 会话文本为空：日志读取失败或该会话没有内容" }
      elseif (-not $report.SystemPromptLayerPresent -and -not $report.ContextPayloadLayerPresent) { Bad "$t 命中 0：注入未生效，不要评价模型态度" }
      elseif (-not $report.SystemPromptLayerPresent) { Bad "$t 系统提示词层命中 0：多半被更高优先的作用域遮蔽" }
      elseif (-not $report.ContextPayloadLayerPresent) { Bad "$t 上下文载荷层命中 0：载荷未加载" }
      else { Ok "$t 两层都进了模型上下文" }
    }
  }
}

# ---------------------------------------------------------------- 5. 汇总
Write-Host ""
Write-Host ("=" * 72) -ForegroundColor DarkGray
if ($script:Failures.Count -eq 0) {
  Write-Host "全部检查通过。真实写入（apply / revert）不在本脚本范围内。" -ForegroundColor Green
  exit 0
}
Write-Host "失败 $($script:Failures.Count) 项：" -ForegroundColor Red
foreach ($f in $script:Failures) { Write-Host "  - $f" -ForegroundColor Red }
exit 1
