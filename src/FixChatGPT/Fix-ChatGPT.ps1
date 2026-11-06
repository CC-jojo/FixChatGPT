<#
    Fix-ChatGPT.ps1   ——  ChatGPT 打不开 一键修复
    ------------------------------------------------------------
    针对 Clash Verge / mihomo 内核环境，自动完成：
      1. 定位 Clash Verge 安装位置并确保代理软件在运行
      2. 检测「重复内核 / 孤儿内核 / 控制端口被抢占」这类导致
         界面设置不生效的故障，必要时干净重启
      3. 切回「规则模式」（全局模式会绕过 ChatGPT 专用分流）
      4. 逐个探测节点，自动挑出真正能连通 OpenAI 的节点
      5. 校验系统代理是否指向正确的本地端口
      6. 真实请求校验 chatgpt.com

    用法：
      双击「一键修复.bat」，或在 PowerShell 里执行本脚本
      可选参数： -DryRun        只看诊断结果，不做任何修改
                 -ProbeTimeoutMs  节点探测超时（毫秒），默认 6000
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [int]$ProbeTimeoutMs = 6000
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
# 本脚本访问 127.0.0.1 的 Clash API 时不能被系统代理劫持
try { [Net.WebRequest]::DefaultWebProxy = [Net.GlobalProxySelection]::GetEmptyWebProxy() } catch {}

$script:ApiPort    = 0
$script:MixedPort  = 0
$script:VergeExe   = $null
$script:Fixes      = New-Object System.Collections.ArrayList
$script:LogPath    = Join-Path $env:TEMP ('Fix-ChatGPT_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.log')
try { Start-Transcript -Path $script:LogPath -Force | Out-Null } catch {}

# ============================ 输出 ============================
function Write-Title($t) {
    Write-Host ''
    Write-Host ('  ' + $t) -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * ($t.Length + 2))) -ForegroundColor DarkGray
}
function Write-Step($i, $total, $t) {
    Write-Host ''
    Write-Host ("[$i/$total] " + $t) -ForegroundColor White
}
function Write-Ok  ($m) { Write-Host ("       [OK] " + $m) -ForegroundColor Green }
function Write-Bad ($m) { Write-Host ("       [!!] " + $m) -ForegroundColor Red }
function Write-Note($m) { Write-Host ("       [--] " + $m) -ForegroundColor Yellow }
function Write-Dim ($m) { Write-Host ("            " + $m) -ForegroundColor DarkGray }
function Add-Fix  ($m) { [void]$script:Fixes.Add($m) }

# ============================ 基础工具 ============================
function Get-CoreProcesses {
    $names = @('verge-mihomo', 'mihomo', 'clash-meta')
    $list  = @()
    foreach ($n in $names) {
        $ps = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($ps) { $list += $ps }
    }
    return @($list)
}

function Get-VergeProcesses {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '(?i)^clash-verge' })
}

function Get-ListenerPids([int]$port) {
    $pids = @()
    try {
        $pids = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
                  Select-Object -ExpandProperty OwningProcess -Unique)
    } catch {
        $pids = @()
    }
    if ($pids.Count -eq 0) {
        # 退化方案：解析 netstat
        $lines = @(netstat -ano 2>$null | Select-String ("LISTENING\s+\d+\s*$") | Select-String (":" + $port + "\s"))
        foreach ($l in $lines) {
            $m = [regex]::Match($l.ToString(), '(\d+)\s*$')
            if ($m.Success) { $pids += [int]$m.Groups[1].Value }
        }
    }
    return @($pids | Where-Object { $_ -gt 0 } | Select-Object -Unique)
}

function Get-ListeningPortsOfPid([int]$procId) {
    try {
        return @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
                 Where-Object { $_.OwningProcess -eq $procId } |
                 Select-Object -ExpandProperty LocalPort -Unique)
    } catch { return @() }
}

function Invoke-ClashApi {
    param([string]$Path, [string]$Method = 'GET', $Body = $null)
    $uri = 'http://127.0.0.1:' + $script:ApiPort + $Path
    $args = @{ Uri = $uri; Method = $Method; TimeoutSec = 8; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $args['Body']        = ($Body | ConvertTo-Json -Compress)
        $args['ContentType'] = 'application/json'
    }
    return Invoke-WebRequest @args
}

function Test-ClashApiAlive([int]$port) {
    if ($port -le 0) { return $false }
    try {
        $r = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port + '/version') -TimeoutSec 2 -UseBasicParsing
        return ($r.StatusCode -eq 200)
    } catch { return $false }
}

# ============================ 1. 定位程序 ============================
function Find-VergeExe {
    foreach ($p in (Get-VergeProcesses)) {
        try { if ($p.Path -and (Test-Path $p.Path)) { return $p.Path } } catch {}
    }
    $cands = @(
        (Join-Path $env:ProgramFiles 'Clash Verge\clash-verge.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Clash Verge\clash-verge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Clash Verge\clash-verge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Clash Verge\clash-verge.exe'),
        'D:\APP\CLASH\INSTALL PLACE\clash-verge.exe',
        'C:\APP\CLASH\INSTALL PLACE\clash-verge.exe'
    )
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return $c } }

    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($r in $roots) {
        $items = Get-ItemProperty $r -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match '(?i)clash' }
        foreach ($i in $items) {
            foreach ($f in @($i.DisplayIcon, $i.InstallLocation)) {
                if (-not $f) { continue }
                $f = ([string]$f).Trim('"')
                if (Test-Path $f -PathType Container) {
                    $t = Join-Path $f 'clash-verge.exe'
                    if (Test-Path $t) { return $t }
                } elseif (Test-Path $f) { return $f }
            }
        }
    }

    foreach ($root in @('C:\', 'D:\', 'E:\')) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem -Path $root -Filter 'clash-verge.exe' -Recurse -Depth 3 -File -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Start-VergeDetached([string]$exePath) {
    # 必须借 explorer 启动，否则进程会随当前会话结束被回收
    $dir = Split-Path -Parent $exePath
    Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $exePath + '"') | Out-Null
    return $dir
}

function Wait-ApiReady([int]$maxSec = 45) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $maxSec) {
        if ($script:ApiPort -gt 0 -and (Test-ClashApiAlive $script:ApiPort)) { return $true }
        foreach ($cp in (Get-CoreProcesses)) {
            foreach ($pt in (Get-ListeningPortsOfPid $cp.Id)) {
                if (Test-ClashApiAlive $pt) { $script:ApiPort = $pt; return $true }
            }
        }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

# ============================ 2. 端口识别 ============================
function Resolve-ApiPort {
    $tried = @()
    foreach ($cp in (Get-CoreProcesses)) { $tried += (Get-ListeningPortsOfPid $cp.Id) }
    $tried += @(9097, 9090, 9091, 6170)
    foreach ($pt in ($tried | Select-Object -Unique)) {
        if (Test-ClashApiAlive $pt) { return [int]$pt }
    }
    return 0
}

function Resolve-MixedPort([int]$apiPort) {
    $ports = @()
    foreach ($cp in (Get-CoreProcesses)) { $ports += (Get-ListeningPortsOfPid $cp.Id) }
    $ports = @($ports | Where-Object { $_ -ne $apiPort -and $_ -gt 0 } | Select-Object -Unique)
    foreach ($prefer in @(7897, 7890, 7891, 1080, 10808, 7080)) {
        if ($ports -contains $prefer) { return [int]$prefer }
    }
    if ($ports.Count -gt 0) { return [int]$ports[0] }
    return 0
}

# ============================ 3. 系统代理 ============================
function Get-SystemProxyState {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $v = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
    return @{
        Enabled   = ($v.ProxyEnable -eq 1)
        Server    = [string]$v.ProxyServer
        AutoConfig = [string]$v.AutoConfigURL
    }
}

function Set-SystemProxyToLocal([int]$port) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    Set-ItemProperty -Path $key -Name 'ProxyEnable' -Value 1
    Set-ItemProperty -Path $key -Name 'ProxyServer' -Value ('127.0.0.1:' + $port)
    $sig = @'
using System;
using System.Runtime.InteropServices;
public class WinInetRefresh {
    [DllImport("wininet.dll", SetLastError = true)]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
'@
    try {
        Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
        [void][WinInetRefresh]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)  # SETTINGS_CHANGED
        [void][WinInetRefresh]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)  # REFRESH
    } catch {}
}

# ============================ 4. 网络校验 ============================
function Test-ThroughProxy {
    param([string]$Url, [int]$ProxyPort, [int]$TimeoutSec = 15)
    $ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
    try {
        $r = Invoke-WebRequest -Uri $Url -Proxy ('http://127.0.0.1:' + $ProxyPort) -TimeoutSec $TimeoutSec `
             -UseBasicParsing -MaximumRedirection 5 -Headers @{ 'User-Agent' = $ua }
        return @{ Ok = $true; Code = [int]$r.StatusCode; Msg = '' }
    } catch {
        $resp = $null
        try { $resp = $_.Exception.Response } catch {}
        if ($null -ne $resp) {
            $code = 0
            try { $code = [int]$resp.StatusCode } catch {}
            if ($code -gt 0) { return @{ Ok = $true; Code = $code; Msg = '' } }
        }
        return @{ Ok = $false; Code = 0; Msg = $_.Exception.Message }
    }
}

function Get-NodeDelay {
    param([string]$Node, [string]$Url = 'https://chatgpt.com')
    $path = '/proxies/' + [uri]::EscapeDataString($Node) + '/delay?timeout=' + $ProbeTimeoutMs + '&url=' + [uri]::EscapeDataString($Url)
    try {
        $r = Invoke-ClashApi -Path $path
        $j = $r.Content | ConvertFrom-Json
        if ($j.delay) { return [int]$j.delay }
        return $null
    } catch { return $null }
}

# ============================ 主流程 ============================
Write-Host ''
Write-Host '  ================================================' -ForegroundColor Cyan
Write-Host '        ChatGPT 打不开  ·  一键修复工具' -ForegroundColor Cyan
Write-Host '  ================================================' -ForegroundColor Cyan
if ($DryRun) { Write-Note '当前为「只看不改」模式（-DryRun），不会修改任何设置' }

$TOTAL = 7
$ok = $false

try {
    # ---------- 1 ----------
    Write-Step 1 $TOTAL '定位代理软件'
    $script:VergeExe = Find-VergeExe
    if (-not $script:VergeExe) {
        Write-Bad '没有找到 Clash Verge。'
        Write-Dim '请确认已安装 Clash Verge，或本脚本不适用于你的代理软件。'
        throw 'not-found'
    }
    Write-Ok ('程序位置：' + $script:VergeExe)
    $vergeRunning = @(Get-VergeProcesses).Count -gt 0
    $cores        = Get-CoreProcesses
    if ($vergeRunning) { Write-Ok 'Clash Verge 正在运行' } else { Write-Note 'Clash Verge 未运行，稍后会启动它' }
    Write-Ok ('内核进程数量：' + $cores.Count)

    # ---------- 2 ----------
    Write-Step 2 $TOTAL '检查内核与控制端口是否健康'
    $script:ApiPort = Resolve-ApiPort
    $needRestart = $false
    $reason      = ''

    if (-not $vergeRunning) {
        $needRestart = $true; $reason = 'Clash Verge 未运行'
    } elseif ($cores.Count -eq 0) {
        $needRestart = $true; $reason = '没有找到内核进程'
    } elseif ($cores.Count -gt 1) {
        $needRestart = $true; $reason = ('检测到 ' + $cores.Count + ' 个内核同时运行（重复/孤儿内核）')
    } elseif ($script:ApiPort -eq 0) {
        $needRestart = $true; $reason = '内核控制端口（API）无法访问'
    }

    if (-not $needRestart) {
        $script:MixedPort = Resolve-MixedPort $script:ApiPort
        $apiPids   = Get-ListenerPids $script:ApiPort
        $mixedPids = @()
        if ($script:MixedPort -gt 0) { $mixedPids = Get-ListenerPids $script:MixedPort }
        $overlap = @($apiPids | Where-Object { $mixedPids -contains $_ })
        if ($mixedPids.Count -eq 0) {
            $needRestart = $true; $reason = '没有找到本地代理监听端口'
        } elseif ($overlap.Count -eq 0) {
            $needRestart = $true; $reason = ('控制端口与代理端口不在同一个内核上（' + $script:ApiPort + ' vs ' + $script:MixedPort + '）')
        }
    }

    if ($needRestart) {
        Write-Bad ('异常：' + $reason)
        Write-Dim '这会导致「界面上改设置不生效」，ChatGPT 因此一直打不开。'
        if ($DryRun) {
            Write-Note '（DryRun）本应清理内核并重启 Clash Verge'
        } else {
            if ($cores.Count -gt 0) {
                foreach ($c in $cores) { Stop-Process -Id $c.Id -Force -ErrorAction SilentlyContinue }
                Start-Sleep -Milliseconds 800
            }
            foreach ($v in (Get-VergeProcesses)) { Stop-Process -Id $v.Id -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Milliseconds 1500
            foreach ($c in (Get-CoreProcesses)) { Stop-Process -Id $c.Id -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Milliseconds 1200

            if ($vergeRunning) { Add-Fix '清理重复/孤儿内核并重启 Clash Verge' }
            else { Add-Fix '启动 Clash Verge' }

            Start-VergeDetached $script:VergeExe | Out-Null
            Write-Dim '已请求启动，等待内核就绪……'
            if (-not (Wait-ApiReady 45)) { throw 'api-not-ready' }

            $script:MixedPort = Resolve-MixedPort $script:ApiPort
            $cores = Get-CoreProcesses
            Write-Ok ('内核已就绪：进程 ' + $cores.Count + ' 个，API 端口 ' + $script:ApiPort + '，代理端口 ' + $script:MixedPort)
        }
    } else {
        Write-Ok ('内核状态正常（单内核），API 端口 ' + $script:ApiPort + '，代理端口 ' + $script:MixedPort)
    }

    # ---------- 3 ----------
    Write-Step 3 $TOTAL '检查分流模式'
    $cfg = (Invoke-ClashApi -Path '/configs').Content | ConvertFrom-Json
    $mode = [string]$cfg.mode
    $tunOn = $false
    try { $tunOn = [bool]$cfg.tun.enable } catch {}
    Write-Ok ('当前模式：' + $mode + '（TUN：' + $(if ($tunOn) { '开启' } else { '关闭' }) + '）')
    if ($mode -ne 'rule') {
        Write-Bad '处于「' + $mode + '」模式：所有流量被强制走同一个节点，ChatGPT 专用分流被绕过'
        if (-not $DryRun) {
            [void](Invoke-ClashApi -Path '/configs' -Method 'PATCH' -Body @{ mode = 'rule' })
            Add-Fix '分流模式已切换为「规则模式」'
            Write-Ok '已切换为规则模式'
        } else { Write-Note '（DryRun）本应切换为规则模式' }
    } else {
        Write-Ok '规则模式正确，ChatGPT 会走专用策略组'
    }

    # ---------- 4 ----------
    Write-Step 4 $TOTAL '定位 ChatGPT 策略组'
    $proxies = (Invoke-ClashApi -Path '/proxies').Content | ConvertFrom-Json
    $groupName = $null
    $members   = @()
    foreach ($prop in $proxies.PSObject.Properties) {
        $v = $prop.Value
        if ($v.type -ne 'Selector') { continue }
        if ($prop.Name -match '(?i)chatgpt|openai') { $groupName = $prop.Name; $members = @($v.all); break }
    }
    if (-not $groupName) {
        foreach ($prop in $proxies.PSObject.Properties) {
            if ($prop.Name -eq 'GLOBAL') {
                $v = $prop.Value
                if ($v.all -and $v.all.Count -gt 0) { $groupName = 'GLOBAL'; $members = @($v.all) }
            }
        }
        if ($groupName) {
            Write-Note '订阅里没有 ChatGPT 专用策略组，改用 GLOBAL 组挑选节点'
        } else {
            Write-Bad '没有找到任何可选策略组，无法自动选节点'
            throw 'no-group'
        }
    } else {
        Write-Ok ('策略组：' + $groupName + '，共 ' + $members.Count + ' 个候选')
        $cur = $proxies.$groupName.now
        Write-Ok ('当前选中：' + $cur)
    }

    # ---------- 5 ----------
    Write-Step 5 $TOTAL '逐个探测节点（目标 chatgpt.com）'
    $results = @()
    $i = 0
    foreach ($n in $members) {
        if ($n -match '(?i)^(DIRECT|REJECT|REJECT-DROP|PASS|COMPATIBLE)$') { continue }
        $i++
        Write-Host ('       · 探测 ' + $n) -NoNewline -ForegroundColor DarkGray
        $d = Get-NodeDelay $n
        if ($null -ne $d) {
            Write-Host ('  ' + $d + 'ms') -ForegroundColor Green
            $results += [pscustomobject]@{ Name = $n; Delay = $d }
        } else {
            Write-Host '  不通' -ForegroundColor DarkGray
        }
    }
    $usable = @($results | Sort-Object Delay)
    if ($usable.Count -eq 0) {
        Write-Bad '所有节点都无法连通 chatgpt.com'
        Write-Dim '通常是订阅到期、流量用尽，或该机场的 OpenAI 线路全部失效。'
        throw 'no-node'
    }
    Write-Ok ('可用节点 ' + $usable.Count + ' / ' + $i + ' 个，最快：' + $usable[0].Name + '（' + $usable[0].Delay + 'ms）')

    # ---------- 6 ----------
    Write-Step 6 $TOTAL '应用节点并做真实请求校验'
    $finalNode = $null
    $finalCode = 0
    foreach ($cand in ($usable | Select-Object -First 4)) {
        if ($DryRun) {
            Write-Note ('（DryRun）本应切换到：' + $cand.Name)
            $finalNode = $cand.Name
            break
        }
        [void](Invoke-ClashApi -Path ('/proxies/' + [uri]::EscapeDataString($groupName)) -Method 'PUT' -Body @{ name = $cand.Name })
        Start-Sleep -Milliseconds 400
        $t = Test-ThroughProxy -Url 'https://chatgpt.com' -ProxyPort $script:MixedPort -TimeoutSec 15
        if ($t.Ok) {
            Write-Ok ('切换到 ' + $cand.Name + ' → chatgpt.com 返回 ' + $t.Code + '，连通正常')
            $finalNode = $cand.Name
            $finalCode = $t.Code
            break
        }
        Write-Note ($cand.Name + ' 延迟正常但请求失败：' + $t.Msg)
    }
    if (-not $finalNode) {
        Write-Bad '没有节点能真正打开 chatgpt.com'
        throw 'verify-failed'
    }
    if ($proxies.$groupName.now -ne $finalNode) { Add-Fix ('ChatGPT 节点已切换为：' + $finalNode) }

    # ---------- 7 ----------
    Write-Step 7 $TOTAL '校验系统代理'
    $sp = Get-SystemProxyState
    $want = '127.0.0.1:' + $script:MixedPort
    if ($tunOn) {
        Write-Ok '检测到 TUN 模式，无需依赖系统代理'
    } elseif ($sp.AutoConfig) {
        Write-Note ('检测到自动配置脚本（PAC）：' + $sp.AutoConfig + '，不自动修改')
    } elseif (-not $sp.Enabled) {
        Write-Bad '系统代理处于关闭状态，浏览器不会走代理'
        if (-not $DryRun) {
            Set-SystemProxyToLocal $script:MixedPort
            Add-Fix ('系统代理已开启并指向 ' + $want)
            Write-Ok ('已开启系统代理：' + $want)
        } else { Write-Note ('（DryRun）本应开启系统代理：' + $want) }
    } elseif ($sp.Server -ne $want) {
        Write-Bad ('系统代理指向 ' + $sp.Server + '，但本地代理端口是 ' + $script:MixedPort)
        if (-not $DryRun) {
            Set-SystemProxyToLocal $script:MixedPort
            Add-Fix ('系统代理已修正为 ' + $want)
            Write-Ok ('已修正系统代理：' + $want)
        } else { Write-Note ('（DryRun）本应修正为 ' + $want) }
    } else {
        Write-Ok ('系统代理正确：' + $sp.Server)
    }

    $ok = $true

} catch {
    $err = $_.Exception.Message
    Write-Host ''
    if ($err -eq 'api-not-ready') {
        Write-Bad 'Clash Verge 已启动，但内核控制端口在 45 秒内没有就绪。'
        Write-Dim '请手动打开 Clash Verge 主界面确认是否正常启动，然后重新运行本工具。'
    } elseif ($err -eq 'no-node') {
        Write-Dim '建议：打开 Clash Verge → 订阅 → 更新订阅后重新运行本工具。'
    } elseif ($err -eq 'verify-failed') {
        Write-Dim '建议：在 Clash Verge 里做一次「延迟测试」，换一个机场节点后重新运行本工具。'
    } elseif ($err -eq 'not-found' -or $err -eq 'no-group') {
        # 已在上面输出过原因
    } else {
        Write-Bad ('执行中断：' + $err)
    }
}

# ============================ 汇总 ============================
Write-Host ''
Write-Host '  ================================================' -ForegroundColor Cyan
if ($ok) {
    Write-Host '   修复完成，现在可以刷新浏览器打开 ChatGPT' -ForegroundColor Green
} else {
    Write-Host '   修复未完成，请查看上方红色提示' -ForegroundColor Red
}
Write-Host '  ================================================' -ForegroundColor Cyan

if ($script:Fixes.Count -gt 0) {
    Write-Host '  本次做过的改动：' -ForegroundColor White
    $k = 0
    foreach ($f in $script:Fixes) { $k++; Write-Host ('    ' + $k + '. ' + $f) -ForegroundColor Gray }
} else {
    Write-Host '  本次没有修改任何设置（环境本来就正常）' -ForegroundColor Gray
}

if ($ok) {
    Write-Host ''
    Write-Host '  提示：' -ForegroundColor Yellow
    Write-Host '   · 若下次又打不开，直接再运行一次本工具即可' -ForegroundColor Gray
    Write-Host '   · 关闭 Clash Verge 请用托盘图标「退出」，不要用任务管理器强杀' -ForegroundColor Gray
    Write-Host ('   · 详细日志：' + $script:LogPath) -ForegroundColor DarkGray
}

Write-Host ''
try { Stop-Transcript | Out-Null } catch {}
exit $(if ($ok) { 0 } else { 1 })
