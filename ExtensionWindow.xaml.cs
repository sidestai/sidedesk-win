using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;

namespace SideDesk;

public partial class ExtensionWindow : Window
{
    private string _selectedType = "open";
    private readonly string[] _presetColors = { "#3B82F6", "#8B5CF6", "#10B981", "#F59E0B", "#EF4444", "#EC4899", "#06B6D4", "#6366F1" };

    public ExtensionWindow()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        BuildColorPalette();
        UpdateFormArea();
    }

    // === 类型卡片点击 ===
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string type)
        {
            _selectedType = type;
            UpdateCardSelection();
            UpdateFormArea();
        }
    }

    private void UpdateCardSelection()
    {
        var accentBrush = (Brush)FindResource("BrushAccent");
        var borderBrush = (Brush)FindResource("BrushBorder");

        cardOpen.BorderBrush = _selectedType == "open" ? accentBrush : borderBrush;
        cardOpen.BorderThickness = _selectedType == "open" ? new Thickness(2) : new Thickness(1);
        cardPaste.BorderBrush = _selectedType == "paste" ? accentBrush : borderBrush;
        cardPaste.BorderThickness = _selectedType == "paste" ? new Thickness(2) : new Thickness(1);
        cardPowerShell.BorderBrush = _selectedType == "powershell" ? accentBrush : borderBrush;
        cardPowerShell.BorderThickness = _selectedType == "powershell" ? new Thickness(2) : new Thickness(1);
        cardCSharp.BorderBrush = _selectedType == "csharp" ? accentBrush : borderBrush;
        cardCSharp.BorderThickness = _selectedType == "csharp" ? new Thickness(2) : new Thickness(1);
    }

    // === 动态表单 ===
    private void UpdateFormArea()
    {
        formArea.Children.Clear();
        switch (_selectedType)
        {
            case "open":
                BuildOpenForm();
                break;
            case "paste":
                BuildPasteForm();
                break;
            case "powershell":
                BuildScriptForm("PowerShell 脚本", "输入 PowerShell 脚本代码...");
                break;
            case "csharp":
                BuildScriptForm("C# 脚本", "输入 C# 脚本代码...");
                break;
        }
    }

    private void BuildOpenForm()
    {
        formArea.Children.Add(new TextBlock
        {
            Text = "⊙ 打开网址 / 程序",
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSizeNormal"),
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        formArea.Children.Add(new TextBlock
        {
            Text = "目标地址",
            Foreground = (Brush)FindResource("BrushTextMuted"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var targetDock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browseBtn = new Button
        {
            Content = "浏览...",
            Style = (Style)FindResource("GhostButtonStyle"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        targetDock.Children.Add(browseBtn);
        var targetBox = new TextBox
        {
            Name = "targetBox",
            Style = (Style)FindResource("GlobalTextBoxStyle")
        };
        targetDock.Children.Add(targetBox);
        formArea.Children.Add(targetDock);

        formArea.Children.Add(new TextBlock
        {
            Text = "支持网址（https://）、可执行文件、文件夹路径或系统协议",
            Foreground = (Brush)FindResource("BrushTextMuted"),
            FontSize = (double)FindResource("FontSizeTiny"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        formArea.Children.Add(new TextBlock
        {
            Text = "启动参数（可选）",
            Foreground = (Brush)FindResource("BrushTextMuted"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        var argsBox = new TextBox
        {
            Name = "argsBox",
            Style = (Style)FindResource("GlobalTextBoxStyle")
        };
        formArea.Children.Add(argsBox);

        browseBtn.Click += (s, ev) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "所有文件 (*.*)|*.*",
                Title = "选择目标"
            };
            if (dlg.ShowDialog() == true)
            {
                targetBox.Text = dlg.FileName;
            }
        };
    }

    private void BuildPasteForm()
    {
        formArea.Children.Add(new TextBlock
        {
            Text = "📋 粘贴文本",
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSizeNormal"),
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        formArea.Children.Add(new TextBlock
        {
            Text = "粘贴内容",
            Foreground = (Brush)FindResource("BrushTextMuted"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var pasteBox = new TextBox
        {
            Name = "pasteBox",
            Style = (Style)FindResource("GlobalTextBoxStyle"),
            Height = 150,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        formArea.Children.Add(pasteBox);
    }

    private void BuildScriptForm(string title, string placeholder)
    {
        formArea.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSizeNormal"),
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        formArea.Children.Add(new TextBlock
        {
            Text = "脚本代码",
            Foreground = (Brush)FindResource("BrushTextMuted"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var scriptBox = new TextBox
        {
            Name = "scriptBox",
            Style = (Style)FindResource("GlobalTextBoxStyle"),
            Height = 200,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas")
        };
        formArea.Children.Add(scriptBox);
    }

    // === 预览面板 ===
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        previewName.Text = string.IsNullOrWhiteSpace(nameBox.Text) ? "未命名扩展" : nameBox.Text;
    }

    private void DescBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        previewDesc.Text = descBox.Text;
    }

    // === 颜色选择 ===
    private void BuildColorPalette()
    {
        foreach (var hex in _presetColors)
        {
            var swatch = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Tag = hex
            };
            swatch.MouseLeftButtonDown += (s, e) =>
            {
                if (s is Border b && b.Tag is string color)
                    colorHexBox.Text = color;
            };
            colorPalette.Children.Add(swatch);
        }
    }

    private void ColorHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHexBox.Text);
            previewCard.BorderBrush = new SolidColorBrush(color);
        }
        catch { }
    }

    // === 操作按钮 ===
    private void TestRun_Click(object sender, RoutedEventArgs e)
    {
        var ext = BuildExtension();
        if (ext == null) return;
        ExecuteExtension(ext);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var ext = BuildExtension();
        if (ext == null) return;
        if (string.IsNullOrWhiteSpace(ext.Name))
        {
            MessageBox.Show("请填写扩展名称", "提示");
            return;
        }
        var app = (App)Application.Current;
        app.Extensions.Add(ext);
        DialogResult = true;
        Close();
    }

    private Extension? BuildExtension()
    {
        string target = "";
        string launchArgs = "";

        foreach (var child in formArea.Children)
        {
            if (child is TextBox tb)
            {
                if (tb.Name == "targetBox" || tb.Name == "pasteBox" || tb.Name == "scriptBox")
                    target = tb.Text;
                else if (tb.Name == "argsBox")
                    launchArgs = tb.Text;
            }
            else if (child is DockPanel dp)
            {
                foreach (var dc in dp.Children)
                {
                    if (dc is TextBox dtb)
                    {
                        if (dtb.Name == "targetBox")
                            target = dtb.Text;
                    }
                }
            }
        }

        return new Extension
        {
            Name = nameBox.Text.Trim(),
            Description = descBox.Text.Trim(),
            Type = _selectedType,
            Target = target,
            ThemeColor = colorHexBox.Text.Trim(),
            LaunchArgs = launchArgs
        };
    }

    private void ExecuteExtension(Extension ext)
    {
        try
        {
            switch (ext.Type)
            {
                case "open":
                    if (!string.IsNullOrEmpty(ext.Target))
                    {
                        var psi = new ProcessStartInfo(ext.Target) { UseShellExecute = true };
                        if (!string.IsNullOrEmpty(ext.LaunchArgs))
                            psi.Arguments = ext.LaunchArgs;
                        Process.Start(psi);
                    }
                    break;
                case "paste":
                    if (!string.IsNullOrEmpty(ext.Target))
                    {
                        Clipboard.SetText(ext.Target);
                        MessageBox.Show("内容已复制到剪贴板，请按 Ctrl+V 粘贴", "提示");
                    }
                    break;
                case "powershell":
                    if (!string.IsNullOrEmpty(ext.Target))
                    {
                        Process.Start(new ProcessStartInfo("powershell", $"-Command \"{ext.Target}\"") { UseShellExecute = false });
                    }
                    break;
                case "csharp":
                    MessageBox.Show("C# 脚本执行功能待实现", "提示");
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"执行失败: {ex.Message}", "错误");
        }
    }
}
