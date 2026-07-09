using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Windows;
using SideDesk.Framework;

namespace SideDesk.Controls
{
    /// <summary>
    /// 本地 UI 模式面板
    /// 负责管理原生控件（分组、便签、剪贴板、系统监控等）
    /// 支持独立自定义布局：每个窗口拥有自己的 Panels 配置
    /// </summary>
    public partial class NativeModePanel : UserControl
    {
        private MainWindow? _mainWindow;

        // 当前窗口配置
        private WindowConfig? _currentWindowConfig;
        private WindowConfigManager? _configManager;

        // 服务实例
        private KeyboardHookService? _keyboardHookService;
        private MouseHookService? _mouseHookService;
        private QuickGroupUIService? _quickGroupUIService;

        // 分组面板容器（供 QuickGroupUIService 使用）
        private StackPanel? _groupPanel;

        // 窗口展开/收缩
        private bool _isExpanded = false;
        private double _originalWidth = 0;
        private const double EXPAND_WIDTH = 100;


        // 计数器 UI 引用
        private Button? _keyCountValueButton;
        private Button? _mouseDistanceValueButton;

        // 初始化标志
        private bool _isInitialized = false;

        // 编辑模式（持久化到 AppSettings）
        private bool _isEditMode = false;

        // 剪贴板历史
        private ClipboardService? _clipboardService;
        private StackPanel? _clipboardHistoryContainer;

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
        /// 加载指定窗口配置并渲染面板
        /// </summary>
        public void LoadWindow(WindowConfig config, WindowConfigManager manager)
        {
            _currentWindowConfig = config;
            _configManager = manager;
            _isEditMode = false;  // 每次启动/加载都强制非编辑模式，编辑状态不持久化



            // 确保全局服务已启动（只启动一次）
            InitializeGlobalServices();

            RenderPanels();
            RefreshToolsBar();  // 刷新底部工具栏
        }

        /// <summary>
        /// 初始化本地 UI 内容（只执行一次）— 兼容旧调用
        /// </summary>
        public void Initialize()
        {
            InitializeGlobalServices();
        }

        /// <summary>
        /// 全局服务初始化（只执行一次）
        /// </summary>
        private void InitializeGlobalServices()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            // 初始化键盘和鼠标钩子服务
            _keyboardHookService = new KeyboardHookService();
            _keyboardHookService.KeyCountChanged += OnKeyCountChanged;

            _mouseHookService = new MouseHookService();
            _mouseHookService.MouseDistanceChanged += OnMouseDistanceChanged;

            // 启用拖放
            AllowDrop = true;
            Drop += NativeModePanel_Drop;
            PreviewDragOver += NativeModePanel_PreviewDragOver;

            Debug.WriteLine("[NativeModePanel] 全局服务已初始化");
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
            // 启动键盘/鼠标钩子
            _keyboardHookService?.Start();
            _mouseHookService?.Start();
        }

        /// <summary>停止所有后台服务</summary>
        public void StopServices()
        {
            _keyboardHookService?.Stop();
            _mouseHookService?.Stop();
            _quickGroupUIService?.Shutdown();
        }



        #region 动态面板渲染

        /// <summary>
        /// 根据当前窗口配置渲染所有面板
        /// </summary>
        private void RenderPanels()
        {
            dynamicContent.Children.Clear();

            if (_currentWindowConfig == null) return;

            foreach (var panel in _currentWindowConfig.Panels)
            {
                var widget = CreatePanelWidget(panel);
                if (widget != null) dynamicContent.Children.Add(widget);
            }

            // 空窗口提示
            if (_currentWindowConfig.Panels.Count == 0)
            {
                dynamicContent.Children.Add(new TextBlock
                {
                    Text = "右键添加模块",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 50, 0, 0),
                    FontSize = 14,
                    Opacity = 0.5
                });
            }

            // 重新渲染后，更新瞬移窗口区（编辑模式切换时需要重建）
            if (_teleportedWindows.Count > 0)
            {
                UpdateTeleportWidget();
            }
        }

        /// <summary>
        /// 根据 PanelType 创建对应的 UI 组件
        /// </summary>
        private UIElement? CreatePanelWidget(PanelItem item)
        {
            return item.Type switch
            {
                PanelType.QuickGroup => BuildQuickGroupWidget(item),
                PanelType.StickyNotes => BuildStickyNotesWidget(item),
                PanelType.Clipboard => BuildClipboardWidget(item),
                PanelType.SystemMonitor => BuildSystemMonitorWidget(item),
                PanelType.UsageStats => BuildUsageStatsWidget(item),
                PanelType.AIChat => BuildAIChatWidget(item),
                PanelType.FileArea => BuildFileAreaWidget(item),
                PanelType.ClipboardHistory => BuildClipboardHistoryWidget(item),
                PanelType.DataDashboard => BuildDataDashboardWidget(item),
                PanelType.Timer => BuildTimerWidget(item),
                PanelType.Group => BuildGroupWidget(item),
                _ => null
            };
        }

        /// <summary>
        /// 通用面板包裹器：带标题和删除按钮
        /// </summary>
        private Border WrapWidget(string defaultTitle, UIElement content, PanelItem item, Action? onAddClick = null)
        {
            var displayTitle = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var titleBlock = new TextBlock
            {
                Text = displayTitle,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("BrushTextMain")
            };

            if (_isEditMode)
            {
                titleBlock.Cursor = Cursors.Hand;
                titleBlock.ToolTip = "双击编辑名称";
                titleBlock.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        var parent = titleBlock.Parent as Panel;
                        if (parent == null) return;

                        var editBox = new TextBox
                        {
                            Text = titleBlock.Text,
                            FontWeight = FontWeights.Bold,
                            MinWidth = 50,
                            Background = Brushes.Transparent,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            BorderBrush = (Brush)FindResource("BrushAccent"),
                            Padding = new Thickness(0),
                            VerticalAlignment = VerticalAlignment.Center,
                            CaretBrush = (Brush)FindResource("BrushTextMain")
                        };

                        int index = parent.Children.IndexOf(titleBlock);
                        parent.Children.Remove(titleBlock);
                        parent.Children.Insert(index, editBox);
                        editBox.Focus();
                        editBox.SelectAll();

                        bool committed = false;
                        void CommitEdit()
                        {
                            if (committed) return;
                            committed = true;
                            var newTitle = editBox.Text.Trim();
                            item.Title = string.IsNullOrEmpty(newTitle) ? null : newTitle;
                            _configManager?.Save();
                            RenderPanels();
                        }

                        editBox.LostFocus += (s2, e2) => CommitEdit();
                        editBox.KeyDown += (s2, e2) =>
                        {
                            if (e2.Key == System.Windows.Input.Key.Enter)
                                CommitEdit();
                            else if (e2.Key == System.Windows.Input.Key.Escape)
                                RenderPanels();
                        };

                        e.Handled = true;
                    }
                };
            }

            var actionBtn = new Button
            {
                Content = _isEditMode ? "✕" : "+",
                FontSize = _isEditMode ? 10 : 14,
                Padding = new Thickness(3, 0, 3, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = _isEditMode ? Visibility.Collapsed : Visibility.Visible
            };

            if (_isEditMode)
            {
                actionBtn.Click += (s, e) => RemovePanel(item);
            }
            else if (onAddClick != null)
            {
                actionBtn.Click += (s, e) => onAddClick();
            }

            DockPanel.SetDock(actionBtn, Dock.Right);
            header.Children.Add(actionBtn);
            header.Children.Add(titleBlock);

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(content);

            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(0, 10, 0, 10),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = stack
            };

            if (_isEditMode)
            {
                border.MouseEnter += (s, e) => actionBtn.Visibility = Visibility.Visible;
                border.MouseLeave += (s, e) => actionBtn.Visibility = Visibility.Collapsed;
            }

            return border;
        }

        private void RemovePanel(PanelItem item)
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            // 先从顶层面板列表删除
            if (_currentWindowConfig.Panels.Remove(item))
            {
                _configManager.Save();
                RenderPanels();
                return;
            }

            // 再从分组的子面板列表中删除
            foreach (var group in _currentWindowConfig.Panels.Where(p => p.Type == PanelType.Group && p.ChildPanels != null))
            {
                if (group.ChildPanels!.Remove(item))
                {
                    _configManager.Save();
                    RenderPanels();
                    return;
                }
            }
        }

        #endregion

        #region Panel Builders

        /// <summary>构建分组容器：可展开/收起、右键添加模块</summary>
        private UIElement BuildGroupWidget(PanelItem item)
        {
            item.ChildPanels ??= new List<PanelItem>();

            var container = new StackPanel();

            // 标题栏（展开/收起箭头 + 分组名）
            var titleBar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var arrowBtn = new Button
            {
                Content = item.IsExpanded ? "▼" : "▶",
                Width = 22, Height = 22,
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                FontSize = 10,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand
            };
            DockPanel.SetDock(arrowBtn, Dock.Left);
            titleBar.Children.Add(arrowBtn);

            var titleBlock = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Title) ? "分组" : item.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Cursor = _isEditMode ? Cursors.Hand : Cursors.Arrow
            };

            // 双击编辑分组名
            if (_isEditMode)
            {
                titleBlock.ToolTip = "双击编辑名称";
                titleBlock.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        var parent = titleBlock.Parent as Panel;
                        if (parent == null) return;
                        var editBox = new TextBox
                        {
                            Text = titleBlock.Text,
                            FontWeight = FontWeights.Bold,
                            MinWidth = 50,
                            Background = Brushes.Transparent,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            BorderBrush = (Brush)FindResource("BrushAccent"),
                            Padding = new Thickness(0),
                            VerticalAlignment = VerticalAlignment.Center,
                            CaretBrush = (Brush)FindResource("BrushTextMain")
                        };
                        int index = parent.Children.IndexOf(titleBlock);
                        parent.Children.Remove(titleBlock);
                        parent.Children.Insert(index, editBox);
                        editBox.Focus();
                        editBox.SelectAll();

                        bool committed = false;
                        void CommitEdit()
                        {
                            if (committed) return;
                            committed = true;
                            var newTitle = editBox.Text.Trim();
                            item.Title = string.IsNullOrEmpty(newTitle) ? null : newTitle;
                            _configManager?.Save();
                            RenderPanels();
                        }
                        editBox.LostFocus += (s2, e2) => CommitEdit();
                        editBox.KeyDown += (s2, e2) =>
                        {
                            if (e2.Key == System.Windows.Input.Key.Enter) CommitEdit();
                            else if (e2.Key == System.Windows.Input.Key.Escape) RenderPanels();
                        };
                        e.Handled = true;
                    }
                };
            }

            // 标题栏右侧：删除按钮
            var deleteBtn = new Button
            {
                Content = "✕", FontSize = 10, Padding = new Thickness(3, 0, 3, 0),
                Background = Brushes.Transparent, Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = Visibility.Collapsed
            };
            deleteBtn.Click += (s, e) => RemovePanel(item);
            DockPanel.SetDock(deleteBtn, Dock.Right);
            titleBar.Children.Insert(0, deleteBtn); // 先加删除按钮（Dock.Right）

            titleBar.Children.Add(titleBlock);

            // 右键菜单：添加模块到分组
            var groupContextMenu = new ContextMenu();
            var addSubMenu = new MenuItem { Header = "添加模块" };
            foreach (var (header, type) in new (string, PanelType)[]
            {
                ("快捷方式", PanelType.QuickGroup),
                ("便签", PanelType.StickyNotes),
                ("剪贴板收藏", PanelType.Clipboard),
                ("系统信息", PanelType.SystemMonitor),
                ("使用统计", PanelType.UsageStats),
                ("AI 对话", PanelType.AIChat),
                ("文件区", PanelType.FileArea),
                ("数据看板", PanelType.DataDashboard),
                ("计时器", PanelType.Timer)
            })
            {
                var mi = new MenuItem { Header = header };
                var panelType = type;
                mi.Click += (s, ev) => AddModuleToGroup(item, panelType);
                addSubMenu.Items.Add(mi);
            }
            groupContextMenu.Items.Add(addSubMenu);
            titleBar.ContextMenu = groupContextMenu;

            container.Children.Add(titleBar);

            // 内容区（子面板）
            var contentPanel = new StackPanel
            {
                Visibility = item.IsExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            foreach (var child in item.ChildPanels)
            {
                var widget = CreatePanelWidget(child);
                if (widget != null) contentPanel.Children.Add(widget);
            }
            container.Children.Add(contentPanel);

            // 展开/收起事件
            arrowBtn.Click += (s, e) =>
            {
                item.IsExpanded = !item.IsExpanded;
                contentPanel.Visibility = item.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                arrowBtn.Content = item.IsExpanded ? "▼" : "▶";
                _configManager?.Save();
            };

            // 分组外框（轻量 Border，无额外标题栏）
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(0, 10, 0, 10),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = container
            };

            if (_isEditMode)
            {
                border.MouseEnter += (s, e) => deleteBtn.Visibility = Visibility.Visible;
                border.MouseLeave += (s, e) => deleteBtn.Visibility = Visibility.Collapsed;
            }

            return border;
        }

        private UIElement BuildQuickGroupWidget(PanelItem item)
        {
            var container = new StackPanel();

            // 若面板未绑定特定分组（兼容旧数据），显示“+ 添加分组”按钮
            if (string.IsNullOrEmpty(item.GroupId))
            {
                var addBtn = new Button
                {
                    Content = "+ 添加分组",
                    Height = 28,
                    Margin = new Thickness(0, 0, 0, 5),
                    Cursor = Cursors.Hand,
                    Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed
                };
                addBtn.Click += (s, e) => _quickGroupUIService?.ShowAddMenu(addBtn);
                container.Children.Add(addBtn);
            }

            // 分组列表
            _groupPanel = new StackPanel();
            container.Children.Add(_groupPanel);

            // 初始化 QuickGroupUIService
            _quickGroupUIService = new QuickGroupUIService(_groupPanel);
            _quickGroupUIService.OwnerWindow = _mainWindow;

            // 1:1 绑定模式：分组删除时同时移除面板
            if (!string.IsNullOrEmpty(item.GroupId))
            {
                _quickGroupUIService.OnGroupDeleted = (gid) =>
                {
                    _currentWindowConfig?.Panels.Remove(item);
                    _configManager?.Save();
                    RenderPanels();
                };
            }

            if (!string.IsNullOrEmpty(item.GroupId))
                _quickGroupUIService.LoadSingleGroup(item.GroupId);
            else
                _quickGroupUIService.LoadGroups();

            // 1:1 绑定时不套 WrapWidget 外框，直接返回内容
            if (!string.IsNullOrEmpty(item.GroupId))
                return container;

            // 兼容旧数据：无 GroupId 时用默认标题包裹
            return WrapWidget("📁 快捷方式", container, item);
        }

        private UIElement BuildStickyNotesWidget(PanelItem item)
        {
            // 便签数据 per-panel 存储（新面板从空开始，旧面板迁移全局数据）
            if (item.StickyNotes == null)
            {
                var globalNotes = StickyNoteManager.GetAll();
                item.StickyNotes = globalNotes.Count > 0
                    ? new List<StickyNote>(globalNotes)
                    : new List<StickyNote>();
                _configManager?.Save();
            }

            var container = new StackPanel();

            // 便签列表
            var notePanel = new StackPanel();
            container.Children.Add(notePanel);

            // 渲染便签
            RefreshPanelStickyNotes(notePanel, item);

            // 添加便签逻辑（用于 WrapWidget 的 onAddClick 回调）
            void AddNote()
            {
                var note = new StickyNote();
                item.StickyNotes.Insert(0, note);
                _configManager?.Save();
                RefreshPanelStickyNotes(notePanel, item);
                OpenPanelStickyNoteEditor(note, notePanel, item);
            }

            return WrapWidget("📝 便签", container, item, _isEditMode ? null : AddNote);
        }

        private UIElement BuildClipboardWidget(PanelItem item)
        {
            // 确保数据列表存在
            item.ClipboardItems ??= new List<ClipboardSavedItem>();

            var container = new StackPanel();

            // 添加按钮
            var addBtn = new Button
            {
                Content = "+ 收藏当前剪贴板",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed
            };
            addBtn.Click += (s, e) =>
            {
                string content = "";
                string type = "Text";
                if (Clipboard.ContainsText())
                {
                    content = Clipboard.GetText();
                    type = "Text";
                }
                else if (Clipboard.ContainsImage())
                {
                    var image = Clipboard.GetImage();
                    content = $"[图片 {image.PixelWidth}x{image.PixelHeight}]";
                    type = "Image";
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    content = string.Join("\n", files.Cast<string>());
                    type = "File";
                }

                if (!string.IsNullOrEmpty(content))
                {
                    item.ClipboardItems.Add(new ClipboardSavedItem
                    {
                        Content = content,
                        ContentType = type
                    });
                    _configManager?.Save();
                    RenderPanels();
                }
            };
            container.Children.Add(addBtn);

            // 已收藏列表
            if (item.ClipboardItems.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "（暂无收藏）",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11,
                    Margin = new Thickness(4)
                });
            }
            else
            {
                foreach (var clip in item.ClipboardItems.OrderByDescending(c => c.SavedAt))
                {
                    var itemPanel = new DockPanel
                    {
                        Margin = new Thickness(0, 2, 0, 2),
                        Height = 24
                    };

                    // 删除按钮
                    var delBtn = new Button
                    {
                        Content = "✕", Width = 20, Height = 20, FontSize = 10,
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand, ToolTip = "删除",
                        Visibility = Visibility.Collapsed
                    };
                    DockPanel.SetDock(delBtn, Dock.Right);
                    itemPanel.Children.Add(delBtn);

                    // 复制按钮
                    var copyBtn = new Button
                    {
                        Content = "⎘", Width = 20, Height = 20, FontSize = 12,
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextSec"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand, ToolTip = "复制到剪贴板",
                        Visibility = Visibility.Collapsed
                    };
                    DockPanel.SetDock(copyBtn, Dock.Right);
                    itemPanel.Children.Add(copyBtn);

                    // 内容预览
                    string displayText = clip.Content;
                    int newline = displayText.IndexOfAny(new char[] { '\n', '\r' });
                    if (newline >= 0) displayText = displayText.Substring(0, newline) + "…";
                    if (displayText.Length > 40) displayText = displayText.Substring(0, 40) + "…";

                    var preview = new TextBlock
                    {
                        Text = displayText,
                        FontSize = 11,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = clip.Content.Length > 200 ? clip.Content.Substring(0, 200) + "..." : clip.Content
                    };
                    itemPanel.Children.Add(preview);

                    // hover 显示按钮（删除按钮仅编辑模式，复制按钮始终可hover）
                    if (_isEditMode)
                    {
                        itemPanel.MouseEnter += (s, ev) => { delBtn.Visibility = Visibility.Visible; copyBtn.Visibility = Visibility.Visible; };
                        itemPanel.MouseLeave += (s, ev) => { delBtn.Visibility = Visibility.Collapsed; copyBtn.Visibility = Visibility.Collapsed; };
                    }
                    else
                    {
                        itemPanel.MouseEnter += (s, ev) => { copyBtn.Visibility = Visibility.Visible; };
                        itemPanel.MouseLeave += (s, ev) => { copyBtn.Visibility = Visibility.Collapsed; };
                    }

                    // 复制事件
                    string clipContent = clip.Content;
                    string clipType = clip.ContentType;
                    copyBtn.Click += (s, ev) =>
                    {
                        try
                        {
                            if (clipType == "File" && clipContent.Contains("\n"))
                            {
                                var sc = new StringCollection();
                                sc.AddRange(clipContent.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                                Clipboard.SetFileDropList(sc);
                            }
                            else
                            {
                                Clipboard.SetText(clipContent);
                            }
                        }
                        catch { }
                        ev.Handled = true;
                    };

                    // 删除事件
                    string clipId = clip.Id;
                    delBtn.Click += (s, ev) =>
                    {
                        item.ClipboardItems.RemoveAll(c => c.Id == clipId);
                        _configManager?.Save();
                        RenderPanels();
                        ev.Handled = true;
                    };

                    container.Children.Add(itemPanel);
                }
            }

            return WrapWidget("📋 剪贴板收藏", container, item);
        }

        private UIElement BuildSystemMonitorWidget(PanelItem item)
        {
            var container = new StackPanel();
            CreateKeyCountPanel(container);
            CreateMouseDistancePanel(container);
            return WrapWidget("💻 系统信息", container, item);
        }

        private UIElement BuildUsageStatsWidget(PanelItem item)
        {
            var container = new StackPanel();
            var btn = new Button
            {
                Content = "📊 查看使用统计",
                Height = 28,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => UsageStatsButton_Click(s, e);
            container.Children.Add(btn);

            var taskbarBtn = new Button
            {
                Content = "📌 任务栏标记",
                Height = 28,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };
            taskbarBtn.Click += (s, e) => TaskbarOverlayButton_Click(s, e);
            container.Children.Add(taskbarBtn);

            return WrapWidget("📊 使用统计", container, item);
        }

        private UIElement BuildAIChatWidget(PanelItem item)
        {
            var btn = new Button
            {
                Content = "🤖 打开AI对话",
                Height = 28,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => AIChatButton_Click(s, e);
            return WrapWidget("🤖 AI 对话", btn, item);
        }

        private UIElement BuildTimerWidget(PanelItem item)
        {
            item.TimerItems ??= new List<TimerItem>();

            var container = new StackPanel();

            // 添加计时器按钮
            var addBtn = new Button
            {
                Content = "+ 添加计时器",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Visibility = item.SkipWrapper ? Visibility.Visible : (_isEditMode ? Visibility.Visible : Visibility.Collapsed)
            };
            addBtn.Click += (s, e) =>
            {
                item.TimerItems.Add(new TimerItem());
                _configManager?.Save();
                RenderPanels();
            };
            container.Children.Add(addBtn);

            // 计时器列表
            foreach (var timer in item.TimerItems)
            {
                var timerCard = CreateTimerCard(timer, item);
                container.Children.Add(timerCard);
            }

            // ✅ 根据SkipWrapper标记决定是否使用外层容器
            if (item.SkipWrapper)
            {
                return container;  // 直接返回内容，不用外层容器
            }
            else
            {
                return WrapWidget("⏱ 计时器", container, item);  // 用外层容器包裹
            }
        }

        private Border CreateTimerCard(TimerItem timer, PanelItem panelItem)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(0, 8, 0, 8),
                Background = (Brush)FindResource("BrushBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };

            var mainStack = new StackPanel();

            // 顶部：名称 + 操作按钮
            var headerPanel = new DockPanel();
            
            var nameBlock = new TextBlock
            {
                Text = timer.Name,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            };

            // 编辑模式下的操作按钮
            var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            
            if (_isEditMode)
            {
                // 编辑按钮
                var editBtn = new Button
                {
                    Content = "✏",
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 12,
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                editBtn.Click += (s, e) => ShowTimerEditDialog(timer, panelItem);
                actionPanel.Children.Add(editBtn);

                // 删除按钮
                var deleteBtn = new Button
                {
                    Content = "✕",
                    Width = 24,
                    Height = 24,
                    FontSize = 12,
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = timer.Id  // 保存timer ID
                };
                deleteBtn.Click += (s, e) =>
                {
                    // 通过ID查找并删除，避免闭包问题
                    var timerId = ((Button)s!).Tag as string;
                    var timerToRemove = panelItem.TimerItems?.FirstOrDefault(t => t.Id == timerId);
                    if (timerToRemove != null)
                    {
                        panelItem.TimerItems?.Remove(timerToRemove);
                        _configManager?.Save();
                        
                        // 如果删除后没有计时器了，询问是否删除整个模块
                        if (panelItem.TimerItems?.Count == 0)
                        {
                            var result = MessageBox.Show(
                                "该计时器模块已没有计时项，是否删除整个模块？",
                                "确认",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);
                            
                            if (result == MessageBoxResult.Yes)
                            {
                                // 删除整个模块
                                if (_currentWindowConfig != null)
                                {
                                    _currentWindowConfig.Panels.Remove(panelItem);
                                    _configManager?.Save();
                                }
                            }
                        }
                        
                        RenderPanels();
                    }
                };
                actionPanel.Children.Add(deleteBtn);
            }

            DockPanel.SetDock(actionPanel, Dock.Right);
            headerPanel.Children.Add(actionPanel);
            headerPanel.Children.Add(nameBlock);

            mainStack.Children.Add(headerPanel);

            // 时间显示
            var timeDisplay = new TextBlock
            {
                Text = FormatTimeSpan(timer.ElapsedTime),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = timer.IsRunning ? (Brush)FindResource("BrushAccent") : (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };
            mainStack.Children.Add(timeDisplay);

            // 控制按钮
            var controlPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            
            // 根据当前运行状态初始化按钮
            var toggleBtn = new Button
            {
                Content = timer.IsRunning ? "⏹ 停止" : "▶ 开始",
                Width = 80,
                Height = 28,
                Background = timer.IsRunning ? Brushes.IndianRed : (Brush)FindResource("BrushAccent"),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            
            // 如果计时器正在运行，启动更新
            if (timer.IsRunning)
            {
                StartTimerUpdate(timer, timeDisplay, panelItem, toggleBtn);
            }
            
            toggleBtn.Click += (s, e) =>
            {
                if (!timer.IsRunning)
                {
                    // 开始计时
                    timer.IsRunning = true;
                    timer.StartTime = DateTime.Now;
                    _configManager?.Save();
                    StartTimerUpdate(timer, timeDisplay, panelItem, toggleBtn);
                    toggleBtn.Content = "⏹ 停止";
                    toggleBtn.Background = Brushes.IndianRed;
                }
                else
                {
                    // 停止计时（累加到今日总计）
                    timer.IsRunning = false;
                    AccumulateToDailyRecord(timer);
                    _configManager?.Save();
                    RenderPanels();
                }
            };
            controlPanel.Children.Add(toggleBtn);

            mainStack.Children.Add(controlPanel);

            // 今日统计
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (timer.DailyRecords.TryGetValue(today, out int todaySeconds) && todaySeconds > 0)
            {
                var todayStat = new TextBlock
                {
                    Text = $"今日总计：{FormatTimeSpan(TimeSpan.FromSeconds(todaySeconds))}",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                mainStack.Children.Add(todayStat);
            }

            card.Child = mainStack;
            return card;
        }

        private void StartTimerUpdate(TimerItem timer, TextBlock timeDisplay, PanelItem panelItem, Button toggleBtn)
        {
            var dispatcherTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            
            dispatcherTimer.Tick += (s, e) =>
            {
                if (!timer.IsRunning || timer.StartTime == null)
                {
                    dispatcherTimer.Stop();
                    return;
                }

                timer.ElapsedTime = DateTime.Now - timer.StartTime.Value;
                timeDisplay.Text = FormatTimeSpan(timer.ElapsedTime);

                // 检查超时
                if (timer.ElapsedTime.TotalSeconds >= timer.TimeoutSeconds)
                {
                    dispatcherTimer.Stop();
                    timer.IsRunning = false;
                    AccumulateToDailyRecord(timer);
                    _configManager?.Save();
                    RenderPanels();
                    MessageBox.Show($"计时器“{timer.Name}”已达到设定时长！", "计时完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };

            dispatcherTimer.Start();
        }

        private void AccumulateToDailyRecord(TimerItem timer)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var secondsToAdd = (int)timer.ElapsedTime.TotalSeconds;
            
            if (timer.DailyRecords.ContainsKey(today))
            {
                timer.DailyRecords[today] += secondsToAdd;
            }
            else
            {
                timer.DailyRecords[today] = secondsToAdd;
            }

            // 不清零，保留当前时间显示，方便继续累加
            // 用户可以选择继续点击开始按钮继续计时
        }

        private void ShowTimerEditDialog(TimerItem timer, PanelItem panelItem)
        {
            var dialog = new Window
            {
                Title = "编辑计时器",
                Width = 350,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = _mainWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushDialogBG")
            };

            var mainPanel = new StackPanel { Margin = new Thickness(20) };

            // 名称
            mainPanel.Children.Add(new TextBlock { Text = "名称：", Margin = new Thickness(0, 0, 0, 5), Foreground = (Brush)FindResource("BrushTextMain") });
            var nameBox = new TextBox { Text = timer.Name, Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(nameBox);

            // 超时时间
            mainPanel.Children.Add(new TextBlock { Text = "最大时长（秒）：", Margin = new Thickness(0, 0, 0, 5), Foreground = (Brush)FindResource("BrushTextMain") });
            var timeoutBox = new TextBox { Text = timer.TimeoutSeconds.ToString(), Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(timeoutBox);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            
            var saveBtn = new Button
            {
                Content = "保存",
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = (Brush)FindResource("BrushAccent"),
                Foreground = Brushes.White
            };
            saveBtn.Click += (s, e) =>
            {
                timer.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "计时器" : nameBox.Text;
                if (int.TryParse(timeoutBox.Text, out int timeout) && timeout > 0)
                {
                    timer.TimeoutSeconds = timeout;
                }
                _configManager?.Save();
                RenderPanels();
                dialog.Close();
            };

            var cancelBtn = new Button
            {
                Content = "取消",
                Width = 80,
                Height = 30
            };
            cancelBtn.Click += (s, e) => dialog.Close();

            btnPanel.Children.Add(saveBtn);
            btnPanel.Children.Add(cancelBtn);
            mainPanel.Children.Add(btnPanel);

            dialog.Content = mainPanel;
            dialog.ShowDialog();
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private UIElement BuildFileAreaWidget(PanelItem item)
        {
            item.FileItems ??= new List<FileAreaItem>();

            var container = new StackPanel();

            // 添加按钮区域
            var addPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5), Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed };

            var addFileBtn = new Button
            {
                Content = "+ 文件",
                Margin = new Thickness(0, 0, 5, 0),
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand
            };
            addFileBtn.Click += (s, e) => AddFileToArea(item);

            var addFolderBtn = new Button
            {
                Content = "+ 文件夹",
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand
            };
            addFolderBtn.Click += (s, e) => AddFolderToArea(item);

            addPanel.Children.Add(addFileBtn);
            addPanel.Children.Add(addFolderBtn);
            container.Children.Add(addPanel);

            // 拖拽事件处理委托
            void HandleFileDragEnter(object sender, DragEventArgs e, Border zone)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    zone.BorderBrush = (Brush)FindResource("BrushAccent");
                }
                e.Handled = true;
            }
            void HandleFileDragLeave(object sender, DragEventArgs e, Border zone)
            {
                zone.BorderBrush = (Brush)FindResource("BrushBorder");
            }
            void HandleFileDrop(object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    foreach (var path in files)
                    {
                        var isDir = System.IO.Directory.Exists(path);
                        item.FileItems!.Add(new FileAreaItem
                        {
                            Path = path,
                            Name = System.IO.Path.GetFileName(path),
                            IsFolder = isDir
                        });
                    }
                    _configManager?.Save();
                    RenderPanels();
                }
            }

            // 拖拽区域
            var dropZone = new Border
            {
                MinHeight = 40,
                Margin = new Thickness(0, 0, 0, 5),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                AllowDrop = true,
                Background = Brushes.Transparent
            };

            dropZone.DragEnter += (s, e) => HandleFileDragEnter(s, e, dropZone);
            dropZone.DragLeave += (s, e) => HandleFileDragLeave(s, e, dropZone);
            dropZone.Drop += (s, e) => { dropZone.BorderBrush = (Brush)FindResource("BrushBorder"); HandleFileDrop(s, e); };

            if (item.FileItems.Count == 0)
            {
                dropZone.Child = new TextBlock
                {
                    Text = "拖拽文件到此处",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 8),
                    FontSize = 12,
                    Opacity = 0.6
                };
                container.Children.Add(dropZone);
            }
            else
            {
                // 文件列表（同时支持拖拽）
                var listBorder = new Border
                {
                    AllowDrop = true,
                    Background = Brushes.Transparent,
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(0, 4, 0, 4)
                };
                var listContainer = new StackPanel();
                listBorder.Child = listContainer;

                listBorder.DragEnter += (s, e) => HandleFileDragEnter(s, e, listBorder);
                listBorder.DragLeave += (s, e) => HandleFileDragLeave(s, e, listBorder);
                listBorder.Drop += (s, e) => { listBorder.BorderBrush = (Brush)FindResource("BrushBorder"); HandleFileDrop(s, e); };

                foreach (var fileItem in item.FileItems.OrderByDescending(f => f.AddedAt))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

                    // 删除按钮
                    var delBtn = new Button
                    {
                        Content = "✕", FontSize = 10,
                        Padding = new Thickness(3, 0, 3, 0),
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Visibility = Visibility.Collapsed
                    };
                    var capturedFileItem = fileItem;
                    delBtn.Click += (s, e) =>
                    {
                        item.FileItems.Remove(capturedFileItem);
                        _configManager?.Save();
                        RenderPanels();
                    };

                    // 打开按钮
                    var openBtn = new Button
                    {
                        Content = "📂", FontSize = 10,
                        Padding = new Thickness(3, 0, 3, 0),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Visibility = Visibility.Collapsed
                    };
                    string filePath = fileItem.Path;
                    openBtn.Click += (s, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                        catch { }
                    };

                    // 图标 + 名称
                    var icon = fileItem.IsFolder ? "📁" : "📄";
                    var nameBlock = new TextBlock
                    {
                        Text = $"{icon} {fileItem.Name}",
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        ToolTip = fileItem.Path,
                        Cursor = Cursors.Hand
                    };

                    // 点击项：目录则打开目录，文件则打开所在目录并选中
                    bool isFolder = fileItem.IsFolder;
                    nameBlock.MouseLeftButtonUp += (s, e) =>
                    {
                        try
                        {
                            if (isFolder)
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false });
                            }
                            else
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
                            }
                        }
                        catch { }
                    };

                    DockPanel.SetDock(delBtn, Dock.Right);
                    DockPanel.SetDock(openBtn, Dock.Right);
                    row.Children.Add(delBtn);
                    row.Children.Add(openBtn);
                    row.Children.Add(nameBlock);

                    // 右键菜单
                    var ctx = new ContextMenu();
                    if (isFolder)
                    {
                        var menuOpen = new MenuItem { Header = "打开" };
                        menuOpen.Click += (s, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false }); }
                            catch { }
                        };
                        ctx.Items.Add(menuOpen);

                        var menuEmpty = new MenuItem { Header = "清空文件夹" };
                        menuEmpty.Click += (s, e) =>
                        {
                            if (MessageBox.Show($"确定清空\n{filePath}\n下的所有内容？", "确认清空", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                            {
                                try
                                {
                                    foreach (var f in System.IO.Directory.GetFiles(filePath)) System.IO.File.Delete(f);
                                    foreach (var d in System.IO.Directory.GetDirectories(filePath)) System.IO.Directory.Delete(d, true);
                                }
                                catch (Exception ex) { MessageBox.Show($"清空失败: {ex.Message}"); }
                            }
                        };
                        ctx.Items.Add(menuEmpty);

                        var menuDel = new MenuItem { Header = "删除" };
                        menuDel.Click += (s, e) =>
                        {
                            if (MessageBox.Show($"确定删除文件夹\n{filePath}\n及其所有内容？", "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                            {
                                try
                                {
                                    System.IO.Directory.Delete(filePath, true);
                                    item.FileItems.Remove(capturedFileItem);
                                    _configManager?.Save();
                                    RenderPanels();
                                }
                                catch (Exception ex) { MessageBox.Show($"删除失败: {ex.Message}"); }
                            }
                        };
                        ctx.Items.Add(menuDel);

                        ctx.Items.Add(new Separator());
                        var menuProps1 = new MenuItem { Header = "属性" };
                        menuProps1.Click += (s, e) =>
                        {
                            try { NativeMethods.ShowFileProperties(filePath); }
                            catch { }
                        };
                        ctx.Items.Add(menuProps1);
                    }
                    else
                    {
                        var menuOpen = new MenuItem { Header = "打开" };
                        menuOpen.Click += (s, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                            catch { }
                        };
                        ctx.Items.Add(menuOpen);

                        var menuLocate = new MenuItem { Header = "定位" };
                        menuLocate.Click += (s, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false }); }
                            catch { }
                        };
                        ctx.Items.Add(menuLocate);

                        var menuDel = new MenuItem { Header = "删除" };
                        menuDel.Click += (s, e) =>
                        {
                            if (MessageBox.Show($"确定删除文件\n{filePath}？", "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                            {
                                try
                                {
                                    System.IO.File.Delete(filePath);
                                    item.FileItems.Remove(capturedFileItem);
                                    _configManager?.Save();
                                    RenderPanels();
                                }
                                catch (Exception ex) { MessageBox.Show($"删除失败: {ex.Message}"); }
                            }
                        };
                        ctx.Items.Add(menuDel);

                        ctx.Items.Add(new Separator());
                        var menuProps2 = new MenuItem { Header = "属性" };
                        menuProps2.Click += (s, e) =>
                        {
                            try { NativeMethods.ShowFileProperties(filePath); }
                            catch { }
                        };
                        ctx.Items.Add(menuProps2);
                    }
                    row.ContextMenu = ctx;

                    // 右键时高亮选中项
                    var highlightBrush = (Brush)FindResource("BrushListViewItemMouseOver");
                    ctx.Opened += (s, e) => row.Background = highlightBrush;
                    ctx.Closed += (s, e) => row.Background = Brushes.Transparent;

                    // hover 显示操作按钮（删除按钮仅编辑模式，打开按钮始终可hover）
                    if (_isEditMode)
                    {
                        row.MouseEnter += (s, e) => { delBtn.Visibility = Visibility.Visible; openBtn.Visibility = Visibility.Visible; };
                        row.MouseLeave += (s, e) => { delBtn.Visibility = Visibility.Collapsed; openBtn.Visibility = Visibility.Collapsed; };
                    }
                    else
                    {
                        row.MouseEnter += (s, e) => { openBtn.Visibility = Visibility.Visible; };
                        row.MouseLeave += (s, e) => { openBtn.Visibility = Visibility.Collapsed; };
                    }

                    listContainer.Children.Add(row);
                }
                container.Children.Add(listBorder);
            }

            return WrapWidget("📁 文件区", container, item);
        }

        private void AddFileToArea(PanelItem item)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "选择文件"
            };
            if (dialog.ShowDialog() == true)
            {
                item.FileItems ??= new List<FileAreaItem>();
                foreach (var path in dialog.FileNames)
                {
                    item.FileItems.Add(new FileAreaItem
                    {
                        Path = path,
                        Name = System.IO.Path.GetFileName(path),
                        IsFolder = false
                    });
                }
                _configManager?.Save();
                RenderPanels();
            }
        }

        private void AddFolderToArea(PanelItem item)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择文件夹",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                item.FileItems ??= new List<FileAreaItem>();
                item.FileItems.Add(new FileAreaItem
                {
                    Path = dialog.FolderName,
                    Name = System.IO.Path.GetFileName(dialog.FolderName),
                    IsFolder = true
                });
                _configManager?.Save();
                RenderPanels();
            }
        }

        #region 剪贴板历史面板

        private UIElement BuildClipboardHistoryWidget(PanelItem item)
        {
            var container = new StackPanel();
            _clipboardHistoryContainer = container;

            // 初始化剪贴板监控服务
            EnsureClipboardServiceStarted();

            // 初始渲染
            RefreshClipboardHistoryUI();

            return WrapWidget("📋 剪贴板历史", container, item);
        }

        private void EnsureClipboardServiceStarted()
        {
            if (_clipboardService != null) return;

            _clipboardService = new ClipboardService();
            _clipboardService.ClipboardChanged += () =>
            {
                Dispatcher.BeginInvoke((Action)RefreshClipboardHistoryUI,
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            // 传入主窗口句柄，确保剪贴板消息可靠送达
            IntPtr mainHwnd = IntPtr.Zero;
            if (_mainWindow != null)
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(_mainWindow);
                mainHwnd = helper.Handle;
            }
            _clipboardService.Start(mainHwnd);
        }

        private void RefreshClipboardHistoryUI()
        {
            if (_clipboardHistoryContainer == null || _clipboardService == null) return;
            _clipboardHistoryContainer.Children.Clear();

            if (_clipboardService.ClipboardHistory.Count == 0)
            {
                _clipboardHistoryContainer.Children.Add(new TextBlock
                {
                    Text = "暂无记录，复制内容后自动显示",
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                    FontSize = (double)Application.Current.FindResource("FontSizeTiny"),
                    Margin = new Thickness(4)
                });
                return;
            }

            foreach (var entry in _clipboardService.ClipboardHistory)
            {
                _clipboardHistoryContainer.Children.Add(BuildClipboardHistoryItem(entry));
            }
        }

        private UIElement BuildClipboardHistoryItem(ClipboardEntry entry)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(3, 1, 3, 1),
                Cursor = Cursors.Hand,
                Height = 22
            };

            // 内容类型图标
            string icon = ClipboardService.GetContentTypeIcon(entry.ContentType);
            string timeStr = entry.Time.ToString("HH:mm:ss");
            var iconText = new TextBlock
            {
                Text = icon,
                FontSize = 12,
                Foreground = ClipboardService.GetContentTypeColor(entry.ContentType),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = timeStr
            };
            DockPanel.SetDock(iconText, Dock.Left);
            row.Children.Add(iconText);

            // 复制按钮（悬停显示）
            var copyBtn = new Button
            {
                Content = "⎘",
                Width = 20, Height = 20,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "复制到剪贴板",
                Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(copyBtn, Dock.Right);
            row.Children.Add(copyBtn);

            row.MouseEnter += (s, e) => copyBtn.Visibility = Visibility.Visible;
            row.MouseLeave += (s, e) => copyBtn.Visibility = Visibility.Collapsed;

            // 文本内容
            string displayText = entry.Text;
            if (entry.ContentType == ClipboardContentType.Text)
            {
                int newline = displayText.IndexOfAny(new char[] { '\n', '\r' });
                if (newline >= 0) displayText = displayText.Substring(0, newline) + "…";
            }
            if (displayText.Length > 30) displayText = displayText.Substring(0, 30) + "…";

            var textBlock = new TextBlock
            {
                Text = displayText,
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = entry.Text.Length > 200 ? entry.Text.Substring(0, 200) + "..." : entry.Text
            };
            row.Children.Add(textBlock);

            // 点击复制逻辑
            var text = entry.Text;
            var filePaths = entry.FilePaths;
            var contentType = entry.ContentType;

            Action doCopy = () =>
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

            copyBtn.Click += (s, e) => { doCopy(); e.Handled = true; };
            row.MouseLeftButtonUp += (s, e) => { if (!e.Handled) doCopy(); };

            return row;
        }

        #endregion

        #endregion

        #region 添加模块菜单事件

        private void AddPanel(PanelType type)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem { Type = type };
            // 新面板初始化为空，避免触发全局迁移
            if (type == PanelType.StickyNotes)
                newPanel.StickyNotes = new List<StickyNote>();
            _currentWindowConfig.Panels.Add(newPanel);

            // 自动开启编辑模式
            _isEditMode = true;
            if (_currentWindowConfig != null)
                _currentWindowConfig.IsEditMode = true;

            _configManager.Save();
            RenderPanels();
        }

        private void AddQuickGroupPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            var menuItem = sender as UIElement ?? this;

            // 直接弹出分组名称对话框（无需临时面板）
            var groupId = ShowGroupNameDialog();
            if (string.IsNullOrEmpty(groupId)) return;

            // 若已有快捷方式面板，复用并绑定到新分组；否则新增
            var existingPanel = _currentWindowConfig.Panels.FirstOrDefault(p => p.Type == PanelType.QuickGroup);
            if (existingPanel != null)
                existingPanel.GroupId = groupId;
            else
                _currentWindowConfig.Panels.Insert(0, new PanelItem { Type = PanelType.QuickGroup, GroupId = groupId });

            _configManager.Save();
            RenderPanels();

            // 延迟弹出添加项菜单
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _quickGroupUIService?.ShowAddShortcutMenuForGroup(menuItem, groupId);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>轻量级分组名称输入对话框，返回 groupId；取消返回 null</summary>
        private string ShowGroupNameDialog()
        {
            string resultId = null;
            var dialog = new Window
            {
                Title = "新建快捷方式",
                Width = 340, Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Owner = _mainWindow
            };
            dialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(dialog);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 15, 10, 5) };
            namePanel.Children.Add(new TextBlock { Text = "分组名:", Foreground = (Brush)FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center, Width = 60 });
            var nameBox = new TextBox { Width = 220, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            namePanel.Children.Add(nameBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 15, 10, 10) };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show("请输入分组名称"); return; }
                var group = new QuickGroup { Name = name, Columns = 2 };
                QuickGroupManager.AddGroup(group);
                QuickGroupManager.Save();
                resultId = group.Id;
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 1);
            grid.Children.Add(btnPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
            return resultId;
        }
        private void AddGroupPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            _currentWindowConfig.Panels.Add(new PanelItem
            {
                Type = PanelType.Group,
                Title = "新分组",
                ChildPanels = new List<PanelItem>(),
                IsExpanded = true
            });
            _configManager.Save();
            RenderPanels();
        }

        private void AddModuleToGroup(PanelItem groupItem, PanelType type)
        {
            groupItem.ChildPanels ??= new List<PanelItem>();
            
            if (type == PanelType.Timer)
            {
                // 对于计时器，创建空的容器，不添加默认计时器
                var timerPanel = new PanelItem 
                { 
                    Type = PanelType.Timer,
                    TimerItems = new List<TimerItem>(),  // ✅ 空列表，不添加默认项
                    SkipWrapper = true  // ✅ 标记跳过外层容器
                };
                
                groupItem.ChildPanels.Add(timerPanel);
            }
            else
            {
                var newPanel = new PanelItem { Type = type };
                // 新面板初始化为空，避免触发全局迁移
                if (type == PanelType.StickyNotes)
                    newPanel.StickyNotes = new List<StickyNote>();
                groupItem.ChildPanels.Add(newPanel);
            }
            
            _configManager?.Save();
            RenderPanels();
        }

        private void AddStickyNotesPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.StickyNotes);
        private void AddClipboardPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.Clipboard);
        private void AddSystemMonitorPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.SystemMonitor);
        private void AddUsageStatsPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.UsageStats);
        private void AddAIChatPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.AIChat);
        private void AddFileAreaPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.FileArea);
        private void AddClipboardHistoryPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.ClipboardHistory);
        private void AddDataDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            _currentWindowConfig.Panels.Add(new PanelItem
            {
                Type = PanelType.DataDashboard,
                Title = "数据看板",
                DashboardItems = new List<DataDashboardItem>()
            });
            _configManager.Save();
            RenderPanels();
        }

        private void AddTimerPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            _currentWindowConfig.Panels.Add(new PanelItem
            {
                Type = PanelType.Timer,
                Title = "⏱ 计时器",
                TimerItems = new List<TimerItem>()
            });
            _configManager.Save();
            RenderPanels();
        }

        #endregion

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

        #region 剪贴板收藏（已移除自动监听）
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

        private void CreateKeyCountPanel(StackPanel container)
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
                Background = (Brush)FindResource("BrushAccent"),
                Foreground = (Brush)FindResource("BrushTextMain"), BorderThickness = new Thickness(0),
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_keyCountValueButton, Dock.Right);
            panel.Children.Add(_keyCountValueButton);

            var nameText = new TextBlock
            {
                Text = "今日按键",
                Padding = new Thickness(8, 5, 0, 5),
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(nameText);

            container.Children.Insert(0, panel);
        }

        private void CreateMouseDistancePanel(StackPanel container)
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
                Background = (Brush)FindResource("BrushAccent"),
                Foreground = (Brush)FindResource("BrushTextMain"), BorderThickness = new Thickness(0),
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_mouseDistanceValueButton, Dock.Right);
            panel.Children.Add(_mouseDistanceValueButton);

            var nameText = new TextBlock
            {
                Text = "鼠标移动",
                Padding = new Thickness(8, 5, 0, 5),
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(nameText);

            container.Children.Add(panel);
        }

        #endregion

        #region 便签

        private StickyNotePanel? _stickyNotePanelDialog;

        private void RefreshStickyNotesInto(StackPanel container)
        {
            container.Children.Clear();
            var notes = StickyNoteManager.GetAll();
            foreach (var note in notes)
            {
                container.Children.Add(BuildStickyNoteItem(note, container));
            }
            if (notes.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "（暂无便签）",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11,
                    Margin = new Thickness(4)
                });
            }
        }

        /// <summary>渲染面板级便签（per-panel 存储）</summary>
        private void RefreshPanelStickyNotes(StackPanel container, PanelItem item)
        {
            container.Children.Clear();
            var notes = item.StickyNotes ?? new List<StickyNote>();
            foreach (var note in notes.OrderByDescending(n => n.UpdatedAt))
            {
                container.Children.Add(BuildPanelStickyNoteItem(note, container, item));
            }
            if (notes.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "（暂无便签）",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11,
                    Margin = new Thickness(4)
                });
            }
        }

        private UIElement BuildPanelStickyNoteItem(StickyNote note, StackPanel container, PanelItem item)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand,
                Height = 22
            };

            var delBtn = new Button
            {
                Content = "✕", Width = 18, Height = 18,
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0), FontSize = 10,
                Padding = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = "删除", Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            if (_isEditMode)
            {
                row.MouseEnter += (s, e) => delBtn.Visibility = Visibility.Visible;
                row.MouseLeave += (s, e) => delBtn.Visibility = Visibility.Collapsed;
            }

            var textBlock = new TextBlock
            {
                Text = note.Preview, FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = note.Content?.Length > 200 ? note.Content.Substring(0, 200) + "..." : note.Content
            };
            row.Children.Add(textBlock);

            row.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled) return;
                OpenPanelStickyNoteEditor(note, container, item);
            };

            delBtn.Click += (s, e) =>
            {
                item.StickyNotes?.RemoveAll(n => n.Id == note.Id);
                _configManager?.Save();
                RefreshPanelStickyNotes(container, item);
                e.Handled = true;
            };

            return row;
        }

        private void OpenPanelStickyNoteEditor(StickyNote note, StackPanel container, PanelItem item)
        {
            if (_stickyNotePanelDialog == null)
                _stickyNotePanelDialog = new StickyNotePanel();

            if (_mainWindow != null)
            {
                // 根据侧边栏停靠位置决定便签弹出方向
                double noteLeft;
                if (_mainWindow.DockSide == DockEdge.Left)
                    noteLeft = _mainWindow.Left + _mainWindow.Width; // 左侧停靠 -> 弹在右边（紧贴）
                else
                    noteLeft = _mainWindow.Left - 420;               // 右侧停靠 -> 弹在左边（紧贴，420是弹窗宽度）

                _stickyNotePanelDialog.Show(_mainWindow,
                    noteLeft,
                    _mainWindow.Top, 300, _mainWindow.Height,
                    note, () =>
                    {
                        note.UpdatedAt = DateTime.Now;
                        _configManager?.Save();
                        RefreshPanelStickyNotes(container, item);
                    });
            }
        }

        private UIElement BuildStickyNoteItem(StickyNote note, StackPanel container)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand,
                Height = 22
            };

            // 删除按钮
            var delBtn = new Button
            {
                Content = "✕", Width = 18, Height = 18,
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0), FontSize = 10,
                Padding = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = "删除", Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            if (_isEditMode)
            {
                row.MouseEnter += (s, e) => delBtn.Visibility = Visibility.Visible;
                row.MouseLeave += (s, e) => delBtn.Visibility = Visibility.Collapsed;
            }

            var textBlock = new TextBlock
            {
                Text = note.Preview, FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = note.Content?.Length > 200 ? note.Content.Substring(0, 200) + "..." : note.Content
            };
            row.Children.Add(textBlock);

            // 点击打开编辑
            string noteId = note.Id;
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled) return;
                OpenStickyNoteEditor(noteId, container);
            };

            delBtn.Click += (s, e) =>
            {
                StickyNoteManager.Delete(noteId);
                RefreshStickyNotesInto(container);
                e.Handled = true;
            };

            return row;
        }

        private void OpenStickyNoteEditor(string noteId, StackPanel container)
        {
            var note = StickyNoteManager.GetById(noteId);
            if (note == null) return;

            if (_stickyNotePanelDialog == null)
                _stickyNotePanelDialog = new StickyNotePanel();

            if (_mainWindow != null)
            {
                // 根据侧边栏停靠位置决定便签弹出方向
                double noteLeft;
                if (_mainWindow.DockSide == DockEdge.Left)
                    noteLeft = _mainWindow.Left + _mainWindow.Width; // 左侧停靠 -> 弹在右边（紧贴）
                else
                    noteLeft = _mainWindow.Left - 420;               // 右侧停靠 -> 弹在左边（紧贴，420是弹窗宽度）

                _stickyNotePanelDialog.Show(_mainWindow,
                    noteLeft,
                    _mainWindow.Top, 300, _mainWindow.Height,
                    note, () => RefreshStickyNotesInto(container));
            }
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
                    Background = (Brush)FindResource("BrushDialogBG")
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
                            Foreground = (Brush)FindResource("BrushTextSec"),
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
                        Foreground = (Brush)FindResource("BrushTextSec"),
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
                Background = (Brush)FindResource("BrushDialogBG")
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

            // 初始值
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

            // 详细信息区
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
                Background = (Brush)FindResource("BrushInputBG"),
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
                new { Header = "剪贴板收藏",   Checked = settings.ShowClipboard,    Key = "ShowClipboard" },
                new { Header = "剪贴板历史",   Checked = settings.ShowClipboardHistory,    Key = "ShowClipboardHistory" }
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
                case "ShowClipboardHistory": settings.ShowClipboardHistory = value; break;
            }
            settings.Save();
        }



        private void ContextMenu_UsageStatsClick(object sender, RoutedEventArgs e)
        {
            UsageStatsButton_Click(sender, e);
        }

        private void ToggleEditMode_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            _isEditMode = menuItem?.IsChecked ?? false;

            // 保存到当前窗口配置
            if (_currentWindowConfig != null)
            {
                _currentWindowConfig.IsEditMode = _isEditMode;
                _configManager?.Save();
            }

            RenderPanels();
        }

        #endregion

        #region 布局与屏幕切换

        /// <summary>
        /// 右键菜单打开时，动态设置布局和屏幕的勾选状态
        /// </summary>
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // 同步编辑模式 checkbox 状态
            editModeMenu.IsChecked = _isEditMode;

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

            // 设置屏幕菜单勾选（已移除）
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

        #endregion

        #region 数据看板面板

        private UIElement BuildDataDashboardWidget(PanelItem item)
        {
            var container = new StackPanel();

            // 标题
            var titleBlock = new TextBlock
            {
                Text = item.Title ?? "数据看板",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            container.Children.Add(titleBlock);

            // 数据项列表
            if (item.DashboardItems != null && item.DashboardItems.Count > 0)
            {
                foreach (var dashItem in item.DashboardItems)
                {
                    var card = new StackPanel
                    {
                        Margin = new Thickness(0, 4, 0, 4),
                        Cursor = Cursors.Hand,
                        Background = Brushes.Transparent // 确保可点击
                    };

                    var labelBlock = new TextBlock
                    {
                        Text = dashItem.Label,
                        FontSize = 11,
                        Foreground = (Brush)FindResource("BrushTextSec")
                    };
                    card.Children.Add(labelBlock);

                    var valueBlock = new TextBlock
                    {
                        Text = dashItem.Value ?? "--",
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        Tag = dashItem.Id
                    };
                    card.Children.Add(valueBlock);

                    // 点击卡片打开目标网址
                    var targetUrl = dashItem.TargetUrl;
                    card.MouseLeftButtonUp += (s, e) =>
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(targetUrl))
                                Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
                        }
                        catch { }
                    };

                    container.Children.Add(card);

                    // 启动定时刷新
                    StartDashboardItemRefresh(dashItem, valueBlock);
                }
            }
            else
            {
                container.Children.Add(new TextBlock
                {
                    Text = "暂无数据项，请编辑添加",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            // 编辑模式：添加按钮
            if (_isEditMode)
            {
                var addBtn = new Button
                {
                    Content = "+ 添加数据项",
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushAccent"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                addBtn.Click += (s, e) =>
                {
                    var newItem = ShowAddDashboardItemDialog();
                    if (newItem != null)
                    {
                        item.DashboardItems ??= new List<DataDashboardItem>();
                        item.DashboardItems.Add(newItem);
                        _configManager?.Save();
                        RenderPanels();
                    }
                };
                container.Children.Add(addBtn);
            }

            return container;
        }

        private void StartDashboardItemRefresh(DataDashboardItem dashItem, TextBlock valueBlock)
        {
            var interval = Math.Max(dashItem.RefreshIntervalSeconds, 10);
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(interval)
            };

            timer.Tick += async (s, e) =>
            {
                try
                {
                    var app = (App)Application.Current;
                    var browsers = app.ProgramRegistry.Browsers;

                    var browserId = dashItem.BrowserId ?? browsers.FirstOrDefault()?.Id;
                    var browser = browsers.FirstOrDefault(b => b.Id == browserId);
                    if (browser == null) return;

                    var profileDir = "";
                    if (!string.IsNullOrEmpty(dashItem.ProfileId))
                    {
                        var profile = browser.Profiles.FirstOrDefault(p => p.Id == dashItem.ProfileId);
                        profileDir = profile?.Directory ?? "";
                    }

                    var instanceKey = $"{browserId}_{dashItem.ProfileId ?? "default"}";
                    var value = await Services.CdpBrowserService.Instance.FetchElementValueAsync(
                        browser.ExePath, profileDir, instanceKey, dashItem.TargetUrl, dashItem.Selector);

                    valueBlock.Text = value;
                    dashItem.Value = value;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DataDashboard] 刷新失败 {dashItem.Label}: {ex.Message}");
                }
            };

            timer.Start();

            // 首次延迟3秒获取一次
            _ = Task.Delay(3000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(async () =>
                {
                    try
                    {
                        var app = (App)Application.Current;
                        var browsers = app.ProgramRegistry.Browsers;
                        var browserId = dashItem.BrowserId ?? browsers.FirstOrDefault()?.Id;
                        var browser = browsers.FirstOrDefault(b => b.Id == browserId);
                        if (browser == null) return;

                        var profileDir = "";
                        if (!string.IsNullOrEmpty(dashItem.ProfileId))
                        {
                            var profile = browser.Profiles.FirstOrDefault(p => p.Id == dashItem.ProfileId);
                            profileDir = profile?.Directory ?? "";
                        }

                        var instanceKey = $"{browserId}_{dashItem.ProfileId ?? "default"}";
                        var value = await Services.CdpBrowserService.Instance.FetchElementValueAsync(
                            browser.ExePath, profileDir, instanceKey, dashItem.TargetUrl, dashItem.Selector);

                        valueBlock.Text = value;
                        dashItem.Value = value;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DataDashboard] 首次获取失败 {dashItem.Label}: {ex.Message}");
                    }
                });
            }, TaskScheduler.Default);
        }

        #endregion

        #region 数据项配置对话框

        private DataDashboardItem? ShowAddDashboardItemDialog()
        {
            var dialog = new Window
            {
                Title = "添加数据项",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.NoResize
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var labelBrush = (Brush)FindResource("BrushTextSec");
            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");

            // 加载模板
            var templateManager = DataTemplateManager.Load();
            var templates = templateManager.Templates;

            // 加载浏览器列表
            var browsers = ((App)Application.Current).ProgramRegistry.Browsers;

            // ===== 根布局 =====
            var rootPanel = new StackPanel { Margin = new Thickness(20) };

            // ===== 模板选择 =====
            rootPanel.Children.Add(new TextBlock
            {
                Text = "模板选择",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var templateCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 12),
                IsEditable = false
            };
            templateCombo.Items.Add(new ComboBoxItem { Content = "自定义", Tag = (string?)null });
            foreach (var t in templates)
            {
                templateCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
            }
            templateCombo.SelectedIndex = 0;
            rootPanel.Children.Add(templateCombo);

            // ===== 基本信息分隔 =====
            rootPanel.Children.Add(new TextBlock
            {
                Text = "── 基本信息 ──",
                Foreground = labelBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 数据项名称
            rootPanel.Children.Add(new TextBlock
            {
                Text = "数据项名称",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var nameBox = new TextBox
            {
                Style = textBoxStyle,
                Margin = new Thickness(0, 0, 0, 12)
            };
            rootPanel.Children.Add(nameBox);

            // ===== 数据源配置分隔 =====
            rootPanel.Children.Add(new TextBlock
            {
                Text = "── 数据源配置 ──",
                Foreground = labelBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 目标网址
            rootPanel.Children.Add(new TextBlock
            {
                Text = "目标网址",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var urlBox = new TextBox
            {
                Style = textBoxStyle,
                Margin = new Thickness(0, 0, 0, 12)
            };
            rootPanel.Children.Add(urlBox);

            // 选择器
            rootPanel.Children.Add(new TextBlock
            {
                Text = "选择器(CSS/XPath)",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var selectorBox = new TextBox
            {
                Style = textBoxStyle,
                Margin = new Thickness(0, 0, 0, 12)
            };
            rootPanel.Children.Add(selectorBox);

            // 刷新间隔
            rootPanel.Children.Add(new TextBlock
            {
                Text = "刷新间隔(秒)",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var intervalBox = new TextBox
            {
                Text = "60",
                Style = textBoxStyle,
                Margin = new Thickness(0, 0, 0, 12)
            };
            rootPanel.Children.Add(intervalBox);

            // ===== 浏览器配置分隔 =====
            rootPanel.Children.Add(new TextBlock
            {
                Text = "── 浏览器配置 ──",
                Foreground = labelBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 浏览器
            rootPanel.Children.Add(new TextBlock
            {
                Text = "浏览器",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var browserCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 12),
                IsEditable = false
            };
            browserCombo.Items.Add(new ComboBoxItem { Content = "默认浏览器", Tag = (string?)null });
            foreach (var b in browsers)
            {
                browserCombo.Items.Add(new ComboBoxItem { Content = b.Name, Tag = b.Id });
            }
            browserCombo.SelectedIndex = 0;
            rootPanel.Children.Add(browserCombo);

            // Profile
            rootPanel.Children.Add(new TextBlock
            {
                Text = "Profile",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var profileCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 16),
                IsEditable = false
            };
            rootPanel.Children.Add(profileCombo);

            // Profile 联动刷新方法
            Action refreshProfiles = () =>
            {
                profileCombo.Items.Clear();
                if (browserCombo.SelectedItem is ComboBoxItem selectedBrowserItem && selectedBrowserItem.Tag is string browserId)
                {
                    var browser = browsers.FirstOrDefault(b => b.Id == browserId);
                    if (browser != null)
                    {
                        foreach (var p in browser.Profiles)
                        {
                            profileCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
                        }
                        if (profileCombo.Items.Count > 0)
                            profileCombo.SelectedIndex = 0;
                    }
                }
            };

            // 初始加载 Profile
            refreshProfiles();

            // 浏览器切换时联动 Profile
            browserCombo.SelectionChanged += (s, e) => refreshProfiles();

            // ===== 模板选择联动 =====
            templateCombo.SelectionChanged += (s, e) =>
            {
                if (templateCombo.SelectedItem is ComboBoxItem selectedItem)
                {
                    var templateId = selectedItem.Tag as string;
                    if (templateId != null)
                    {
                        var tmpl = templates.FirstOrDefault(t => t.Id == templateId);
                        if (tmpl != null)
                        {
                            nameBox.Text = tmpl.Name;
                            urlBox.Text = tmpl.TargetUrl;
                            selectorBox.Text = tmpl.Selector;
                            intervalBox.Text = tmpl.RefreshIntervalSeconds.ToString();
                        }
                    }
                    else
                    {
                        // 自定义：清空
                        nameBox.Text = "";
                        urlBox.Text = "";
                        selectorBox.Text = "";
                        intervalBox.Text = "60";
                    }
                }
            };

            // ===== 按钮区域 =====
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "取消",
                Style = (Style)FindResource("StandardButtonStyle"),
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancelBtn.Click += (s, e) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };

            var confirmBtn = new Button
            {
                Content = "确定",
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(20, 8, 20, 8)
            };
            confirmBtn.Click += (s, e) =>
            {
                // 验证必填字段
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    MessageBox.Show("请输入数据项名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    nameBox.Focus();
                    return;
                }
                if (string.IsNullOrWhiteSpace(urlBox.Text))
                {
                    MessageBox.Show("请输入目标网址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    urlBox.Focus();
                    return;
                }
                if (string.IsNullOrWhiteSpace(selectorBox.Text))
                {
                    MessageBox.Show("请输入选择器", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    selectorBox.Focus();
                    return;
                }

                dialog.DialogResult = true;
                dialog.Close();
            };

            buttonPanel.Children.Add(cancelBtn);
            buttonPanel.Children.Add(confirmBtn);
            rootPanel.Children.Add(buttonPanel);

            dialog.Content = rootPanel;

            // 显示对话框
            if (dialog.ShowDialog() == true)
            {
                var item = new DataDashboardItem
                {
                    Label = nameBox.Text.Trim(),
                    TargetUrl = urlBox.Text.Trim(),
                    Selector = selectorBox.Text.Trim(),
                    RefreshIntervalSeconds = int.TryParse(intervalBox.Text.Trim(), out var interval) && interval > 0 ? interval : 60
                };

                // 记录模板ID
                if (templateCombo.SelectedItem is ComboBoxItem selItem && selItem.Tag is string tmplId)
                {
                    item.TemplateId = tmplId;
                }

                // 记录浏览器和Profile
                if (browserCombo.SelectedItem is ComboBoxItem browserItem && browserItem.Tag is string bId)
                {
                    item.BrowserId = bId;
                }
                if (profileCombo.SelectedItem is ComboBoxItem profileItem && profileItem.Tag is string pId)
                {
                    item.ProfileId = pId;
                }

                return item;
            }

            return null;
        }

        #endregion

        #region 窗口级工具栏

        /// <summary>
        /// "工具"菜单展开时动态填充可切换的工具选项
        /// </summary>
        private void ToolsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;

            menu.Items.Clear();

            if (_currentWindowConfig == null)
            {
                menu.Items.Add(new MenuItem { Header = "(未加载窗口配置)", IsEnabled = false });
                return;
            }

            // 定义可用工具列表
            var availableTools = new dynamic[]
            {
                new { Key = "ClipboardHistory", Header = "📋 剪贴板历史", Action = (Action)ShowClipboardHistoryTool, IsCheckable = true },
                new { Key = "AIChat", Header = "🤖 AI 助手", Action = (Action)ShowAIChatTool, IsCheckable = true },
                new { Key = "VoiceInput", Header = "🎤 语音输入", Action = (Action)ShowVoiceInputTool, IsCheckable = true },
                new { Key = "WindowTeleport", Header = "🚀 窗口瞬移", Action = (Action)ShowWindowTeleportTool, IsCheckable = false },
                new { Key = "TitleLock", Header = "🔒 窗口标题锁定器", Action = (Action)ShowTitleLockTool, IsCheckable = false },
                // 后续可以在这里添加更多工具
            };

            foreach (var tool in availableTools)
            {
                var mi = new MenuItem
                {
                    Header = tool.Header,
                    IsCheckable = tool.IsCheckable,
                    Tag = tool.Key
                };
                
                // 只有可勾选的工具才显示勾选状态
                if (tool.IsCheckable && _currentWindowConfig != null)
                {
                    mi.IsChecked = _currentWindowConfig.EnabledTools.Contains(tool.Key);
                }
                
                string key = tool.Key;
                Action action = tool.Action;
                bool isCheckable = tool.IsCheckable;
                
                mi.Click += (s, ev) =>
                {
                    if (isCheckable)
                    {
                        // 可勾选工具：切换状态
                        ToggleTool(key, mi.IsChecked, action);
                    }
                    else
                    {
                        // 直接使用的工具：立即执行
                        action();
                    }
                };
                menu.Items.Add(mi);
            }
        }

        /// <summary>
        /// 切换工具显示状态
        /// </summary>
        private void ToggleTool(string key, bool isChecked, Action showAction)
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            if (isChecked)
            {
                // 勾选：添加到启用列表
                if (!_currentWindowConfig.EnabledTools.Contains(key))
                {
                    _currentWindowConfig.EnabledTools.Add(key);
                }
                // 立即显示工具
                showAction();
            }
            else
            {
                // 取消勾选：从启用列表移除
                _currentWindowConfig.EnabledTools.Remove(key);
            }

            _configManager.Save();
            RefreshToolsBar();
        }

        /// <summary>
        /// 刷新底部工具栏显示
        /// </summary>
        private void RefreshToolsBar()
        {
            if (toolsBarContent == null || toolsBar == null) return;

            toolsBarContent.Children.Clear();

            if (_currentWindowConfig == null || _currentWindowConfig.EnabledTools.Count == 0)
            {
                toolsBar.Visibility = Visibility.Collapsed;
                return;
            }

            // 显示启用的工具按钮
            bool first = true;
            foreach (var toolKey in _currentWindowConfig.EnabledTools)
            {
                if (!first)
                {
                    // 添加分隔符
                    toolsBarContent.Children.Add(new Separator 
                    { 
                        Height = 1,
                        Margin = new Thickness(0, 3, 0, 3),
                    });
                }
                first = false;

                // 根据工具类型创建对应的按钮
                Button? toolButton = null;
                switch (toolKey)
                {
                    case "ClipboardHistory":
                    case "AIChat":
                    case "VoiceInput":
                        toolButton = CreateToolButton(toolKey);
                        break;
                    case "WindowTeleport":
                    case "TitleLock":
                        // 这些是直接使用的工具，不需要在底部显示按钮
                        break;
                }

                if (toolButton != null)
                {
                    toolsBarContent.Children.Add(toolButton);
                }
            }

            toolsBar.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 显示窗口标题锁定器管理界面
        /// </summary>
        private void ShowTitleLockTool()
        {
            var window = new TitleLockWindow();
            // 获取主窗口作为Owner
            if (_mainWindow != null)
                window.Owner = _mainWindow;
            window.ShowDialog();
        }

        /// <summary>
        /// 创建工具元素（按钮或容器）
        /// </summary>
        private UIElement? CreateToolElement(string toolKey)
        {
            // 语音输入工具返回StackPanel容器
            if (toolKey == "VoiceInput")
            {
                if (_voiceInputContainer == null)
                {
                    _voiceInputContainer = CreateVoiceInputContainer();
                }
                _voiceInputContainer.Visibility = Visibility.Visible;
                return _voiceInputContainer;
            }

            // 其他工具返回Button
            var btn = CreateToolButton(toolKey);
            return btn;
        }

        /// <summary>
        /// 创建工具按钮
        /// </summary>
        private Button? CreateToolButton(string toolKey)
        {
            Button? btn = toolKey switch
            {
                "ClipboardHistory" => new Button
                {
                    Content = "📋 剪贴板历史",
                    Padding = new Thickness(8, 4, 8, 4),
                    MinHeight = 22,
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = toolKey,
                    VerticalAlignment = VerticalAlignment.Center
                },
                
                "AIChat" => new Button
                {
                    Content = "🤖 AI 助手",
                    Padding = new Thickness(8, 4, 8, 4),
                    MinHeight = 22,
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = toolKey,
                    VerticalAlignment = VerticalAlignment.Center
                },
                
                "VoiceInput" => new Button
                {
                    Content = "🎤 语音输入",
                    Padding = new Thickness(8, 4, 8, 4),
                    MinHeight = 22,
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = toolKey,
                    VerticalAlignment = VerticalAlignment.Center
                },
                
                // 窗口瞬移不需要在底部显示按钮，它是直接使用的工具
                // 窗口标题锁定器也不需要显示按钮，它是直接打开窗口的工具
                
                _ => null
            };

            if (btn != null)
            {
                btn.Click += (s, e) =>
                {
                    if (toolKey == "ClipboardHistory") ShowClipboardHistoryTool();
                    else if (toolKey == "AIChat") ShowAIChatTool();
                    else if (toolKey == "VoiceInput") ShowVoiceInputTool();
                };
            }

            return btn;
        }

        /// <summary>
        /// 显示剪贴板历史工具
        /// </summary>
        private void ShowClipboardHistoryTool()
        {
            // 确保剪贴板服务已启动
            EnsureClipboardServiceStarted();
            
            // 如果底部工具栏中没有剪贴板历史容器，则添加
            if (_clipboardHistoryContainer == null)
            {
                _clipboardHistoryContainer = new StackPanel();
                toolsBarContent.Children.Add(_clipboardHistoryContainer);
            }
            
            RefreshClipboardHistoryUI();
        }

        /// <summary>
        /// 显示AI助手工具
        /// </summary>
        private void ShowAIChatTool()
        {
            AIChatButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// 显示语音输入工具
        /// </summary>
        private void ShowVoiceInputTool()
        {
            try
            {
                // 创建容器（如果不存在）
                if (_voiceInputContainer == null)
                {
                    _voiceInputContainer = CreateVoiceInputContainer();
                }
                
                // 刷新工具栏，会自动包含语音输入
                RefreshToolsBar();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"语音输入功能初始化失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private StackPanel? _voiceInputContainer;
        private System.Speech.Recognition.SpeechRecognitionEngine? _speechEngine;
        private bool _isVoiceRecording = false;
        
        // 瞬移窗口管理
        private List<TaskbarOverlayManager.WindowInfo> _teleportedWindows = new();
        private Border? _teleportWidget;
        private ListBox? _teleportListBox;

        private StackPanel CreateVoiceInputContainer()
        {
            var container = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // 语音输入按钮
            var voiceBtnBorder = new Border
            {
                Background = (Brush)FindResource("BrushAccent"),
                CornerRadius = new CornerRadius(4),
                MinHeight = 28,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Button
                {
                    Content = "🎤 点击开始",
                    Padding = new Thickness(12, 5, 12, 5),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Name = "VoiceInputButton"
                }
            };

            // 状态文本
            var statusText = new TextBlock
            {
                Text = "就绪",
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 12,
                Name = "VoiceInputStatus"
            };

            container.Children.Add(voiceBtnBorder);
            container.Children.Add(statusText);

            // 初始化语音识别
            var voiceBtn = voiceBtnBorder.Child as Button;
            if (voiceBtn != null)
            {
                InitializeSpeechRecognition(voiceBtn, statusText);
            }

            return container;
        }

        private void InitializeSpeechRecognition(Button voiceBtn, TextBlock statusText)
        {
            // 需要获取voiceBtnBorder，从父级获取
            var voiceBtnBorder = voiceBtn.Parent as Border;
            
            try
            {
                _speechEngine = new System.Speech.Recognition.SpeechRecognitionEngine(
                    new System.Globalization.CultureInfo("zh-CN"));

                // 创建语法：识别所有语音
                var dictationGrammar = new System.Speech.Recognition.DictationGrammar();
                _speechEngine.LoadGrammar(dictationGrammar);

                // 语音识别完成事件
                _speechEngine.SpeechRecognized += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var recognizedText = e.Result.Text;
                        statusText.Text = $"识别：{recognizedText}";
                        
                        // 可以将识别结果发送到AI对话或其他处理逻辑
                        HandleVoiceInput(recognizedText);
                    });
                };

                // 识别结束事件
                _speechEngine.RecognizeCompleted += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        voiceBtn.Content = "🎤 点击开始";
                        voiceBtn.Background = Brushes.Transparent;
                        voiceBtnBorder.Background = (Brush)FindResource("BrushAccent");
                        statusText.Text = "就绪";
                        _isVoiceRecording = false;
                    });
                };

                // 设置音频输入
                _speechEngine.SetInputToDefaultAudioDevice();

                // 按钮点击事件
                voiceBtn.Click += (s, e) =>
                {
                    if (!_isVoiceRecording)
                    {
                        StartVoiceRecognition(voiceBtn, statusText, voiceBtnBorder);
                    }
                    else
                    {
                        StopVoiceRecognition(voiceBtn, statusText, voiceBtnBorder);
                    }
                };
            }
            catch (Exception ex)
            {
                statusText.Text = $"语音初始化失败：{ex.Message}";
            }
        }

        private void StartVoiceRecognition(Button voiceBtn, TextBlock statusText, Border voiceBtnBorder)
        {
            try
            {
                _isVoiceRecording = true;
                voiceBtn.Content = "🎤 正在聆听...";
                voiceBtnBorder.Background = Brushes.Red;
                statusText.Text = "正在聆听...";
                _speechEngine?.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
            }
            catch (Exception ex)
            {
                statusText.Text = $"启动失败：{ex.Message}";
                _isVoiceRecording = false;
            }
        }

        private void StopVoiceRecognition(Button voiceBtn, TextBlock statusText, Border voiceBtnBorder)
        {
            try
            {
                _speechEngine?.RecognizeAsyncStop();
                _isVoiceRecording = false;
            }
            catch (Exception ex)
            {
                statusText.Text = $"停止失败：{ex.Message}";
            }
        }

        private void HandleVoiceInput(string recognizedText)
        {
            // 这里可以扩展：将语音识别结果发送到AI对话、剪贴板等
            // 示例：显示消息框
            // MessageBox.Show($"识别结果：{recognizedText}", "语音输入", MessageBoxButton.OK, MessageBoxImage.Information);
            
            // 或者可以将文本复制到剪贴板
            try
            {
                Clipboard.SetText(recognizedText);
            }
            catch { }
        }

        /// <summary>
        /// 显示窗口瞬移工具
        /// </summary>
        private void ShowWindowTeleportTool()
        {
            try
            {
                // 获取任务栏窗口列表
                var windows = TaskbarOverlayManager.GetTaskbarWindows();
                if (windows.Count == 0)
                {
                    MessageBox.Show("未找到可用的任务栏窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 创建窗口选择对话框
                var pickerDialog = new Window
                {
                    Title = "🚀 窗口瞬移 - 选择要移动的窗口",
                    Width = 600,
                    Height = 500,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = _mainWindow,
                    ResizeMode = ResizeMode.CanResize,
                    Background = (Brush)FindResource("BrushDialogBG")
                };

                var mainPanel = new DockPanel { Margin = new Thickness(20) };
                
                // 顶部说明
                var headerText = new TextBlock
                {
                    Text = "选择任务栏中的窗口，点击\"瞬移\"将其移动到左侧Dock区域",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 0, 0, 15),
                    TextWrapping = TextWrapping.Wrap
                };
                DockPanel.SetDock(headerText, Dock.Top);
                mainPanel.Children.Add(headerText);

                // 窗口列表
                var windowListBox = new ListBox
                {
                    Margin = new Thickness(0, 0, 0, 15),
                    Background = (Brush)FindResource("BrushInputBG"),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)FindResource("BrushBorder")
                };

                // 填充窗口列表
                foreach (var win in windows)
                {
                    var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };

                    // 图标
                    if (win.Icon != null)
                    {
                        itemPanel.Children.Add(new Image
                        {
                            Source = win.Icon,
                            Width = 24,
                            Height = 24,
                            Margin = new Thickness(0, 0, 12, 0)
                        });
                    }

                    // 窗口信息
                    var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    infoPanel.Children.Add(new TextBlock
                    {
                        Text = win.Title,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    infoPanel.Children.Add(new TextBlock
                    {
                        Text = $"进程: {win.ProcessName}",
                        FontSize = 11,
                        Margin = new Thickness(0, 3, 0, 0),
                        Foreground = (Brush)FindResource("BrushTextMuted")
                    });

                    itemPanel.Children.Add(infoPanel);

                    var listBoxItem = new ListBoxItem { Content = itemPanel, Tag = win };
                    windowListBox.Items.Add(listBoxItem);
                }

                DockPanel.SetDock(windowListBox, Dock.Top);
                mainPanel.Children.Add(windowListBox);

                // 底部按钮
                var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(buttonPanel, Dock.Bottom);

                var teleportBtn = new Button
                {
                    Content = "🚀 瞬移到左侧",
                    Width = 140,
                    Height = 35,
                    Margin = new Thickness(0, 0, 10, 0),
                    Background = (Brush)FindResource("BrushAccent"),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold
                };

                var cancelBtn = new Button
                {
                    Content = "取消",
                    Width = 80,
                    Height = 35
                };

                buttonPanel.Children.Add(teleportBtn);
                buttonPanel.Children.Add(cancelBtn);
                mainPanel.Children.Add(buttonPanel);

                pickerDialog.Content = mainPanel;

                // 瞬移按钮点击事件
                teleportBtn.Click += (s, e) =>
                {
                    if (windowListBox.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is TaskbarOverlayManager.WindowInfo selectedWindow)
                    {
                        TeleportWindowToDock(selectedWindow);
                        pickerDialog.DialogResult = true;
                        pickerDialog.Close();
                    }
                    else
                    {
                        MessageBox.Show("请先选择一个窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };

                // 取消按钮点击事件
                cancelBtn.Click += (s, e) =>
                {
                    pickerDialog.DialogResult = false;
                    pickerDialog.Close();
                };

                // 双击瞬移
                windowListBox.MouseDoubleClick += (s, e) =>
                {
                    if (windowListBox.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is TaskbarOverlayManager.WindowInfo selectedWindow)
                    {
                        TeleportWindowToDock(selectedWindow);
                        pickerDialog.DialogResult = true;
                        pickerDialog.Close();
                    }
                };

                pickerDialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开窗口瞬移工具失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 将窗口瞬移到左侧Dock区域
        /// </summary>
        private void TeleportWindowToDock(TaskbarOverlayManager.WindowInfo windowInfo)
        {
            try
            {
                IntPtr hwnd = windowInfo.Handle;

                // 1. 如枟窗口最小化，先恢复
                if (NativeMethods.IsIconic(hwnd))
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                }

                // ✅ 2. 先从任务栏隐藏窗口（在移动之前）
                HideWindowFromTaskbar(hwnd);

                // 3. 获取主屏幕工作区
                var workingArea = SystemParameters.WorkArea;

                // 4. 计算左侧区域位置（左侧 1/3 宽度）
                int dockWidth = (int)(workingArea.Width / 3);
                int dockHeight = (int)workingArea.Height;
                int dockX = (int)workingArea.Left;
                int dockY = (int)workingArea.Top;

                // 5. 移动窗口到左侧
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    dockX, dockY, dockWidth, dockHeight,
                    0); // 无特殊标志

                // 6. 添加到瞬移窗口列表
                if (!_teleportedWindows.Any(w => w.Handle == hwnd))
                {
                    _teleportedWindows.Add(windowInfo);
                }

                // 7. 更新UI显示
                UpdateTeleportWidget();

                MessageBox.Show($"已将窗口“{windowInfo.Title}”移动到左侧Dock区域", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移动窗口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 从任务栏隐藏窗口
        /// </summary>
        private void HideWindowFromTaskbar(IntPtr hwnd)
        {
            try
            {
                // 方法1: 使用 ITaskbarList COM 接口从任务栏删除窗口
                var taskbarList = (ITaskbarList)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090")));
                taskbarList.HrInit();
                taskbarList.DeleteTab(hwnd);
                Marshal.ReleaseComObject(taskbarList);
                
                // ✅ 方法2: 设置WS_EX_TOOLWINDOW扩展样式（使窗口不在任务栏显示）
                int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle |= (int)NativeMethods.WS_EX_TOOLWINDOW;
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
                
                Debug.WriteLine($"已从任务栏隐藏窗口: {hwnd}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏任务栏图标失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 更新瞬移窗口区UI
        /// </summary>
        private void UpdateTeleportWidget()
        {
            // 如果没有瞬移窗口，隐藏组件
            if (_teleportedWindows.Count == 0)
            {
                if (_teleportWidget != null)
                {
                    _teleportWidget.Visibility = Visibility.Collapsed;
                }
                return;
            }

            // 创建或更新组件
            if (_teleportWidget == null)
            {
                _teleportWidget = CreateTeleportWidget();
                
                // 插入到动态内容区的最前面
                if (dynamicContent != null)
                {
                    dynamicContent.Children.Insert(0, _teleportWidget);
                }
            }

            // 更新窗口列表
            if (_teleportListBox != null)
            {
                _teleportListBox.Items.Clear();
                
                foreach (var window in _teleportedWindows)
                {
                    var itemPanel = new StackPanel 
                    { 
                        Orientation = Orientation.Horizontal, 
                        Margin = new Thickness(3, 2, 3, 2) 
                    };

                    // 图标
                    if (window.Icon != null)
                    {
                        itemPanel.Children.Add(new Image
                        {
                            Source = window.Icon,
                            Width = 16,
                            Height = 16,
                            Margin = new Thickness(0, 0, 5, 0)
                        });
                    }

                    // 窗口标题
                    var titleText = new TextBlock
                    {
                        Text = window.Title,
                        FontWeight = FontWeights.Normal,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    itemPanel.Children.Add(titleText);

                    // ✅ 使整个项可点击并带到前台
                    itemPanel.MouseLeftButtonUp += (s, e) =>
                    {
                        BringWindowToFront(window.Handle);
                        e.Handled = true;
                    };

                    // 还原按钮
                    var restoreBtn = new Button
                    {
                        Content = "↺",
                        Width = 20,
                        Height = 20,
                        Margin = new Thickness(4, 0, 0, 0),
                        ToolTip = "还原到任务栏",
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        FontSize = 12
                    };
                    
                    var capturedWindow = window;
                    restoreBtn.Click += (s, e) =>
                    {
                        RestoreWindowFromDock(capturedWindow);
                        e.Handled = true; // 防止触发itemPanel的点击事件
                    };
                    
                    itemPanel.Children.Add(restoreBtn);

                    var listBoxItem = new ListBoxItem 
                    { 
                        Content = itemPanel,
                        Tag = window
                    };
                    _teleportListBox.Items.Add(listBoxItem);
                }
            }

            _teleportWidget.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 创建瞬移窗口区组件
        /// </summary>
        private Border CreateTeleportWidget()
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(0, 10, 0, 10),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel();

            // 标题栏
            var headerPanel = new DockPanel();
            var titleText = new TextBlock
            {
                Text = "🚀 瞬移窗口",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
            headerStack.Children.Add(titleText);

            DockPanel.SetDock(headerStack, Dock.Left);
            headerPanel.Children.Add(headerStack);

            stack.Children.Add(headerPanel);

            // 窗口列表
            _teleportListBox = new ListBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                MaxHeight = 300
            };

            // 隐藏滚动条但保持滚动功能
            ScrollViewer.SetVerticalScrollBarVisibility(_teleportListBox, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(_teleportListBox, ScrollBarVisibility.Hidden);

            stack.Children.Add(_teleportListBox);
            border.Child = stack;

            return border;
        }

        /// <summary>
        /// 从 Dock区域还原窗口（保持工具窗口样式，不在任务栏显示）
        /// </summary>
        private void RestoreWindowFromDock(TaskbarOverlayManager.WindowInfo windowInfo)
        {
            try
            {
                IntPtr hwnd = windowInfo.Handle;
                
                // ✅ 只从瞬移列表中移除，不改变窗口的工具窗口样式
                // 窗口将保持 WS_EX_TOOLWINDOW 样式，不会在任务栏显示
                _teleportedWindows.RemoveAll(w => w.Handle == hwnd);
                
                // 更新UI
                UpdateTeleportWidget();
                
                MessageBox.Show($"已将窗口“{windowInfo.Title}”从瞬移列表中移除\n窗口仍保持工具窗口样式，不会在任务栏显示", "已移除", MessageBoxButton.OK, MessageBoxImage.Information);
                Debug.WriteLine($"已从瞬移列表移除窗口: {hwnd}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移除窗口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 将窗口带到前台
        /// </summary>
        private void BringWindowToFront(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;

                // 如果窗口最小化，先恢复
                if (NativeMethods.IsIconic(hwnd))
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                }

                // 将窗口带到前台
                NativeMethods.SetForegroundWindow(hwnd);
                
                // 确保窗口显示
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                
                Debug.WriteLine($"已将窗口带到前台: {hwnd}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"带窗口到前台失败：{ex.Message}");
            }
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        }

        #endregion
    }
}
