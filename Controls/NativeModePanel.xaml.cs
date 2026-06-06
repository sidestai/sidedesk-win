using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Theming;
using SideDesk.Windows;
using SideDesk.Framework;

namespace SideDesk.Controls
{
    /// <summary>
    /// 本地 UI 模式面板
    /// 负责管理原生控件（分组、便签、剪贴板、系统监控等）
    /// </summary>
    public partial class NativeModePanel : UserControl
    {
        private MainWindow? _mainWindow;

        // 服务实例
        private ClipboardService? _clipboardService;
        private KeyboardHookService? _keyboardHookService;
        private MouseHookService? _mouseHookService;
        private QuickGroupUIService? _quickGroupUIService;

        // 窗口展开/收缩
        private bool _isExpanded = false;
        private double _originalWidth = 0;
        private const double EXPAND_WIDTH = 100;

        // 剪贴板 UI 状态
        private bool _clipboardExpanded = true;

        // 计数器 UI 引用
        private Button? _keyCountValueButton;
        private Button? _mouseDistanceValueButton;

        // 初始化标志
        private bool _isInitialized = false;

        // 对话框实例
        private AIChatDialog? _aiChatDialog;
        private WindowUsagePanel? _windowUsagePanel;

        /// <summary>请求主窗口调整宽度</summary>
        public event Action<double>? RequestWidthChange;

        public NativeModePanel()
        {
            InitializeComponent();
            Loaded += NativeModePanel_Loaded;
            Unloaded += NativeModePanel_Unloaded;
        }

        /// <summary>
        /// 设置主窗口引用
        /// </summary>
        public void SetMainWindow(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            if (_quickGroupUIService != null)
                _quickGroupUIService.OwnerWindow = mainWindow;
        }

        /// <summary>
        /// 初始化本地 UI 内容（只执行一次）
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            // 初始化快捷分组服务
            _quickGroupUIService = new QuickGroupUIService(groupPanel);
            _quickGroupUIService.OwnerWindow = _mainWindow;
            _quickGroupUIService.LoadGroups();

            // 初始化剪贴板服务
            _clipboardService = new ClipboardService();
            _clipboardService.ClipboardChanged += OnClipboardChanged;

            // 初始化键盘和鼠标钩子服务
            _keyboardHookService = new KeyboardHookService();
            _keyboardHookService.KeyCountChanged += OnKeyCountChanged;

            _mouseHookService = new MouseHookService();
            _mouseHookService.MouseDistanceChanged += OnMouseDistanceChanged;

            // 创建计数器 UI
            CreateKeyCountPanel();
            CreateMouseDistancePanel();

            // 便签
            RefreshStickyNotes();

            // 启用拖放
            AllowDrop = true;
            Drop += NativeModePanel_Drop;
            PreviewDragOver += NativeModePanel_PreviewDragOver;

            Debug.WriteLine("[NativeModePanel] 本地 UI 内容已加载");
        }

        private void NativeModePanel_Loaded(object sender, RoutedEventArgs e)
        {
            StartServices();
        }

        private void NativeModePanel_Unloaded(object sender, RoutedEventArgs e)
        {
            StopServices();
        }

        /// <summary>启动所有后台服务</summary>
        public void StartServices()
        {
            // 启动剪贴板监控（需要窗口句柄）
            if (_clipboardService != null && _mainWindow != null)
            {
                var helper = new WindowInteropHelper(_mainWindow);
                if (helper.Handle != IntPtr.Zero)
                {
                    _clipboardService.Start(helper.Handle);
                    // 注册 WndProc 钩子
                    var source = HwndSource.FromHwnd(helper.Handle);
                    source?.AddHook(WndProc);
                }
            }

            // 启动键盘/鼠标钩子
            _keyboardHookService?.Start();
            _mouseHookService?.Start();
        }

        /// <summary>停止所有后台服务</summary>
        public void StopServices()
        {
            _clipboardService?.Stop();
            _keyboardHookService?.Stop();
            _mouseHookService?.Stop();
            _quickGroupUIService?.Shutdown();

            // 移除 WndProc 钩子
            if (_mainWindow != null)
            {
                var helper = new WindowInteropHelper(_mainWindow);
                if (helper.Handle != IntPtr.Zero)
                {
                    var source = HwndSource.FromHwnd(helper.Handle);
                    source?.RemoveHook(WndProc);
                }
            }
        }

        /// <summary>处理窗口消息（用于剪贴板监控）</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            _clipboardService?.ProcessMessage(msg);
            return IntPtr.Zero;
        }

        #region 窗口展开/收缩

        /// <summary>展开面板（增加宽度）</summary>
        public void Expand()
        {
            if (_isExpanded || _mainWindow == null) return;
            _originalWidth = _mainWindow.Width;
            _isExpanded = true;
            RequestWidthChange?.Invoke(_originalWidth + EXPAND_WIDTH);
        }

        /// <summary>收缩面板（恢复宽度）</summary>
        public void Collapse()
        {
            if (!_isExpanded) return;
            _isExpanded = false;
            RequestWidthChange?.Invoke(_originalWidth);
        }

        public bool IsExpanded => _isExpanded;

        #endregion

        #region 剪贴板 UI

        private void OnClipboardChanged()
        {
            Dispatcher.BeginInvoke(RefreshClipboardUI, DispatcherPriority.Background);
        }

        private void RefreshClipboardUI()
        {
            clipboardPanel.Children.Clear();

            if (_clipboardService == null || _clipboardService.ClipboardHistory.Count == 0)
            {
                clipboardPanel.Children.Add(new TextBlock
                {
                    Text = "（暂无剪贴板记录）",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    Margin = new Thickness(4)
                });
                return;
            }

            foreach (var entry in _clipboardService.ClipboardHistory)
            {
                clipboardPanel.Children.Add(BuildClipboardItem(entry));
            }

            UpdateClipboardVisibility();
        }

        private UIElement BuildClipboardItem(ClipboardEntry entry)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(3, 1, 3, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Height = 22
            };

            // 类型图标
            string icon = ClipboardService.GetContentTypeIcon(entry.ContentType);
            string timeStr = entry.Time.ToString("HH:mm:ss");
            var iconText = new TextBlock
            {
                Text = icon, FontSize = 12,
                Foreground = ClipboardService.GetContentTypeColor(entry.ContentType),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = timeStr
            };
            DockPanel.SetDock(iconText, Dock.Left);
            row.Children.Add(iconText);

            // 复制按钮（默认隐藏）
            var copyBtn = new Button
            {
                Content = "⎘", Width = 20, Height = 20,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                BorderThickness = new Thickness(0), FontSize = 12,
                Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "复制到剪贴板", Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(copyBtn, Dock.Right);
            row.Children.Add(copyBtn);

            row.MouseEnter += (s, e) => copyBtn.Visibility = Visibility.Visible;
            row.MouseLeave += (s, e) => copyBtn.Visibility = Visibility.Collapsed;

            // 文本内容
            string displayText = entry.Text;
            if (entry.ContentType != ClipboardContentType.Text)
            {
                if (displayText.Length > 30) displayText = displayText.Substring(0, 30) + "…";
            }
            else
            {
                int newline = displayText.IndexOfAny(new char[] { '\n', '\r' });
                if (newline >= 0) displayText = displayText.Substring(0, newline) + "…";
                if (displayText.Length > 30) displayText = displayText.Substring(0, 30) + "…";
            }

            var textBlock = new TextBlock
            {
                Text = displayText, FontSize = 11,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = entry.Text.Length > 200 ? entry.Text.Substring(0, 200) + "..." : entry.Text
            };
            row.Children.Add(textBlock);

            // 点击/按钮复制
            string text = entry.Text;
            var filePaths = entry.FilePaths;
            var contentType = entry.ContentType;

            Action copyAction = () =>
            {
                try
                {
                    if (contentType == ClipboardContentType.File && filePaths != null && filePaths.Count > 0)
                    {
                        var sc = new StringCollection();
                        sc.AddRange(filePaths.ToArray());
                        Clipboard.SetFileDropList(sc);
                    }
                    else
                    {
                        Clipboard.SetText(text);
                    }
                }
                catch { }
            };

            copyBtn.Click += (s, e) => { copyAction(); e.Handled = true; };
            row.MouseLeftButtonUp += (s, e) => { if (!e.Handled) copyAction(); };

            return row;
        }

        private void UpdateClipboardVisibility()
        {
            clipboardPanel.Visibility = _clipboardExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 键盘/鼠标计数器 UI

        private void OnKeyCountChanged(int count)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_keyCountValueButton != null)
                    _keyCountValueButton.Content = count.ToString("N0");
                keyCountText.Text = $"⌨ {count:N0}";
            });
        }

        private void OnMouseDistanceChanged(long distance)
        {
            // 降低更新频率（每100次调用更新一次 UI）
            if (distance % 100 != 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_mouseDistanceValueButton != null)
                    _mouseDistanceValueButton.Content = MouseHookService.FormatDistance(distance);
                mouseDistText.Text = $"🖱 {MouseHookService.FormatDistance(distance)}";
            });
        }

        private void CreateKeyCountPanel()
        {
            var panel = new DockPanel
            {
                Margin = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent
            };

            _keyCountValueButton = new Button
            {
                Content = (_keyboardHookService?.TodayKeyCount ?? 0).ToString("N0"),
                Width = 70, Height = 28,
                Margin = new Thickness(2, 0, 4, 0), Padding = new Thickness(2),
                Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#27AE60")!,
                Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_keyCountValueButton, Dock.Right);
            panel.Children.Add(_keyCountValueButton);

            var nameText = new TextBlock
            {
                Text = "今日按键",
                Padding = new Thickness(8, 5, 0, 5),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(nameText);

            systemMonitorPanel.Children.Insert(0, panel);
        }

        private void CreateMouseDistancePanel()
        {
            var panel = new DockPanel
            {
                Margin = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent
            };

            _mouseDistanceValueButton = new Button
            {
                Content = MouseHookService.FormatDistance(_mouseHookService?.TodayMouseDistance ?? 0),
                Width = 70, Height = 28,
                Margin = new Thickness(2, 0, 4, 0), Padding = new Thickness(2),
                Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#2980B9")!,
                Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_mouseDistanceValueButton, Dock.Right);
            panel.Children.Add(_mouseDistanceValueButton);

            var nameText = new TextBlock
            {
                Text = "鼠标移动",
                Padding = new Thickness(8, 5, 0, 5),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(nameText);

            systemMonitorPanel.Children.Add(panel);
        }

        #endregion

        #region 便签

        private StickyNotePanel? _stickyNotePanelDialog;

        private void RefreshStickyNotes()
        {
            stickyNotePanel.Children.Clear();
            var notes = StickyNoteManager.GetAll();
            foreach (var note in notes)
            {
                stickyNotePanel.Children.Add(BuildStickyNoteItem(note));
            }
            if (notes.Count == 0)
            {
                stickyNotePanel.Children.Add(new TextBlock
                {
                    Text = "（暂无便签）",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    Margin = new Thickness(4)
                });
            }
        }

        private UIElement BuildStickyNoteItem(StickyNote note)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Height = 22
            };

            // 删除按钮
            var delBtn = new Button
            {
                Content = "✕", Width = 18, Height = 18,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 80, 80)),
                BorderThickness = new Thickness(0), FontSize = 10,
                Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "删除", Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            row.MouseEnter += (s, e) => delBtn.Visibility = Visibility.Visible;
            row.MouseLeave += (s, e) => delBtn.Visibility = Visibility.Collapsed;

            var textBlock = new TextBlock
            {
                Text = note.Preview, FontSize = 11,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = note.Content?.Length > 200 ? note.Content.Substring(0, 200) + "..." : note.Content
            };
            row.Children.Add(textBlock);

            // 点击打开编辑
            string noteId = note.Id;
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled) return;
                OpenStickyNoteEditor(noteId);
            };

            delBtn.Click += (s, e) =>
            {
                StickyNoteManager.Delete(noteId);
                RefreshStickyNotes();
                e.Handled = true;
            };

            return row;
        }

        private void OpenStickyNoteEditor(string noteId)
        {
            var note = StickyNoteManager.GetById(noteId);
            if (note == null) return;

            if (_stickyNotePanelDialog == null)
                _stickyNotePanelDialog = new StickyNotePanel();

            if (_mainWindow != null)
            {
                _stickyNotePanelDialog.Show(_mainWindow,
                    _mainWindow.Left + _mainWindow.Width,
                    _mainWindow.Top, 300, _mainWindow.Height,
                    note, () => RefreshStickyNotes());
            }
        }

        private void AddStickyNote_Click(object sender, RoutedEventArgs e)
        {
            var note = new StickyNote();
            StickyNoteManager.Add(note);
            RefreshStickyNotes();
            OpenStickyNoteEditor(note.Id);
        }

        #endregion

        #region 拖放处理

        private void NativeModePanel_PreviewDragOver(object sender, DragEventArgs e)
        {
            // 内部分组拖拽排序不拦截
            if (e.Data.GetDataPresent("QuickGroupId")) return;
            _quickGroupUIService?.HandleDragOver(e);
        }

        private void NativeModePanel_Drop(object sender, DragEventArgs e)
        {
            _quickGroupUIService?.HandleDrop(e);
        }

        #endregion

        #region 事件处理

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: 打开设置窗口
            MessageBox.Show("设置功能待实现", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AIChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_aiChatDialog == null)
                _aiChatDialog = new AIChatDialog();

            if (_aiChatDialog.IsOpen)
                return;

            if (_mainWindow != null)
            {
                _aiChatDialog.Show(_mainWindow,
                    _mainWindow.Left + _mainWindow.Width,
                    _mainWindow.Top, 400, _mainWindow.Height);
            }
        }

        private void ThemeEditorButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = new ThemeEditor(_mainWindow);
            editor.Show();
        }

        private void AddGroupButton_Click(object sender, RoutedEventArgs e)
        {
            _quickGroupUIService?.ShowAddMenu((Button)sender);
        }

        private void TaskbarOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var windows = TaskbarOverlayManager.GetTaskbarWindows();
                if (windows.Count == 0)
                {
                    MessageBox.Show("未找到可用的任务栏窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Window pickerDialog = new Window
                {
                    Title = "选择窗口",
                    Width = 800,
                    Height = 540,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.CanResize,
                    Owner = _mainWindow,
                    Background = Brushes.White
                };

                // 主布局：左 = 已锁定列表，右 = 可选窗口列表 + 按钮
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
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                DockPanel.SetDock(leftHeader, Dock.Top);
                leftPanel.Children.Add(leftHeader);

                var lockedListBox = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(lockedListBox, Dock.Top);

                var lockedBtnPanel = new StackPanel { Orientation = Orientation.Horizontal };
                DockPanel.SetDock(lockedBtnPanel, Dock.Bottom);
                var deleteRuleBtn = new Button { Content = "删除规则", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
                var refreshLockedBtn = new Button { Content = "刷新", Width = 60, Height = 28 };
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

                        ImageSource iconSrc = null;
                        if (!string.IsNullOrEmpty(r.ExecutableDir) && !string.IsNullOrEmpty(r.ProcessName))
                        {
                            try
                            {
                                string exeGuess = System.IO.Path.Combine(r.ExecutableDir, r.ProcessName + ".exe");
                                iconSrc = GetExeIconSource(exeGuess);
                            }
                            catch { }
                        }
                        if (iconSrc != null)
                        {
                            rowPanel.Children.Add(new Image
                            {
                                Source = iconSrc,
                                Width = 20,
                                Height = 20,
                                Margin = new Thickness(0, 0, 8, 0),
                                VerticalAlignment = VerticalAlignment.Center
                            });
                        }
                        else
                        {
                            rowPanel.Children.Add(new System.Windows.Shapes.Rectangle
                            {
                                Width = 20,
                                Height = 20,
                                Margin = new Thickness(0, 0, 8, 0),
                                Fill = Brushes.Transparent
                            });
                        }

                        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        string head = $"[{r.LockText}]  {r.ProcessName}";
                        sp.Children.Add(new TextBlock
                        {
                            Text = head,
                            FontWeight = FontWeights.Bold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        string detail = !string.IsNullOrEmpty(r.MatchPattern)
                            ? $"匹配（{(r.MatchMode == TitleMatchMode.Equals ? "等于" : "包含")}）：{r.MatchPattern}"
                            : $"原标题：{r.OriginalTitle}";
                        sp.Children.Add(new TextBlock
                        {
                            Text = detail,
                            Foreground = Brushes.Gray,
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
                                Foreground = Brushes.Gray,
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
                        if (MessageBox.Show($"确定删除规则：[{selRule.LockText}] {selRule.ProcessName}？\n\n注意：仅删除规则，如果该窗口当前仍被锁定，下次开机将不再自动锁定。",
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
                    Text = "选择一个窗口来锁定任务栏标题",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                DockPanel.SetDock(rightHeader, Dock.Top);
                rightPanel.Children.Add(rightHeader);

                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(btnPanel, Dock.Bottom);
                var cancelBtn = new Button { Content = "取消", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
                cancelBtn.Click += (s, ev) => pickerDialog.Close();
                var okBtn = new Button { Content = "下一步", Width = 80, Height = 30, IsDefault = true };
                btnPanel.Children.Add(cancelBtn);
                btnPanel.Children.Add(okBtn);
                rightPanel.Children.Add(btnPanel);

                var listBox = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var w in windows)
                {
                    var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
                    if (w.Icon != null)
                    {
                        itemPanel.Children.Add(new Image
                        {
                            Source = w.Icon,
                            Width = 20,
                            Height = 20,
                            Margin = new Thickness(0, 0, 8, 0)
                        });
                    }
                    var textPanel = new StackPanel();
                    textPanel.Children.Add(new TextBlock
                    {
                        Text = w.Title,
                        FontWeight = FontWeights.Bold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 420
                    });
                    textPanel.Children.Add(new TextBlock
                    {
                        Text = w.ProcessName,
                        Foreground = Brushes.Gray,
                        FontSize = 11
                    });
                    itemPanel.Children.Add(textPanel);

                    var item = new ListBoxItem { Content = itemPanel, Tag = w };
                    listBox.Items.Add(item);
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
            catch (Exception ex)
            {
                MessageBox.Show($"任务栏标记功能出错：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowOverlayEditor(TaskbarOverlayManager.WindowInfo window)
        {
            Window editorDialog = new Window
            {
                Title = $"锁定标题: {window.Title}",
                Width = 560,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResize,
                Owner = _mainWindow,
                Background = Brushes.White
            };

            var rootPanel = new DockPanel { Margin = new Thickness(15) };

            // 顶部标题
            var header = new TextBlock
            {
                Text = "锁定任务栏标题",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(header, Dock.Top);
            rootPanel.Children.Add(header);

            // 输入区
            var inputPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(inputPanel, Dock.Top);
            inputPanel.Children.Add(new TextBlock
            {
                Text = "请输入要锁定的标题文字（1-2个字符）：",
                Margin = new Thickness(0, 0, 0, 5)
            });

            // 初始值：优先取已锁定文字；否则取当前窗口标题的前 1-2 个字符
            string initialText = "1";
            try
            {
                var locked = TaskbarOverlayManager.GetLockedWindows();
                if (locked != null && locked.TryGetValue(window.Handle, out string lockedText) && !string.IsNullOrEmpty(lockedText))
                {
                    initialText = lockedText;
                }
                else if (!string.IsNullOrEmpty(window.Title))
                {
                    string t = window.Title.Trim();
                    initialText = t.Length >= 2 ? t.Substring(0, 2) : t.Substring(0, 1);
                }
            }
            catch { }

            var textBox = new TextBox
            {
                Height = 32,
                FontSize = 16,
                MaxLength = 2,
                Text = initialText,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            textBox.Loaded += (s, ev) => { textBox.SelectAll(); textBox.Focus(); };
            inputPanel.Children.Add(textBox);

            // 可选的模糊匹配设置
            inputPanel.Children.Add(new TextBlock
            {
                Text = "匹配字符串（可选，启动时按此自动锁定）：",
                Margin = new Thickness(0, 12, 0, 5)
            });

            string preMatchPattern = string.Empty;
            TitleMatchMode preMatchMode = TitleMatchMode.Contains;
            try
            {
                var existingDetails = TaskbarOverlayManager.GetWindowDetails(window.Handle);
                if (existingDetails != null)
                {
                    var match = TitleLockRuleManager.FindMatch(existingDetails.ProcessName, existingDetails.Title, existingDetails.WorkingDirectory);
                    if (match != null && !string.IsNullOrEmpty(match.MatchPattern))
                    {
                        preMatchPattern = match.MatchPattern;
                        preMatchMode = match.MatchMode;
                    }
                }
            }
            catch { }

            var matchBox = new TextBox
            {
                Height = 28,
                FontSize = 13,
                Text = preMatchPattern,
                ToolTip = "留空表示不启用模糊匹配；仅在下次启动时按此自动识别同进程中符合条件的窗口"
            };
            inputPanel.Children.Add(matchBox);

            var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var modeEqualsRb = new RadioButton
            {
                Content = "完全相同",
                Margin = new Thickness(0, 0, 15, 0),
                IsChecked = preMatchMode == TitleMatchMode.Equals
            };
            var modeContainsRb = new RadioButton
            {
                Content = "包含",
                IsChecked = preMatchMode != TitleMatchMode.Equals
            };
            modePanel.Children.Add(modeEqualsRb);
            modePanel.Children.Add(modeContainsRb);
            inputPanel.Children.Add(modePanel);
            rootPanel.Children.Add(inputPanel);

            // 底部按钮
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            var copyBtn = new Button { Content = "复制信息", Width = 90, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
            var unlockBtn = new Button { Content = "解除锁定", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
            unlockBtn.Click += (s, ev) =>
            {
                TaskbarOverlayManager.UnlockWindowTitle(window.Handle);
                editorDialog.Close();
            };
            var cancelBtn = new Button { Content = "取消", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
            cancelBtn.Click += (s, ev) => editorDialog.Close();
            var okBtn = new Button { Content = "锁定", Width = 80, Height = 30, IsDefault = true };
            okBtn.Click += (s, ev) =>
            {
                string lockText = textBox.Text.Trim();
                if (string.IsNullOrEmpty(lockText))
                {
                    MessageBox.Show("请输入 1-2 个字符的标题文字", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (lockText.Length > 2)
                    lockText = lockText.Substring(0, 2);

                bool ok = TaskbarOverlayManager.LockWindowTitle(
                    window.Handle,
                    lockText,
                    string.IsNullOrEmpty(matchBox.Text) ? null : matchBox.Text.Trim(),
                    modeEqualsRb.IsChecked == true ? TitleMatchMode.Equals : TitleMatchMode.Contains);
                if (!ok)
                {
                    MessageBox.Show("锁定标题失败，可能权限不足。请以管理员身份运行。", "锁定失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                editorDialog.Close();
            };
            btnPanel.Children.Add(copyBtn);
            btnPanel.Children.Add(unlockBtn);
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            rootPanel.Children.Add(btnPanel);

            // 详细信息区（占余下空间）
            var detailsHeader = new TextBlock
            {
                Text = "窗口 / 进程详细信息（可复制用于重启后自动锁定）：",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            DockPanel.SetDock(detailsHeader, Dock.Top);
            rootPanel.Children.Add(detailsHeader);

            var details = TaskbarOverlayManager.GetWindowDetails(window.Handle);
            string detailsText = BuildWindowDetailsText(details);

            var detailsBox = new TextBox
            {
                Text = detailsText,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.WhiteSmoke,
                Padding = new Thickness(8)
            };
            rootPanel.Children.Add(detailsBox);

            copyBtn.Click += (s, ev) =>
            {
                try
                {
                    Clipboard.SetText(detailsBox.Text);
                    MessageBox.Show("信息已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            };

            editorDialog.Content = rootPanel;
            editorDialog.ShowDialog();
        }

        private static string BuildWindowDetailsText(TaskbarOverlayManager.WindowDetails d)
        {
            if (d == null) return "(无信息)";
            var sb = new StringBuilder();
            sb.AppendLine($"窗口句柄 (HWND) : 0x{d.Handle.ToInt64():X}");
            sb.AppendLine($"窗口标题       : {d.Title}");
            sb.AppendLine($"窗口类名       : {d.ClassName}");
            sb.AppendLine($"窗口 Style     : 0x{d.StyleFlags:X8}");
            sb.AppendLine($"窗口 ExStyle   : 0x{d.ExStyleFlags:X8}");
            sb.AppendLine();
            sb.AppendLine($"进程 ID (PID)  : {d.ProcessId}");
            sb.AppendLine($"进程名         : {d.ProcessName}");
            sb.AppendLine($"可执行文件     : {d.ExecutablePath}");
            sb.AppendLine($"工作目录       : {d.WorkingDirectory}");
            sb.AppendLine($"运行用户       : {d.UserName}");
            sb.AppendLine($"启动时间       : {(d.StartTime.HasValue ? d.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "")}");
            sb.AppendLine($"父进程 ID      : {d.ParentProcessId}");
            sb.AppendLine($"父进程名       : {d.ParentProcessName}");
            sb.AppendLine();
            sb.AppendLine("命令行参数：");
            sb.AppendLine(string.IsNullOrEmpty(d.CommandLine) ? "  (无法获取，可能需要管理员权限)" : "  " + d.CommandLine);
            return sb.ToString();
        }

        private static ImageSource GetExeIconSource(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try
            {
                if (!System.IO.File.Exists(exePath)) return null;
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    if (icon == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (src.CanFreeze) src.Freeze();
                    return src;
                }
            }
            catch { return null; }
        }

        private void UsageStatsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_windowUsagePanel == null)
                _windowUsagePanel = new WindowUsagePanel();

            if (_windowUsagePanel.IsOpen)
                return;

            if (_mainWindow != null)
            {
                _windowUsagePanel.Show(_mainWindow,
                    _mainWindow.Left + _mainWindow.Width,
                    _mainWindow.Top, 400, _mainWindow.Height);
            }
        }

        #endregion

        #region 右键菜单事件处理

        /// <summary>
        /// "显示设置"菜单展开时动态填充可切换的显示选项
        /// </summary>
        private void DisplaySettingsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;

            menu.Items.Clear();

            var settings = AppSettings.Instance;
            var items = new[]
            {
                new { Header = "便签",           Checked = settings.ShowStickyNotes, Key = "ShowStickyNotes" },
                new { Header = "自定义程序书签", Checked = settings.ShowQuickGroups,  Key = "ShowQuickGroups" },
                new { Header = "鼠键计数器",   Checked = settings.ShowCounter,      Key = "ShowCounter" },
                new { Header = "AI 对话",        Checked = settings.ShowAIChat,       Key = "ShowAIChat" },
                new { Header = "使用时长分析", Checked = settings.ShowUsageStats,   Key = "ShowUsageStats" },
                new { Header = "剪贴板历史",   Checked = settings.ShowClipboard,    Key = "ShowClipboard" }
            };

            foreach (var def in items)
            {
                var mi = new MenuItem
                {
                    Header = def.Header,
                    IsCheckable = true,
                    IsChecked = def.Checked,
                    Tag = def.Key
                };
                string key = def.Key;
                mi.Click += (s, ev) => ToggleDisplaySetting(key, mi.IsChecked);
                menu.Items.Add(mi);
            }
        }

        private void ToggleDisplaySetting(string key, bool value)
        {
            var settings = AppSettings.Instance;
            switch (key)
            {
                case "ShowStickyNotes": settings.ShowStickyNotes = value; break;
                case "ShowQuickGroups":  settings.ShowQuickGroups = value; break;
                case "ShowCounter":      settings.ShowCounter = value; break;
                case "ShowAIChat":       settings.ShowAIChat = value; break;
                case "ShowUsageStats":   settings.ShowUsageStats = value; break;
                case "ShowClipboard":    settings.ShowClipboard = value; break;
            }
            settings.Save();
        }

        /// <summary>
        /// "主题"菜单展开时动态填充可用主题列表
        /// </summary>
        private void ThemeMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;

            // 移除旧的动态项（保留主题编辑器项和 Separator）
            for (int i = menu.Items.Count - 1; i >= 0; i--)
            {
                var item = menu.Items[i];
                var mi = item as MenuItem;
                if (mi != null && mi.Tag is string && (string)mi.Tag == "DynamicTheme")
                {
                    menu.Items.RemoveAt(i);
                }
            }

            // 动态填充主题列表
            try
            {
                string current = AppSettings.Instance.CurrentTheme;
                foreach (var name in ThemeManager.GetAvailableThemes())
                {
                    var item = new MenuItem
                    {
                        Header = name,
                        Tag = "DynamicTheme",
                        IsCheckable = true,
                        IsChecked = string.Equals(name, current, StringComparison.Ordinal)
                    };
                    string capturedName = name;
                    item.Click += (s, ev) => ApplyThemeByName(capturedName);
                    menu.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("填充主题列表失败: " + ex.Message);
            }
        }

        private void ApplyThemeByName(string name)
        {
            try
            {
                ThemeManager.ApplyByName(name);
                AppSettings.Instance.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show("应用主题失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ThemeEditorMenu_Click(object sender, RoutedEventArgs e)
        {
            var editor = new ThemeEditor(_mainWindow);
            editor.Show();
        }

        private void ContextMenu_AIChatClick(object sender, RoutedEventArgs e)
        {
            AIChatButton_Click(sender, e);
        }

        private void ContextMenu_UsageStatsClick(object sender, RoutedEventArgs e)
        {
            UsageStatsButton_Click(sender, e);
        }

        #endregion

        #region 布局与屏幕切换

        /// <summary>
        /// 右键菜单打开时，动态设置布局和屏幕的勾选状态
        /// </summary>
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            var currentLayout = app.CurrentLayout;

            // 设置布局菜单勾选
            if (LayoutMenu?.Items != null)
            {
                foreach (var item in LayoutMenu.Items)
                {
                    if (item is MenuItem mi)
                    {
                        mi.IsChecked = mi.Header switch
                        {
                            "左侧停靠" => currentLayout == LayoutMode.Left,
                            "右侧停靠" => currentLayout == LayoutMode.Right,
                            "双侧停靠" => currentLayout == LayoutMode.Both,
                            _ => false
                        };
                    }
                }
            }

            // 设置屏幕菜单勾选
            if (ScreenMenu?.Items != null && _mainWindow != null)
            {
                var currentScreen = _mainWindow.CurrentScreen;
                foreach (var item in ScreenMenu.Items)
                {
                    if (item is MenuItem mi)
                    {
                        mi.IsChecked = mi.Header switch
                        {
                            "切换到 A 屏" => currentScreen == ScreenType.A,
                            "切换到 B 屏" => currentScreen == ScreenType.B,
                            _ => false
                        };
                    }
                }
            }
        }

        private void Layout_Left_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayout(LayoutMode.Left);
        }

        private void Layout_Right_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayout(LayoutMode.Right);
        }

        private void Layout_Both_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayout(LayoutMode.Both);
        }

        private void SwitchToScreenA_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;
            _mainWindow.SwitchScreen(ScreenType.A);
        }

        private void SwitchToScreenB_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;
            _mainWindow.SwitchScreen(ScreenType.B);
        }

        #endregion
    }
}
