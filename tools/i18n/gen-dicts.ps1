# =============================================================================
# gen-dicts.ps1 — 按 tools/i18n/keymap.json 重建全部词典（幂等，可增量重跑）
# 输入：
#   - tools/i18n/keymap.json       { 中文原文: semantic.key }
#   - tools/i18n/canonical.json    { semantic.key: zh-CN 权威中文原文 }（合并组）
#   - langs/en.json（旧）           英文来源；可选 ∪ Framework/LangEn.cs（已删除时跳过）
#   - langs/{lang}.json（旧）      其余 11 语言译文
# 输出：
#   - langs/zh-CN.json             { 语义键: 中文原文 }（中文权威来源，新建）
#   - langs/en.json                { 语义键: 英文 }（重写）
#   - langs/{11 语言}.json         键替换为语义键，缺失键以英文占位
#   - langs/_legacy.json           keymap 副本（运行时旧键归一化）
#   - tools/i18n/pending-translations.md  各语言待译清单（已扣除覆盖译文）
# 覆盖机制：
#   - tools/i18n/translations-override.json  { lang: { semantic.key: 译文 } }
#     在词典生成后按语言覆盖对应键值（补译成果的持久化落点，重跑不丢失）。
# 全部输出：按键字母序排序、UTF-8（无 BOM）、2 空格缩进。
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\i18n\gen-dicts.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Root     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LangsDir = Join-Path $Root 'langs'
$KeymapP  = Join-Path $PSScriptRoot 'keymap.json'
$CanonP   = Join-Path $PSScriptRoot 'canonical.json'
$LangEnCs = Join-Path $Root 'Framework\LangEn.cs'
$SrcDir   = Join-Path $PSScriptRoot 'sources'

# 幂等保障：首次运行时快照原始（中文键）词典；重跑始终从快照读取，
# 避免把上一轮已语义化的 langs/*.json 误当成源词典。
if (-not (Test-Path $SrcDir)) {
    New-Item -ItemType Directory -Path $SrcDir | Out-Null
    foreach ($f in (Get-ChildItem -Path $LangsDir -Filter '*.json')) {
        Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $SrcDir $f.Name)
    }
    Write-Host ("[snapshot] 已快照原始词典到 {0}（{1} 个文件）" -f $SrcDir, @(Get-ChildItem $SrcDir -Filter '*.json').Count)
}

function Read-Utf8([string]$path) { [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }
function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
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

function ConvertFrom-CSharpLiteral([string]$s) {
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
                '"'  { [void]$sb.Append('"'); $i += 2; break }
                "'"  { [void]$sb.Append("'"); $i += 2; break }
                '\'  { [void]$sb.Append('\'); $i += 2; break }
                'u'  {
                    $hex = $s.Substring($i + 2, 4)
                    [void]$sb.Append([char][int]::Parse($hex, [System.Globalization.NumberStyles]::HexNumber))
                    $i += 6; break
                }
                default { [void]$sb.Append($n); $i += 2; break }
            }
        } else {
            [void]$sb.Append($c); $i++
        }
    }
    $sb.ToString()
}

# 读取扁平 JSON 词典为 Hashtable（精确键，保真值）
function Read-JsonDict([string]$path) {
    $dict = @{}
    if (-not (Test-Path $path)) { return $dict }
    $obj = (Read-Utf8 $path) | ConvertFrom-Json
    foreach ($p in $obj.PSObject.Properties) { $dict[$p.Name] = [string]$p.Value }
    $dict
}

function Sort-Ordinal([string[]]$items) {
    $arr = [string[]]@($items)
    [Array]::Sort($arr, [System.StringComparer]::Ordinal)
    ,$arr
}

# =============================================================================
Write-Host "[1/5] 读取 keymap / canonical / 双源英文 ..."
$keymap = Read-JsonDict $KeymapP
if ($keymap.Count -eq 0) { throw "keymap.json 为空，请先运行 gen-keymap.ps1" }
$canonical = @{}
if (Test-Path $CanonP) { $canonical = Read-JsonDict $CanonP }

$zhKeys = @($keymap.Keys)
$semKeys = Sort-Ordinal (@($keymap.Values) | Sort-Object -Unique)
Write-Host ("    中文键: {0}, 语义键(唯一): {1}" -f $zhKeys.Count, $semKeys.Count)

# 英文来源：快照 en.json（中文键）+ 可选 LangEn.cs（阶段 4 已删除该双源）
$oldEn = Read-JsonDict (Join-Path $SrcDir 'en.json')
$langEnDict = @{}
if (Test-Path $LangEnCs) {
    $csText = Read-Utf8 $LangEnCs
    $rxEntry = [regex]'\[\s*"((?:[^"\\]|\\.)*)"\s*\]\s*=\s*"((?:[^"\\]|\\.)*)"'
    foreach ($m in $rxEntry.Matches($csText)) {
        $langEnDict[(ConvertFrom-CSharpLiteral $m.Groups[1].Value)] = ConvertFrom-CSharpLiteral $m.Groups[2].Value
    }
}
Write-Host ("    旧 en.json: {0}, LangEn.cs: {1}" -f $oldEn.Count, $langEnDict.Count)

# 每个语义键的英文 / 中文权威值（合并组以 canonical 对应中文键的英文为准）
# 英文三级回退：快照 sources/en.json（中文键）→ LangEn.cs（若存在）→ 当前 langs/en.json（语义键）
$curEnJson = Read-JsonDict (Join-Path $LangsDir 'en.json')
$semEn = @{}
foreach ($zh in (Sort-Ordinal $zhKeys)) {
    $sem = $keymap[$zh]
    if ($semEn.ContainsKey($sem)) { continue }   # 合并组：首个（按序，且 canonical 组已保证英文一致）
    $en = $null
    if ($oldEn.ContainsKey($zh)) { $en = $oldEn[$zh] }
    elseif ($langEnDict.ContainsKey($zh)) { $en = $langEnDict[$zh] }
    elseif ($curEnJson.ContainsKey($sem)) { $en = $curEnJson[$sem] }
    if ($null -eq $en) { throw "键 [$zh] 在 sources/en.json、LangEn.cs、langs/en.json 中均无英文译文" }
    $semEn[$sem] = $en
}

# =============================================================================
Write-Host "[2/5] 生成 zh-CN.json / en.json ..."
# 合并组无 canonical 登记时的权威中文选取（幂等确定）：
#   候选变体先按 Ordinal 排序，优先选不含 & 实体的变体（XAML 实体变体如 &#x0a;
#   在词典中会显示为原始实体文本），避免 Hashtable 无序导致的重跑结果漂移。
function Select-ZhCanonical([string]$sem) {
    $candidates = Sort-Ordinal @($zhKeys | Where-Object { $keymap[$_] -eq $sem })
    foreach ($c in $candidates) { if ($c -notmatch '&') { return $c } }
    return $candidates[0]
}
$zhCn = New-Object System.Collections.Specialized.OrderedDictionary
foreach ($sem in $semKeys) {
    $canon = $null
    if ($canonical.ContainsKey($sem)) { $canon = $canonical[$sem] }
    else { $canon = Select-ZhCanonical $sem }
    $zhCn[$sem] = $canon
}
Write-Utf8 (Join-Path $LangsDir 'zh-CN.json') ((ConvertTo-OrderedJson $zhCn) + "`r`n")

$enOut = New-Object System.Collections.Specialized.OrderedDictionary
foreach ($sem in $semKeys) { $enOut[$sem] = $semEn[$sem] }
Write-Utf8 (Join-Path $LangsDir 'en.json') ((ConvertTo-OrderedJson $enOut) + "`r`n")

# =============================================================================
Write-Host "[3/6] 读取补译覆盖 translations-override.json ..."
$OverrideP = Join-Path $PSScriptRoot 'translations-override.json'
$overrides = @{}
if (Test-Path $OverrideP) {
    $ovObj = (Read-Utf8 $OverrideP) | ConvertFrom-Json
    foreach ($lp in $ovObj.PSObject.Properties) {
        $sub = @{}
        foreach ($kp in $lp.Value.PSObject.Properties) { $sub[$kp.Name] = [string]$kp.Value }
        $overrides[$lp.Name] = $sub
    }
    Write-Host ("    覆盖语言: {0}（{1}）" -f ($overrides.Keys -join ', '), (@($overrides.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum))
} else {
    Write-Host "    （文件不存在，跳过）"
}

# =============================================================================
Write-Host "[4/6] 重建其余 11 个语言词典（缺失以英文占位，随后应用覆盖）..."
$OtherLangs = @('de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-TW')
$pending = @{}
foreach ($lang in $OtherLangs) {
    $old = Read-JsonDict (Join-Path $SrcDir ($lang + '.json'))
    $out = New-Object System.Collections.Specialized.OrderedDictionary
    $miss = New-Object System.Collections.Generic.List[object]
    foreach ($sem in $semKeys) {
        $canon = [string]$zhCn[$sem]
        if ($old.ContainsKey($canon)) {
            $out[$sem] = $old[$canon]
        } else {
            # 合并组中任一旧中文键命中也算命中（按 Ordinal 固定次序，保证幂等）
            $hit = $null
            foreach ($zh in (Sort-Ordinal @($zhKeys | Where-Object { $keymap[$_] -eq $sem }))) {
                if ($old.ContainsKey($zh)) { $hit = $old[$zh]; break }
            }
            if ($null -ne $hit) { $out[$sem] = $hit }
            else {
                $out[$sem] = $semEn[$sem]   # 英文占位
                [void]$miss.Add([pscustomobject]@{ Key = $sem; Zh = $canon; En = $semEn[$sem] })
            }
        }
    }
    # 旧词典孤儿（不在 keymap 中文键集合内）
    $orphans = @($old.Keys | Where-Object { -not $keymap.ContainsKey($_) })
    if ($orphans.Count -gt 0) {
        Write-Host ("    [warn] {0}: {1} 个孤儿键被丢弃: {2}" -f $lang, $orphans.Count, (($orphans | Select-Object -First 5) -join ' | '))
    }
    # 应用补译覆盖：覆盖键不再计入待译清单；未知键告警（防键名笔误静默丢失）
    $appliedN = 0
    if ($overrides.ContainsKey($lang)) {
        foreach ($sem in $overrides[$lang].Keys) {
            if ($out.Contains($sem)) { $out[$sem] = $overrides[$lang][$sem]; $appliedN++ }
            else { Write-Host ("    [warn] {0}: 覆盖键不在键集中，已忽略: {1}" -f $lang, $sem) }
        }
    }
    $missFinal = New-Object System.Collections.Generic.List[object]
    foreach ($x in $miss) {
        if ($overrides.ContainsKey($lang) -and $overrides[$lang].ContainsKey($x.Key)) { continue }
        [void]$missFinal.Add($x)
    }
    Write-Utf8 (Join-Path $LangsDir ($lang + '.json')) ((ConvertTo-OrderedJson $out) + "`r`n")
    $pending[$lang] = $missFinal
    Write-Host ("    {0}: {1} 键（英文占位 {2}，覆盖补译 {3}，剩余待译 {4}）" -f $lang, $out.Count, $miss.Count, $appliedN, $missFinal.Count)
}

# =============================================================================
Write-Host "[5/6] 写出 langs/_legacy.json（keymap 副本）..."
$legacy = New-Object System.Collections.Specialized.OrderedDictionary
foreach ($zh in (Sort-Ordinal $zhKeys)) { $legacy[$zh] = $keymap[$zh] }
Write-Utf8 (Join-Path $LangsDir '_legacy.json') ((ConvertTo-OrderedJson $legacy) + "`r`n")

# =============================================================================
Write-Host "[6/6] 写出待译清单 pending-translations.md ..."
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# 待译清单（英文占位键）')
[void]$md.AppendLine('')
[void]$md.AppendLine(('由 gen-dicts.ps1 生成。各语言词典中这些键当前以英文值占位，翻译后写入 tools/i18n/translations-override.json 并重跑本脚本即可（键值覆盖、待译清单自动扣除）。'))
[void]$md.AppendLine('')
foreach ($lang in $OtherLangs) {
    $miss = $pending[$lang]
    [void]$md.AppendLine(('## {0}（{1} 条）' -f $lang, $miss.Count))
    if ($miss.Count -eq 0) { [void]$md.AppendLine('（无）'); [void]$md.AppendLine(''); continue }
    [void]$md.AppendLine('')
    [void]$md.AppendLine('| 语义键 | 中文原文 | 英文 |')
    [void]$md.AppendLine('|---|---|---|')
    foreach ($x in $miss) {
        $zh = $x.Zh.Replace("`n", ' ').Replace('|', '\|')
        $en = $x.En.Replace("`n", ' ').Replace('|', '\|')
        [void]$md.AppendLine(('| `{0}` | {1} | {2} |' -f $x.Key, $zh, $en))
    }
    [void]$md.AppendLine('')
}
Write-Utf8 (Join-Path $PSScriptRoot 'pending-translations.md') $md.ToString()

Write-Host "完成。词典目录: $LangsDir"
