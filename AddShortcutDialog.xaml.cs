using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SideDesk;

public partial class AddShortcutDialog : Window
{
    private bool _isBatchMode = false;
    private string _selectedColor = "#3498DB";
    private readonly string[] _presetColors =
    {
        "#3498DB", "#2ECC71", "#E74C3C", "#9B59B6",
        "#F39C12", "#1ABC9C", "#E67E22", "#34495E",
        "#2980B9", "#27AE60", "#C0392B", "#8E44AD"
    };

    /// <summary>单个模式结果</summary>
    public ShortcutResult? SingleResult { get; private set; }
    /// <summary>批量模式结果</summary>
    public List<ShortcutResult>? BatchResults { get; private set; }

    public AddShortcutDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

        // 加载浏览器列表
        var app = (App)Application.Current;
        browserCombo.Items.Add("默认浏览器");
        foreach (var browser in app.ProgramRegistry.Browsers)
        {
            browserCombo.Items.Add(browser.Name);
        }
        browserCombo.SelectedIndex = 0;

        // 初始化颜色选择器
        InitColorPicker();

        // 图标预览
        iconBox.TextChanged += (s, e) => iconPreview.Text = iconBox.Text.Trim();
    }

    private void InitColorPicker()
    {
        var accentBrush = (Brush)FindResource("BrushAccent");
        foreach (var color in _presetColors)
        {
            var swatch = new Border
            {
                Width = 32, Height = 32,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(6),
                Background = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!,
                BorderBrush = color == _selectedColor ? accentBrush : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = color
            };
            swatch.MouseLeftButtonUp += (s, e) =>
            {
                _selectedColor = (string)swatch.Tag!;
                foreach (UIElement child in colorPickerPanel.Children)
                {
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                }
                swatch.BorderBrush = accentBrush;
            };
            colorPickerPanel.Children.Add(swatch);
        }
    }

    private void BrowserCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (browserCombo.SelectedIndex <= 0)
        {
            profilePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var app = (App)Application.Current;
        var browserIndex = browserCombo.SelectedIndex - 1;
        if (browserIndex >= 0 && browserIndex < app.ProgramRegistry.Browsers.Count)
        {
            var browser = app.ProgramRegistry.Browsers[browserIndex];
            profileCombo.Items.Clear();
            profileCombo.Items.Add("无");
            foreach (var profile in browser.Profiles)
            {
                profileCombo.Items.Add(profile.Name);
            }
            profileCombo.SelectedIndex = 0;
            profilePanel.Visibility = browser.Profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ToggleBatch_Click(object sender, RoutedEventArgs e)
    {
        _isBatchMode = !_isBatchMode;
        singlePanel.Visibility = _isBatchMode ? Visibility.Collapsed : Visibility.Visible;
        batchPanel.Visibility = _isBatchMode ? Visibility.Visible : Visibility.Collapsed;
        toggleBatchBtn.Content = _isBatchMode ? "单个模式" : "批量模式";
        this.Height = _isBatchMode ? 350 : 290;
        this.Width = _isBatchMode ? 500 : 400;

        if (_isBatchMode && batchRows.Children.Count == 0)
        {
            AddBatchRow();
            AddBatchRow();
            AddBatchRow();
        }
    }

    private void AddBatchRow()
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

        var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");

        var iconBox = new TextBox
        {
            Margin = new Thickness(0, 0, 5, 0),
            Style = textBoxStyle,
            Height = 30,
            FontSize = (double)FindResource("FontSizeSmallPlus"),
            MaxLength = 4
        };
        Grid.SetColumn(iconBox, 0);

        var nameBox = new TextBox
        {
            Margin = new Thickness(0, 0, 5, 0),
            Style = textBoxStyle,
            Height = 30,
            FontSize = (double)FindResource("FontSizeSmallPlus")
        };
        Grid.SetColumn(nameBox, 1);

        var urlBox = new TextBox
        {
            Margin = new Thickness(0, 0, 5, 0),
            Style = textBoxStyle,
            Text = "https://",
            Height = 30,
            FontSize = (double)FindResource("FontSizeSmallPlus")
        };
        Grid.SetColumn(urlBox, 2);

        var appCheck = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsChecked = true,
            Style = (Style)FindResource("RaycastSwitchStyle")
        };
        Grid.SetColumn(appCheck, 3);

        nameBox.TextChanged += BatchRow_TextChanged;
        urlBox.TextChanged += BatchRow_TextChanged;

        row.Children.Add(iconBox);
        row.Children.Add(nameBox);
        row.Children.Add(urlBox);
        row.Children.Add(appCheck);

        batchRows.Children.Add(row);
    }

    private void BatchRow_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (batchRows.Children.Count == 0) return;
        var lastRow = batchRows.Children[batchRows.Children.Count - 1] as Grid;
        if (lastRow == null) return;

        string name = "";
        string url = "";
        foreach (UIElement child in lastRow.Children)
        {
            if (child is TextBox tb)
            {
                int col = Grid.GetColumn(tb);
                if (col == 1) name = tb.Text;
                else if (col == 2) url = tb.Text;
            }
        }

        if (!string.IsNullOrEmpty(name) || (url != "https://" && !string.IsNullOrEmpty(url)))
        {
            AddBatchRow();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchMode)
        {
            BatchResults = new List<ShortcutResult>();
            foreach (UIElement element in batchRows.Children)
            {
                if (element is not Grid row) continue;

                string name = "";
                string url = "";
                string icon = "";
                bool isApp = false;

                foreach (UIElement child in row.Children)
                {
                    if (child is TextBox tb)
                    {
                        int col = Grid.GetColumn(tb);
                        if (col == 0) icon = tb.Text?.Trim() ?? "";
                        else if (col == 1) name = tb.Text?.Trim() ?? "";
                        else if (col == 2) url = tb.Text?.Trim() ?? "";
                    }
                    else if (child is CheckBox cb)
                    {
                        isApp = cb.IsChecked ?? false;
                    }
                }

                if (url == "https://") url = "";
                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(url))
                {
                    BatchResults.Add(new ShortcutResult
                    {
                        Name = name,
                        Icon = string.IsNullOrEmpty(icon) ? null : icon,
                        Url = url,
                        IsAppMode = isApp
                    });
                }
            }

            if (BatchResults.Count > 0)
            {
                DialogResult = true;
                Close();
            }
        }
        else
        {
            var url = urlBox.Text?.Trim() ?? "";
            if (url == "https://") url = "";

            // 获取选中的浏览器 ID
            string? browserId = null;
            string? profileId = null;
            if (browserCombo.SelectedIndex > 0)
            {
                var app = (App)Application.Current;
                var browserIndex = browserCombo.SelectedIndex - 1;
                if (browserIndex >= 0 && browserIndex < app.ProgramRegistry.Browsers.Count)
                {
                    var browser = app.ProgramRegistry.Browsers[browserIndex];
                    browserId = browser.Id;

                    if (profileCombo.SelectedIndex > 0)
                    {
                        var profileIndex = profileCombo.SelectedIndex - 1;
                        if (profileIndex >= 0 && profileIndex < browser.Profiles.Count)
                        {
                            profileId = browser.Profiles[profileIndex].Id;
                        }
                    }
                }
            }

            SingleResult = new ShortcutResult
            {
                Name = nameBox.Text?.Trim() ?? "",
                Icon = string.IsNullOrEmpty(iconBox.Text.Trim()) ? null : iconBox.Text.Trim(),
                Url = url,
                IsAppMode = appModeCheck.IsChecked ?? false,
                BrowserId = browserId,
                ProfileId = profileId,
                BackgroundColor = _selectedColor
            };
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public class ShortcutResult
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string Url { get; set; } = "";
    public bool IsAppMode { get; set; } = false;
    public string? BrowserId { get; set; }
    public string? ProfileId { get; set; }
    public string BackgroundColor { get; set; } = "#3498DB";
}
