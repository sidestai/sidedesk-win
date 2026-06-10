using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Windows;
using System.Text;
using Velopack;

namespace SideDesk
{
    public partial class SettingsWindow : Window
    {
        private readonly App _app;
        private bool _isLoading = true;
        private ProgramRegistryManager _registry = null!;

        public SettingsWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            LoadCurrentSettings();
            _isLoading = false;

            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
        }

        private void LoadCurrentSettings()
        {
            // 主题模式
            themeModeCombo.Items.Add("深色");
            themeModeCombo.Items.Add("浅色");
            themeModeCombo.Items.Add("跟随系统");

            switch (_app.AppSettings.ThemeMode?.ToLower())
            {
                case "light": themeModeCombo.SelectedIndex = 1; break;
                case "system": themeModeCombo.SelectedIndex = 2; break;
                default: themeModeCombo.SelectedIndex = 0; break;
            }

            // 面板宽度
            widthBox.Text = _app.AppSettings.PanelWidth.ToString();

            // 停靠位置
            dockCombo.Items.Add("左侧");
            dockCombo.Items.Add("右侧");
            dockCombo.Items.Add("双侧");

            switch (_app.CurrentLayout)
            {
                case LayoutMode.Right: dockCombo.SelectedIndex = 1; break;
                case LayoutMode.Both: dockCombo.SelectedIndex = 2; break;
                default: dockCombo.SelectedIndex = 0; break;
            }

            // 程序注册表
            _registry = _app.ProgramRegistry;
            RefreshProgramList();
            RefreshBrowserList();

            // 显示默认 Profile 目录
            profileBaseDirText.Text = string.IsNullOrEmpty(_registry.DefaultProfileBaseDir) 
                ? "未设置" 
                : _registry.DefaultProfileBaseDir;

            RefreshExtensionList();

            titleLockSwitch.IsChecked = _app.AppSettings.EnableTitleLock;

            InitUpdateUI();
        }

        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (pageGeneral == null || pageAppearance == null || pageProgramManager == null || pageExtensions == null || pageTools == null || pageModules == null || pageAbout == null) return;
        
            // 隱藏所有页面
            pageGeneral.Visibility = Visibility.Collapsed;
            pageAppearance.Visibility = Visibility.Collapsed;
            pageProgramManager.Visibility = Visibility.Collapsed;
            pageExtensions.Visibility = Visibility.Collapsed;
            pageTools.Visibility = Visibility.Collapsed;
            pageModules.Visibility = Visibility.Collapsed;
            pageAbout.Visibility = Visibility.Collapsed;
        
            // 显示选中页面
            switch (navList.SelectedIndex)
            {
                case 0: pageGeneral.Visibility = Visibility.Visible; break;
                case 1: pageAppearance.Visibility = Visibility.Visible; break;
                case 2: pageProgramManager.Visibility = Visibility.Visible; break;
                case 3: pageExtensions.Visibility = Visibility.Visible; break;
                case 4: pageTools.Visibility = Visibility.Visible; break;
                case 5: pageModules.Visibility = Visibility.Visible; RefreshTemplateList(); break;
                case 6: pageAbout.Visibility = Visibility.Visible; break;
            }
        }

        private void ThemeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            string mode = themeModeCombo.SelectedIndex switch
            {
                1 => "Light",
                2 => "System",
                _ => "Dark"
            };

            _app.AppSettings.ThemeMode = mode;
            _app.AppSettings.Save();
            App.ApplyTheme(mode);
        }

        private void Width_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyWidth();
        }

        private void Width_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyWidth();
            }
        }

        private void ApplyWidth()
        {
            if (_isLoading) return;
            if (!int.TryParse(widthBox.Text, out int width) || width < 50 || width > 500)
            {
                widthBox.Text = _app.AppSettings.PanelWidth.ToString();
                return;
            }

            if (width != _app.AppSettings.PanelWidth)
            {
                _app.AppSettings.PanelWidth = width;
                _app.AppSettings.Save();
                _app.ApplyPanelWidth(width);
            }
        }

        private void Dock_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            LayoutMode newLayout = dockCombo.SelectedIndex switch
            {
                1 => LayoutMode.Right,
                2 => LayoutMode.Both,
                _ => LayoutMode.Left
            };

            if (newLayout != _app.CurrentLayout)
            {
                _app.AppSettings.Layout = newLayout;
                _app.AppSettings.Save();
                _app.SwitchLayout(newLayout);
            }
        }

        #region 程序管理

        private void RefreshProgramList()
        {
            programListPanel.Children.Clear();
            foreach (var prog in _registry.Programs)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                var nameBlock = new TextBlock
                {
                    Text = prog.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal")
                };
                var pathBlock = new TextBlock
                {
                    Text = prog.ExePath,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                info.Children.Add(nameBlock);
                info.Children.Add(pathBlock);
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var progId = prog.Id;
                delBtn.Click += (s, e) => { RemoveProgram(progId); };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                programListPanel.Children.Add(row);
            }

            if (_registry.Programs.Count == 0)
            {
                programListPanel.Children.Add(new TextBlock
                {
                    Text = "暂无已注册程序",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
            }
        }

        private void AddProgram_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择程序"
            };
            if (dlg.ShowDialog() == true)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                _registry.Programs.Add(new ProgramEntry { Name = name, ExePath = dlg.FileName });
                _registry.Save();
                RefreshProgramList();
            }
        }

        private void RemoveProgram(string id)
        {
            _registry.Programs.RemoveAll(p => p.Id == id);
            _registry.Save();
            RefreshProgramList();
        }

        #endregion

        #region 浏览器管理

        private void RefreshBrowserList()
        {
            browserListPanel.Children.Clear();
            foreach (var browser in _registry.Browsers)
            {
                var card = new Border
                {
                    Background = (Brush)FindResource("BrushInputBG"),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var cardContent = new StackPanel();

                // 浏览器头部（名称 + 路径 + 删除按钮）
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var browserInfo = new StackPanel();
                browserInfo.Children.Add(new TextBlock
                {
                    Text = browser.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal")
                });
                browserInfo.Children.Add(new TextBlock
                {
                    Text = browser.ExePath,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(browserInfo, 0);
                header.Children.Add(browserInfo);

                var delBrowserBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Top
                };
                var browserId = browser.Id;
                delBrowserBtn.Click += (s, e) => { RemoveBrowser(browserId); };
                Grid.SetColumn(delBrowserBtn, 1);
                header.Children.Add(delBrowserBtn);

                cardContent.Children.Add(header);

                // UserData 列表
                if (browser.Profiles.Count > 0)
                {
                    var profilesTitle = new TextBlock
                    {
                        Text = "UserData 配置",
                        Foreground = (Brush)FindResource("BrushTextSec"),
                        FontSize = (double)FindResource("FontSizeSmall"),
                        Margin = new Thickness(0, 8, 0, 4)
                    };
                    cardContent.Children.Add(profilesTitle);

                    foreach (var profile in browser.Profiles)
                    {
                        var profileRow = new Grid { Margin = new Thickness(12, 0, 0, 4) };
                        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var profileInfo = new StackPanel();
                        profileInfo.Children.Add(new TextBlock
                        {
                            Text = profile.Name,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            FontSize = (double)FindResource("FontSizeSmall")
                        });
                        profileInfo.Children.Add(new TextBlock
                        {
                            Text = profile.Directory,
                            Foreground = (Brush)FindResource("BrushTextMuted"),
                            FontSize = (double)FindResource("FontSizeTiny"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        Grid.SetColumn(profileInfo, 0);
                        profileRow.Children.Add(profileInfo);

                        var delProfileBtn = new Button
                        {
                            Content = "✕",
                            Style = (Style)FindResource("GhostButtonStyle"),
                            Padding = new Thickness(4, 1, 4, 1),
                            FontSize = 10
                        };
                        var pBrowserId = browser.Id;
                        var pProfileId = profile.Id;
                        delProfileBtn.Click += (s, e) => { RemoveProfile(pBrowserId, pProfileId); };
                        Grid.SetColumn(delProfileBtn, 1);
                        profileRow.Children.Add(delProfileBtn);

                        cardContent.Children.Add(profileRow);
                    }
                }

                // 添加 UserData 按钮
                var addProfileBtn = new Button
                {
                    Content = "+ UserData",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var addBrowserId = browser.Id;
                addProfileBtn.Click += (s, e) => { AddProfile(addBrowserId); };
                cardContent.Children.Add(addProfileBtn);

                card.Child = cardContent;
                browserListPanel.Children.Add(card);
            }
        }

        private void AddBrowser_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "浏览器 (*.exe)|*.exe",
                Title = "选择浏览器主程序"
            };
            if (dlg.ShowDialog() == true)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                _registry.Browsers.Add(new BrowserConfig { Name = name, ExePath = dlg.FileName });
                _registry.Save();
                RefreshBrowserList();
            }
        }

        private void RemoveBrowser(string id)
        {
            _registry.Browsers.RemoveAll(b => b.Id == id);
            _registry.Save();
            RefreshBrowserList();
        }

        private void AddProfile(string browserId)
        {
            var browser = _registry.GetBrowserById(browserId);
            if (browser == null) return;

            var dlg = new OpenFolderDialog
            {
                Title = "选择 UserData 目录"
            };
            if (dlg.ShowDialog() == true)
            {
                var name = System.IO.Path.GetFileName(dlg.FolderName);
                if (string.IsNullOrEmpty(name)) name = "Profile";
                browser.Profiles.Add(new UserDataProfile { Name = name, Directory = dlg.FolderName });
                _registry.Save();
                RefreshBrowserList();
            }
        }

        private void RemoveProfile(string browserId, string profileId)
        {
            var browser = _registry.GetBrowserById(browserId);
            browser?.Profiles.RemoveAll(p => p.Id == profileId);
            _registry.Save();
            RefreshBrowserList();
        }

        #endregion

        private void SelectProfileBaseDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择默认 Profile 目录"
            };
            if (dlg.ShowDialog() == true)
            {
                _registry.DefaultProfileBaseDir = dlg.FolderName;
                _registry.Save();
                profileBaseDirText.Text = dlg.FolderName;
            }
        }

        #region 扩展管理

        private void RefreshExtensionList()
        {
            extensionListPanel.Children.Clear();
            var app = (App)Application.Current;

            if (app.Extensions.Extensions.Count == 0)
            {
                extensionListPanel.Children.Add(new TextBlock
                {
                    Text = "暂无扩展",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
                return;
            }

            foreach (var ext in app.Extensions.Extensions)
            {
                var card = new Border
                {
                    Background = (Brush)FindResource("BrushInputBG"),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = ext.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.SemiBold
                });

                string typeLabel = ext.Type switch
                {
                    "open" => "打开网址/程序",
                    "paste" => "粘贴文本",
                    "powershell" => "PowerShell 脚本",
                    "csharp" => "C# 脚本",
                    _ => ext.Type
                };
                info.Children.Add(new TextBlock
                {
                    Text = typeLabel + (string.IsNullOrEmpty(ext.Description) ? "" : " — " + ext.Description),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var extId = ext.Id;
                delBtn.Click += (s, e) =>
                {
                    var app2 = (App)Application.Current;
                    app2.Extensions.Remove(extId);
                    RefreshExtensionList();
                };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                card.Child = row;
                extensionListPanel.Children.Add(card);
            }
        }

        private void NewExtension_Click(object sender, RoutedEventArgs e)
        {
            var win = new ExtensionWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                RefreshExtensionList();
            }
        }

        #endregion

        #region 工具 - 任务栏标题锁定

        private void TitleLockSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _app.AppSettings.EnableTitleLock = titleLockSwitch.IsChecked == true;
            _app.AppSettings.Save();
        }

        private void TaskbarOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            var windows = TaskbarOverlayManager.GetTaskbarWindows();
            if (windows.Count == 0)
            {
                MessageBox.Show("未找到可用的任务栏窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pickerDialog = new Window
            {
                Title = "选择窗口",
                Width = 800,
                Height = 540,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResize,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            pickerDialog.SourceInitialized += (s2, e2) => App.UpdateWindowDwmTheme(pickerDialog);

            var grid = new Grid { Margin = new Thickness(15) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ===== 左侧：已锁定窗口列表 =====
            var leftPanel = new DockPanel();
            Grid.SetColumn(leftPanel, 0);
            var leftHeader = new TextBlock
            {
                Text = "已锁定窗口列表",
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(leftHeader, Dock.Top);
            leftPanel.Children.Add(leftHeader);

            var lockedListBox = new ListBox
            {
                Background = (Brush)FindResource("BrushInputBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(lockedListBox, Dock.Top);

            var lockedBtnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(lockedBtnPanel, Dock.Bottom);
            var deleteRuleBtn = new Button
            {
                Content = "删除规则",
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var refreshLockedBtn = new Button
            {
                Content = "刷新",
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 6, 10, 6)
            };
            lockedBtnPanel.Children.Add(deleteRuleBtn);
            lockedBtnPanel.Children.Add(refreshLockedBtn);
            leftPanel.Children.Add(lockedBtnPanel);
            leftPanel.Children.Add(lockedListBox);

            Action reloadLocked = () =>
            {
                lockedListBox.Items.Clear();
                var rules = TitleLockRuleManager.GetAll();
                foreach (var r in rules)
                {
                    var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
                    rowPanel.Children.Add(new System.Windows.Shapes.Rectangle
                    {
                        Width = 20, Height = 20,
                        Margin = new Thickness(0, 0, 8, 0),
                        Fill = Brushes.Transparent
                    });

                    var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    string head = $"[{r.LockText}]  {r.ProcessName}";
                    sp.Children.Add(new TextBlock
                    {
                        Text = head,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    string detail = !string.IsNullOrEmpty(r.MatchPattern)
                        ? $"匹配（{(r.MatchMode == TitleMatchMode.Equals ? "等于" : "包含")}）：{r.MatchPattern}"
                        : $"原标题：{r.OriginalTitle}";
                    sp.Children.Add(new TextBlock
                    {
                        Text = detail,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    rowPanel.Children.Add(sp);
                    lockedListBox.Items.Add(new ListBoxItem { Content = rowPanel, Tag = r });
                }
                if (lockedListBox.Items.Count == 0)
                {
                    lockedListBox.Items.Add(new ListBoxItem
                    {
                        Content = new TextBlock
                        {
                            Text = "（暂无锁定规则）",
                            Foreground = (Brush)FindResource("BrushTextMuted"),
                            FontStyle = FontStyles.Italic
                        },
                        IsHitTestVisible = false
                    });
                }
            };
            reloadLocked();

            deleteRuleBtn.Click += (s, ev) =>
            {
                if (lockedListBox.SelectedItem is ListBoxItem li && li.Tag is TitleLockRule selRule)
                {
                    if (MessageBox.Show($"确定删除规则：[{selRule.LockText}] {selRule.ProcessName}？",
                        "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    {
                        TitleLockRuleManager.RemoveExact(selRule);
                        reloadLocked();
                    }
                }
                else
                {
                    MessageBox.Show("请选中要删除的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            refreshLockedBtn.Click += (s, ev) => reloadLocked();

            grid.Children.Add(leftPanel);

            // ===== 右侧：可选窗口 =====
            var rightPanel = new DockPanel();
            Grid.SetColumn(rightPanel, 2);
            var rightHeader = new TextBlock
            {
                Text = "选择一个窗口来锁定标题",
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(rightHeader, Dock.Top);
            rightPanel.Children.Add(rightHeader);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            var cancelBtn = new Button
            {
                Content = "取消",
                Style = (Style)FindResource("StandardButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancelBtn.Click += (s, ev) => pickerDialog.Close();
            var okBtn = new Button
            {
                Content = "下一步",
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8)
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            rightPanel.Children.Add(btnPanel);

            var listBox = new ListBox
            {
                Background = (Brush)FindResource("BrushInputBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            foreach (var w in windows)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
                if (w.Icon != null)
                {
                    itemPanel.Children.Add(new System.Windows.Controls.Image
                    {
                        Source = w.Icon,
                        Width = 20, Height = 20,
                        Margin = new Thickness(0, 0, 8, 0)
                    });
                }
                var textPanel = new StackPanel();
                textPanel.Children.Add(new TextBlock
                {
                    Text = w.Title,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 420
                });
                textPanel.Children.Add(new TextBlock
                {
                    Text = w.ProcessName,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11
                });
                itemPanel.Children.Add(textPanel);
                listBox.Items.Add(new ListBoxItem { Content = itemPanel, Tag = w });
            }
            rightPanel.Children.Add(listBox);

            okBtn.Click += (s, ev) =>
            {
                if (listBox.SelectedItem == null)
                {
                    MessageBox.Show("请选择一个窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var selected = (TaskbarOverlayManager.WindowInfo)((ListBoxItem)listBox.SelectedItem).Tag;
                pickerDialog.Close();
                ShowOverlayEditor(selected);
            };

            grid.Children.Add(rightPanel);
            pickerDialog.Content = grid;
            pickerDialog.ShowDialog();
        }

        private void ShowOverlayEditor(TaskbarOverlayManager.WindowInfo window)
        {
            var editorDialog = new Window
            {
                Title = $"锁定标题: {window.Title}",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            editorDialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(editorDialog);

            var rootPanel = new StackPanel { Margin = new Thickness(20) };

            rootPanel.Children.Add(new TextBlock
            {
                Text = "锁定任务栏标题",
                FontSize = (double)FindResource("FontSizeSubtitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            rootPanel.Children.Add(new TextBlock
            {
                Text = $"窗口：{window.Title}",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 4)
            });
            rootPanel.Children.Add(new TextBlock
            {
                Text = $"进程：{window.ProcessName}",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            rootPanel.Children.Add(new TextBlock
            {
                Text = "锁定后显示的标题文字：",
                Foreground = (Brush)FindResource("BrushTextSec"),
                FontSize = (double)FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            // 初始值：已锁定文字或窗口标题前2字符
            string initialText = "";
            var locked = TaskbarOverlayManager.GetLockedWindows();
            if (locked != null && locked.TryGetValue(window.Handle, out string lockedText) && !string.IsNullOrEmpty(lockedText))
            {
                initialText = lockedText;
            }
            else if (!string.IsNullOrEmpty(window.Title) && window.Title.Length >= 2)
            {
                initialText = window.Title.Substring(0, 2);
            }
            else if (!string.IsNullOrEmpty(window.Title))
            {
                initialText = window.Title;
            }

            var lockTextBox = new TextBox
            {
                Text = initialText,
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                MaxLength = 10
            };
            rootPanel.Children.Add(lockTextBox);

            // 匹配模式（可选）
            rootPanel.Children.Add(new TextBlock
            {
                Text = "匹配模式（用于自动恢复匹配）：",
                Foreground = (Brush)FindResource("BrushTextSec"),
                FontSize = (double)FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 16, 0, 6)
            });
            var matchPatternBox = new TextBox
            {
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                Text = window.Title
            };
            rootPanel.Children.Add(matchPatternBox);
            rootPanel.Children.Add(new TextBlock
            {
                Text = "留空则精确匹配当前标题",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeTiny"),
                Margin = new Thickness(0, 4, 0, 0)
            });

            // 按钮
            var editorBtnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var editorCancelBtn = new Button
            {
                Content = "取消",
                Style = (Style)FindResource("StandardButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            editorCancelBtn.Click += (s, ev) => editorDialog.Close();
            var editorOkBtn = new Button
            {
                Content = "锁定",
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8)
            };
            editorOkBtn.Click += (s, ev) =>
            {
                string text = lockTextBox.Text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    MessageBox.Show("请输入锁定文字", "提示");
                    return;
                }
                string pattern = matchPatternBox.Text.Trim();
                TaskbarOverlayManager.LockWindowTitle(window.Handle, text, pattern, TitleMatchMode.Contains);
                editorDialog.Close();
                MessageBox.Show($"已锁定: [{text}]", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            editorBtnPanel.Children.Add(editorCancelBtn);
            editorBtnPanel.Children.Add(editorOkBtn);
            rootPanel.Children.Add(editorBtnPanel);

            editorDialog.Content = rootPanel;
            editorDialog.ShowDialog();
        }

        #endregion

        #region 模块 - 数据模版管理

        private void RefreshTemplateList()
        {
            templateListPanel.Children.Clear();
            var manager = DataTemplateManager.Load();

            if (manager.Templates.Count == 0)
            {
                templateListPanel.Children.Add(new TextBlock
                {
                    Text = "暂无数据模版",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
                return;
            }

            foreach (var tpl in manager.Templates)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                var nameBlock = new TextBlock
                {
                    Text = tpl.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.SemiBold
                };
                info.Children.Add(nameBlock);

                var descText = string.IsNullOrEmpty(tpl.Category) ? tpl.Description : $"[{tpl.Category}] {tpl.Description}";
                info.Children.Add(new TextBlock
                {
                    Text = descText,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"目标: {tpl.TargetUrl}  |  选择器: {tpl.Selector}  |  刷新: {tpl.RefreshIntervalSeconds}s",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var tplId = tpl.Id;
                delBtn.Click += (s, e) => { RemoveTemplate(tplId); };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                templateListPanel.Children.Add(row);
            }
        }

        private void AddTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "新建数据模版",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(dialog);

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock
            {
                Text = "新建数据模版",
                FontSize = (double)FindResource("FontSizeSubtitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 名称
            panel.Children.Add(new TextBlock { Text = "模版名称", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);

            // 描述
            panel.Children.Add(new TextBlock { Text = "描述", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var descBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(descBox);

            // 分类
            panel.Children.Add(new TextBlock { Text = "分类", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var categoryBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(categoryBox);

            // 目标网址
            panel.Children.Add(new TextBlock { Text = "目标网址", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var urlBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(urlBox);

            // 浏览器选择
            panel.Children.Add(new TextBlock { Text = "浏览器", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var browserCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            browserCombo.Items.Add("默认浏览器");
            foreach (var b in _registry.Browsers)
                browserCombo.Items.Add(b.Name);
            browserCombo.SelectedIndex = 0;
            panel.Children.Add(browserCombo);

            // 容器(Profile)选择
            panel.Children.Add(new TextBlock { Text = "容器 (Profile)", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var profileCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            profileCombo.Items.Add("默认");
            profileCombo.SelectedIndex = 0;
            panel.Children.Add(profileCombo);

            // 浏览器切换时更新Profile列表
            browserCombo.SelectionChanged += (s, ev) =>
            {
                profileCombo.Items.Clear();
                profileCombo.Items.Add("默认");
                if (browserCombo.SelectedIndex > 0)
                {
                    var selectedBrowser = _registry.Browsers[browserCombo.SelectedIndex - 1];
                    foreach (var p in selectedBrowser.Profiles)
                        profileCombo.Items.Add(p.Name);
                }
                profileCombo.SelectedIndex = 0;
            };

            // 选择器
            panel.Children.Add(new TextBlock { Text = "选择器 (CSS Selector 或 XPath)", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var selectorGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var selectorBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle") };
            Grid.SetColumn(selectorBox, 0);
            selectorGrid.Children.Add(selectorBox);
            var pickSelectorBtn = new Button
            {
                Content = "选择",
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pickSelectorBtn, 1);
            selectorGrid.Children.Add(pickSelectorBtn);
            panel.Children.Add(selectorGrid);

            // 选择按钮点击逻辑
            pickSelectorBtn.Click += async (s, ev) =>
            {
                // 校验URL
                if (string.IsNullOrWhiteSpace(urlBox.Text))
                {
                    MessageBox.Show("请先输入目标网址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 确定浏览器路径
                string browserPath = "";
                string profileDir = "";
                if (browserCombo.SelectedIndex > 0)
                {
                    var selectedBrowser = _registry.Browsers[browserCombo.SelectedIndex - 1];
                    browserPath = selectedBrowser.ExePath;
                    if (profileCombo.SelectedIndex > 0)
                        profileDir = selectedBrowser.Profiles[profileCombo.SelectedIndex - 1].Directory;
                }
                else
                {
                    // 默认浏览器：尝试找 chrome/edge
                    var defaultPaths = new[]
                    {
                        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
                    };
                    browserPath = defaultPaths.FirstOrDefault(System.IO.File.Exists) ?? "";
                    if (string.IsNullOrEmpty(browserPath))
                    {
                        MessageBox.Show("未找到可用浏览器，请在浏览器下拉框中选择一个已注册的浏览器", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                // 禁用按钮
                pickSelectorBtn.IsEnabled = false;
                pickSelectorBtn.Content = "选择中...";

                try
                {
                    var (port, process) = await Services.CdpBrowserService.Instance.LaunchVisibleBrowserAsync(browserPath, profileDir, urlBox.Text.Trim());
                    if (process == null)
                    {
                        MessageBox.Show("启动浏览器失败\n\n可能原因：该浏览器已在运行中，请先关闭浏览器后重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    try
                    {
                        var selector = await Services.CdpBrowserService.Instance.PickElementSelectorAsync(port, urlBox.Text.Trim());
                        if (!string.IsNullOrEmpty(selector))
                        {
                            selectorBox.Text = selector;
                        }
                        else
                        {
                            MessageBox.Show("未选择到元素（超时或取消）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    finally
                    {
                        // 关闭浏览器
                        try
                        {
                            if (!process.HasExited)
                                process.Kill();
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"选择器获取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    pickSelectorBtn.IsEnabled = true;
                    pickSelectorBtn.Content = "选择";
                }
            };

            // 刷新间隔
            panel.Children.Add(new TextBlock { Text = "刷新间隔（秒）", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var intervalBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Text = "60", Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(intervalBox);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("StandardButtonStyle"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0) };
            cancelBtn.Click += (s, ev) => dialog.Close();
            var okBtn = new Button { Content = "保存", Style = (Style)FindResource("PrimaryButtonStyle"), Padding = new Thickness(16, 8, 16, 8) };
            okBtn.Click += (s, ev) =>
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(urlBox.Text) || string.IsNullOrWhiteSpace(selectorBox.Text))
                {
                    MessageBox.Show("请填写名称、目标网址和选择器", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                int interval = 60;
                int.TryParse(intervalBox.Text, out interval);
                if (interval < 10) interval = 10;

                var template = new Models.DataTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = nameBox.Text.Trim(),
                    Description = descBox.Text.Trim(),
                    Category = categoryBox.Text.Trim(),
                    TargetUrl = urlBox.Text.Trim(),
                    Selector = selectorBox.Text.Trim(),
                    RefreshIntervalSeconds = interval,
                    DefaultBrowserId = browserCombo.SelectedIndex > 0 ? _registry.Browsers[browserCombo.SelectedIndex - 1].Id : null,
                    DefaultProfileId = (browserCombo.SelectedIndex > 0 && profileCombo.SelectedIndex > 0)
                        ? _registry.Browsers[browserCombo.SelectedIndex - 1].Profiles[profileCombo.SelectedIndex - 1].Id
                        : null
                };

                var manager = DataTemplateManager.Load();
                manager.SaveUserTemplate(template);
                dialog.Close();
                RefreshTemplateList();
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            panel.Children.Add(btnPanel);

            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private void RemoveTemplate(string templateId)
        {
            if (MessageBox.Show("确定删除该模版？", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            // 从 user-templates.json 中删除
            var userFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-templates", "user-templates.json");
            if (System.IO.File.Exists(userFile))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(userFile);
                    var list = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<Models.DataTemplate>>(json) ?? new();
                    list.RemoveAll(t => t.Id == templateId);
                    System.IO.File.WriteAllText(userFile, Newtonsoft.Json.JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented));
                }
                catch { }
            }
            RefreshTemplateList();
        }

        #endregion

        #region 自动更新

        private UpdateInfo? _newVersionUpdateInfo;
        private bool _isCheckingUpdate;
        private bool _isDownloadingUpdate;
        private bool _updateEventsSubscribed;

        private void InitUpdateUI()
        {
            // 设置开关状态
            autoUpdateSwitch.IsChecked = _app.AppSettings.EnableAutoUpdate;

            // 显示当前版本
            try
            {
                var mgr = new UpdateManager(""); // dummy，仅获取版本
                if (mgr.IsInstalled)
                    versionText.Text = $"版本 v{mgr.CurrentVersion}";
            }
            catch { }

            // 订阅更新事件
            SubscribeUpdateEvents();

            // 如果已有更新就绪，直接显示
            if (VelopackUpdateService.Instance.IsUpdateReady)
            {
                applyUpdatePanel.Visibility = Visibility.Visible;
                updateStatusText.Text = "更新包已就绪，重启即可生效！";
                checkUpdateBtn.IsEnabled = false;
            }
        }

        private void SubscribeUpdateEvents()
        {
            if (_updateEventsSubscribed) return;
            _updateEventsSubscribed = true;

            VelopackUpdateService.Instance.UpdateStatusChanged += status =>
            {
                Dispatcher.Invoke(() => updateStatusText.Text = status);
            };

            VelopackUpdateService.Instance.DownloadProgressChanged += progress =>
            {
                Dispatcher.Invoke(() =>
                {
                    downloadProgressBar.Value = progress;
                    progressPercentText.Text = $"{progress}%";
                    if (progress >= 100)
                    {
                        downloadProgressPanel.Visibility = Visibility.Collapsed;
                        applyUpdatePanel.Visibility = Visibility.Visible;
                        _isDownloadingUpdate = false;
                    }
                });
            };

            VelopackUpdateService.Instance.UpdateReadyChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    applyUpdatePanel.Visibility = Visibility.Visible;
                    checkUpdateBtn.IsEnabled = false;
                });
            };
        }

        private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
        {
            _app.AppSettings.EnableAutoUpdate = autoUpdateSwitch.IsChecked == true;
            _app.AppSettings.Save();
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate || _isDownloadingUpdate) return;

            _isCheckingUpdate = true;
            checkUpdateBtn.IsEnabled = false;
            applyUpdatePanel.Visibility = Visibility.Collapsed;
            downloadProgressPanel.Visibility = Visibility.Collapsed;

            try
            {
                var updateInfo = await VelopackUpdateService.Instance.CheckForUpdatesAsync();
                if (updateInfo != null)
                {
                    _newVersionUpdateInfo = updateInfo;
                    // 自动开始下载
                    _isDownloadingUpdate = true;
                    downloadProgressPanel.Visibility = Visibility.Visible;
                    downloadProgressBar.Value = 0;
                    progressPercentText.Text = "0%";

                    var success = await VelopackUpdateService.Instance.DownloadUpdatesAsync(updateInfo);
                    if (!success)
                    {
                        downloadProgressPanel.Visibility = Visibility.Collapsed;
                        checkUpdateBtn.IsEnabled = true;
                    }
                }
                else
                {
                    checkUpdateBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                updateStatusText.Text = $"检查失败: {ex.Message}";
                checkUpdateBtn.IsEnabled = true;
            }
            finally
            {
                _isCheckingUpdate = false;
                if (!_isDownloadingUpdate)
                    checkUpdateBtn.IsEnabled = true;
            }
        }

        private void ApplyUpdateAndRestart_Click(object sender, RoutedEventArgs e)
        {
            var updateInfo = VelopackUpdateService.Instance.ReadyUpdateInfo ?? _newVersionUpdateInfo;
            if (updateInfo == null)
            {
                MessageBox.Show("更新信息不存在，请重新检查更新。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                VelopackUpdateService.Instance.ApplyAndRestart(updateInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用更新并重启失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
