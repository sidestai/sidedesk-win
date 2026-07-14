[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$filePath = 'Services\QuickGroupUIService.cs'
$content = [System.IO.File]::ReadAllText($filePath, [System.Text.Encoding]::UTF8)

# 修复 Window Background = (Brush)Application.Current.FindResource("BrushDialogBG")
# 这种模式在 Window 对象初始化器中
$content = $content -replace 'Background = \(Brush\)Application\.Current\.FindResource\("BrushDialogBG"\),\s*\n(\s*)Foreground = \(Brush\)Application\.Current\.FindResource\("BrushTextMain"\)', 'Foreground = (Brush)Application.Current.FindResource("BrushTextMain")'

# 保存
[System.IO.File]::WriteAllText($filePath, $content, [System.Text.Encoding]::UTF8)
Write-Host "Done - QuickGroupUIService.cs"
