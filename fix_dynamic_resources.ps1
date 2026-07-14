[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 修复 QuickGroupUIService.cs
$files = @(
    'Services\QuickGroupUIService.cs',
    'Controls\NativeModePanel.xaml.cs',
    'Controls\WindowUsagePanel.cs',
    'Controls\StickyNotePanel.cs',
    'AIChatDialog.cs'
)

foreach ($file in $files) {
    if (Test-Path $file) {
        $content = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
        
        # 替换 Background = (Brush)...FindResource("BrushXXX") 模式
        # 这种模式需要改为先创建对象，再调用 SetResourceReference
        
        # 保存修改后的内容
        [System.IO.File]::WriteAllText($file, $content, [System.Text.Encoding]::UTF8)
        Write-Host "Processed: $file"
    }
}

Write-Host "Done"
