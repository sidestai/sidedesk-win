# =============================================================================
# check-i18n.ps1 — 多语言词典 / 代码一致性校验
# 校验项：
#   [1] 语义键全局唯一（13 个词典 + keymap 值）
#   [2] 词典值不得含字面 XML 实体（&#...;、&amp; 等，会原样显示给用户）
#   [3] 每键各语言 {n} 占位索引集合一致（以 zh-CN 为基准，忽略格式说明符与顺序）
#   [4] 代码字面量键 与 词典键 双向孤儿检测
#   [5] 扫描 XAML Text=/Header=/Content=/ToolTip=/PlaceholderText= 与
#       C# .Text/.Content/.Header 赋值、对象初始化器 Header=/Content= 中的中文字面量
#       （日志 / 协议文件路径白名单豁免）
# 输出：控制台摘要 + tools/i18n/check-report.md
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\i18n\check-i18n.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Root     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LangsDir = Join-Path $Root 'langs'
$KeymapP  = Join-Path $PSScriptRoot 'keymap.json'
$ReportP  = Join-Path $PSScriptRoot 'check-report.md'

function Read-Utf8([string]$path) { [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }

function Read-JsonDict([string]$path) {
    $dict = @{}
    if (-not (Test-Path $path)) { return $dict }
    $obj = (Read-Utf8 $path) | ConvertFrom-Json
    foreach ($p in $obj.PSObject.Properties) { $dict[$p.Name] = [string]$p.Value }
    $dict
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

function Get-PlaceholderSet([string]$s) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    # 只取占位索引，忽略格式说明符（如 {1:MMMM}）；比对时只看索引集合，不看顺序
    foreach ($m in [regex]::Matches($s, '\{(\d+)(:[^}]*)?\}')) { [void]$set.Add($m.Groups[1].Value) }
    ($set | Sort-Object { [int]$_ }) -join ','
}

# =============================================================================
Write-Host "=== check-i18n ==="
$issues = New-Object System.Collections.Generic.List[object]

# ---------------------------------------------------------------------------
Write-Host "[1/5] 语义键全局唯一 ..."
$AllLangs = @('zh-CN', 'en', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-TW')
$dicts = @{}
$keyCounts = @{}
foreach ($l in $AllLangs) {
    $dicts[$l] = Read-JsonDict (Join-Path $LangsDir ($l + '.json'))
    $keyCounts[$l] = $dicts[$l].Count
}
$keymap = Read-JsonDict $KeymapP
$semVals = @($keymap.Values) | Sort-Object -Unique
$dups = @($semVals | Group-Object | Where-Object { $_.Count -gt 1 })
$dupInDict = @{}
foreach ($l in $AllLangs) {
    $g = @($dicts[$l].Keys | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($g.Count -gt 0) { $dupInDict[$l] = $g }
}
# 键集合一致性（全部词典应等于 keymap 语义键集合）
$refSet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($s in $semVals) { [void]$refSet.Add($s) }
$keysetMismatch = @{}
foreach ($l in $AllLangs) {
    $cur = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($s in $dicts[$l].Keys) { [void]$cur.Add([string]$s) }
    if ($cur.Count -ne $refSet.Count) {
        $missing = @($refSet | Where-Object { -not $cur.Contains($_) })
        $extra   = @($cur | Where-Object { -not $refSet.Contains($_) })
        $keysetMismatch[$l] = @{ Missing = $missing; Extra = $extra }
    }
}
Write-Host ("    keymap 语义键(唯一): {0}; 词典重复键: {1}; 键集合不一致: {2}" -f $semVals.Count, ($dupInDict.Count), ($keysetMismatch.Count))
if ($dups.Count -gt 0) { [void]$issues.Add(@{ Check = 1; Level = 'ERROR'; Msg = ("keymap 值重复: " + (($dups | ForEach-Object { $_.Name }) -join ', ')) }) }
foreach ($k in $dupInDict.Keys) { [void]$issues.Add(@{ Check = 1; Level = 'ERROR'; Msg = ("词典 {0} 存在重复键: {1}" -f $k, (($dupInDict[$k] | ForEach-Object { $_.Name }) -join ', ')) }) }
foreach ($k in $keysetMismatch.Keys) {
    $m = $keysetMismatch[$k]
    [void]$issues.Add(@{ Check = 1; Level = 'ERROR'; Msg = ("词典 {0} 键集合与 keymap 不一致: 缺 {1} 个 / 多 {2} 个" -f $k, $m.Missing.Count, $m.Extra.Count) })
}

# ---------------------------------------------------------------------------
Write-Host "[2/5] 词典值字面 XML 实体断言 ..."
# 词典值中的字面实体（如 &#x0a;、&amp;）不会被解码，会原样显示给用户（如 ToolTip），
# 真实换行/符号应直接以 \n / 原字符写入词典。
$rxXmlEntity = '&(?:#[0-9]+|#x[0-9a-fA-F]+|amp|lt|gt|quot|apos);'
$entityIssues = 0
foreach ($l in $AllLangs) {
    foreach ($k in ($dicts[$l].Keys | Sort-Object)) {
        $v = $dicts[$l][$k]
        if ($v -match $rxXmlEntity) {
            $entityIssues++
            [void]$issues.Add(@{ Check = 2; Level = 'ERROR'; Msg = ("词典 {0} 值含字面 XML 实体: {1} = {2}" -f $l, $k, $v) })
        }
    }
}
Write-Host ("    字面实体: {0} 处" -f $entityIssues)

# ---------------------------------------------------------------------------
Write-Host "[3/5] 占位符 {n} 索引集合一致性（以 zh-CN 为基准，忽略格式说明符与顺序）..."
$phIssues = New-Object System.Collections.Generic.List[object]
foreach ($sem in ($dicts['zh-CN'].Keys | Sort-Object)) {
    $base = Get-PlaceholderSet $dicts['zh-CN'][$sem]
    foreach ($l in $AllLangs) {
        if ($l -eq 'zh-CN') { continue }
        if (-not $dicts[$l].ContainsKey($sem)) { continue }
        $cur = Get-PlaceholderSet $dicts[$l][$sem]
        if ($cur -ne $base) {
            [void]$phIssues.Add([pscustomobject]@{ Lang = $l; Key = $sem; Base = "{{$($base.Replace(',', '}{'))}}"; Actual = "{{$($cur.Replace(',', '}{'))}}" })
        }
    }
}
Write-Host ("    占位符不一致: {0} 处" -f $phIssues.Count)
foreach ($x in $phIssues) {
    [void]$issues.Add(@{ Check = 3; Level = 'ERROR'; Msg = ("[{0}] {1}: 基准 {2} ≠ 实际 {3}" -f $x.Lang, $x.Key, $x.Base, $x.Actual) })
}

# ---------------------------------------------------------------------------
Write-Host "[4/5] 代码字面量键 ↔ 词典键 双向孤儿检测 ..."
$csFiles  = @(Get-ChildItem -Path $Root -Recurse -Include '*.cs'  | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$xamlFiles = @(Get-ChildItem -Path $Root -Recurse -Include '*.xaml' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

$usedSem = New-Object System.Collections.Generic.HashSet[string]
$litNotFound = New-Object System.Collections.Generic.List[object]   # 代码字面量无法映射到词典
# 豁免：注释行中的示例调用（如 Lang.cs 类注释里的 Lang.T("键") 用法示例）
$exemptCallLiterals = @('键')
# 按行扫描用：剔除 // 与 /// 注释行，避免示例文案误报；同时收集有效调用
function Get-CallLiterals([string]$text) {
    $lits = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`n")) {
        if ($line.TrimStart().StartsWith('//')) { continue }
        foreach ($m in $rxCsCall.Matches($line)) {
            [void]$lits.Add((ConvertFrom-CSharpLiteral $m.Groups[1].Value))
        }
    }
    return $lits
}
$rxCsCall = [regex]'Lang\.(?:T|TF)\s*\(\s*(?:@\s*)?"((?:[^"\\]|\\.)*)"'
foreach ($f in $csFiles) {
    $text = Read-Utf8 $f.FullName
    foreach ($lit in (Get-CallLiterals $text)) {
        if ($exemptCallLiterals -contains $lit) { continue }
        if ($keymap.ContainsKey($lit)) { [void]$usedSem.Add($keymap[$lit]) }
        elseif ($refSet.Contains($lit)) { [void]$usedSem.Add($lit) }
        else { [void]$litNotFound.Add([pscustomobject]@{ File = $f.FullName.Substring($Root.Length + 1); Literal = $lit }) }
    }
}
$rxXamlT = [regex]'\{l:T(?:F)?\s+([^}]+)\}'
foreach ($f in $xamlFiles) {
    $text = Read-Utf8 $f.FullName
    foreach ($m in $rxXamlT.Matches($text)) {
        $lit = $m.Groups[1].Value.Trim()
        # 剥离 XAML 中可选的包裹引号：{l:T '文本'} / {l:T "文本"}
        if ($lit.Length -ge 2 -and (($lit[0] -eq "'" -and $lit[-1] -eq "'") -or ($lit[0] -eq '"' -and $lit[-1] -eq '"'))) {
            $lit = $lit.Substring(1, $lit.Length - 2)
        }
        if ($keymap.ContainsKey($lit)) { [void]$usedSem.Add($keymap[$lit]) }
        elseif ($refSet.Contains($lit)) { [void]$usedSem.Add($lit) }
        else { [void]$litNotFound.Add([pscustomobject]@{ File = $f.FullName.Substring($Root.Length + 1); Literal = $lit }) }
    }
}
$dictUnused = @($refSet | Where-Object { -not $usedSem.Contains($_) })
Write-Host ("    代码字面量（归一后）命中词典键: {0}; 字面量无映射: {1}; 词典键未被代码引用: {2}" -f $usedSem.Count, $litNotFound.Count, $dictUnused.Count)
# 注：当前阶段调用点仍为中文原文，字面量无映射 = 词典缺键（ERROR）；
#     词典键未被引用多为动态拼接/示例文案，列为 INFO。
foreach ($x in ($litNotFound | Sort-Object File, Literal)) {
    [void]$issues.Add(@{ Check = 4; Level = 'ERROR'; Msg = ("代码字面量无词典映射 [{0}] {1}" -f $x.File, $x.Literal) })
}
foreach ($k in ($dictUnused | Sort-Object)) {
    [void]$issues.Add(@{ Check = 4; Level = 'INFO'; Msg = ("词典键未被静态引用（可能动态拼接）: {0}" -f $k) })
}

# ---------------------------------------------------------------------------
Write-Host '[5/5] 硬编码中文字面量扫描（XAML Text/Header/Content/ToolTip/PlaceholderText 属性 与 C# .Text/.Content/.Header 赋值、对象初始化器）...'
$zhRe = '[\u4e00-\u9fff]'
# 白名单：日志 / 协议文件路径（按行内容特征豁免）
function Test-Whitelisted([string]$line) {
    if ($line -match '(?i)\b(Log|Logger|Debug\.WriteLine|Trace\.|Console\.(Write|Error)|AppendLog|AddLog|NotifyLog)\s*\(') { return $true }
    if ($line -match '(?i)(https?://|\.md|\.txt|protocol|agreement|privacy|terms|license|\.csproj|\.sln)') { return $true }
    if ($line -match '[A-Za-z]:\\\\|\\\\\\\\') { return $true }   # 文件路径字符串
    return $false
}
$hardcoded = New-Object System.Collections.Generic.List[object]
$rxXamlAttr = [regex]'\b(Text|Header|Content|ToolTip|PlaceholderText)\s*=\s*"([^"]*)"'
foreach ($f in $xamlFiles) {
    $text = Read-Utf8 $f.FullName
    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        foreach ($m in $rxXamlAttr.Matches($ln)) {
            $v = $m.Groups[2].Value
            if ($v -match $zhRe -and $v -notmatch '^\s*\{' -and -not (Test-Whitelisted $ln)) {
                [void]$hardcoded.Add([pscustomobject]@{ File = ($f.FullName.Substring($Root.Length + 1)); Line = ($i + 1); Attr = $m.Groups[1].Value; Text = $v })
            }
        }
    }
}
$rxCsText = [regex]'\.(Text|Content|Header)\s*=\s*"((?:[^"\\]|\\.)*)"'
# 对象初始化器中的属性赋值（无点前缀）：new MenuItem { Header = "中文" } 等；
# 要求等号前是行首/{/,/( 或空白，避免误匹配方法实参命名（如 Foo(Content: "x")）。
$rxCsInit = [regex]'(?<![.\w])(Header|Content)\s*=\s*"((?:[^"\\]|\\.)*)"'
foreach ($f in $csFiles) {
    $text = Read-Utf8 $f.FullName
    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        if ($ln.TrimStart().StartsWith('//')) { continue }   # 剔除注释行，避免示例文案误报
        foreach ($m in $rxCsText.Matches($ln)) {
            $v = $m.Groups[2].Value
            if ($v -match $zhRe -and -not (Test-Whitelisted $ln)) {
                [void]$hardcoded.Add([pscustomobject]@{ File = ($f.FullName.Substring($Root.Length + 1)); Line = ($i + 1); Attr = ('.' + $m.Groups[1].Value); Text = (ConvertFrom-CSharpLiteral $v) })
            }
        }
        foreach ($m in $rxCsInit.Matches($ln)) {
            $v = $m.Groups[2].Value
            if ($v -match $zhRe -and -not (Test-Whitelisted $ln)) {
                # 与点前缀规则重叠时避免重复上报：若该行同位置已被 .Text/.Content/.Header 规则命中则跳过
                $dup = $false
                foreach ($m2 in $rxCsText.Matches($ln)) { if ($m2.Groups[2].Value -eq $m.Groups[2].Value) { $dup = $true; break } }
                if (-not $dup) {
                    [void]$hardcoded.Add([pscustomobject]@{ File = ($f.FullName.Substring($Root.Length + 1)); Line = ($i + 1); Attr = ($m.Groups[1].Value + '='); Text = (ConvertFrom-CSharpLiteral $v) })
                }
            }
        }
    }
}
Write-Host ("    硬编码中文字面量: {0} 处" -f $hardcoded.Count)
foreach ($x in $hardcoded) {
    [void]$issues.Add(@{ Check = 5; Level = 'WARN'; Msg = ("[{0}:{1}] {2} = {3}" -f $x.File, $x.Line, $x.Attr, $x.Text) })
}

# =============================================================================
Write-Host ''
Write-Host '=== 词典键数统计 ==='
foreach ($l in $AllLangs) { Write-Host ("    {0,-8}: {1}" -f $l, $keyCounts[$l]) }
Write-Host ''
Write-Host '=== 问题摘要 ==='
$errN  = @($issues | Where-Object { $_.Level -eq 'ERROR' }).Count
$warnN = @($issues | Where-Object { $_.Level -eq 'WARN' }).Count
$infoN = @($issues | Where-Object { $_.Level -eq 'INFO' }).Count
Write-Host ("    ERROR: {0} | WARN: {1} | INFO: {2}" -f $errN, $warnN, $infoN)

# 报告
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# check-i18n 校验报告')
[void]$md.AppendLine('')
[void]$md.AppendLine(('生成时间: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')))
[void]$md.AppendLine('')
[void]$md.AppendLine('## 词典键数')
[void]$md.AppendLine('')
[void]$md.AppendLine('| 语言 | 键数 |')
[void]$md.AppendLine('|---|---|')
foreach ($l in $AllLangs) { [void]$md.AppendLine(('| {0} | {1} |' -f $l, $keyCounts[$l])) }
[void]$md.AppendLine(('| keymap 语义键(唯一) | {0} |' -f $semVals.Count))
[void]$md.AppendLine('')
[void]$md.AppendLine(('## 问题汇总（ERROR {0} / WARN {1} / INFO {2}）' -f $errN, $warnN, $infoN))
[void]$md.AppendLine('')
foreach ($lvl in @('ERROR', 'WARN', 'INFO')) {
    $grp = @($issues | Where-Object { $_.Level -eq $lvl })
    if ($grp.Count -eq 0) { continue }
    [void]$md.AppendLine(('### {0}（{1}）' -f $lvl, $grp.Count))
    [void]$md.AppendLine('')
    foreach ($x in $grp) { [void]$md.AppendLine(('- [检查{0}] {1}' -f $x.Check, $x.Msg)) }
    [void]$md.AppendLine('')
}
[System.IO.File]::WriteAllText($ReportP, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("报告已写出: {0}" -f $ReportP)
if ($errN -gt 0) { exit 1 }
