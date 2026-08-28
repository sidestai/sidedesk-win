# =============================================================================
# add-new-keys.ps1 — 阶段2：将 new-keys.json 的新增文案并入 keymap 与英文源
# 输入：tools/i18n/new-keys.json  { 中文原文: { key: 语义键, en: 英文 | __MERGE__ } }
# 输出：
#   - tools/i18n/keymap.json        追加 { 中文原文: 语义键 }
#   - tools/i18n/sources/en.json    追加 { 中文原文: 英文 }（旧键格式，供 gen-dicts 读取）
# __MERGE__ 表示并入已有语义键（如 XAML 实体变体），不新增英文。
# 用法：powershell -ExecutionPolicy Bypass -File tools\i18n\add-new-keys.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$I18n    = $PSScriptRoot
$KeymapP = Join-Path $I18n 'keymap.json'
$EnP     = Join-Path $I18n 'sources\en.json'
$NewP    = Join-Path $I18n 'new-keys.json'

function Read-Utf8([string]$path) { [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }
function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Read-JsonOrdered([string]$path) {
    $dict = New-Object System.Collections.Specialized.OrderedDictionary
    $obj = (Read-Utf8 $path) | ConvertFrom-Json
    foreach ($p in $obj.PSObject.Properties) { $dict[$p.Name] = [string]$p.Value }
    $dict
}
function ConvertTo-JsonString([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($c in $s.ToCharArray()) {
        $code = [int]$c
        switch ($c) {
            '"'  { [void]$sb.Append('\"'); break }
            '\'  { [void]$sb.Append('\\'); break }
            "`n" { [void]$sb.Append('\n'); break }
            "`r" { [void]$sb.Append('\r'); break }
            "`t" { [void]$sb.Append('\t'); break }
            "`b" { [void]$sb.Append('\b'); break }
            "`f" { [void]$sb.Append('\f'); break }
            default {
                if ($code -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', $code) }
                else { [void]$sb.Append($c) }
            }
        }
    }
    [void]$sb.Append('"')
    $sb.ToString()
}
function ConvertTo-OrderedJson([System.Collections.Specialized.OrderedDictionary]$dict) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('{')
    $i = 0; $n = $dict.Count
    foreach ($k in $dict.Keys) {
        $i++
        $line = '  ' + (ConvertTo-JsonString ([string]$k)) + ': ' + (ConvertTo-JsonString ([string]$dict[$k]))
        if ($i -lt $n) { $line += ',' }
        [void]$sb.AppendLine($line)
    }
    [void]$sb.Append('}')
    $sb.ToString()
}
function Sort-DictOrdinal([System.Collections.Specialized.OrderedDictionary]$dict) {
    $keys = @($dict.Keys)
    [Array]::Sort($keys, [System.StringComparer]::Ordinal)
    $out = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($k in $keys) { $out[[string]$k] = [string]$dict[$k] }
    $out
}

$keymap = Read-JsonOrdered $KeymapP
$en     = Read-JsonOrdered $EnP
$new    = (Read-Utf8 $NewP) | ConvertFrom-Json

$existingSem = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($v in $keymap.Values) { [void]$existingSem.Add([string]$v) }

$added = 0; $merged = 0
foreach ($p in $new.PSObject.Properties) {
    $zh  = $p.Name
    $sem = [string]$p.Value.key
    $enV = [string]$p.Value.en
    if ($keymap.Contains($zh)) {
        Write-Host ("    [skip] 已存在: {0} => {1}" -f $zh, $keymap[$zh])
        continue
    }
    if ($enV -eq '__MERGE__') {
        if (-not $existingSem.Contains($sem)) { throw "合并目标语义键不存在: $sem (zh=$zh)" }
        $keymap[$zh] = $sem
        $merged++
        Write-Host ("    [merge] {0} => {1}" -f $zh, $sem)
        continue
    }
    if ($existingSem.Contains($sem)) { throw "语义键冲突（已被占用）: $sem (zh=$zh)" }
    $keymap[$zh] = $sem
    $en[$zh] = $enV
    [void]$existingSem.Add($sem)
    $added++
}

Write-Utf8 $KeymapP ((ConvertTo-OrderedJson (Sort-DictOrdinal $keymap)) + "`r`n")
Write-Utf8 $EnP ((ConvertTo-OrderedJson (Sort-DictOrdinal $en)) + "`r`n")
Write-Host ("完成：新增 {0} 键，合并 {1} 键；keymap 中文键总数 {2}" -f $added, $merged, $keymap.Count)
