# =============================================================================
# replace-xaml.ps1 — 阶段3：按 keymap 将 .xaml 中 {l:T}/{l:TF} 的中文键替换为语义键
# 规则：
#   - 匹配 {l:T ...} / {l:TF ...}（内容允许跨行），剥离可选包裹引号，
#     解码 XML 实体（&#x0a; &#10; &amp; &lt; &gt; &quot; &apos;）后与 keymap 中文键精确匹配。
#   - 命中即统一改写为无引号的 {l:T semantic.key}（语义键为纯 ASCII，无需引号）。
#   - 已是语义键（未命中且无中文）的调用点不动；未命中且含中文的记入未映射清单。
#   - 保留原文件 BOM/无 BOM 编码；跳过 bin/obj/tools 目录。
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\i18n\replace-xaml.ps1 `
#     [-Pattern '*.xaml'] [-Dry]
#   -Pattern 支持相对仓库根的通配路径（可多个/分号分隔），缺省全库 *.xaml。
# 输出：控制台统计 + tools\i18n\replace-log.md（追加）
# =============================================================================
param(
    [string[]]$Pattern,
    [switch]$Dry
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$I18n    = $PSScriptRoot
$Root    = (Resolve-Path (Join-Path $I18n '..\..')).Path
$KeymapP = Join-Path $I18n 'keymap.json'
$LogP    = Join-Path $I18n 'replace-log.md'

function Read-Utf8([string]$path) { [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }

# ---------- keymap ----------
$keymap = @{}
$kmObj = (Read-Utf8 $KeymapP) | ConvertFrom-Json
foreach ($p in $kmObj.PSObject.Properties) { $keymap[$p.Name] = [string]$p.Value }
Write-Host ("keymap 中文键: {0}" -f $keymap.Count)

# ---------- 收集目标文件 ----------
$patterns = @($Pattern | ForEach-Object { $_ -split ';' } | Where-Object { $_ -ne '' })
if ($patterns.Count -eq 0) { $patterns = @('*.xaml') }
$files = New-Object System.Collections.Generic.List[string]
foreach ($pat in $patterns) {
    foreach ($f in (Get-ChildItem -Path $Root -Recurse -Include '*.xaml' | Where-Object {
                $_.FullName -notmatch '\\(bin|obj)\\' -and
                $_.FullName -notmatch '\\tools\\' -and
                $_.FullName -like (Join-Path $Root $pat)
            })) {
        if (-not $files.Contains($f.FullName)) { [void]$files.Add($f.FullName) }
    }
}
Write-Host ("目标文件: {0}" -f $files.Count)

# ---------- XML 实体解码（属性值 → 运行时文本） ----------
function ConvertFrom-XamlEntities([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    while ($i -lt $s.Length) {
        $c = $s[$i]
        if ($c -eq '&' -and $s.IndexOf(';', $i) -gt $i) {
            $semi = $s.IndexOf(';', $i)
            $ent = $s.Substring($i + 1, $semi - $i - 1)
            $decoded = $null
            switch ($ent) {
                'amp'  { $decoded = '&'; break }
                'lt'   { $decoded = '<'; break }
                'gt'   { $decoded = '>'; break }
                'quot' { $decoded = '"'; break }
                'apos' { $decoded = "'"; break }
                default {
                    if ($ent -match '^#x([0-9a-fA-F]+)$') {
                        $decoded = [char][int]::Parse($Matches[1], [System.Globalization.NumberStyles]::HexNumber)
                    } elseif ($ent -match '^#(\d+)$') {
                        $decoded = [char][int]$Matches[1]
                    }
                }
            }
            if ($null -ne $decoded) { [void]$sb.Append($decoded); $i = $semi + 1; continue }
        }
        [void]$sb.Append($c); $i++
    }
    $sb.ToString()
}

# ---------- 正则：{l:T ...} / {l:TF ...}（内容跨行） ----------
$rx = [regex]'\{l:(TF?)\s+([^}]*?)\s*\}'

$totalReplaced = 0; $totalFiles = 0
$unmapped = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Text.StringBuilder
[void]$log.AppendLine(('## XAML 批次 {0}（{1}）' -f ((Get-Date -Format 'HH:mm:ss')), ($patterns -join '; ')))
[void]$log.AppendLine('')
[void]$log.AppendLine('| 文件 | 替换数 |')
[void]$log.AppendLine('|---|---|')

foreach ($fp in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($fp)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom -and $text.Length -gt 0 -and [int]$text[0] -eq 0xFEFF) { $text = $text.Substring(1) }

    $count = 0
    $sb = New-Object System.Text.StringBuilder
    $last = 0
    foreach ($m in $rx.Matches($text)) {
        $ext  = $m.Groups[1].Value              # T / TF
        $body = $m.Groups[2].Value.Trim()
        # 剥离可选包裹引号
        if ($body.Length -ge 2 -and (($body[0] -eq "'" -and $body[-1] -eq "'") -or ($body[0] -eq '"' -and $body[-1] -eq '"'))) {
            $body = $body.Substring(1, $body.Length - 2)
        }
        $lit = ConvertFrom-XamlEntities $body
        [void]$sb.Append($text, $last, $m.Index - $last)
        if ($keymap.ContainsKey($lit)) {
            $count++
            [void]$sb.Append(('{l:' + $ext + ' ' + $keymap[$lit] + '}'))
        } elseif ($keymap.ContainsKey($body)) {
            # 实体形态本身在 keymap 中登记（如 &#x0a; 变体）
            $count++
            [void]$sb.Append(('{l:' + $ext + ' ' + $keymap[$body] + '}'))
        } else {
            if ($lit -match '[\u4e00-\u9fff]') {
                [void]$unmapped.Add([pscustomobject]@{ File = $fp.Substring($Root.Length + 1); Lit = $lit })
            }
            [void]$sb.Append($m.Value)
        }
        $last = $m.Index + $m.Length
    }
    [void]$sb.Append($text, $last, $text.Length - $last)

    if ($count -gt 0) {
        $totalFiles++
        if (-not $Dry) {
            $enc = New-Object System.Text.UTF8Encoding($hasBom)
            [System.IO.File]::WriteAllText($fp, $sb.ToString(), $enc)
        }
    }
    [void]$log.AppendLine(('| {0} | {1} |' -f $fp.Substring($Root.Length + 1), $count))
    $totalReplaced += $count
}

[void]$log.AppendLine('')
[void]$log.AppendLine(('替换合计: {0} 处 / {1} 文件{2}' -f $totalReplaced, $totalFiles, $(if ($Dry) { '（Dry 未落盘）' } else { '' })))
if ($unmapped.Count -gt 0) {
    [void]$log.AppendLine('')
    [void]$log.AppendLine(('未映射中文键: {0} 处' -f $unmapped.Count))
    foreach ($x in $unmapped) {
        [void]$log.AppendLine(('- [{0}] {1}' -f $x.File, ($x.Lit.Replace("`n", '\n'))))
    }
}
[void]$log.AppendLine('')
Add-Content -Path $LogP -Value $log.ToString() -Encoding UTF8

Write-Host ("替换: {0} 处 / {1} 文件{2}" -f $totalReplaced, $totalFiles, $(if ($Dry) { '（Dry 未落盘）' } else { '' }))
if ($unmapped.Count -gt 0) {
    Write-Host ("未映射中文键: {0} 处（见 replace-log.md）" -f $unmapped.Count)
}
