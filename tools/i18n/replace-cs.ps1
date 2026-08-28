# =============================================================================
# replace-cs.ps1 — 阶段2：按 keymap 将 .cs 中 Lang.T/TF 的中文字面量替换为语义键
# 规则：
#   - 只匹配 Lang.T( / Lang.TF( 后（允许跨行空白）紧跟的完整字符串字面量，
#     引号边界锚定；支持普通字符串（含 \n \" \uXXXX 转义）与 @"" 逐字字符串。
#   - 字面量先解码为运行时值，再与 keymap 中文键精确匹配（天然规避前缀误伤，
#     与“按键长降序”等效；首尾空格键按精确匹配原样保留语义）。
#   - 命中即把字面量替换为语义键（纯 ASCII），未命中（含中文）记入未映射清单。
#   - 保留原文件 BOM/无 BOM 编码；仅整词替换引号内内容，不触碰其他代码。
#   - 跳过 Framework\LangEn.cs（词典数据）与 tools/obj/bin 目录。
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\i18n\replace-cs.ps1 `
#     -Pattern 'Services\*' [-Dry]
#   -Pattern 支持相对仓库根的通配路径（可多个），-Dry 只报告不落盘。
# 输出：控制台统计 + tools\i18n\replace-log.md（追加）
# =============================================================================
param(
    [Parameter(Mandatory = $true)][string[]]$Pattern,
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
# 按键长降序（防御性：当前用精确匹配，排序仅用于未映射提示的展示顺序）
$kmKeysSorted = @($keymap.Keys) | Sort-Object { -$_.Length }
Write-Host ("keymap 中文键: {0}" -f $kmKeysSorted.Count)

# ---------- 收集目标文件 ----------
# 兼容外层 shell 转义限制：支持分号分隔多模式，如 -Pattern 'Framework\*;Services\*'
$patterns = @($Pattern | ForEach-Object { $_ -split ';' } | Where-Object { $_ -ne '' })
$files = New-Object System.Collections.Generic.List[string]
foreach ($pat in $patterns) {
    foreach ($f in (Get-ChildItem -Path $Root -Recurse -Include '*.cs' | Where-Object {
                $_.FullName -notmatch '\\(bin|obj)\\' -and
                $_.FullName -notmatch '\\tools\\' -and
                $_.FullName -like (Join-Path $Root $pat)
            })) {
        if (-not $files.Contains($f.FullName)) { [void]$files.Add($f.FullName) }
    }
}
# 硬排除：词典数据文件（键是数据不是调用点）
$files = @($files | Where-Object { $_ -notmatch 'Framework\\LangEn\.cs$' })
Write-Host ("目标文件: {0}" -f $files.Count)

# ---------- 解码 C# 字面量 ----------
function ConvertFrom-CSharpLiteral([string]$s, [bool]$verbatim) {
    if ($verbatim) { return $s.Replace('""', '"') }
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    while ($i -lt $s.Length) {
        $c = $s[$i]
        if ($c -eq '\' -and ($i + 1) -lt $s.Length) {
            $n = $s[$i + 1]
            switch ($n) {
                'n'  { [void]$sb.Append("`n"); $i += 2; break }
                'r'  { [void]$sb.Append("`r"); $i += 2; break }
                't'  { [void]$sb.Append("`t"); $i += 2; break }
                '0'  { [void]$sb.Append([char]0); $i += 2; break }
                'b'  { [void]$sb.Append("`b"); $i += 2; break }
                'f'  { [void]$sb.Append("`f"); $i += 2; break }
                'v'  { [void]$sb.Append([char]11); $i += 2; break }
                'a'  { [void]$sb.Append([char]7); $i += 2; break }
                '"'  { [void]$sb.Append('"'); $i += 2; break }
                "'"  { [void]$sb.Append("'"); $i += 2; break }
                '\'  { [void]$sb.Append('\'); $i += 2; break }
                'u'  {
                    $hex = $s.Substring($i + 2, 4)
                    [void]$sb.Append([char][int]::Parse($hex, [System.Globalization.NumberStyles]::HexNumber))
                    $i += 6; break
                }
                'U'  {
                    $hex = $s.Substring($i + 2, 8)
                    $cp = [int]::Parse($hex, [System.Globalization.NumberStyles]::HexNumber)
                    [void]$sb.Append([char]::ConvertFromUtf32($cp))
                    $i += 10; break
                }
                'x'  {
                    $j = $i + 2; $hex = ''
                    while ($j -lt $s.Length -and $hex.Length -lt 4 -and ($s[$j] -match '[0-9a-fA-F]')) { $hex += $s[$j]; $j++ }
                    [void]$sb.Append([char][int]::Parse($hex, [System.Globalization.NumberStyles]::HexNumber))
                    $i = $j; break
                }
                default { [void]$sb.Append($n); $i += 2; break }
            }
        } else {
            [void]$sb.Append($c); $i++
        }
    }
    $sb.ToString()
}

# ---------- 正则：Lang.T( / Lang.TF( 后紧跟的字符串字面量 ----------
$rxNormal   = [regex]'(Lang\.(?:T|TF)\s*\(\s*)"((?:[^"\\]|\\.)*)"'
$rxVerbatim = [regex]'(Lang\.(?:T|TF)\s*\(\s*)@"((?:[^"]|"")*)"'

$totalReplaced = 0; $totalFiles = 0
$unmapped = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Text.StringBuilder
[void]$log.AppendLine(('## 批次 {0}（{1}）' -f ((Get-Date -Format 'HH:mm:ss')), ($patterns -join '; ')))
[void]$log.AppendLine('')
[void]$log.AppendLine('| 文件 | 替换数 |')
[void]$log.AppendLine('|---|---|')

# ---------- 替换实现（手工遍历，反向替换避免偏移失效；PS 5.1 兼容） ----------
function Replace-LangCalls([string]$text, [regex]$rx, [bool]$verbatim) {
    $sb = New-Object System.Text.StringBuilder
    $last = 0
    foreach ($m in $rx.Matches($text)) {
        $lit = ConvertFrom-CSharpLiteral $m.Groups[2].Value $verbatim
        [void]$sb.Append($text, $last, $m.Index - $last)
        if ($keymap.ContainsKey($lit)) {
            $script:count++
            [void]$sb.Append($m.Groups[1].Value)
            [void]$sb.Append('"')
            [void]$sb.Append($keymap[$lit])
            [void]$sb.Append('"')
        } else {
            if ($lit -match '[\u4e00-\u9fff]') {
                [void]$unmapped.Add([pscustomobject]@{ File = $script:curFile; Lit = $lit })
            }
            [void]$sb.Append($m.Value)
        }
        $last = $m.Index + $m.Length
    }
    [void]$sb.Append($text, $last, $text.Length - $last)
    $sb.ToString()
}

foreach ($fp in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($fp)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom -and $text.Length -gt 0 -and [int]$text[0] -eq 0xFEFF) { $text = $text.Substring(1) }

    $script:count = 0
    $script:curFile = $fp.Substring($Root.Length + 1)
    # 先逐字串后普通串；两轮互不干扰（替换产物为纯 ASCII 语义键）
    $text = Replace-LangCalls $text $rxVerbatim $true
    $text = Replace-LangCalls $text $rxNormal $false
    $count = $script:count

    if ($count -gt 0) {
        $totalFiles++
        if (-not $Dry) {
            $enc = New-Object System.Text.UTF8Encoding($hasBom)
            [System.IO.File]::WriteAllText($fp, $text, $enc)
        }
    }
    [void]$log.AppendLine(('| {0} | {1} |' -f $fp.Substring($Root.Length + 1), $count))
    $totalReplaced += $count
}

[void]$log.AppendLine('')
[void]$log.AppendLine(('替换合计: {0} 处 / {1} 文件{2}' -f $totalReplaced, $totalFiles, $(if ($Dry) { '（Dry 未落盘）' } else { '' })))
if ($unmapped.Count -gt 0) {
    [void]$log.AppendLine('')
    [void]$log.AppendLine(('未映射中文字面量: {0} 处' -f $unmapped.Count))
    foreach ($x in $unmapped) {
        [void]$log.AppendLine(('- [{0}] {1}' -f $x.File, ($x.Lit.Replace("`n", '\n'))))
    }
}
[void]$log.AppendLine('')
Add-Content -Path $LogP -Value $log.ToString() -Encoding UTF8

Write-Host ("替换: {0} 处 / {1} 文件{2}" -f $totalReplaced, $totalFiles, $(if ($Dry) { '（Dry 未落盘）' } else { '' }))
if ($unmapped.Count -gt 0) {
    Write-Host ("未映射中文字面量: {0} 处（见 replace-log.md）" -f $unmapped.Count)
}
