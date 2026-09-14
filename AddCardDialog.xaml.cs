using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SideDesk;

/// <summary>
/// 添加卡片对话框 - 显示所有可用卡片类型，点击添加
/// </summary>
public partial class AddCardDialog : Window
{
    /// <summary>卡片类型项</summary>
    public class CardTypeItem
    {
        public string Icon { get; set; } = "";
        public string Name { get; set; } = "";
        public string HandlerName { get; set; } = "";
    }

    /// <summary>选择卡片后的回调（参数为处理方法名）</summary>
    private readonly Action<string>? _onCardSelected;

    /// <summary>创建添加卡片对话框</summary>
    /// <param name="onCardSelected">选择卡片后的回调，参数为处理方法名（如 "AddGroupPanel_Click"）</param>
    public AddCardDialog(Action<string>? onCardSelected = null)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        _onCardSelected = onCardSelected;
        PopulateCardTypes();
    }

    /// <summary>填充所有卡片类型（按分类分组：分类标题 + WrapPanel 按钮组）</summary>
    private void PopulateCardTypes()
    {
        var categories = new List<(string CatKey, CardTypeItem[] Items)>
        {
            ("addCard.catBasic", new[]
            {
                C("📂", "common.group", "AddGroupPanel_Click"),
                C("📝", "common.stickyNotes", "AddStickyNotesPanel_Click"),
                C("📁", "common.fileArea", "AddFileAreaPanel_Click"),
            }),
            ("addCard.catEfficiency", new[]
            {
                C("⚡", "common.shortcut", "AddQuickGroupPanel_Click"),
                C("🍅", "common.pomodoroAlarm", "AddPomodoroPanel_Click"),
            }),
            ("addCard.catTodo", new[]
            {
                C("✅", "common.todayTodos", "AddTodoPanel_Click"),
                C("📋", "common.clipboardFavorites", "AddClipboardPanel_Click"),
                C("✔", "common.checkIn", "AddCheckInPanel_Click"),
                C("🔥", "common.dailyActivity", "AddDailyActivityPanel_Click"),
            }),
            ("addCard.catTime", new[]
            {
                C("⏱", "common.timer", "AddTimerPanel_Click"),
                C("⏳", "common.countdown", "AddCountdownPanel_Click"),
                C("📅", "common.calendar", "AddCalendarPanel_Click"),
            }),
            ("addCard.catData", new[]
            {
                C("💻", "common.systemInfo", "AddSystemMonitorPanel_Click"),
                C("📊", "common.screenTime", "AddScreenTimePanel_Click"),
                C("📈", "common.dataDashboard", "AddDataDashboard_Click"),
                C("🚦", "common.signalLight", "AddSignalLightPanel_Click"),
            }),
            ("addCard.catSocial", new[]
            {
                C("👥", "common.friends", "AddFriendPanel_Click"),
                C("🔍", "settings.baiduSearch", "AddWebBaiduPanel_Click"),
            }),
            ("addCard.catTeam", new[]
            {
                C("📦", "nativePanel.addFromTeamComponent", "AddFromTeamLibrary_Click"),
            }),
        };

        for (int i = 0; i < categories.Count; i++)
        {
            var (catKey, items) = categories[i];

            // 分类标题
            cardSections.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T(catKey),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, i == 0 ? 0 : 14, 0, 4)
            });

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items) wrap.Children.Add(BuildCardButton(item));
            cardSections.Children.Add(wrap);
        }
    }

    /// <summary>卡片类型项快捷构造（名称走 i18n）</summary>
    private static CardTypeItem C(string icon, string nameKey, string handler)
        => new() { Icon = icon, Name = Framework.Lang.T(nameKey), HandlerName = handler };

    /// <summary>单个卡片类型按钮：彩色 emoji 图标 + 名称（样式共用 CardButtonStyle）</summary>
    private Button BuildCardButton(CardTypeItem item)
    {
        var btn = new Button
        {
            Tag = item,
            Style = (Style)FindResource("CardButtonStyle")
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        // 彩色 emoji 必须用 Emoji.Wpf.TextBlock（普通 TextBlock 在 WPF 下只出单色轮廓）
        content.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = item.Icon,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("BrushTextMain")
        });
        btn.Content = content;
        btn.Click += CardButton_Click;
        return btn;
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CardTypeItem item)
        {
            _onCardSelected?.Invoke(item.HandlerName);
            DialogResult = true;
            Close();
        }
    }
}
