# =============================================================================
# gen-keymap.ps1 — 多语言语义键改造 阶段1 工具
# 从 langs/en.json ∪ Framework/LangEn.cs 提取全部唯一中文键，
# 按英文译文生成 slug 候选语义键，结合调用点文件归域，输出：
#   - tools/i18n/keymap.json          最终键映射 { 中文原文: semantic.key }
#   - tools/i18n/conflict-report.md   冲突报告（候选重复/占位符不一致/来源差异等）
#   - tools/i18n/extraction.json      提取明细（供人工审定）
# 可选输入：
#   - tools/i18n/keymap-overrides.json  人工审定覆盖表 { 中文原文: semantic.key }
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\i18n\gen-keymap.ps1
# =============================================================================
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Root      = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$EnJson    = Join-Path $Root 'langs\en.json'
$LangEnCs  = Join-Path $Root 'Framework\LangEn.cs'
$OutDir    = $PSScriptRoot
$Overrides = Join-Path $OutDir 'keymap-overrides.json'

function Read-Utf8([string]$path) { [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }
function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# ---- JSON 字符串转义（RFC 8259，保留非 ASCII 原样） ----
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

# 有序字典 -> 缩进 JSON（按键序，键序由调用方保证）
function ConvertTo-OrderedJson([System.Collections.Specialized.OrderedDictionary]$dict) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('{')
    $i = 0; $n = $dict.Count
    foreach ($k in $dict.Keys) {
        $i++
        $line = '  ' + (ConvertTo-JsonString $k) + ': ' + (ConvertTo-JsonString ([string]$dict[$k]))
        if ($i -lt $n) { $line += ',' }
        [void]$sb.AppendLine($line)
    }
    [void]$sb.Append('}')
    $sb.ToString()
}

# ---- C# 字符串字面量反转义 ----
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
                    if ($i + 5 -lt $s.Length + 1) {
                        $hex = $s.Substring($i + 2, 4)
                        [void]$sb.Append([char][int]::Parse($hex, [System.Globalization.NumberStyles]::HexNumber))
                        $i += 6
                    } else { [void]$sb.Append($n); $i += 2 }
                    break
                }
                default { [void]$sb.Append($n); $i += 2; break }
            }
        } else {
            [void]$sb.Append($c); $i++
        }
    }
    $sb.ToString()
}

# ---- 英文译文 -> slug 候选（lowerCamel，最多 4 个词） ----
function ConvertTo-Slug([string]$en) {
    if ([string]::IsNullOrWhiteSpace($en)) { return $null }
    $s = [regex]::Replace($en, '\{[^{}]*\}', ' ')     # 去掉 {0} {1:xxx} 占位
    $s = $s.ToLowerInvariant()
    $s = [regex]::Replace($s, '[^a-z0-9]+', ' ')
    $words = @($s.Trim() -split '\s+' | Where-Object { $_ })
    if ($words.Count -eq 0) { return $null }
    $take = @($words | Select-Object -First 4)
    $head = $take[0]
    $tail = @($take | Select-Object -Skip 1 | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) })
    $slug = $head + ($tail -join '')
    if ($slug.Length -gt 48) { $slug = $slug.Substring(0, 48) }
    $slug
}

# ---- 占位符索引集合 {0} {1:format} ----
function Get-PlaceholderIndexes([string]$s) {
    @([regex]::Matches($s, '\{(\d+)(?:[:}]|,)', 'None') | ForEach-Object { $_.Groups[1].Value }) +
    @([regex]::Matches($s, '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value }) |
        Sort-Object -Unique
}

# =============================================================================
# 1. 提取双源词典
# =============================================================================
Write-Host "[1/6] 提取 langs/en.json 与 Framework/LangEn.cs ..."
$enDict = @{}
$raw = Read-Utf8 $EnJson
foreach ($p in ([PSCustomObject]($raw | ConvertFrom-Json)).PSObject.Properties) {
    $enDict[$p.Name] = [string]$p.Value
}

$langEnDict = @{}
if (Test-Path $LangEnCs) {
    $csText = Read-Utf8 $LangEnCs
    $rxEntry = [regex]'\[\s*"((?:[^"\\]|\\.)*)"\s*\]\s*=\s*"((?:[^"\\]|\\.)*)"'
    foreach ($m in $rxEntry.Matches($csText)) {
        $k = ConvertFrom-CSharpLiteral $m.Groups[1].Value
        $v = ConvertFrom-CSharpLiteral $m.Groups[2].Value
        $langEnDict[$k] = $v
    }
}

$allKeys = New-Object System.Collections.Generic.List[string]
foreach ($k in $enDict.Keys) { [void]$allKeys.Add($k) }
foreach ($k in $langEnDict.Keys) { if (-not $enDict.ContainsKey($k)) { [void]$allKeys.Add($k) } }
$allKeys = @($allKeys | Sort-Object)
Write-Host ("    en.json: {0} 键, LangEn.cs: {1} 键, 并集: {2} 键" -f $enDict.Count, $langEnDict.Count, $allKeys.Count)

# 来源差异统计
$onlyLangEn = @($allKeys | Where-Object { -not $enDict.ContainsKey($_) })
$onlyEnJson = @($allKeys | Where-Object { -not $langEnDict.ContainsKey($_) })
$diffTrans  = New-Object System.Collections.Generic.List[object]
foreach ($k in $allKeys) {
    if ($enDict.ContainsKey($k) -and $langEnDict.ContainsKey($k) -and $enDict[$k] -cne $langEnDict[$k]) {
        [void]$diffTrans.Add([pscustomobject]@{ Key = $k; EnJson = $enDict[$k]; LangEn = $langEnDict[$k] })
    }
}

# =============================================================================
# 2. 调用点扫描：每个中文键出现在哪些源文件（用于归域）
# =============================================================================
Write-Host "[2/6] 扫描调用点（*.cs / *.xaml）..."
$srcFiles = @(Get-ChildItem $Root -Recurse -Include *.cs, *.xaml -File |
    Where-Object {
        $rel = $_.FullName.Substring($Root.Length + 1)
        $rel -notmatch '^(obj|bin|_PACK|tools|langs|\.vs|node_modules)\\' -and
        $_.FullName -ne $LangEnCs
    })
$fileTexts = @{}
foreach ($f in $srcFiles) { $fileTexts[$f.FullName] = Read-Utf8 $f.FullName }
Write-Host ("    源文件数: {0}" -f $srcFiles.Count)

# 文件名 -> 一级域（按序，先匹配先得；路径相对根目录）
$DomainRules = @(
    @{ Rx = 'SettingsWindow';                                Domain = 'settings' },
    @{ Rx = 'AIChat';                                        Domain = 'aiChat' },
    @{ Rx = 'TodoDialog';                                    Domain = 'todo' },
    @{ Rx = 'StickyNote';                                    Domain = 'sticky' },
    @{ Rx = 'Clipboard(Pick|Service)';                       Domain = 'clipboard' },
    @{ Rx = '(CloudDrive|BaiduPan|AddCloudFile|CloudSync)';  Domain = 'cloudDrive' },
    @{ Rx = 'LoginWindow';                                   Domain = 'login' },
    @{ Rx = 'AddShortcutDialog';                             Domain = 'shortcut' },
    @{ Rx = '(ScreenManagerWindow|ScreenCaptureLogger|ScreenTime)'; Domain = 'screenTime' },
    @{ Rx = 'DigitalEmployee';                               Domain = 'digitalEmployee' },
    @{ Rx = 'ThemedMessageBox';                              Domain = 'messageBox' },
    @{ Rx = '(MarketWindow|PluginEditor|PluginTest|DataTemplateMarket|PluginMarket)'; Domain = 'market' },
    @{ Rx = '(AddWindowDialog|EditWindowDialog|SelectWindowDialog|WindowFinder|WindowLogger|WindowUsage|WebViewWindow|WebViewModePanel|CdpBrowser|CdpConnect)'; Domain = 'window' },
    @{ Rx = '(TeamLibrary|TeamService|FriendService)';       Domain = 'team' },
    @{ Rx = '(NotificationAction|NotificationService|NotificationCenter)'; Domain = 'notification' },
    @{ Rx = 'VelopackUpdate';                                Domain = 'update' },
    @{ Rx = '(DataVariable|VariableSet|DataTemplate|RowChange|LocalDb|DataPolling)'; Domain = 'data' },
    @{ Rx = '(HttpServer|WebSocketServer|BackendWebSocket|HttpLog|McpServer|McpTool)'; Domain = 'system' },
    @{ Rx = '(MainWindow|AppBarWindow|NativeModePanel|SidebarPanel|FloatingContextPanel|ModuleControl|TaskbarOverlay|EmojiTextBox|WsClientsWindow|Commands\.cs|Converters\.cs|Utilities\.cs|App\.xaml)'; Domain = 'nativePanel' },
    @{ Rx = '(AutoStart|GlobalHotkey|KeyboardHook|FocusWatch|VirtualDesktop|InstalledApps|DeviceId|ThemeManager|Favicon|AITool|ApiKey|SyncEncryption|QuickGroup|TitleLock|ProgramCategory)'; Domain = 'nativePanel' }
)

function Get-FileDomain([string]$relPath) {
    foreach ($r in $DomainRules) {
        if ($relPath -match $r.Rx) { return $r.Domain }
    }
    'misc'
}

# 跨模块复用词（出现于 >=4 个文件且跨 >=3 个域）自动归 common
$usage = @{}          # key -> @{ Files = @(); Domains = @{} }
$escCache = @{}
foreach ($k in $allKeys) {
    $escCache[$k] = $k.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
}
$fi = 0
foreach ($f in $srcFiles) {
    $fi++
    if ($fi % 25 -eq 0) { Write-Host ("    扫描进度 {0}/{1}" -f $fi, $srcFiles.Count) }
    $text = $fileTexts[$f.FullName]
    $rel = $f.FullName.Substring($Root.Length + 1)
    $dom = Get-FileDomain $rel
    foreach ($k in $allKeys) {
        if ($text.IndexOf($k, [System.StringComparison]::Ordinal) -ge 0 -or
            ($escCache[$k] -ne $k -and $text.IndexOf($escCache[$k], [System.StringComparison]::Ordinal) -ge 0)) {
            if (-not $usage.ContainsKey($k)) { $usage[$k] = @{ Files = New-Object System.Collections.Generic.List[string]; Domains = @{} } }
            [void]$usage[$k].Files.Add($rel)
            $usage[$k].Domains[$dom] = 1
        }
    }
}

# =============================================================================
# 3. 归域 + slug 候选
# =============================================================================
Write-Host "[3/6] 归域与生成候选键 ..."
$entry = @{}   # zh -> @{ En; Domain; Slug; Files; Sources }
foreach ($k in $allKeys) {
    $en = if ($enDict.ContainsKey($k)) { $enDict[$k] } else { $langEnDict[$k] }
    $domain = 'misc'
    $files = @(); $doms = @()
    if ($usage.ContainsKey($k)) {
        $files = @($usage[$k].Files)
        $doms  = @($usage[$k].Domains.Keys)
        if ($files.Count -ge 4 -and $doms.Count -ge 3) {
            $domain = 'common'
        } elseif ($doms.Count -ge 1) {
            # 单一域取该域；多域取非 nativePanel/misc 中最具体的，否则第一个
            $spec = @($doms | Where-Object { $_ -notin @('nativePanel', 'misc') })
            $domain = if ($spec.Count -ge 1) { $spec[0] } else { $doms[0] }
        }
    }
    $slug = ConvertTo-Slug $en
    if (-not $slug) {
        # 无英文词（纯符号等）：用稳定哈希命名
        $h = 0; foreach ($c in $k.ToCharArray()) { $h = ($h * 31 + [int]$c) % 46656 }
        $slug = 'sym' + [Convert]::ToString($h, 16)
    }
    $src = @()
    if ($enDict.ContainsKey($k)) { $src += 'en.json' }
    if ($langEnDict.ContainsKey($k)) { $src += 'LangEn' }
    $entry[$k] = @{ En = $en; Domain = $domain; Slug = $slug; Files = $files; Domains = $doms; Sources = $src }
}

# =============================================================================
# 4. 加载人工审定覆盖表（键为自动候选键，避免手写中文原文出错）
#    结构: { "rename": { "<自动键>": "<最终语义键>" },
#            "canonical": { "<最终语义键>": "<zh-CN 权威中文原文>" } }
# =============================================================================
$renameMap = @{}
$canonicalMap = @{}
if (Test-Path $Overrides) {
    Write-Host "[4/6] 加载人工审定覆盖表 keymap-overrides.json ..."
    $oRaw = Read-Utf8 $Overrides | ConvertFrom-Json
    if ($oRaw.PSObject.Properties.Name -contains 'rename') {
        foreach ($p in $oRaw.rename.PSObject.Properties) { $renameMap[$p.Name] = [string]$p.Value }
    } else {
        # 兼容旧格式：直接就是 { zh: key }
        foreach ($p in $oRaw.PSObject.Properties) { $renameMap[$p.Name] = [string]$p.Value }
    }
    if ($oRaw.PSObject.Properties.Name -contains 'canonical') {
        foreach ($p in $oRaw.canonical.PSObject.Properties) { $canonicalMap[$p.Name] = [string]$p.Value }
    }
    Write-Host ("    rename 条目: {0}, canonical 条目: {1}" -f $renameMap.Count, $canonicalMap.Count)
} else {
    Write-Host "[4/6] 无覆盖表，跳过"
}

# =============================================================================
# 5. 组装 + 候选冲突消解（确定性）
# =============================================================================
Write-Host "[5/6] 冲突消解 ..."
$keymap = @{}
$collisionLog = New-Object System.Collections.Generic.List[object]
$taken = @{}
foreach ($k in ($allKeys | Sort-Object { $_.Length })) {
    $e = $entry[$k]
    $cand = "$($e.Domain).$($e.Slug)"
    $final = $cand
    if ($taken.ContainsKey($final)) {
        $winnerZh = $taken[$cand]
        # 冲突：优先尝试追加更多英文词，再退化为计数后缀
        $extra = ConvertTo-Slug ($e.En + ' ' + $entry[$winnerZh].En)
        $alt = "$($e.Domain).$extra"
        if ($extra -and $extra -ne $e.Slug -and -not $taken.ContainsKey($alt)) {
            $final = $alt
        } else {
            $i = 2
            while ($taken.ContainsKey("$cand$i")) { $i++ }
            $final = "$cand$i"
        }
        [void]$collisionLog.Add([pscustomobject]@{
            Candidate = $cand
            WinnerZh  = $winnerZh
            WinnerEn  = $entry[$winnerZh].En
            LoserZh   = $k
            LoserEn   = $e.En
            Resolved  = $final
        })
    }
    $taken[$final] = $k
    $keymap[$k] = $final
}

# 应用人工审定改名（旧自动键 -> 新语义键；允许多个中文键改名到同一目标 = 有意合并）
$mergeLog = New-Object System.Collections.Generic.List[object]
if ($renameMap.Count -gt 0) {
    foreach ($k in @($keymap.Keys)) {
        if ($renameMap.ContainsKey($keymap[$k])) { $keymap[$k] = $renameMap[$keymap[$k]] }
    }
    # 校验：未参与合并的键必须全局唯一；合并组必须有 canonical 或自动选首个（排序）
    $byFinal = @{}
    foreach ($k in $keymap.Keys) {
        if (-not $byFinal.ContainsKey($keymap[$k])) { $byFinal[$keymap[$k]] = New-Object System.Collections.Generic.List[string] }
        [void]$byFinal[$keymap[$k]].Add($k)
    }
    foreach ($f in $byFinal.Keys) {
        if ($byFinal[$f].Count -gt 1) {
            [void]$mergeLog.Add([pscustomobject]@{ Final = $f; Sources = ($byFinal[$f] | Sort-Object) })
        }
    }
}
# 写出 canonical.json（合并组的权威中文原文）
$canonOut = New-Object System.Collections.Specialized.OrderedDictionary
foreach ($m in ($mergeLog | Sort-Object Final)) {
    $canon = if ($canonicalMap.ContainsKey($m.Final)) { $canonicalMap[$m.Final] } else { $m.Sources[0] }
    $canonOut[$m.Final] = $canon
}
Write-Utf8 (Join-Path $OutDir 'canonical.json') ((ConvertTo-OrderedJson $canonOut) + "`r`n")

# =============================================================================
# 6. 输出
# =============================================================================
Write-Host "[6/6] 写出 keymap.json / conflict-report.md / extraction.json ..."

# keymap.json（按语义键排序输出，值稳定）
$sorted = New-Object System.Collections.Specialized.OrderedDictionary
foreach ($k in ($keymap.Keys | Sort-Object { $keymap[$_] }, { $_ })) { $sorted[$k] = $keymap[$k] }
Write-Utf8 (Join-Path $OutDir 'keymap.json') ((ConvertTo-OrderedJson $sorted) + "`r`n")

# extraction.json（明细）
$ex = New-Object System.Collections.Specialized.OrderedDictionary
foreach ($k in ($allKeys | Sort-Object)) {
    $e = $entry[$k]
    $ex[$k] = [pscustomobject]@{
        en      = $e.En
        key     = $keymap[$k]
        domain  = $e.Domain
        sources = ($e.Sources -join ',')
        files   = ($e.Files | Select-Object -First 8)
    }
}
$exJson = ($ex | ConvertTo-Json -Depth 4)
Write-Utf8 (Join-Path $OutDir 'extraction.json') $exJson

# conflict-report.md
$phMismatch = New-Object System.Collections.Generic.List[object]
foreach ($k in $allKeys) {
    $zhPh = @(Get-PlaceholderIndexes $k)
    $enPh = @(Get-PlaceholderIndexes $entry[$k].En)
    if (($zhPh -join ',') -ne ($enPh -join ',')) {
        [void]$phMismatch.Add([pscustomobject]@{ Key = $k; ZhPh = ($zhPh -join ','); EnPh = ($enPh -join ','); En = $entry[$k].En })
    }
}
$domainStat = @{}
foreach ($k in $allKeys) { $d = $keymap[$k].Split('.')[0]; $domainStat[$d] = ($domainStat[$d] + 1) }
$notFound = @($allKeys | Where-Object { -not $usage.ContainsKey($_) })

$rp = New-Object System.Text.StringBuilder
[void]$rp.AppendLine('# keymap 冲突与差异报告')
[void]$rp.AppendLine('')
[void]$rp.AppendLine(('- 总键数（并集）: {0}' -f $allKeys.Count))
[void]$rp.AppendLine(('- 仅 LangEn.cs: {0}，仅 en.json: {1}，双源译文不一致: {2}' -f $onlyLangEn.Count, $onlyEnJson.Count, $diffTrans.Count))
[void]$rp.AppendLine(('- 占位符 {{n}} 不一致: {0}' -f $phMismatch.Count))
[void]$rp.AppendLine(('- 候选键冲突（已消解）: {0}' -f $collisionLog.Count))
[void]$rp.AppendLine(('- 人工合并（多中文键 -> 同一语义键）: {0}' -f $mergeLog.Count))
[void]$rp.AppendLine(('- 代码中未找到调用点: {0}' -f $notFound.Count))
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 域分布')
foreach ($d in ($domainStat.Keys | Sort-Object)) { [void]$rp.AppendLine(('- {0}: {1}' -f $d, $domainStat[$d])) }
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 占位符不一致明细')
foreach ($x in $phMismatch) {
    [void]$rp.AppendLine(('- [{0}] zh:[{1}] en:[{2}] => {3}' -f $x.Key, $x.ZhPh, $x.EnPh, $x.En))
}
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 双源译文不一致（en.json 为准）')
foreach ($x in $diffTrans) {
    [void]$rp.AppendLine(('- [{0}]' -f $x.Key))
    [void]$rp.AppendLine(('  - en.json: {0}' -f $x.EnJson))
    [void]$rp.AppendLine(('  - LangEn : {0}' -f $x.LangEn))
}
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 候选键冲突消解记录')
foreach ($x in $collisionLog) {
    [void]$rp.AppendLine(('- 候选 `{0}`' -f $x.Candidate))
    [void]$rp.AppendLine(('  - 保留 [{0}] ({1})' -f $x.WinnerZh, $x.WinnerEn))
    [void]$rp.AppendLine(('  - 改名 [{0}] ({1}) => `{2}`' -f $x.LoserZh, $x.LoserEn, $x.Resolved))
}
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 人工合并记录')
foreach ($m in ($mergeLog | Sort-Object Final)) {
    $canon = if ($canonicalMap.ContainsKey($m.Final)) { $canonicalMap[$m.Final] } else { $m.Sources[0] }
    [void]$rp.AppendLine(('- `{0}` <= [{1}]（权威: [{2}]）' -f $m.Final, ($m.Sources -join '] ['), $canon))
}
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 仅 LangEn.cs 存在的键')
foreach ($k in $onlyLangEn) { [void]$rp.AppendLine(('- [{0}] => {1} = {2}' -f $k, $keymap[$k], $entry[$k].En)) }
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 仅 en.json 存在的键')
foreach ($k in $onlyEnJson) { [void]$rp.AppendLine(('- [{0}] => {1} = {2}' -f $k, $keymap[$k], $entry[$k].En)) }
[void]$rp.AppendLine('')
[void]$rp.AppendLine('## 代码中未找到调用点的键（可能动态拼接或已废弃）')
foreach ($k in $notFound) { [void]$rp.AppendLine(('- [{0}] => {1}' -f $k, $keymap[$k])) }
Write-Utf8 (Join-Path $OutDir 'conflict-report.md') $rp.ToString()

Write-Host ("完成: {0} 个语义键 -> {1}" -f $keymap.Count, (Join-Path $OutDir 'keymap.json'))
