using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Models;

namespace SideDesk;

public partial class ClipboardPickDialog : Window
{
    private readonly List<(ClipboardSavedItem Item, CheckBox Check)> _rows = new();
    private bool _suppress = false;

    /// <summary>用户勾选并确认的收藏项</summary>
    public List<ClipboardSavedItem> SelectedItems { get; private set; } = new();

    public ClipboardPickDialog(List<ClipboardSavedItem> candidates)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        BuildList(candidates);
    }

    private void BuildList(List<ClipboardSavedItem> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            selectAllCheck.Visibility = Visibility.Collapsed;
            listPanel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("clipboard.nothingToFavorite"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(4, 24, 4, 24),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];

            string icon = item.ContentType switch
            {
                "Image" => "🖼️",
                "File" => "📁",
                _ => "📝"
            };

            string preview = item.Content;
            int nl = preview.IndexOfAny(new[] { '\n', '\r' });
            if (nl >= 0) preview = preview.Substring(0, nl) + " …";
            if (preview.Length > 50) preview = preview.Substring(0, 50) + "…";

            var label = new Emoji.Wpf.TextBlock
            {
                Text = $"{icon}  {preview}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("BrushTextMain"),
                ToolTip = item.Content.Length > 300 ? item.Content.Substring(0, 300) + "..." : item.Content
            };

            var check = new CheckBox
            {
                Content = label,
                IsChecked = i == 0,
                Margin = new Thickness(2, 5, 2, 5),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            check.Checked += (s, e) => SyncSelectAll();
            check.Unchecked += (s, e) => SyncSelectAll();

            _rows.Add((item, check));
            listPanel.Children.Add(check);
        }

        SyncSelectAll();
    }

    private void SyncSelectAll()
    {
        if (_suppress) return;
        _suppress = true;
        selectAllCheck.IsChecked = _rows.Count > 0 && _rows.All(r => r.Check.IsChecked == true);
        _suppress = false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _suppress = true;
        bool target = selectAllCheck.IsChecked == true;
        foreach (var r in _rows) r.Check.IsChecked = target;
        _suppress = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedItems = _rows.Where(r => r.Check.IsChecked == true).Select(r => r.Item).ToList();
        if (SelectedItems.Count == 0)
        {
            MessageBox.Show(Framework.Lang.T("clipboard.pleaseSelectAtLeast"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
