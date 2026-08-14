using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Services.Plugins;
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

        // 数字员工全局库（懒加载，模块间共享）
        private DigitalEmployeeManager? _digitalEmployeeManager;
        private DigitalEmployeeManager DigitalEmployees
        {
            get
            {
                if (_digitalEmployeeManager == null)
                {
                    _digitalEmployeeManager = DigitalEmployeeManager.Load();
                    _digitalEmployeeManager.EnsureDefault();
                }
                return _digitalEmployeeManager;
            }
        }

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

        // 顶层面板拖拽排序指示器（编辑模式下上下拖动模块/分组调换位置）
        // 指示器为 XAML 中的常驻叠加层 panelDropIndicator，不再动态插入/移除子元素，避免布局抖动

        // 右键菜单打开时记录的插入位置：当右键点在两个模块之间时，新增模块插入到该位置；null 表示按默认追加
        private int? _pendingInsertIndex;

        // 今日待办到点提醒
        private DispatcherTimer? _todoReminderTimer;
        private static readonly HashSet<string> _remindedTodoIds = new();

        // 剪贴板历史
        private ClipboardService? _clipboardService;
        private StackPanel? _clipboardHistoryContainer;
        private Popup? _clipboardHistoryPopup;
        /// <summary>剪贴板历史→收藏拖拽的自定义数据格式</summary>
        private const string ClipboardDragFormat = "SideDesk/ClipboardEntry";

        // 对话框实例
        private AIChatDialog? _aiChatDialog;
        private SideDesk.Windows.AIChatWebWindow? _aiChatWebWindow;
        private WindowUsagePanel? _windowUsagePanel;

        /// <summary>请求主窗口调整宽度</summary>
        public event Action<double>? RequestWidthChange;

        public NativeModePanel()
        {
            InitializeComponent();
            Loaded += NativeModePanel_Loaded;
            Unloaded += NativeModePanel_Unloaded;
            
            // 订阅主题变更事件，重建所有面板（独立模块 + 快捷分组）以应用新主题颜色
            App.ThemeChanged += () =>
            {
                RenderPanels();
            };
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

            // 剪贴板监控随面板初始化启动（随时记录，供历史弹窗展示最近 50 条）
            EnsureClipboardServiceStarted();

            // 顶层面板拖拽排序（编辑模式下上下拖动模块/分组调换位置），只订阅一次
            dynamicContent.AllowDrop = true;
            dynamicContent.DragOver += DynamicContent_DragOver;
            dynamicContent.DragLeave += DynamicContent_DragLeave;
            dynamicContent.Drop += DynamicContent_Drop;

            // 置顶区拖放（编辑模式下拖入/拖出置顶区、区内调序），只订阅一次
            pinnedContent.AllowDrop = true;
            pinnedContent.DragOver += PinnedContent_DragOver;
            pinnedContent.DragLeave += PinnedContent_DragLeave;
            pinnedContent.Drop += PinnedContent_Drop;

            // 今日待办到点提醒（每 30 秒检查一次）
            _todoReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _todoReminderTimer.Tick += TodoReminderTick;
            _todoReminderTimer.Start();

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
        /// <summary>外部触发重新渲染面板（例如设置变更后）。</summary>
        public void RefreshLayout() => RenderPanels();

        private Emoji.Wpf.TextBlock? _pinnedPlaceholder; // 置顶区编辑模式占位提示（复用实例，避免重复创建）

        private void RenderPanels()
        {
            dynamicContent.Children.Clear();
            pinnedContent.Children.Clear();

            // 重置快捷分组服务引用：避免切换到「无快捷分组面板」的窗口时，
            // 面板级拖放仍使用上一个窗口遗留的服务，导致文件被拖到其他窗口的分组。
            // 同时停掉旧服务的后台计时器，避免定时器泄漏（不关闭已打开的窗口）。
            _quickGroupUIService?.StopWatchers();
            _quickGroupUIService = null;
            _groupPanel = null;

            if (_currentWindowConfig == null)
            {
                pinnedArea.Visibility = Visibility.Collapsed;
                return;
            }

            // 行级同步：通知 CloudSyncService 数据可能变更（防抖 2s 后自动推送）
            Services.CloudSyncService.Instance.NotifyRowsChanged();

            bool hasPinned = false;
            foreach (var panel in _currentWindowConfig.Panels)
            {
                // 分组内的置顶子面板：提取到置顶区显示（BuildGroupWidget 会跳过这些子面板）
                if (panel.Type == PanelType.Group && panel.ChildPanels != null && !panel.IsPinned)
                {
                    foreach (var child in panel.ChildPanels)
                    {
                        if (!child.IsPinned) continue;
                        var childWidget = CreatePanelWidget(child);
                        if (childWidget != null)
                        {
                            if (childWidget is FrameworkElement cwfe && cwfe.Uid != "wrapHandled")
                                childWidget = WrapResizable(cwfe, child);
                            pinnedContent.Children.Add(childWidget);
                            hasPinned = true;
                        }
                    }
                }

                var widget = BuildPanelWidget(panel);
                if (widget != null)
                {
                    // 置顶模块渲染到置顶区（压缩下方滚动区高度），其余渲染到滚动区
                    if (panel.IsPinned)
                    {
                        pinnedContent.Children.Add(widget);
                        hasPinned = true;
                    }
                    else
                    {
                        dynamicContent.Children.Add(widget);
                    }
                }
            }

            // 置顶区可见性 + 编辑模式占位提示
            SyncPinnedAreaVisibility(hasPinned);

            // 空窗口提示
            if (_currentWindowConfig.Panels.Count == 0)
            {
                dynamicContent.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("右键添加模块"),
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

        /// <summary>构建单个面板的可渲染 widget（可调高包装 + 面板 Id 标记 + 编辑模式拖拽手柄），供全量/增量渲染复用</summary>
        private UIElement? BuildPanelWidget(PanelItem panel)
        {
            var widget = CreatePanelWidget(panel);
            if (widget == null) return null;

            // 未走 WrapWidget 的自定义卡片模块，在顶层统一附加可调高能力（编辑模式显示拖拽手柄）
            if (widget is FrameworkElement wfe && wfe.Uid != "wrapHandled")
                widget = WrapResizable(wfe, panel);

            // 标记面板 Id（供右键位置插入计算）；编辑模式下额外挂载拖拽调序手柄
            if (widget is FrameworkElement fe)
            {
                fe.Tag = panel.Id;
                if (_isEditMode)
                    AttachPanelDragHandlers(fe, panel);
            }
            return widget;
        }

        /// <summary>当前配置是否存在置顶面板（含分组内被置顶的子面板）</summary>
        private bool AnyPinnedPanels()
        {
            if (_currentWindowConfig == null) return false;
            foreach (var p in _currentWindowConfig.Panels)
            {
                if (p.IsPinned) return true;
                if (p.Type == PanelType.Group && p.ChildPanels != null && p.ChildPanels.Any(c => c.IsPinned)) return true;
            }
            return false;
        }

        /// <summary>同步置顶区可见性：有置顶模块时显示；编辑模式下始终显示（空时附占位提示，方便拖入置顶）</summary>
        private void SyncPinnedAreaVisibility(bool hasPinned)
        {
            pinnedArea.Visibility = (hasPinned || _isEditMode) ? Visibility.Visible : Visibility.Collapsed;
            if (_isEditMode && !hasPinned)
            {
                if (_pinnedPlaceholder == null)
                {
                    _pinnedPlaceholder = new Emoji.Wpf.TextBlock
                    {
                        Text = Framework.Lang.T("📌 拖拽模块到此处可置顶"),
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 2),
                        FontSize = 12,
                        Opacity = 0.6
                    };
                }
                if (!pinnedContent.Children.Contains(_pinnedPlaceholder))
                    pinnedContent.Children.Add(_pinnedPlaceholder);
            }
            else if (_pinnedPlaceholder != null)
            {
                pinnedContent.Children.Remove(_pinnedPlaceholder);
            }
        }

        /// <summary>
        /// 增量插入新增的顶层面板 widget：不重建已有模块，保留滚动位置与各面板运行状态。
        /// panelIndex 为面板在 Panels 中的下标（AddPanelToGroupOrTop 返回）。
        /// </summary>
        private void InsertPanelWidgetIncremental(PanelItem panel, int panelIndex)
        {
            var widget = BuildPanelWidget(panel);
            if (widget == null) { RenderPanels(); return; } // 构建失败兜底全量重绘

            if (panel.IsPinned)
            {
                pinnedContent.Children.Add(widget);
            }
            else
            {
                // 首个模块加入时，滚动区里只有“右键添加模块”空态占位，先清掉
                if (_currentWindowConfig != null && _currentWindowConfig.Panels.Count == 1)
                    dynamicContent.Children.Clear();

                // 滚动区实际插入位置 = 排在前面的非置顶面板数（置顶面板渲染在置顶区）
                int childIndex = 0;
                if (_currentWindowConfig != null)
                {
                    for (int i = 0; i < panelIndex && i < _currentWindowConfig.Panels.Count; i++)
                        if (!_currentWindowConfig.Panels[i].IsPinned) childIndex++;
                }
                if (childIndex > dynamicContent.Children.Count) childIndex = dynamicContent.Children.Count;
                dynamicContent.Children.Insert(childIndex, widget);
            }

            SyncPinnedAreaVisibility(AnyPinnedPanels());
            if (_teleportedWindows.Count > 0)
                UpdateTeleportWidget();
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
                PanelType.TodoList => BuildTodoWidget(item),
                PanelType.SignalLight => BuildSignalLightWidget(item),
                PanelType.DigitalEmployee => BuildDigitalEmployeeWidget(item),
                PanelType.Friend => BuildFriendWidget(item),
                PanelType.ImageCarousel => BuildImageCarouselWidget(item),
                PanelType.DailyActivity => BuildDailyActivityWidget(item),
                PanelType.Calendar => BuildCalendarWidget(item),
                PanelType.Plugin => BuildPluginWidget(item),
                PanelType.ScreenTime => BuildScreenTimeWidget(item),
                PanelType.Countdown => BuildCountdownWidget(item),
                PanelType.Pomodoro => BuildPomodoroWidget(item),
                PanelType.Group => BuildGroupWidget(item),
                _ => null
            };
        }

        /// <summary>
        /// 通用面板包裹器：基于 ModuleControl 基础组件，提供统一的标题、菜单、外框、拖拽能力
        /// </summary>
        private UIElement WrapWidget(string defaultTitle, UIElement content, PanelItem item, Action? onAddClick = null, ContextMenu? actionMenu = null, string addMenuHeader = null)
        {
            addMenuHeader ??= Framework.Lang.T("添加");
            var module = CreateModuleControl(defaultTitle, item);

            // 将 onAddClick 转为自定义菜单项（编辑模式下也保留：
            // 否则新建模块自动进入编辑模式后，用户找不到“向当前模块添加内容”的入口，
            // 只能走右键菜单重新新建一个同类模块）
            if (onAddClick != null)
            {
                var addMi = new MenuItem { Header = addMenuHeader };
                addMi.Click += (s, e) => onAddClick();
                module.AddCustomMenuItem(addMi);
            }

            // 将 actionMenu 的菜单项合并为自定义菜单项
            if (actionMenu != null)
            {
                var items = new List<UIElement>();
                foreach (var mi in actionMenu.Items)
                {
                    if (mi is MenuItem menuItem)
                    {
                        var copy = new MenuItem { Header = menuItem.Header };
                        // 复制点击事件（通过重新触发原始菜单项的 Click）
                        var original = menuItem;
                        copy.Click += (s, e) => original.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, original));
                        items.Add(copy);
                    }
                }
                if (items.Count > 0) module.AddCustomMenuItems(items);
            }

            module.SetContent(content);
            return module;
        }

        /// <summary>空状态提示文本：点击直接触发添加动作（透明背景覆盖整个区域，手型光标）</summary>
        private UIElement BuildEmptyAddTip(string text, Action onAdd, Thickness? margin = null)
        {
            var tip = new Emoji.Wpf.TextBlock
            {
                Text = text,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Background = Brushes.Transparent, // 命中整个区域而非仅文字笔画
                FontSize = 12,
                Opacity = 0.7,
                Margin = margin ?? new Thickness(2, 2, 0, 2),
                Cursor = Cursors.Hand
            };
            tip.MouseLeftButtonUp += (s, e) => onAdd();
            return tip;
        }

        /// <summary>创建 ModuleControl 实例并注入回调</summary>
        private ModuleControl CreateModuleControl(string defaultTitle, PanelItem item, bool groupMode = false)
        {
            var module = new ModuleControl(item, defaultTitle, _isEditMode, _configManager, groupMode)
            {
                OnRenderPanels = () => RenderPanels(),
                OnRemovePanel = (p) => RemovePanel(p),
                OnShowSettings = (p, t) => ShowPanelSettingsDialog(p, t),
                OnRenamePanel = (p, t) => PromptRenamePanel(p, t),
                // 窗口级引用分享限制下发到每个模块（与模块自身限制叠加）
                WindowEditBlocked = _currentWindowConfig?.IsShareReadOnly ?? false,
                WindowDeleteBlocked = _currentWindowConfig?.IsShareDeleteBlocked ?? false
            };

            // 模块级分享（已同步且有窗口 ServerId 时显示；引用导入的模块需分享者授予再分享权限）
            var app = (App)Application.Current;
            if (!string.IsNullOrEmpty(item.ServerId)
                && !string.IsNullOrEmpty(_currentWindowConfig?.ServerId)
                && !string.IsNullOrEmpty(app.AppSettings.AccountToken)
                && (item.ShareMode != "reference" || item.AllowReShare))
            {
                // Tag=keepWhenReadOnly：只读模块若允许再分享，菜单项不被禁用
                var shareMi = new MenuItem { Header = Framework.Lang.T("分享模块"), Tag = "keepWhenReadOnly" };
                shareMi.Click += (_, _) => ShareModule_Click(item);
                module.AddCustomMenuItem(shareMi);
            }

            return module;
        }

        /// <summary>构建「置顶显示」可勾选菜单项：勾选后模块固定渲染在滚动区上方的置顶区，压缩滚动区高度</summary>
        private MenuItem BuildPinMenuItem(PanelItem item)
        {
            var mi = new MenuItem
            {
                Header = Framework.Lang.T("置顶显示"),
                IsCheckable = true,
                IsChecked = item.IsPinned
            };
            mi.Click += (s, e) =>
            {
                item.IsPinned = mi.IsChecked;
                _configManager?.Save();
                RenderPanels();
            };
            return mi;
        }

        /// <summary>
        /// 将模块内容包入可约束高度的 ScrollViewer：
        /// 设置了 item.PanelHeight 时应用固定高度并内部滚动；未设置时为自动高度。
        /// 内容未溢出（无法滚动）时将滑轮事件转发给外层页面，避免吞掉页面滚动。
        /// </summary>
        private ScrollViewer BuildResizableHost(UIElement content, PanelItem item)
        {
            var host = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            };
            if (item.PanelHeight.HasValue && item.PanelHeight.Value > 0)
            {
                host.Height = item.PanelHeight.Value;
                // 固定高度时，让内容至少擑满可视区域：短内容填满整个模块，长内容仍可滚动
                if (content is FrameworkElement cfe)
                {
                    cfe.SetBinding(FrameworkElement.MinHeightProperty,
                        new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = host });
                }
            }

            host.PreviewMouseWheel += (s, e) =>
            {
                var sv = (ScrollViewer)s;
                if ((e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight) ||
                    (e.Delta > 0 && sv.VerticalOffset <= 0))
                {
                    e.Handled = true;
                    (sv.Parent as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                        Source = sv
                    });
                }
            };
            return host;
        }

        /// <summary>模块高度拖拽进行中标志：为 true 时抑制顶层面板的拖拽调序逻辑，避免二者冲突。</summary>
        private bool _resizingModule;

        /// <summary>
        /// 创建模块底部拖拽手柄：拖拽调整内容区高度并持久化到 item.PanelHeight；双击恢复自动高度。
        /// </summary>
        private Border CreateResizeGrip(ScrollViewer host, PanelItem item)
        {
            const double MinHeight = 48;
            var grip = new Border
            {
                Height = 6,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = Framework.Lang.T("拖拽调整模块高度（双击恢复自动高度）")
            };
            var line = new Border
            {
                Width = 34,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)FindResource("BrushTextMuted"),
                Opacity = 0.35
            };
            grip.Child = line;
            grip.MouseEnter += (s, e) => line.Opacity = 0.8;
            grip.MouseLeave += (s, e) => line.Opacity = 0.35;

            bool dragging = false, moved = false;
            double startY = 0, startHeight = 0;
            grip.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    host.ClearValue(FrameworkElement.HeightProperty);
                    // 同时清除内容的 MinHeight 绑定（固定高度时曾绑定到视口高度），否则模块仍被钉在原高度无法收缩
                    if (host.Content is FrameworkElement contentFe)
                        System.Windows.Data.BindingOperations.ClearBinding(contentFe, FrameworkElement.MinHeightProperty);
                    item.PanelHeight = null;
                    _configManager?.Save();
                    dragging = false;
                    _resizingModule = false;
                    grip.ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }
                dragging = true;
                moved = false;
                _resizingModule = true;
                startY = e.GetPosition(this).Y;
                startHeight = (double.IsNaN(host.Height) || host.Height <= 0) ? host.ActualHeight : host.Height;
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                double dy = e.GetPosition(this).Y - startY;
                if (!moved && Math.Abs(dy) < 3) return;  // 微小位移视为单击，不改变高度
                moved = true;
                double h = startHeight + dy;
                if (h < MinHeight) h = MinHeight;
                host.Height = h;
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                _resizingModule = false;
                grip.ReleaseMouseCapture();
                if (moved)  // 仅真实拖拽才持久化高度，避免单击误将高度固定
                {
                    item.PanelHeight = host.Height;
                    _configManager?.Save();
                }
                e.Handled = true;
            };
            grip.LostMouseCapture += (s, e) => { _resizingModule = false; };
            return grip;
        }

        /// <summary>
        /// 为未走 WrapWidget 的自定义卡片模块统一附加「可调整高度」能力：
        /// 优先将拖拽手柄放入卡片外框内部底部（与 WrapWidget 模块视觉一致，而非悬浮在卡片下方）；
        /// 编辑模式才显示手柄并允许拖拽。WrapWidget 已自带手柄，不在此重复包裹（通过 Uid 标记区分）。
        /// </summary>
        private UIElement WrapResizable(FrameworkElement widget, PanelItem item)
        {
            // 带固定标题栏的卡片（标题栏 Uid = panelHeader）：标题栏保持固定，仅标题栏下方到底部的内容参与滚动
            if (widget is Border headerCard && headerCard.Child is StackPanel ms
                && ms.Children.Count >= 1 && ms.Children[0] is FrameworkElement hdr && hdr.Uid == PanelHeaderUid)
            {
                // 拆分“标题栏”与“其余内容”
                var contentChildren = ms.Children.Cast<UIElement>().Skip(1).ToList();
                ms.Children.Clear();
                headerCard.Child = null;

                UIElement content;
                if (contentChildren.Count == 1)
                {
                    content = contentChildren[0];
                }
                else
                {
                    var cs = new StackPanel();
                    foreach (var c in contentChildren) cs.Children.Add(c);
                    content = cs;
                }

                var contentHost = BuildResizableHost(content, item);
                var outer = new StackPanel();
                outer.Children.Add(hdr);
                outer.Children.Add(contentHost);
                if (_isEditMode)
                    outer.Children.Add(CreateResizeGrip(contentHost, item));
                headerCard.Child = outer;
                return headerCard;
            }

            // 标准卡片：将卡片内容包入可约束高度的 ScrollViewer，手柄置于卡片外框内部底部
            if (widget is Border card && card.Child is UIElement inner)
            {
                card.Child = null; // 先解除与外框的父关系，再交给 ScrollViewer 托管
                var host = BuildResizableHost(inner, item);
                if (!_isEditMode)
                {
                    card.Child = host;
                    return card;
                }
                var innerStack = new StackPanel();
                innerStack.Children.Add(host);
                innerStack.Children.Add(CreateResizeGrip(host, item));
                card.Child = innerStack;
                return card;
            }

            // 回退：非标准外框卡片（如直接返回的原始内容），在顶层包裹
            var spacing = widget.Margin;
            widget.Margin = new Thickness(0);
            var outerHost = BuildResizableHost(widget, item);
            if (!_isEditMode)
            {
                outerHost.Margin = spacing;
                return outerHost;
            }
            var stack = new StackPanel { Margin = spacing };
            stack.Children.Add(outerHost);
            stack.Children.Add(CreateResizeGrip(outerHost, item));
            return stack;
        }

        private void RemovePanel(PanelItem item)
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            // 先从顶层面板列表删除
            if (_currentWindowConfig.Panels.Remove(item))
            {
                _configManager.Save();
                NotifyServerDeletePanel(item);
                RenderPanels();
                TryUnloadOrphanPlugin(item);
                RequestGCAfterRemoval();
                return;
            }

            // 再从分组的子面板列表中删除
            foreach (var group in _currentWindowConfig.Panels.Where(p => p.Type == PanelType.Group && p.ChildPanels != null))
            {
                if (group.ChildPanels!.Remove(item))
                {
                    _configManager.Save();
                    NotifyServerDeletePanel(item);
                    RenderPanels();
                    TryUnloadOrphanPlugin(item);
                    RequestGCAfterRemoval();
                    return;
                }
            }
        }

        /// <summary>
        /// 模块删除后通知服务端软删除对应 item（Group 类型由服务端连带删子项）。
        /// 旧版只删本地不通知服务端，残留的 item 会被结构对账“补齐缺失”重新带回本地，
        /// 造成已删除模块“复活”/导入副本重复。
        /// </summary>
        private static void NotifyServerDeletePanel(PanelItem item)
        {
            try
            {
                var app = (App)Application.Current;
                var settings = app.AppSettings;
                if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return;
                if (string.IsNullOrEmpty(item.ServerId)) return;
                _ = Services.CloudSyncService.Instance.DeleteItemOnServerAsync(item.ServerId!);
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[NativeModePanel] 通知服务端删除模块失败: {ex.Message}");
            }
        }

        /// <summary>模块级分享：创建分享链接（传入 itemServerId）</summary>
        private async void ShareModule_Click(PanelItem item)
        {
            var barServerId = _currentWindowConfig?.ServerId;

            // 如果模块还没有 ServerId，调用轻量接口创建
            if (string.IsNullOrEmpty(item.ServerId))
            {
                if (string.IsNullOrEmpty(barServerId))
                {
                    MessageBox.Show(Framework.Lang.T("窗口尚未同步，请先执行一次云同步。"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var newId = await Services.CloudSyncService.Instance.EnsureItemServerIdAsync(item, barServerId);
                if (string.IsNullOrEmpty(newId))
                {
                    MessageBox.Show(Framework.Lang.T("模块同步失败，无法创建分享。请确保已登录并开启云同步。"), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var itemServerId = item.ServerId;

            var dialog = new System.Windows.Window
            {
                Title = Framework.Lang.T("分享模块"),
                Width = 320,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var radioStyle = (Style)FindResource("DarkRadioButtonStyle");
            var checkStyle = (Style)FindResource("DarkCheckBoxStyle");

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("选择分享模式："),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            var copyRadio = new RadioButton { Content = Framework.Lang.T("复制（对方获得独立副本）"), Style = radioStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            var refRadio = new RadioButton { Content = Framework.Lang.T("引用（对方看到你的实时数据）"), Style = radioStyle, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(copyRadio);
            panel.Children.Add(refRadio);

            var permLabel = new TextBlock
            {
                Text = Framework.Lang.T("导入者权限："),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(permLabel);
            var cbEdit = new CheckBox { Content = Framework.Lang.T("允许编辑"), Style = checkStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            var cbDelete = new CheckBox { Content = Framework.Lang.T("允许删除"), Style = checkStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            var cbReShare = new CheckBox { Content = Framework.Lang.T("允许再分享"), Style = checkStyle, IsChecked = false, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(cbEdit);
            panel.Children.Add(cbDelete);
            panel.Children.Add(cbReShare);

            var confirmBtn = new Button
            {
                Content = Framework.Lang.T("创建分享链接"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand
            };
            panel.Children.Add(confirmBtn);
            dialog.Content = panel;

            // 分享模式与权限联动：
            // 复制模式 —— 对方获得独立副本，权限固定为全部允许，整个权限区隐藏；
            // 引用模式 —— 显示权限区，逐项可配，默认全部不选中
            void ApplyShareMode(bool isCopy)
            {
                var vis = isCopy ? Visibility.Collapsed : Visibility.Visible;
                permLabel.Visibility = cbEdit.Visibility = cbDelete.Visibility = cbReShare.Visibility = vis;
                cbEdit.IsChecked = cbDelete.IsChecked = cbReShare.IsChecked = isCopy;
            }
            copyRadio.Checked += (_, _) => ApplyShareMode(true);
            refRadio.Checked += (_, _) => ApplyShareMode(false);
            ApplyShareMode(true);

            confirmBtn.Click += async (_, _) =>
            {
                string mode = copyRadio.IsChecked == true ? "copy" : "reference";
                try
                {
                    var app = (App)Application.Current;
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        barServerId,
                        itemServerId,
                        shareMode = mode,
                        allowEdit = cbEdit.IsChecked == true,
                        allowDelete = cbDelete.IsChecked == true,
                        allowReShare = cbReShare.IsChecked == true
                    });
                    using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, Constants.URLs.ServerBaseUrl + "/api/sidebar/share/create") { Content = content };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                    using var resp = await Services.HttpLog.SendAsync(http, req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root["code"]?.ToObject<int>() == 200)
                    {
                        string code = root["data"]?["shareCode"]?.ToString() ?? "";
                        dialog.Close();
                        ThemedMessageBox.ShowShareResult(
                            Application.Current.MainWindow,
                            code,
                            $"{Constants.URLs.FrontendBaseUrl}/s/{code}"
                        );
                    }
                    else
                    {
                        MessageBox.Show(root["msg"]?.ToString() ?? Framework.Lang.T("创建分享失败"), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Framework.Lang.TF("创建分享失败: {0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>删除插件面板后，若所有窗口配置中均无该插件的引用，则卸载 DLL 释放内存。</summary>
        private void TryUnloadOrphanPlugin(PanelItem removedItem)
        {
            if (removedItem.Type != PanelType.Plugin || string.IsNullOrEmpty(removedItem.PluginId))
                return;

            var pluginId = removedItem.PluginId;

            // 遍历所有窗口配置，检查是否还有其他面板引用同一插件
            bool stillReferenced = _configManager!.Windows.Any(wc =>
                wc.Panels.Any(p => IsPluginReference(p, pluginId)));

            if (!stillReferenced)
            {
                Services.Plugins.PluginLoaderService.Instance.UnloadPlugin(pluginId);
                Debug.WriteLine($"[NativeModePanel] 插件 {pluginId} 无剩余引用，已卸载 DLL");

                // AssemblyLoadContext.Unload() 是异步的，实际需要 Gen2 GC 才能回收 ALC 及其程序集。
                // 延迟执行避免阻塞 UI；必须 Gen2 + WaitForPendingFinalizers + 二次 Collect。
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(500);
                    GC.Collect(2, GCCollectionMode.Forced);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced);

                    // 修剪工作集，让 Task Manager 能看到内存下降（OS 否则不会立即回收物理页）
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetCurrentProcess();
                        NativeMethods.SetProcessWorkingSetSize(proc.Handle, -1, -1);
                    }
                    catch { /* ignore */ }

                    Debug.WriteLine($"[NativeModePanel] 插件 {pluginId} ALC 回收完成");
                });
            }
        }

        private static bool IsPluginReference(PanelItem p, string pluginId)
        {
            if (p.Type == PanelType.Plugin && p.PluginId == pluginId)
                return true;
            // 分组内的子面板也要检查
            if (p.Type == PanelType.Group && p.ChildPanels != null)
                return p.ChildPanels.Any(c => c.Type == PanelType.Plugin && c.PluginId == pluginId);
            return false;
        }

        /// <summary>模块删除后延迟触发 GC，回收大对象内存（图片/位图等）。
        /// 使用延迟避免阻塞 UI；仅收集 Gen0/Gen1 以降低性能影响。</summary>
        private static void RequestGCAfterRemoval()
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Thread.Sleep(500); // 等待 Unloaded 事件处理完毕
                GC.Collect(1, GCCollectionMode.Optimized);
            });
        }

        /// <summary>弹出对话框修改面板/分组标题</summary>
        private void PromptRenamePanel(PanelItem item, string defaultTitle)
        {
            var current = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;
            var dialog = new RenameDialog(Framework.Lang.T("修改名称"), current)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                var newTitle = dialog.NewName;
                item.Title = string.IsNullOrEmpty(newTitle) ? null : newTitle;
                _configManager?.Save();
                RenderPanels();
            }
        }

        #endregion

        #region Panel Builders

        /// <summary>构建分组容器：可展开/收起、右键添加模块</summary>
        private UIElement BuildGroupWidget(PanelItem item)
        {
            item.ChildPanels ??= new List<PanelItem>();

            // 基于 ModuleControl 基础组件（Group 模式：无 ScrollViewer、无 resize、自定义菜单）
            var module = CreateModuleControl(Framework.Lang.T("分组"), item, groupMode: true);
            module.BGColorOverride = item.BGColor ?? item.GroupBGColor;
            module.SetMenuButtonContent("⋯");
            module.SetHeaderMargin(new Thickness(0, 0, 0, item.IsExpanded ? 6 : 0));

            // 展开/收起箭头按钮
            var arrowBtn = new Button
            {
                Content = item.IsExpanded ? "▼" : "▶",
                Width = 16, Height = 16,
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                FontSize = 9,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand
            };
            module.InsertHeaderLeft(arrowBtn);

            // 内容区（子面板）：高度完全由内部组件撑开，分组自身不设固定高度、不加滚动条
            var contentPanel = new StackPanel
            {
                Visibility = item.IsExpanded ? Visibility.Visible : Visibility.Collapsed
            };

            // 右键菜单：添加模块到分组
            var groupContextMenu = new ContextMenu();
            int pendingGroupInsertIdx = -1; // 右键位置对应的插入索引
            groupContextMenu.Opened += (s, ev) =>
            {
                // 根据鼠标在 contentPanel 中的 Y 坐标计算插入位置
                var pos = Mouse.GetPosition(contentPanel);
                double y = 0;
                pendingGroupInsertIdx = 0;
                foreach (UIElement child in contentPanel.Children)
                {
                    if (child is not FrameworkElement cfe) continue;
                    double h = cfe.ActualHeight + cfe.Margin.Top + cfe.Margin.Bottom;
                    if (pos.Y < y + h / 2) break;
                    y += h;
                    pendingGroupInsertIdx++;
                }
            };
            var addSubMenu = new MenuItem { Header = Framework.Lang.T("添加模块") };
            foreach (var (header, type) in new (string, PanelType)[]
            {
                (Framework.Lang.T("快捷方式"), PanelType.QuickGroup),
                (Framework.Lang.T("便签"), PanelType.StickyNotes),
                (Framework.Lang.T("今日待办"), PanelType.TodoList),
                (Framework.Lang.T("剪贴板收藏"), PanelType.Clipboard),
                (Framework.Lang.T("系统信息"), PanelType.SystemMonitor),
                (Framework.Lang.T("使用统计"), PanelType.UsageStats),
                (Framework.Lang.T("AI 对话"), PanelType.AIChat),
                (Framework.Lang.T("文件区"), PanelType.FileArea),
                (Framework.Lang.T("数据看板"), PanelType.DataDashboard),
                (Framework.Lang.T("计时器"), PanelType.Timer),
                (Framework.Lang.T("倒计时"), PanelType.Countdown),
                (Framework.Lang.T("番茄闹钟"), PanelType.Pomodoro),
                (Framework.Lang.T("信号灯"), PanelType.SignalLight),
                (Framework.Lang.T("图片轮播"), PanelType.ImageCarousel),
                (Framework.Lang.T("每日活跃"), PanelType.DailyActivity),
                (Framework.Lang.T("日历"), PanelType.Calendar),
                (Framework.Lang.T("好友"), PanelType.Friend),
                (Framework.Lang.T("屏幕时长"), PanelType.ScreenTime)
            })
            {
                var mi = new MenuItem { Header = header };
                var panelType = type;
                mi.Click += (s, ev) => AddModuleToGroup(item, panelType, pendingGroupInsertIdx);
                addSubMenu.Items.Add(mi);
            }
            // 插件子菜单（分组内添加插件）
            var groupPluginSub = new MenuItem { Header = Framework.Lang.T("插件") };
            groupPluginSub.Items.Add(new MenuItem { Header = Framework.Lang.T("(加载中...)"), IsEnabled = false }); // 占位项，让 WPF 识别为子菜单并显示箭头
            groupPluginSub.SubmenuOpened += (s, ev) =>
            {
                groupPluginSub.Items.Clear();
                var plugins = PluginLoaderService.Instance.Plugins;
                if (plugins.Count == 0)
                {
                    groupPluginSub.Items.Add(new MenuItem { Header = Framework.Lang.T("(无已加载插件)"), IsEnabled = false });
                    return;
                }
                foreach (var p in plugins)
                {
                    var pmi = new MenuItem { Header = $"{p.Config.Icon} {p.Config.Name}" };
                    var pid = p.Config.Id;
                    var capturedGroup = item;
                    pmi.Click += (s2, ev2) =>
                    {
                        InsertChildPanel(capturedGroup, new PanelItem
                        {
                            Type = PanelType.Plugin,
                            PluginId = pid,
                            Title = $"{p.Config.Icon} {p.Config.Name}"
                        }, pendingGroupInsertIdx);
                        _configManager?.Save();
                        RenderPanels();
                    };
                    groupPluginSub.Items.Add(pmi);
                }
            };
            addSubMenu.Items.Add(new Separator());
            addSubMenu.Items.Add(groupPluginSub);
            // 添加数据块：子菜单展开时加载设置中的数据项（模版）
            var groupDataBlockSub = new MenuItem { Header = Framework.Lang.T("添加数据块") };
            groupDataBlockSub.Items.Add(new MenuItem { Header = Framework.Lang.T("(加载中...)"), IsEnabled = false });
            var capturedGroup2 = item;
            groupDataBlockSub.SubmenuOpened += (s, ev) =>
                PopulateDataBlockMenu(groupDataBlockSub, tpl => AddDataBlockToGroup(capturedGroup2, tpl, pendingGroupInsertIdx), () =>
                {
                    var customItem = ShowAddDashboardItemDialog();
                    if (customItem != null)
                    {
                        InsertChildPanel(capturedGroup2, new PanelItem
                        {
                            Type = PanelType.DataDashboard,
                            DashboardItems = new List<DataDashboardItem> { customItem }
                        }, pendingGroupInsertIdx);
                        _configManager?.Save();
                        if (_configManager != null) Services.DataPollingService.Instance.SyncFromConfigs(_configManager);
                        RenderPanels();
                    }
                });
            addSubMenu.Items.Add(groupDataBlockSub);
            groupContextMenu.Items.Add(addSubMenu);
            groupContextMenu.Items.Add(new Separator());
            var editGroupCtxMi = new MenuItem { Header = Framework.Lang.T("编辑分组") };
            editGroupCtxMi.Click += (s, ev) => ShowPanelSettingsDialog(item, Framework.Lang.T("分组"));
            groupContextMenu.Items.Add(editGroupCtxMi);
            var copyGroupCtxMi = new MenuItem { Header = Framework.Lang.T("复制分组") };
            copyGroupCtxMi.Click += (s, ev) => DuplicateGroup(item);
            groupContextMenu.Items.Add(copyGroupCtxMi);
            groupContextMenu.Items.Add(BuildPinMenuItem(item));
            var deleteGroupItem = new MenuItem { Header = Framework.Lang.T("删除分组") };
            deleteGroupItem.Click += (s, ev) => ConfirmRemoveGroup(item);
            groupContextMenu.Items.Add(deleteGroupItem);
            module.SetModuleContextMenu(groupContextMenu);
            module.Header.ContextMenu = groupContextMenu;

            // 填充子面板内容
            foreach (var child in item.ChildPanels)
            {
                if (child.IsPinned) continue; // 已置顶的子面板由 RenderPanels 提取到置顶区渲染
                var widget = CreatePanelWidget(child);
                if (widget != null) contentPanel.Children.Add(widget);
            }

            // 展开/收起事件
            arrowBtn.Click += (s, e) =>
            {
                item.IsExpanded = !item.IsExpanded;
                contentPanel.Visibility = item.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                arrowBtn.Content = item.IsExpanded ? "▼" : "▶";
                module.SetHeaderMargin(new Thickness(0, 0, 0, item.IsExpanded ? 6 : 0));
                _configManager?.Save();
            };

            module.SetContent(contentPanel);
            return module;
        }

        /// <summary>模块/分组设置对话框：修改标题和背景色</summary>
        private void ShowPanelSettingsDialog(PanelItem item, string defaultTitle)
        {
            bool isGroup = item.Type == PanelType.Group;
            var dialog = new Window
            {
                Title = isGroup ? Framework.Lang.T("分组设置") : Framework.Lang.T("模块设置"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(16) };

            // 名称
            root.Children.Add(new TextBlock
            {
                Text = isGroup ? Framework.Lang.T("分组名称") : Framework.Lang.T("模块名称"),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var titleBox = new EmojiTextBox
            {
                // 未自定义标题的模块 Title 为 null，回退显示默认标题（与模块上实际显示的一致），避免空名称
                Text = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title,
                Margin = new Thickness(0, 0, 0, 14)
            };
            root.Children.Add(titleBox);

            // 归属选择（个人 / 团队）
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("归属"),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var ownerCombo = new ComboBox
            {
                Style = (Style)FindResource("GlobalComboBoxStyle"),
                Margin = new Thickness(0, 0, 0, 14)
            };
            ownerCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("个人"), Tag = null });
            ownerCombo.SelectedIndex = 0;
            root.Children.Add(ownerCombo);

            // 归属提示文本（团队共享说明）
            var ownerHint = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, -10, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(ownerHint);

            // 异步加载团队列表
            string? pendingTeamId = item.OwnerType == "team" ? item.TeamId : null;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var app = (App)Application.Current;
                if (string.IsNullOrEmpty(app.AppSettings.AccountToken)) return; // 未登录只有个人

                var teams = await Services.TeamService.Instance.ListTeamsAsync();
                if (teams == null) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var t in teams)
                        ownerCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });

                    // 编辑模式恢复原团队选中
                    if (!string.IsNullOrEmpty(pendingTeamId))
                    {
                        for (int i = 1; i < ownerCombo.Items.Count; i++)
                        {
                            if ((ownerCombo.Items[i] as ComboBoxItem)?.Tag as string == pendingTeamId)
                            { ownerCombo.SelectedIndex = i; break; }
                        }
                    }
                });
            });

            ownerCombo.SelectionChanged += (s, e) =>
            {
                var selected = ownerCombo.SelectedItem as ComboBoxItem;
                bool isTeam = !string.IsNullOrEmpty(selected?.Tag as string);
                ownerHint.Text = isTeam
                    ? Framework.Lang.T("团队可见：团队其他成员可从团队组件库添加此模块，数据共享互通")
                    : "";
            };

            // 背景色
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("背景色"),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            string? selectedColor = isGroup ? (item.BGColor ?? item.GroupBGColor) : item.BGColor;
            Border? selectedSwatch = null;
            var accentBrush = (Brush)FindResource("BrushAccent");
            var colorPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

            // “默认”选项（跟随主题）
            var defaultSwatch = new Border
            {
                Width = 28, Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = selectedColor == null ? accentBrush : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = Framework.Lang.T("默认"), FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("BrushTextMuted")
                }
            };
            if (selectedColor == null) selectedSwatch = defaultSwatch;
            defaultSwatch.MouseLeftButtonUp += (s, e) =>
            {
                selectedColor = null;
                if (selectedSwatch != null) selectedSwatch.BorderBrush = Brushes.Transparent;
                selectedSwatch = defaultSwatch;
                defaultSwatch.BorderBrush = accentBrush;
            };
            colorPanel.Children.Add(defaultSwatch);

            var presetColors = new[]
            {
                "#1E293B", "#2D3748", "#374151", "#3B4252",
                "#1A3A5C", "#1C4532", "#4A1D1D", "#4C1D95",
                "#7C2D12", "#164E63", "#3F6212", "#701A75"
            };
            foreach (var c in presetColors)
            {
                var swatch = new Border
                {
                    Width = 28, Height = 28,
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(4),
                    Background = (SolidColorBrush)new BrushConverter().ConvertFrom(c)!,
                    BorderBrush = string.Equals(c, selectedColor, StringComparison.OrdinalIgnoreCase) ? accentBrush : Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand,
                    Tag = c
                };
                if (string.Equals(c, selectedColor, StringComparison.OrdinalIgnoreCase)) selectedSwatch = swatch;
                swatch.MouseLeftButtonUp += (s, e) =>
                {
                    selectedColor = (string)swatch.Tag!;
                    if (selectedSwatch != null) selectedSwatch.BorderBrush = Brushes.Transparent;
                    selectedSwatch = swatch;
                    swatch.BorderBrush = accentBrush;
                };
                colorPanel.Children.Add(swatch);
            }
            root.Children.Add(colorPanel);

            // 自定义颜色输入
            var customDock = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            customDock.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("自定义："), VerticalAlignment = VerticalAlignment.Center, FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextSec")
            });
            var customBox = new TextBox
            {
                Text = selectedColor ?? "",
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                FontSize = 11, Width = 100
            };
            customDock.Children.Add(customBox);
            var applyColorBtn = new Button
            {
                Content = Framework.Lang.T("应用"), FontSize = 10, Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)FindResource("GhostButtonStyle")
            };
            applyColorBtn.Click += (s, e) =>
            {
                var txt = customBox.Text.Trim();
                if (string.IsNullOrEmpty(txt)) return;
                try
                {
                    new BrushConverter().ConvertFrom(txt);
                    selectedColor = txt;
                    if (selectedSwatch != null) selectedSwatch.BorderBrush = Brushes.Transparent;
                    selectedSwatch = null;
                }
                catch { MessageBox.Show(Framework.Lang.T("无效的颜色值"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            customDock.Children.Add(applyColorBtn);
            root.Children.Add(customDock);

            // 按钮区
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("取消"), Width = 75, MinHeight = 30, Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("StandardButtonStyle")
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            var saveBtn = new Button
            {
                Content = Framework.Lang.T("保存"), Width = 75, MinHeight = 30, IsDefault = true,
                Style = (Style)FindResource("StandardButtonStyle")
            };
            saveBtn.Click += async (s, e) =>
            {
                item.Title = string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text.Trim();
                item.BGColor = selectedColor;

                // 归属变更检测与实时更新
                var ownerItem = ownerCombo.SelectedItem as ComboBoxItem;
                string? newTeamId = ownerItem?.Tag as string;
                string newOwnerType = string.IsNullOrEmpty(newTeamId) ? "personal" : "team";
                string? newTeamName = string.IsNullOrEmpty(newTeamId) ? null : ownerItem?.Content as string;

                bool ownerChanged = item.OwnerType != newOwnerType || item.TeamId != newTeamId;
                if (ownerChanged)
                {
                    item.OwnerType = newOwnerType;
                    item.TeamId = newTeamId;
                    item.TeamName = newTeamName;
                }
                // 团队模块每次保存都对齐服务端（修复历史脏数据：本地已是团队但服务器 corp_id 仍为空）；
                // 个人模块仅在归属变化时推送；分组内子模块不在此补创建，留给全量同步
                bool needSync = ownerChanged || item.OwnerType == "team";
                if (needSync)
                {
                    var isTopLevel = _currentWindowConfig?.Panels.Contains(item) == true;
                    var syncResult = await Services.CloudSyncService.Instance.UpdateItemOwnershipAsync(item,
                        isTopLevel ? _currentWindowConfig : null);
                    if (syncResult == false)
                    {
                        // 同步失败不阻断本地保存（下次全量同步会重试），但明确提示用户
                        var err = Services.CloudSyncService.Instance.LastOwnershipError;
                        MessageBox.Show(Framework.Lang.TF("归属已保存到本地，但同步服务器失败：\n{0}\n\n下次云同步时会再次尝试。", err),
                            Framework.Lang.T("同步提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                _configManager?.Save();
                RenderPanels();
                dialog.Close();
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        /// <summary>构建“模块设置”菜单项</summary>
        private MenuItem BuildSettingsMenuItem(PanelItem item, string defaultTitle)
        {
            var mi = new MenuItem { Header = Framework.Lang.T("模块设置") };
            mi.Click += (s, e) => ShowPanelSettingsDialog(item, defaultTitle);
            return mi;
        }

        /// <summary>复制分组：深拷贝分组及其子面板，插入到原分组后方</summary>
        private void DuplicateGroup(PanelItem item)
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(item);
            var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<PanelItem>(json);
            if (clone == null) return;

            RegeneratePanelIds(clone);
            clone.Title = (string.IsNullOrEmpty(item.Title) ? Framework.Lang.T("分组") : item.Title) + " 副本";

            var idx = _currentWindowConfig.Panels.IndexOf(item);
            _currentWindowConfig.Panels.Insert(idx >= 0 ? idx + 1 : _currentWindowConfig.Panels.Count, clone);
            _configManager.Save();
            RenderPanels();
        }

        /// <summary>递归重新生成面板 Id，避免复制后 Id 冲突</summary>
        private static void RegeneratePanelIds(PanelItem p)
        {
            p.Id = Guid.NewGuid().ToString();
            if (p.ChildPanels != null)
                foreach (var c in p.ChildPanels) RegeneratePanelIds(c);
        }

        /// <summary>删除分组前弹出确认提示</summary>
        private void ConfirmRemoveGroup(PanelItem item)
        {
            var name = string.IsNullOrEmpty(item.Title) ? Framework.Lang.T("分组") : item.Title;
            var count = item.ChildPanels?.Count ?? 0;
            var msg = count > 0
                ? Framework.Lang.TF("确定删除分组「{0}」吗？\n该分组内含有 {1} 个模块，将一并删除。", name, count)
                : Framework.Lang.TF("确定删除分组「{0}」吗？", name);
            if (ShowConfirmDialog(Framework.Lang.T("删除分组"), msg))
                RemovePanel(item);
        }

        /// <summary>主题风格确认弹框，返回 true 表示用户点击了“确定”</summary>
        private bool ShowConfirmDialog(string title, string message, string okText = null, string cancelText = null)
        {
            okText ??= Framework.Lang.T("确定");
            cancelText ??= Framework.Lang.T("取消");
            var dialog = new Window
            {
                Title = title,
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 20)
            });

            bool result = false;
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button
            {
                Content = cancelText, Width = 75, MinHeight = 30, Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("StandardButtonStyle"),
                IsCancel = true
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            var okBtn = new Button
            {
                Content = okText, Width = 75, MinHeight = 30, IsDefault = true,
                Style = (Style)FindResource("StandardButtonStyle")
            };
            okBtn.Click += (s, e) => { result = true; dialog.Close(); };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
            return result;
        }

        private UIElement BuildQuickGroupWidget(PanelItem item)
        {
            // 快捷项数据为面板自有（原 quickgroups.json 已一次性迁入面板），每个快捷项走行级存储
            item.QuickItems ??= new List<QuickItem>();

            var container = new StackPanel();
            _groupPanel = new StackPanel();
            container.Children.Add(_groupPanel);

            // 初始化 QuickGroupUIService（同窗口存在多个快捷面板时，
            // 释放上一个面板服务的计时器，避免泄漏）
            _quickGroupUIService?.StopWatchers();
            _quickGroupUIService = new QuickGroupUIService(_groupPanel);
            _quickGroupUIService.OwnerWindow = _mainWindow;
            _quickGroupUIService.OwnerWindowConfig = _currentWindowConfig;

            // 分组删除时门面已移除对应面板，这里只需保存并重绘
            _quickGroupUIService.OnGroupDeleted = (gid) =>
            {
                _configManager?.Save();
                RenderPanels();
            };

            _quickGroupUIService.LoadSingleGroup(item.Id);
            return container;
        }

        private UIElement BuildStickyNotesWidget(PanelItem item)
        {
            // 便签数据 per-panel 存储：新建/导入的模块一律从空列表开始。
            // 旧版全局库（sticky-notes.json）已在升级后首次启动时由 WindowConfigManager.Load 一次性迁入遗留模块，
            // 不能在此处再播种，否则导入分享得到的新模块会被全局遗留数据污染
            item.StickyNotes ??= new List<StickyNote>();

            var container = new StackPanel();

            // 便签列表
            var notePanel = new StackPanel();
            container.Children.Add(notePanel);

            // 渲染便签（空列表时点击提示可直接添加）
            RefreshPanelStickyNotes(notePanel, item, AddNote);

            // 添加便签逻辑（用于 WrapWidget 的 onAddClick 回调）
            void AddNote()
            {
                // 新建便签不立即写入存储：输入内容或显式保存时才正式提交记录（避免留下空便签）
                var note = new StickyNote();
                OpenPanelStickyNoteEditor(note, notePanel, item, n => item.StickyNotes.Insert(0, n));
            }

            return WrapWidget(Framework.Lang.T("📝 便签"), container, item, _isEditMode ? null : AddNote);
        }

        private UIElement BuildClipboardWidget(PanelItem item)
        {
            // 确保数据列表存在
            item.ClipboardItems ??= new List<ClipboardSavedItem>();

            var container = new StackPanel();

            // 已收藏列表
            if (item.ClipboardItems.Count == 0)
            {
                container.Children.Add(BuildEmptyAddTip(Framework.Lang.T("（暂无收藏，点击添加）"), () => ShowClipboardPickDialog(item)));
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

                    // 右键菜单：删除收藏项（无需进入编辑模式）
                    var clipMenu = new ContextMenu();
                    var clipDelItem = new MenuItem { Header = Framework.Lang.T("删除") };
                    var capturedClip = clip;
                    clipDelItem.Click += (s, ev) =>
                    {
                        item.ClipboardItems.RemoveAll(c => c.Id == capturedClip.Id);
                        _configManager?.Save();
                        RenderPanels();
                    };
                    clipMenu.Items.Add(clipDelItem);
                    var clipHideMi = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = item.HideFrame };
                    clipHideMi.Click += (s, ev) => { item.HideFrame = clipHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
                    clipMenu.Items.Add(clipHideMi);
                    itemPanel.ContextMenu = clipMenu;

                    // 删除按钮
                    var delBtn = new Button
                    {
                        Content = "✕", Width = 20, Height = 20, FontSize = 10,
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand, ToolTip = Framework.Lang.T("删除"),
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
                        Cursor = Cursors.Hand, ToolTip = Framework.Lang.T("复制到剪贴板"),
                        Visibility = Visibility.Collapsed
                    };
                    DockPanel.SetDock(copyBtn, Dock.Right);
                    itemPanel.Children.Add(copyBtn);

                    // 内容预览
                    string displayText = clip.Content;
                    int newline = displayText.IndexOfAny(new char[] { '\n', '\r' });
                    if (newline >= 0) displayText = displayText.Substring(0, newline) + "…";
                    if (displayText.Length > 40) displayText = displayText.Substring(0, 40) + "…";

                    var preview = new Emoji.Wpf.TextBlock
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

            var favModule = WrapWidget(Framework.Lang.T("📋 剪贴板收藏"), container, item, () => ShowClipboardPickDialog(item));

            // 拖拽收藏：从剪贴板历史弹窗拖条目到本模块即添加收藏
            favModule.AllowDrop = true;
            favModule.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(ClipboardDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            favModule.Drop += (s, e) =>
            {
                if (e.Data.GetData(ClipboardDragFormat) is ClipboardEntry entry)
                {
                    AddClipboardFavoriteFromEntry(item, entry);
                    e.Handled = true;
                }
            };
            return favModule;
        }

        /// <summary>将剪贴板历史条目添加为收藏（与拾取对话框同一数据模型）</summary>
        private void AddClipboardFavoriteFromEntry(PanelItem item, ClipboardEntry entry)
        {
            item.ClipboardItems ??= new List<ClipboardSavedItem>();
            string type = entry.ContentType switch
            {
                ClipboardContentType.Image => "Image",
                ClipboardContentType.File => "File",
                _ => "Text"
            };
            string content = entry.Text;
            if (type == "File" && entry.FilePaths != null && entry.FilePaths.Count > 0)
                content = string.Join("\n", entry.FilePaths);
            if (string.IsNullOrWhiteSpace(content)) return;
            item.ClipboardItems.Add(new ClipboardSavedItem { Content = content, ContentType = type });
            _configManager?.Save();
            RenderPanels();
        }

        /// <summary>弹出对话框列出当前剪贴板内容与历史记录，支持多选后批量收藏</summary>
        private void ShowClipboardPickDialog(PanelItem item)
        {
            item.ClipboardItems ??= new List<ClipboardSavedItem>();

            var candidates = new List<ClipboardSavedItem>();
            var seen = new HashSet<string>();

            void AddCandidate(string content, string type)
            {
                if (string.IsNullOrWhiteSpace(content)) return;
                if (!seen.Add(type + "|" + content)) return;
                candidates.Add(new ClipboardSavedItem { Content = content, ContentType = type });
            }

            // 1. 当前系统剪贴板
            try
            {
                if (Clipboard.ContainsText())
                {
                    AddCandidate(Clipboard.GetText(), "Text");
                }
                else if (Clipboard.ContainsImage())
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                        AddCandidate(Framework.Lang.TF("[图片 {0}x{1}]", image.PixelWidth, image.PixelHeight), "Image");
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    AddCandidate(string.Join("\n", files.Cast<string>()), "File");
                }
            }
            catch { }

            // 2. 剪贴板历史记录
            EnsureClipboardServiceStarted();
            if (_clipboardService != null)
            {
                foreach (var entry in _clipboardService.ClipboardHistory)
                {
                    string type = entry.ContentType switch
                    {
                        ClipboardContentType.Image => "Image",
                        ClipboardContentType.File => "File",
                        _ => "Text"
                    };
                    string content = entry.Text;
                    if (type == "File" && entry.FilePaths != null && entry.FilePaths.Count > 0)
                        content = string.Join("\n", entry.FilePaths);
                    AddCandidate(content, type);
                }
            }

            var dialog = new ClipboardPickDialog(candidates) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                foreach (var sel in dialog.SelectedItems)
                {
                    item.ClipboardItems.Add(new ClipboardSavedItem
                    {
                        Content = sel.Content,
                        ContentType = sel.ContentType
                    });
                }
                _configManager?.Save();
                RenderPanels();
            }
        }

        private UIElement BuildSystemMonitorWidget(PanelItem item)
        {
            var container = new StackPanel();
            CreateKeyCountPanel(container);
            CreateMouseDistancePanel(container);
            return WrapWidget(Framework.Lang.T("💻 系统信息"), container, item);
        }

        private UIElement BuildUsageStatsWidget(PanelItem item)
        {
            var container = new StackPanel();
            var btn = new Button
            {
                Content = Framework.Lang.T("📊 查看使用统计"),
                Height = 28,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => UsageStatsButton_Click(s, e);
            container.Children.Add(btn);

            var taskbarBtn = new Button
            {
                Content = Framework.Lang.T("📌 任务栏标记"),
                Height = 28,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };
            taskbarBtn.Click += (s, e) => TaskbarOverlayButton_Click(s, e);
            container.Children.Add(taskbarBtn);

            return WrapWidget(Framework.Lang.T("📊 使用统计"), container, item);
        }

        private UIElement BuildAIChatWidget(PanelItem item)
        {
            var btn = new Button
            {
                Content = Framework.Lang.T("🤖 打开AI对话"),
                Height = 28,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => AIChatButton_Click(s, e);
            return WrapWidget(Framework.Lang.T("🤖 AI 对话"), btn, item);
        }

        private UIElement BuildTodoWidget(PanelItem item)
        {
            item.TodoItems ??= new List<TodoItem>();

            var container = new StackPanel();

            // 待办列表：未完成在前，已完成在后
            foreach (var todo in item.TodoItems.OrderBy(t => t.IsDone))
            {
                container.Children.Add(CreateTodoRow(todo, item));
            }

            // 空列表提示：点击直接弹出添加待办对话框
            if (item.TodoItems.Count == 0)
                container.Children.Add(BuildEmptyAddTip(Framework.Lang.T("暂无待办，点击添加"), () => ShowTodoDialog(item)));

            if (item.SkipWrapper)
                return container;

            // 标题栏「+」弹出添加待办对话框
            return WrapWidget(Framework.Lang.T("今日待办"), container, item, () => ShowTodoDialog(item));
        }

        #region 信号灯面板

        /// <summary>根据样式创建灯形状控件（lit 为考虑变量条件后的实际点亮状态）</summary>
        private FrameworkElement CreateLightShape(SignalLightItem light, bool lit)
        {
            var size = light.Size;
            var shape = light.LightStyle switch
            {
                "Square" => (System.Windows.Shapes.Shape)new System.Windows.Shapes.Rectangle
                {
                    Width = size, Height = size,
                    RadiusX = size * 0.15, RadiusY = size * 0.15
                },
                "Pixel" => new System.Windows.Shapes.Rectangle
                {
                    Width = size, Height = size,
                    RadiusX = 0, RadiusY = 0
                },
                _ => new System.Windows.Shapes.Ellipse
                {
                    Width = size, Height = size
                }
            };
            ApplyLightVisual(shape, light, lit);

            shape.Cursor = Cursors.Hand;
            shape.MouseLeftButtonUp += (s, e) =>
            {
                light.IsOn = !light.IsOn;
                _configManager?.Save();
                // 重新渲染整个面板
                if (shape.Parent is Panel parentPanel)
                {
                    // 通过重新调用 RenderPanels 来刷新
                    RenderPanels();
                }
            };

            var lightMenu = new ContextMenu();
            // 右键单个灯：编辑灯信息 / 删除该灯
            var detailMi = new MenuItem { Header = Framework.Lang.T("编辑") };
            detailMi.Click += (s, e) =>
            {
                ShowSignalLightDetailDialog(light);
                _configManager?.Save();
                RenderPanels();
            };
            lightMenu.Items.Add(detailMi);
            var delLightMi = new MenuItem { Header = Framework.Lang.T("删除") };
            delLightMi.Click += (s, e) =>
            {
                var owner = FindSignalLightOwner(light);
                owner?.SignalLights?.Remove(light);
                _configManager?.Save();
                RenderPanels();
            };
            lightMenu.Items.Add(delLightMi);
            lightMenu.Items.Add(new Separator());
            var editMi = new MenuItem { Header = Framework.Lang.T("设置信号灯") };
            editMi.Click += (s, e) =>
            {
                var owner = FindSignalLightOwner(light);
                if (owner != null) ShowSignalLightEditDialog(owner);
            };
            lightMenu.Items.Add(editMi);
            shape.ContextMenu = lightMenu;

            return shape;
        }

        /// <summary>设置灯形状的点亮/熄灭视觉（填充色 + 发光）</summary>
        private static void ApplyLightVisual(System.Windows.Shapes.Shape shape, SignalLightItem light, bool lit)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(lit ? light.Color : "#333333");
            shape.Fill = new SolidColorBrush(color);
            shape.Effect = lit
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = color,
                    BlurRadius = light.Size * 0.4,
                    ShadowDepth = 0,
                    Opacity = 0.7
                }
                : null;
        }

        /// <summary>判断单个信号灯条件是否满足：Op = 等于/大于/小于/不存在；EQ/GT/LT 在变量缺失时不满足</summary>
        private static bool SignalCondMet(SignalLightCondition c)
        {
            if (string.IsNullOrEmpty(c.VarKey)) return false;
            var src = Services.DataPollingService.Instance.FindItemByVarKey(c.VarKey);
            var missing = src == null || string.IsNullOrEmpty(src.Value) || src.Value == "--";
            if (string.Equals(c.Op, "MISSING", StringComparison.OrdinalIgnoreCase)) return missing;
            if (missing) return false;
            var val = src.Value.Trim();
            var expect = (c.Value ?? "").Trim();
            if (string.Equals(c.Op, "GT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Op, "LT", StringComparison.OrdinalIgnoreCase))
            {
                // 数值比较优先（值文本中提取数字），非数值回退字符串序比较
                var a = ExtractNumber(val);
                var b = ExtractNumber(expect);
                var cmp = a != null && b != null
                    ? a.Value.CompareTo(b.Value)
                    : string.CompareOrdinal(val, expect);
                return string.Equals(c.Op, "GT", StringComparison.OrdinalIgnoreCase) ? cmp > 0 : cmp < 0;
            }
            return string.Equals(val, expect, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从值文本提取数字（优先整串解析，否则取首个数字串），失败返回 null</summary>
        private static double? ExtractNumber(string s)
        {
            s = s.Trim();
            if (double.TryParse(s, out var d)) return d;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"-?\d+(\.\d+)?");
            return m.Success && double.TryParse(m.Value, out var v) ? v : null;
        }

        /// <summary>条件组判定：OR 任一满足 / AND 全部满足（空组不满足）</summary>
        private static bool SignalCondsMet(List<SignalLightCondition>? conds, string mode)
        {
            if (conds == null || conds.Count == 0) return false;
            var and = string.Equals(mode, "AND", StringComparison.OrdinalIgnoreCase);
            foreach (var c in conds)
            {
                var met = SignalCondMet(c);
                if (and && !met) return false;
                if (!and && met) return true;
            }
            return and;
        }

        /// <summary>查找包含指定信号灯的 PanelItem（含子面板）</summary>
        private PanelItem? FindSignalLightOwner(SignalLightItem light)
        {
            if (_currentWindowConfig == null) return null;
            foreach (var panel in _currentWindowConfig.Panels)
            {
                if (panel.SignalLights != null && panel.SignalLights.Contains(light)) return panel;
                if (panel.ChildPanels != null)
                {
                    foreach (var child in panel.ChildPanels)
                        if (child.SignalLights != null && child.SignalLights.Contains(light)) return child;
                }
            }
            return null;
        }

        private UIElement BuildSignalLightWidget(PanelItem item)
        {
            item.SignalLights ??= new List<SignalLightItem>
            {
                new() { Color = "#FF4444", Size = 36, Icon = "", IsOn = true },
                new() { Color = "#FFAA00", Size = 36, Icon = "", IsOn = true },
                new() { Color = "#44CC44", Size = 36, Icon = "", IsOn = true }
            };

            var container = new StackPanel();
            var shapeRefs = new List<(SignalLightItem light, System.Windows.Shapes.Shape shape)>();
            DispatcherTimer? blinkTimer = null;

            // 闪烁相位：按各灯频率由系统时钟计算亮/灭半周期
            bool BlinkPhaseOn(SignalLightItem light)
            {
                var intervalMs = (long)(Math.Max(0.1, light.BlinkIntervalSeconds) * 1000);
                return Environment.TickCount64 / intervalMs % 2 == 0;
            }

            // 状态优先级：闪烁 > 关 > 开 > 手动 IsOn；变量值更新或闪烁计时触发时重算视觉
            void RefreshVisuals()
            {
                bool anyBlink = false;
                foreach (var (light, shape) in shapeRefs)
                {
                    bool lit;
                    if (SignalCondsMet(light.BlinkConditions, light.BlinkConditionMode)) { anyBlink = true; lit = BlinkPhaseOn(light); }
                    else if (SignalCondsMet(light.OffConditions, light.OffConditionMode)) lit = false;
                    else if (SignalCondsMet(light.OnConditions, light.OnConditionMode)) lit = true;
                    else lit = light.IsOn;
                    ApplyLightVisual(shape, light, lit);
                }
                if (anyBlink && blinkTimer == null)
                {
                    blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    blinkTimer.Tick += (s, e) => RefreshVisuals();
                    blinkTimer.Start();
                }
                else if (!anyBlink && blinkTimer != null)
                {
                    blinkTimer.Stop();
                    blinkTimer = null;
                }
            }

            void RenderLights()
            {
                container.Children.Clear();
                shapeRefs.Clear();
                var hAlign = item.SignalLightAlignment switch
                {
                    "Left" => HorizontalAlignment.Left,
                    "Right" => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center
                };
                var lightsPanel = item.SignalLightVertical
                    ? (Panel)new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = hAlign }
                    : new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = hAlign };

                foreach (var light in item.SignalLights)
                {
                    var lightContainer = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(item.SignalLightSpacing, 4, item.SignalLightSpacing, 4)
                    };

                    // 图标（灯上方）
                    if (!string.IsNullOrEmpty(light.Icon))
                    {
                        var iconBlock = new Emoji.Wpf.TextBlock
                        {
                            Text = light.Icon,
                            FontSize = light.Size * 0.5,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 0, 0, 2)
                        };
                        lightContainer.Children.Add(iconBlock);
                    }

                    var shape = CreateLightShape(light, light.IsOn);
                    if (shape is System.Windows.Shapes.Shape s) shapeRefs.Add((light, s));
                    lightContainer.Children.Add(shape);
                    lightsPanel.Children.Add(lightContainer);
                }

                container.Children.Add(lightsPanel);
                RefreshVisuals();
            }

            RenderLights();

            // 变量值更新时重新评估灯状态；控件卸载时取消订阅、停表（RenderPanels 会重建 UI）
            Action<string, string> dataHandler = (id, val) => Dispatcher.InvokeAsync(() => RefreshVisuals());
            Services.DataPollingService.Instance.DataUpdated += dataHandler;

            // 定时监控（每 1 秒）：重算三态条件，保证变量缺失/未被轮询等场景也能及时激活状态
            var monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            monitorTimer.Tick += (s, e) => RefreshVisuals();
            monitorTimer.Start();

            container.Unloaded += (s, e) =>
            {
                Services.DataPollingService.Instance.DataUpdated -= dataHandler;
                monitorTimer.Stop();
                blinkTimer?.Stop();
                blinkTimer = null;
            };

            // 每个信号灯模块只有一组灯（单个 PanelItem 内记录多个灯），菜单项用“设置信号灯”更达意
            return WrapWidget(Framework.Lang.T("🚦 信号灯"), container, item, () => ShowSignalLightEditDialog(item), null, "设置信号灯");
        }

        private void ShowSignalLightEditDialog(PanelItem item)
        {
            item.SignalLights ??= new List<SignalLightItem>();

            var dialog = new Window
            {
                Title = Framework.Lang.T("设置信号灯"),
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.NoResize
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };
            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");
            var comboStyle = (Style)FindResource("GlobalComboBoxStyle");

            // 第一行：排列方向 + 灯样式
            var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var orientLabel = new TextBlock { Text = Framework.Lang.T("排列方向："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(orientLabel, Dock.Left);
            row1.Children.Add(orientLabel);
            var orientCombo = new ComboBox { Style = comboStyle, Width = 100 };
            orientCombo.Items.Add(Framework.Lang.T("横向"));
            orientCombo.Items.Add(Framework.Lang.T("纵向"));
            orientCombo.SelectedIndex = item.SignalLightVertical ? 1 : 0;
            row1.Children.Add(orientCombo);

            // Dock.Right 先加控件（最右），再加标签（控件左边）
            var styleCombo = new ComboBox { Style = comboStyle, Width = 100 };
            styleCombo.Items.Add(Framework.Lang.T("圆形"));
            styleCombo.Items.Add(Framework.Lang.T("方形"));
            styleCombo.Items.Add(Framework.Lang.T("像素"));
            var firstLight = item.SignalLights.FirstOrDefault();
            styleCombo.SelectedIndex = firstLight?.LightStyle switch
            {
                "Square" => 1,
                "Pixel" => 2,
                _ => 0
            };
            DockPanel.SetDock(styleCombo, Dock.Right);
            row1.Children.Add(styleCombo);
            var styleLabel = new TextBlock { Text = Framework.Lang.T("  灯样式："), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(styleLabel, Dock.Right);
            row1.Children.Add(styleLabel);
            root.Children.Add(row1);

            // 第二行：大小 + 间距 + 对齐
            var row2 = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var sizeLabel = new TextBlock { Text = Framework.Lang.T("灯大小："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sizeLabel, Dock.Left);
            row2.Children.Add(sizeLabel);
            var sizeBox = new TextBox { Text = firstLight?.Size.ToString() ?? "36", Style = textBoxStyle, Width = 70 };
            row2.Children.Add(sizeBox);

            // Dock.Right 从右到左：对齐控件 → 对齐标签 → 间距控件 → 间距标签
            var alignCombo = new ComboBox { Style = comboStyle, Width = 80 };
            alignCombo.Items.Add(Framework.Lang.T("居左"));
            alignCombo.Items.Add(Framework.Lang.T("居中"));
            alignCombo.Items.Add(Framework.Lang.T("居右"));
            alignCombo.SelectedIndex = item.SignalLightAlignment switch
            {
                "Left" => 0,
                "Right" => 2,
                _ => 1
            };
            DockPanel.SetDock(alignCombo, Dock.Right);
            row2.Children.Add(alignCombo);

            var alignLabel = new TextBlock { Text = Framework.Lang.T("  对齐："), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(alignLabel, Dock.Right);
            row2.Children.Add(alignLabel);

            var spacingBox = new TextBox { Text = item.SignalLightSpacing.ToString(), Style = textBoxStyle, Width = 50 };
            DockPanel.SetDock(spacingBox, Dock.Right);
            row2.Children.Add(spacingBox);

            var spacingLabel = new TextBlock { Text = Framework.Lang.T("  间距："), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(spacingLabel, Dock.Right);
            row2.Children.Add(spacingLabel);
            root.Children.Add(row2);

            // 灯列表
            var lightsStack = new StackPanel();
            void RenderEditList()
            {
                lightsStack.Children.Clear();
                for (int i = 0; i < item.SignalLights.Count; i++)
                {
                    int idx = i; // 闭包捕获
                    var light = item.SignalLights[i];
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

                    // 序号
                    var idxLabel = new TextBlock { Text = $"{i + 1}.", Width = 25, VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(idxLabel, Dock.Left);
                    row.Children.Add(idxLabel);

                    // 右侧按钮：编辑 + 删除（列表本身只读展示，点编辑进入灯详情窗口修改）
                    var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var editBtn = new Button
                    {
                        Content = "✎", Width = 22, Height = 22, FontSize = 10,
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextSec"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 0),
                        ToolTip = Framework.Lang.T("编辑")
                    };
                    editBtn.Click += (s, e) => { ShowSignalLightDetailDialog(light); RenderEditList(); };
                    rightBtns.Children.Add(editBtn);
                    var delBtn = new Button
                    {
                        Content = "✕", Width = 22, Height = 22, FontSize = 10,
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand
                    };
                    delBtn.Click += (s, e) => { item.SignalLights.RemoveAt(idx); RenderEditList(); };
                    rightBtns.Children.Add(delBtn);
                    DockPanel.SetDock(rightBtns, Dock.Right);
                    row.Children.Add(rightBtns);

                    // 只读展示：色块 + 颜色值 + 大小 + 图标
                    var swatch = new System.Windows.Shapes.Ellipse
                    {
                        Width = 14, Height = 14,
                        Fill = TryParseLightBrush(light.Color),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    row.Children.Add(swatch);
                    var colorText = new TextBlock { Text = light.Color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                    row.Children.Add(colorText);
                    var sizeText = new TextBlock
                    {
                        Text = $"{Framework.Lang.T("大小:")}{light.Size:0.##}",
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)FindResource("BrushTextSec"),
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    row.Children.Add(sizeText);
                    if (!string.IsNullOrEmpty(light.Icon))
                    {
                        var iconText = new Emoji.Wpf.TextBlock
                        {
                            Text = light.Icon,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = (Brush)FindResource("BrushTextSec")
                        };
                        row.Children.Add(iconText);
                    }

                    lightsStack.Children.Add(row);

                    // 右键行：编辑 / 删除（与右侧按钮等价）
                    var rowMenu = new ContextMenu();
                    var rowEditMi = new MenuItem { Header = Framework.Lang.T("编辑") };
                    rowEditMi.Click += (s, e) => { ShowSignalLightDetailDialog(light); RenderEditList(); };
                    rowMenu.Items.Add(rowEditMi);
                    var rowDelMi = new MenuItem { Header = Framework.Lang.T("删除") };
                    rowDelMi.Click += (s, e) => { item.SignalLights.RemoveAt(idx); RenderEditList(); };
                    rowMenu.Items.Add(rowDelMi);
                    row.ContextMenu = rowMenu;
                }
            }
            RenderEditList();
            root.Children.Add(lightsStack);

            // 添加灯按钮
            var addBtn = new Button
            {
                Content = Framework.Lang.T("+ 添加灯"),
                Style = (Style)FindResource("StandardButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 16)
            };
            addBtn.Click += (s, e) =>
            {
                item.SignalLights.Add(new SignalLightItem { Color = "#FF4444", Size = 36, Icon = "", IsOn = true });
                RenderEditList();
            };
            root.Children.Add(addBtn);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                item.SignalLightVertical = orientCombo.SelectedIndex == 1;
                var newStyle = styleCombo.SelectedIndex switch
                {
                    1 => "Square",
                    2 => "Pixel",
                    _ => "Circle"
                };
                double newSize = 36;
                if (double.TryParse(sizeBox.Text.Trim(), out double parsedSize) && parsedSize > 0)
                    newSize = parsedSize;
                double newSpacing = 5;
                if (double.TryParse(spacingBox.Text.Trim(), out double parsedSpacing) && parsedSpacing >= 0)
                    newSpacing = parsedSpacing;
                item.SignalLightSpacing = newSpacing;
                item.SignalLightAlignment = alignCombo.SelectedIndex switch
                {
                    0 => "Left",
                    2 => "Right",
                    _ => "Center"
                };
                foreach (var light in item.SignalLights)
                {
                    light.LightStyle = newStyle;
                    light.Size = newSize;
                }
                _configManager?.Save();
                RenderPanels();
                dialog.Close();
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        /// <summary>灯详情编辑窗口：修改单个灯的颜色/大小/图标，点确定才写回</summary>
        private void ShowSignalLightDetailDialog(SignalLightItem light)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("灯详情"),
                Width = 500,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.NoResize
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };
            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");
            var comboStyle = (Style)FindResource("GlobalComboBoxStyle");

            // 颜色（输入框 + 实时色块预览）
            var colorRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var colorLabel = new TextBlock { Text = Framework.Lang.T("颜色："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(colorLabel, Dock.Left);
            colorRow.Children.Add(colorLabel);
            var swatch = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Fill = TryParseLightBrush(light.Color) };
            DockPanel.SetDock(swatch, Dock.Right);
            colorRow.Children.Add(swatch);
            var colorBox = new TextBox { Text = light.Color, Style = textBoxStyle };
            colorBox.TextChanged += (s, e) => swatch.Fill = TryParseLightBrush(colorBox.Text.Trim());
            colorRow.Children.Add(colorBox);
            root.Children.Add(colorRow);

            // 大小
            var sizeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var sizeLabel = new TextBlock { Text = Framework.Lang.T("灯大小："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sizeLabel, Dock.Left);
            sizeRow.Children.Add(sizeLabel);
            var sizeBox = new TextBox { Text = light.Size.ToString(), Style = textBoxStyle, Width = 80 };
            sizeRow.Children.Add(sizeBox);
            root.Children.Add(sizeRow);

            // 图标
            var iconRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var iconLabel = new TextBlock { Text = Framework.Lang.T("图标："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(iconLabel, Dock.Left);
            iconRow.Children.Add(iconLabel);
            var iconBox = new TextBox { Text = light.Icon ?? "", Style = textBoxStyle };
            iconRow.Children.Add(iconBox);
            root.Children.Add(iconRow);

            // ===== 三态变量条件（开 / 关 / 闪烁）：满足任一条件即进入该状态 =====
            var varSources = CollectLightVarSources();

            UIElement BuildCondSection(string title, List<SignalLightCondition> conds, Func<string> getMode, Action<string> setMode)
            {
                var sec = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                // 标题行：状态名 + 条件组合方式（OR 任一 / AND 全部）
                var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var modeCombo = new ComboBox { Style = comboStyle, Width = 160 };
                modeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("任一满足即触发(或)"), Tag = "OR" });
                modeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("全部满足才触发(与)"), Tag = "AND" });
                modeCombo.SelectedIndex = string.Equals(getMode(), "AND", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                modeCombo.SelectionChanged += (s, e) => { if (modeCombo.SelectedItem is ComboBoxItem ci) setMode(ci.Tag as string ?? "OR"); };
                DockPanel.SetDock(modeCombo, Dock.Right);
                headRow.Children.Add(modeCombo);
                headRow.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
                sec.Children.Add(headRow);
                var rows = new StackPanel();
                void RenderRows()
                {
                    rows.Children.Clear();
                    for (int i = 0; i < conds.Count; i++)
                    {
                        int idx = i;
                        var c = conds[idx];
                        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

                        var delBtn = new Button
                        {
                            Content = "✕", Width = 22, Height = 22, FontSize = 10,
                            Background = Brushes.Transparent,
                            Foreground = (Brush)FindResource("BrushTextMuted"),
                            BorderThickness = new Thickness(0), Padding = new Thickness(0),
                            Cursor = Cursors.Hand
                        };
                        delBtn.Click += (s, e) => { conds.RemoveAt(idx); RenderRows(); };
                        DockPanel.SetDock(delBtn, Dock.Right);
                        row.Children.Add(delBtn);

                        // 变量选择
                        var varCombo = new ComboBox { Style = comboStyle, Width = 170, Margin = new Thickness(0, 0, 6, 0) };
                        foreach (var d in varSources)
                            varCombo.Items.Add(new ComboBoxItem { Content = $"{d.VarKey}（{d.Label}）", Tag = d.VarKey });
                        for (int k = 0; k < varCombo.Items.Count; k++)
                        {
                            if (varCombo.Items[k] is ComboBoxItem ci0 && string.Equals(ci0.Tag as string, c.VarKey, StringComparison.OrdinalIgnoreCase))
                            { varCombo.SelectedIndex = k; break; }
                        }
                        varCombo.SelectionChanged += (s, e) => { if (varCombo.SelectedItem is ComboBoxItem ci) c.VarKey = ci.Tag as string ?? ""; };
                        row.Children.Add(varCombo);

                        // 比较操作符：等于 / 大于 / 小于 / 不存在
                        var opCombo = new ComboBox { Style = comboStyle, Width = 90, Margin = new Thickness(0, 0, 4, 0) };
                        opCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("等于"), Tag = "EQ" });
                        opCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("大于"), Tag = "GT" });
                        opCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("小于"), Tag = "LT" });
                        opCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("不存在"), Tag = "MISSING" });
                        opCombo.SelectedIndex = c.Op switch { "GT" => 1, "LT" => 2, "MISSING" => 3, _ => 0 };
                        row.Children.Add(opCombo);
                        var valBox = new TextBox { Text = c.Value, Style = textBoxStyle, Width = 70, Margin = new Thickness(0, 0, 6, 0) };
                        valBox.TextChanged += (s, e) => c.Value = valBox.Text.Trim();
                        row.Children.Add(valBox);

                        // 操作符为「不存在」时值无意义，禁用
                        void SyncEnabled()
                        {
                            valBox.IsEnabled = !string.Equals(c.Op, "MISSING", StringComparison.OrdinalIgnoreCase);
                        }
                        SyncEnabled();
                        opCombo.SelectionChanged += (s, e) =>
                        {
                            if (opCombo.SelectedItem is ComboBoxItem ci) c.Op = ci.Tag as string ?? "EQ";
                            SyncEnabled();
                        };

                        rows.Children.Add(row);
                    }
                }
                RenderRows();
                sec.Children.Add(rows);
                var addCondBtn = new Button
                {
                    Content = Framework.Lang.T("+ 添加条件"),
                    Style = (Style)FindResource("StandardButtonStyle"),
                    Padding = new Thickness(8, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                addCondBtn.Click += (s, e) => { conds.Add(new SignalLightCondition()); RenderRows(); };
                sec.Children.Add(addCondBtn);
                return sec;
            }

            root.Children.Add(BuildCondSection(Framework.Lang.T("开"), light.OnConditions, () => light.OnConditionMode, m => light.OnConditionMode = m));
            root.Children.Add(BuildCondSection(Framework.Lang.T("关"), light.OffConditions, () => light.OffConditionMode, m => light.OffConditionMode = m));
            root.Children.Add(BuildCondSection(Framework.Lang.T("闪烁"), light.BlinkConditions, () => light.BlinkConditionMode, m => light.BlinkConditionMode = m));

            // 闪烁频率
            var freqRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var freqLabel = new TextBlock { Text = Framework.Lang.T("闪烁频率(秒/次)"), Width = 120, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(freqLabel, Dock.Left);
            freqRow.Children.Add(freqLabel);
            var freqBox = new TextBox { Text = light.BlinkIntervalSeconds.ToString(), Style = textBoxStyle, Width = 80 };
            freqBox.TextChanged += (s, e) => { if (double.TryParse(freqBox.Text.Trim(), out double fv) && fv >= 0.1) light.BlinkIntervalSeconds = fv; };
            freqRow.Children.Add(freqBox);
            root.Children.Add(freqRow);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                light.Color = colorBox.Text.Trim();
                if (double.TryParse(sizeBox.Text.Trim(), out double v) && v > 0) light.Size = v;
                light.Icon = iconBox.Text.Trim();
                dialog.Close();
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        /// <summary>解析 hex 颜色为画刷，解析失败回退灰色（用于列表色块展示）</summary>
        private static Brush TryParseLightBrush(string? hex)
        {
            try { return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex ?? "")); }
            catch { return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)); }
        }

        /// <summary>收集本地全部变量定义（面板数据项 + 后台数据项 + 变量集），按 VarKey 去重，供信号灯条件选择</summary>
        private List<DataDashboardItem> CollectLightVarSources()
        {
            var app = (App)Application.Current;
            var seen = new Dictionary<string, DataDashboardItem>(StringComparer.OrdinalIgnoreCase);
            void Collect(PanelItem p)
            {
                if (p.DashboardItems != null)
                {
                    foreach (var d in p.DashboardItems)
                        if (!string.IsNullOrEmpty(d.VarKey) && string.IsNullOrEmpty(d.RefVarKey))
                            seen.TryAdd(d.VarKey, d);
                }
                if (p.ChildPanels != null)
                    foreach (var cp in p.ChildPanels) Collect(cp);
            }
            foreach (var win in app.WindowConfigs.Windows)
                foreach (var p in win.Panels) Collect(p);
            foreach (var d in app.WindowConfigs.BackgroundDashboardItems)
                if (!string.IsNullOrEmpty(d.VarKey))
                    seen.TryAdd(d.VarKey, d);
            // 变量集（多变量聚合生成的新变量）同样可作为条件来源
            foreach (var d in Services.VariableSetService.Instance.GetItems())
                seen.TryAdd(d.VarKey!, d);
            return seen.Values.ToList();
        }

        #endregion

        private UIElement CreateTodoRow(TodoItem todo, PanelItem panelItem)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            // 勾选框（左侧）
            var check = new CheckBox
            {
                IsChecked = todo.IsDone,
                Style = (Style)FindResource("TodoCheckBoxStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);

            // 闹钟图标（紧贴文字左侧；有任务时间时显示，鼠标悬停显示具体日期）
            if (todo.DueTime != null)
            {
                var remindText = todo.RemindBeforeMinutes < 0
                    ? ""
                    : (todo.RemindBeforeMinutes == 0 ? Framework.Lang.T("，准时提醒") : Framework.Lang.TF("，提前{0}分钟", todo.RemindBeforeMinutes));
                var clock = new Emoji.Wpf.TextBlock
                {
                    Text = "\u23F0",
                    FontSize = 13,
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{todo.DueTime.Value:yyyy-MM-dd HH:mm}{remindText}"
                };
                DockPanel.SetDock(clock, Dock.Left);
                row.Children.Add(clock);
            }

            // 文本（填充剩余空间；完成后加删除线并变灰）
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };

            var textBlock = new Emoji.Wpf.TextBlock
            {
                Text = todo.Text,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource(todo.IsDone ? "BrushTextMuted" : "BrushTextMain"),
                TextDecorations = todo.IsDone ? TextDecorations.Strikethrough : null
            };
            textStack.Children.Add(textBlock);

            // 单击查看详情
            textStack.MouseLeftButtonUp += (s, e) => ShowTodoDetail(todo, panelItem);
            textStack.ToolTip = Framework.Lang.TF("{0}\n（点击查看详情）", todo.Text);
            row.Children.Add(textStack);

            check.Checked += (s, e) => { todo.IsDone = true; _configManager?.Save(); RenderPanels(); };
            check.Unchecked += (s, e) => { todo.IsDone = false; _configManager?.Save(); RenderPanels(); };

            // 右键菜单：删除
            var menu = new ContextMenu();
            var del = new MenuItem { Header = Framework.Lang.T("删除") };
            del.Click += (s, e) => { panelItem.TodoItems?.Remove(todo); _configManager?.Save(); RenderPanels(); };
            menu.Items.Add(del);
            var todoHideMi = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = panelItem.HideFrame };
            todoHideMi.Click += (s, e) => { panelItem.HideFrame = todoHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            menu.Items.Add(todoHideMi);
            row.ContextMenu = menu;

            return row;
        }

        /// <summary>弹出对话框添加新待办</summary>
        private void ShowTodoDialog(PanelItem panelItem)
        {
            panelItem.TodoItems ??= new List<TodoItem>();
            var dialog = new TodoDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                panelItem.TodoItems.Add(dialog.Result);
                _configManager?.Save();
                RenderPanels();
            }
        }

        /// <summary>弹出对话框编辑已有待办</summary>
        private void EditTodo(TodoItem todo, PanelItem panelItem)
        {
            var dialog = new TodoDialog(todo) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                _configManager?.Save();
                RenderPanels();
            }
        }

        /// <summary>点击待办项时弹出详情窗口，展示完整内容；支持编辑和删除</summary>
        private void ShowTodoDetail(TodoItem todo, PanelItem panelItem)
        {
            var owner = Window.GetWindow(this);
            var detailWin = new Window
            {
                Title = Framework.Lang.T("待办详情"),
                Width = 380,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = owner,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            detailWin.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(detailWin);

            var root = new StackPanel { Margin = new Thickness(20) };

            // 状态行
            var statusText = todo.IsDone ? Framework.Lang.T("✅ 已完成") : Framework.Lang.T("⏳ 未完成");
            var statusBlock = new Emoji.Wpf.TextBlock
            {
                Text = statusText,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(statusBlock);

            // 标题
            var titleHeader = new TextBlock { Text = Framework.Lang.T("标题"), FontSize = 12, Foreground = (Brush)FindResource("BrushTextMuted"), Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(titleHeader);
            var titleBlock = new Emoji.Wpf.TextBlock
            {
                Text = todo.Text,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 14)
            };
            root.Children.Add(titleBlock);

            // 详细内容
            var descHeader = new TextBlock { Text = Framework.Lang.T("详细内容"), FontSize = 12, Foreground = (Brush)FindResource("BrushTextMuted"), Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(descHeader);
            var descScroll = new ScrollViewer
            {
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Emoji.Wpf.TextBlock
                {
                    Text = string.IsNullOrEmpty(todo.Description) ? Framework.Lang.T("（无详细内容）") : todo.Description,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = string.IsNullOrEmpty(todo.Description)
                        ? (Brush)FindResource("BrushTextMuted")
                        : (Brush)FindResource("BrushTextMain"),
                    Opacity = string.IsNullOrEmpty(todo.Description) ? 0.6 : 1.0
                }
            };
            root.Children.Add(descScroll);

            // 时间信息
            if (todo.DueTime.HasValue)
            {
                var remindText = todo.RemindBeforeMinutes < 0
                    ? ""
                    : (todo.RemindBeforeMinutes == 0 ? Framework.Lang.T("，准时提醒") : Framework.Lang.TF("，提前{0}分钟提醒", todo.RemindBeforeMinutes));
                var timeBlock = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.TF("\u23F0 任务时间：{0:yyyy-MM-dd HH:mm}{1}", todo.DueTime.Value, remindText),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 14, 0, 0)
                };
                root.Children.Add(timeBlock);
            }

            // 创建时间
            var createdBlock = new TextBlock
            {
                Text = Framework.Lang.TF("创建时间：{0:yyyy-MM-dd HH:mm}", todo.CreatedAt),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 6, 0, 0)
            };
            root.Children.Add(createdBlock);

            // 按钮区
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };

            var editBtn = new Button { Content = Framework.Lang.T("编辑"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("StandardButtonStyle") };
            editBtn.Click += (s, e) =>
            {
                detailWin.Close();
                EditTodo(todo, panelItem);
            };
            btnPanel.Children.Add(editBtn);

            var closeBtn = new Button { Content = Framework.Lang.T("关闭"), Width = 70, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
            closeBtn.Click += (s, e) => detailWin.Close();
            btnPanel.Children.Add(closeBtn);

            root.Children.Add(btnPanel);
            detailWin.Content = root;
            detailWin.ShowDialog();
        }

        /// <summary>定时检查当前窗口的待办，到点（含提前量）时弹窗提醒</summary>
        private void TodoReminderTick(object? sender, EventArgs e)
        {
            if (_currentWindowConfig == null) return;
            var now = DateTime.Now;

            void Check(List<PanelItem> panels)
            {
                foreach (var p in panels)
                {
                    if (p.Type == PanelType.TodoList && p.TodoItems != null)
                    {
                        foreach (var todo in p.TodoItems)
                        {
                            if (todo.IsDone || todo.DueTime == null || todo.RemindBeforeMinutes < 0) continue;
                            if (todo.Reminded || _remindedTodoIds.Contains(todo.Id)) continue;
                            var remindAt = todo.DueTime.Value.AddMinutes(-todo.RemindBeforeMinutes);
                            if (now >= remindAt)
                            {
                                todo.Reminded = true;
                                _remindedTodoIds.Add(todo.Id);
                                _configManager?.Save();
                                MessageBox.Show(Framework.Lang.TF("待办提醒：{0}\n任务时间：{1:MM-dd HH:mm}", todo.Text, todo.DueTime.Value),
                                    Framework.Lang.T("今日待办"), MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                        }
                    }
                    if (p.ChildPanels != null) Check(p.ChildPanels);
                }
            }
            Check(_currentWindowConfig.Panels);
        }

        private UIElement BuildTimerWidget(PanelItem item)
        {
            item.TimerItems ??= new List<TimerItem>();
        
            var container = new StackPanel();
        
            // 计时器列表
            foreach (var timer in item.TimerItems)
            {
                var timerCard = CreateTimerCard(timer, item);
                container.Children.Add(timerCard);
            }
        
            // 添加计时器逻辑（空状态点击与标题栏菜单共用）
            void AddTimer()
            {
                item.TimerItems.Add(new TimerItem());
                _configManager?.Save();
                RenderPanels();
            }

            // 空列表提示：点击直接添加计时器
            if (item.TimerItems.Count == 0)
                container.Children.Add(BuildEmptyAddTip(Framework.Lang.T("暂无计时器，点击添加"), AddTimer));
        
            // 基于 ModuleControl 基础组件包装
            var module = CreateModuleControl(Framework.Lang.T("⏱ 计时器"), item);
            var addTimerMi = new MenuItem { Header = Framework.Lang.T("添加计时器") };
            addTimerMi.Click += (s, e) => AddTimer();
            module.AddCustomMenuItem(addTimerMi);
            module.SetContent(container);
            return module;
        }

        // ==================== 图片轮播模块 ====================

        /// <summary>加载指定目录下的图片文件路径列表</summary>
        private List<string> LoadCarouselImages(PanelItem item)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(item.CarouselFolder) || !System.IO.Directory.Exists(item.CarouselFolder))
                return result;
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            try
            {
                var opt = item.CarouselIncludeSubfolders
                    ? System.IO.SearchOption.AllDirectories
                    : System.IO.SearchOption.TopDirectoryOnly;
                foreach (var f in System.IO.Directory.EnumerateFiles(item.CarouselFolder, "*.*", opt))
                {
                    if (exts.Contains(System.IO.Path.GetExtension(f)))
                        result.Add(f);
                }
                result.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return result;
        }

        private UIElement BuildImageCarouselWidget(PanelItem item)
        {
            var images = LoadCarouselImages(item);
            int index = 0;
            var rnd = new Random();

            // 图片显示控件
            var image = new Image
            {
                Stretch = item.CarouselStretch switch
                {
                    "UniformToFill" => Stretch.UniformToFill,
                    "Fill" => Stretch.Fill,
                    _ => Stretch.Uniform
                },
                Cursor = Cursors.Hand
            };

            // 序号标签
            var infoLabel = new TextBlock
            {
                FontSize = 10,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 4)
            };

            // 空状态提示
            var emptyHint = new TextBlock
            {
                Text = Framework.Lang.T("未设置目录或无图片\n点标题栏 ▾ → 设置"),
                TextAlignment = TextAlignment.Center,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var displayGrid = new Grid { Height = item.CarouselHeight, ClipToBounds = true };
            displayGrid.Children.Add(image);
            displayGrid.Children.Add(infoLabel);
            displayGrid.Children.Add(emptyHint);

            void ShowImage()
            {
                if (images.Count == 0)
                {
                    image.Source = null;
                    infoLabel.Visibility = Visibility.Collapsed;
                    emptyHint.Visibility = Visibility.Visible;
                    return;
                }
                emptyHint.Visibility = Visibility.Collapsed;
                if (index < 0) index = images.Count - 1;
                if (index >= images.Count) index = 0;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(images[index]);
                    // 限制解码宽度，避免全分辨率解码占用大量内存（4K图片全解码≈33MB，600px≈仅 0.8MB）
                    bmp.DecodePixelWidth = 600;
                    bmp.EndInit();
                    bmp.Freeze();
                    image.Source = bmp;
                    infoLabel.Text = $"{index + 1}/{images.Count}";
                    infoLabel.Visibility = Visibility.Visible;
                }
                catch { image.Source = null; }
            }

            void Next()
            {
                if (images.Count == 0) return;
                if (item.CarouselRandom && images.Count > 1)
                {
                    int n; do { n = rnd.Next(images.Count); } while (n == index);
                    index = n;
                }
                else index = (index + 1) % images.Count;
                ShowImage();
            }

            void Prev()
            {
                if (images.Count == 0) return;
                index = (index - 1 + images.Count) % images.Count;
                ShowImage();
            }

            image.MouseLeftButtonUp += (s, e) => Next();
            ShowImage();

            // 自动播放定时器（非编辑模式、至少两张图）
            if (item.CarouselAutoPlay && !_isEditMode && images.Count > 1)
            {
                var playTimer = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromSeconds(Math.Max(1, item.CarouselIntervalSeconds))
                };
                playTimer.Tick += (s, e) => Next();
                playTimer.Start();
                // 面板重新渲染/窗口关闭导致控件卸载时停止，避免定时器泄漏
                displayGrid.Unloaded += (s, e) => playTimer.Stop();
            }

            // 基于 ModuleControl 基础组件包装
            var module = CreateModuleControl(Framework.Lang.T("🖼 图片轮播"), item);
            var carouselSettingsMi = new MenuItem { Header = Framework.Lang.T("设置目录/播放规则...") };
            carouselSettingsMi.Click += (s, e) => ShowCarouselSettingsDialog(item);
            module.AddCustomMenuItem(carouselSettingsMi);
            var prevMi = new MenuItem { Header = Framework.Lang.T("上一张") };
            prevMi.Click += (s, e) => Prev();
            module.AddCustomMenuItem(prevMi);
            var nextMi = new MenuItem { Header = Framework.Lang.T("下一张") };
            nextMi.Click += (s, e) => Next();
            module.AddCustomMenuItem(nextMi);
            var playMi = new MenuItem { Header = item.CarouselAutoPlay ? Framework.Lang.T("暂停自动播放") : Framework.Lang.T("开始自动播放") };
            playMi.Click += (s, e) => { item.CarouselAutoPlay = !item.CarouselAutoPlay; _configManager?.Save(); RenderPanels(); };
            module.AddCustomMenuItem(playMi);
            module.SetContent(displayGrid);
            return module;
        }

        /// <summary>图片轮播设置对话框：目录 + 播放规则</summary>
        private void ShowCarouselSettingsDialog(PanelItem item)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("图片轮播设置"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Owner = Window.GetWindow(this)
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(16) };
            TextBlock MakeLabel(string t) => new Emoji.Wpf.TextBlock
            {
                Text = t,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 4)
            };

            // 图片目录
            root.Children.Add(MakeLabel(Framework.Lang.T("图片目录")));
            var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var browseBtn = new Button { Content = Framework.Lang.T("浏览"), Width = 60, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            DockPanel.SetDock(browseBtn, Dock.Right);
            var folderBox = new TextBox { Text = item.CarouselFolder ?? "", IsReadOnly = true, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            browseBtn.Click += (s, e) =>
            {
                var fd = new Microsoft.Win32.OpenFolderDialog { Title = Framework.Lang.T("选择图片目录") };
                if (!string.IsNullOrWhiteSpace(item.CarouselFolder) && System.IO.Directory.Exists(item.CarouselFolder))
                    fd.InitialDirectory = item.CarouselFolder;
                if (fd.ShowDialog() == true) folderBox.Text = fd.FolderName;
            };
            folderRow.Children.Add(browseBtn);
            folderRow.Children.Add(folderBox);
            root.Children.Add(folderRow);

            // 切换间隔
            root.Children.Add(MakeLabel(Framework.Lang.T("切换间隔（秒）")));
            var intervalBox = new TextBox { Text = item.CarouselIntervalSeconds.ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(intervalBox);

            // 显示高度
            root.Children.Add(MakeLabel(Framework.Lang.T("显示高度（px）")));
            var heightBox = new TextBox { Text = ((int)item.CarouselHeight).ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(heightBox);

            // 播放顺序
            root.Children.Add(MakeLabel(Framework.Lang.T("播放顺序")));
            var orderCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            orderCombo.Items.Add(Framework.Lang.T("顺序播放"));
            orderCombo.Items.Add(Framework.Lang.T("随机播放"));
            orderCombo.SelectedIndex = item.CarouselRandom ? 1 : 0;
            root.Children.Add(orderCombo);

            // 填充方式
            root.Children.Add(MakeLabel(Framework.Lang.T("填充方式")));
            var fitCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            fitCombo.Items.Add(Framework.Lang.T("适应（完整显示）"));
            fitCombo.Items.Add(Framework.Lang.T("填满（裁剪铺满）"));
            fitCombo.Items.Add(Framework.Lang.T("拉伸（变形铺满）"));
            fitCombo.SelectedIndex = item.CarouselStretch switch { "UniformToFill" => 1, "Fill" => 2, _ => 0 };
            root.Children.Add(fitCombo);

            // 开关项
            var subChk = new CheckBox { Content = Framework.Lang.T("包含子目录"), IsChecked = item.CarouselIncludeSubfolders, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(subChk);
            var autoChk = new CheckBox { Content = Framework.Lang.T("自动播放"), IsChecked = item.CarouselAutoPlay, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 16) };
            root.Children.Add(autoChk);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                item.CarouselFolder = string.IsNullOrWhiteSpace(folderBox.Text) ? null : folderBox.Text;
                if (int.TryParse(intervalBox.Text, out var iv) && iv >= 1) item.CarouselIntervalSeconds = iv;
                if (double.TryParse(heightBox.Text, out var h) && h >= 40 && h <= 800) item.CarouselHeight = h;
                item.CarouselRandom = orderCombo.SelectedIndex == 1;
                item.CarouselStretch = fitCombo.SelectedIndex switch { 1 => "UniformToFill", 2 => "Fill", _ => "Uniform" };
                item.CarouselIncludeSubfolders = subChk.IsChecked ?? false;
                item.CarouselAutoPlay = autoChk.IsChecked ?? true;
                _configManager?.Save();
                RenderPanels();
                dialog.DialogResult = true;
            };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private Border CreateTimerCard(TimerItem timer, PanelItem panelItem)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(3),
                Background = (Brush)FindResource("BrushBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };

            // 右键菜单：计时器级 + 模块级操作
            var cardMenu = new ContextMenu();
            var deleteMenuItem = new MenuItem { Header = Framework.Lang.T("删除计时器") };
            deleteMenuItem.Click += (s, e) => DeleteTimer(timer, panelItem);
            cardMenu.Items.Add(deleteMenuItem);
            cardMenu.Items.Add(new Separator());
            cardMenu.Items.Add(BuildSettingsMenuItem(panelItem, Framework.Lang.T("⏱ 计时器")));
            var cardHideMi = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = panelItem.HideFrame };
            cardHideMi.Click += (s, e) => { panelItem.HideFrame = cardHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            cardMenu.Items.Add(cardHideMi);
            cardMenu.Items.Add(BuildPinMenuItem(panelItem));
            var cardDelModuleMi = new MenuItem { Header = Framework.Lang.T("删除模块") };
            cardDelModuleMi.Click += (s, e) => RemovePanel(panelItem);
            cardMenu.Items.Add(cardDelModuleMi);
            card.ContextMenu = cardMenu;

            var mainStack = new StackPanel();

            // 时间显示（先声明，供 toggleBtn 的点击事件引用）
            var timeDisplay = new Emoji.Wpf.TextBlock
            {
                Text = FormatTimeSpan(timer.GetElapsed()),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = timer.IsRunning ? (Brush)FindResource("BrushAccent") : (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3)
            };
            // 用 Viewbox 包裹实现自适应缩放：时间超长时自动缩小字号以完整显示
            var timeBox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = timeDisplay
            };

            // 标题行：名称 + 开始/停止圆钮 + 编辑按钮
            var headerPanel = new DockPanel();
            
            var nameBlock = new Emoji.Wpf.TextBlock
            {
                Text = timer.Name,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            };

            // 开始/停止圆钮（放在标题右侧）
            // 默认按钮模板的背景是矩形且不会被外层 Border 裁剪，故自定义圆形模板让背景真正圆角
            var toggleBtn = new Button
            {
                Content = timer.IsRunning ? BuildStopIcon() : BuildPlayIcon(),
                Width = 22,
                Height = 22,
                FontSize = 10,
                Background = timer.IsRunning ? Brushes.IndianRed : new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Template = BuildCircleButtonTemplate()
            };
            DockPanel.SetDock(toggleBtn, Dock.Right);

            // 如果计时器正在运行，启动更新
            if (timer.IsRunning)
            {
                StartTimerUpdate(timer, timeDisplay, panelItem, toggleBtn, card);
            }

            toggleBtn.Click += (s, e) =>
            {
                if (!timer.IsRunning)
                {
                    timer.IsRunning = true;
                    timer.StartTime = DateTime.Now;
                    _configManager?.Save();
                    StartTimerUpdate(timer, timeDisplay, panelItem, toggleBtn, card);
                    toggleBtn.Content = BuildStopIcon();
                    toggleBtn.Background = Brushes.IndianRed;
                }
                else
                {
                    if (timer.StartTime.HasValue)
                        timer.ElapsedTime += (DateTime.Now - timer.StartTime.Value);
                    timer.IsRunning = false;
                    timer.StartTime = null;
                    AccumulateToDailyRecord(timer);
                    _configManager?.Save();
                    RenderPanels();
                }
            };

            // 编辑按钮
            var editBtn = new Button
            {
                Content = "✏",
                Width = 24,
                Height = 24,
                FontSize = 12,
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed
            };
            DockPanel.SetDock(editBtn, Dock.Right);
            editBtn.Click += (s, e) => ShowTimerEditDialog(timer, panelItem);

            headerPanel.Children.Add(editBtn);
            headerPanel.Children.Add(toggleBtn);
            headerPanel.Children.Add(nameBlock);

            mainStack.Children.Add(headerPanel);
            mainStack.Children.Add(timeBox);

            // 今日统计
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (timer.DailyRecords.TryGetValue(today, out int todaySeconds) && todaySeconds > 0)
            {
                var todayStat = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.TF("今日总计：{0}", FormatTimeSpan(TimeSpan.FromSeconds(todaySeconds))),
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

        /// <summary>圆形按钮模板：默认按钮模板背景为矩形无法圆角，自定义带 CornerRadius 的模板</summary>
        private static ControlTemplate BuildCircleButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        /// <summary>播放图标：白色右指三角形。三角形质心在包围盒中心左侧 4/3px（平底边在左、尖端在右），
        /// 放包围盒居中后视觉仍偏左，故用 RenderTransform 右移该偏移量实现光学居中。</summary>
        private static UIElement BuildPlayIcon()
        {
            return new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection { new Point(0, 0), new Point(0, 10), new Point(8, 5) },
                Fill = Brushes.White,
                Width = 8,
                Height = 10,
                RenderTransform = new TranslateTransform(1.33, 0)
            };
        }

        /// <summary>停止图标：白色方块（用几何形状代替字符，确保圆钮内精确居中）</summary>
        private static UIElement BuildStopIcon()
        {
            return new System.Windows.Shapes.Rectangle
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.White
            };
        }

        /// <summary>
        /// 删除指定计时器；若删除后模块为空，询问是否删除整个计时器模块。
        /// 供编辑模式的✕按钮与右键菜单共用。
        /// </summary>
        private void DeleteTimer(TimerItem timer, PanelItem panelItem)
        {
            if (panelItem.TimerItems == null) return;
            if (!panelItem.TimerItems.Remove(timer)) return;
            _configManager?.Save();

            // 如果删除后没有计时器了，询问是否删除整个模块
            if (panelItem.TimerItems.Count == 0)
            {
                var result = MessageBox.Show(
                    Framework.Lang.T("该计时器模块已没有计时项，是否删除整个模块？"),
                    Framework.Lang.T("确认"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes && _currentWindowConfig != null)
                {
                    _currentWindowConfig.Panels.Remove(panelItem);
                    _configManager?.Save();
                }
            }

            RenderPanels();
        }

        private void StartTimerUpdate(TimerItem timer, TextBlock timeDisplay, PanelItem panelItem, Button toggleBtn, FrameworkElement? lifecycleOwner = null)
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
                // 不再在到达预设时长时弹窗提醒/自动停止，计时器持续计时直到手动停止
            };

            dispatcherTimer.Start();

            // 控件卸载时停止定时器，避免 DispatcherTimer 泄漏（保持整棵 UI 树存活）
            if (lifecycleOwner != null)
                lifecycleOwner.Unloaded += (s, e) => dispatcherTimer.Stop();
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
                Title = Framework.Lang.T("编辑计时器"),
                Width = 350,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = _mainWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushDialogBG")
            };

            var mainPanel = new StackPanel { Margin = new Thickness(20) };

            // 名称
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("名称："), Margin = new Thickness(0, 0, 0, 5), Foreground = (Brush)FindResource("BrushTextMain") });
            var nameBox = new TextBox { Text = timer.Name, Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(nameBox);

            // 超时时间
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("最大时长（秒）："), Margin = new Thickness(0, 0, 0, 5), Foreground = (Brush)FindResource("BrushTextMain") });
            var timeoutBox = new TextBox { Text = timer.TimeoutSeconds.ToString(), Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(timeoutBox);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            
            var saveBtn = new Button
            {
                Content = Framework.Lang.T("保存"),
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = (Brush)FindResource("BrushAccent"),
                Foreground = (Brush)FindResource("BrushAccentText")
            };
            saveBtn.Click += (s, e) =>
            {
                timer.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Framework.Lang.T("计时器") : nameBox.Text;
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
                Content = Framework.Lang.T("取消"),
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

            // 用 Grid 作为内容容器，使其唯一子项（文件列表/拖拽区）在固定高度时可垂直擑满
            var container = new Grid();

            // 拖拽事件处理委托
            Panel? fileAreaListContainer = null; // 稍后赋值，用于检测落点是否在目录项上

            // 判断拖放位置是否在某个目录项上
            bool IsDropOverFolderItem(DragEventArgs e)
            {
                if (fileAreaListContainer == null) return false;
                try
                {
                    var pt = e.GetPosition(fileAreaListContainer);
                    var hit = VisualTreeHelper.HitTest(fileAreaListContainer, pt);
                    if (hit == null) return false;
                    var dep = hit.VisualHit;
                    while (dep != null && dep != fileAreaListContainer)
                    {
                        if (dep is FrameworkElement fe)
                        {
                            // 检查 Tag 是否标记为文件夹项
                            if (fe.Tag is string t && t == "__folder__") return true;
                            // 检查 DataContext
                            if (fe.DataContext is FileAreaItem fai && fai.IsFolder) return true;
                        }
                        dep = VisualTreeHelper.GetParent(dep);
                    }
                }
                catch { }
                return false;
            }

            void HandleFileDragEnter(object sender, DragEventArgs e, Border zone)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    // 如果落在目录项上，不处理（让目录项自己处理）
                    if (IsDropOverFolderItem(e)) return;
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
                    // 如果落在目录项上，不添加到列表（由目录项的 HandleDropOnFolder 处理）
                    if (IsDropOverFolderItem(e))
                    {
                        e.Handled = true;
                        return;
                    }
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
                    // 截止事件冒泡：文件已落入文件区，阻止继续传递到底层窗口（MainWindow.Window_Drop），
                    // 避免重复弹出 FileDropDialog。仅当文件未被文件区接收时，底层窗口才会弹窗提示用户。
                    e.Handled = true;
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

            if (item.FileAreaLayout == "Popup")
            {
                // ===== 弹出显示布局：仅显示一个文件夹图标+label，点击弹出浮层列出所有文件项 =====
                // 弹出布局本质紧凑，直接清除固定高度设置，使其随内容自动缩放
                if (item.PanelHeight.HasValue)
                {
                    item.PanelHeight = null;
                    _configManager?.Save();
                }
                container.Children.Add(BuildFileAreaPopupContent(item));
            }
            else if (item.FileItems.Count == 0)
            {
                dropZone.Child = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("拖拽文件到此处"),
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
                // 文件列表（支持多种布局 + 拖拽）
                var listBorder = new Border
                {
                    AllowDrop = true,
                    Background = Brushes.Transparent,
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4)
                };

                listBorder.DragEnter += (s, e) => HandleFileDragEnter(s, e, listBorder);
                listBorder.DragOver += (s, e) =>
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    {
                        // 如果拖在目录项上，不设置 Effects（让目录项自己的 DragOver 处理）
                        if (!IsDropOverFolderItem(e))
                        {
                            e.Effects = DragDropEffects.Copy;
                        }
                    }
                    e.Handled = true;
                };
                listBorder.DragLeave += (s, e) => HandleFileDragLeave(s, e, listBorder);
                listBorder.Drop += (s, e) => { listBorder.BorderBrush = (Brush)FindResource("BrushBorder"); HandleFileDrop(s, e); };

                Panel listContainer;
                bool isIconLayout = item.FileAreaLayout == "LargeIcons" || item.FileAreaLayout == "SmallIcons";
                int cols = Math.Clamp(item.FileAreaColumns, 1, 4);

                if (isIconLayout)
                {
                    var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                    listContainer = wrap;
                }
                else if (cols > 1)
                {
                    var grid = new UniformGrid { Columns = cols };
                    listContainer = grid;
                }
                else
                {
                    var sp = new StackPanel();
                    listContainer = sp;
                }

                listBorder.Child = listContainer;
                fileAreaListContainer = listContainer; // 供 IsDropOverFolderItem 使用

                foreach (var fileItem in item.FileItems.OrderByDescending(f => f.AddedAt))
                {
                    string filePath = fileItem.Path;
                    bool isFolder = fileItem.IsFolder;
                    bool isCloud = fileItem.IsCloudFile;
                    var capturedFileItem = fileItem;

                    if (isIconLayout)
                    {
                        // ===== 图标布局（大/小图标）=====
                        bool isLarge = item.FileAreaLayout == "LargeIcons";
                        double iconSize = isLarge ? 48 : 28;
                        double itemW = isLarge ? 80 : 60;

                        var iconStack = new StackPanel
                        {
                            Width = itemW,
                            Margin = new Thickness(4),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Cursor = Cursors.Hand,
                            Tag = isFolder ? "__folder__" : null
                        };

                        // 图标（云文件/云目录带云标记；云项保留 Grid 引用供上传进度环叠加）
                        Grid? cloudIconGrid = isCloud ? BuildCloudIcon(isFolder, iconSize) : null;
                        FrameworkElement iconElement = cloudIconGrid != null
                            ? cloudIconGrid
                            : new Emoji.Wpf.TextBlock
                            {
                                Text = isFolder ? "📁" : "📄",
                                FontSize = iconSize,
                                HorizontalAlignment = HorizontalAlignment.Center
                            };
                        iconStack.Children.Add(iconElement);

                        // 名称
                        var nameText = new Emoji.Wpf.TextBlock
                        {
                            Text = fileItem.Name,
                            FontSize = isLarge ? 12 : 11,
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            TextWrapping = TextWrapping.Wrap,
                            MaxHeight = isLarge ? 32 : 16,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            ToolTip = isCloud ? BuildCloudToolTip(fileItem) : fileItem.Path,
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        iconStack.Children.Add(nameText);

                        // 点击打开
                        iconStack.MouseLeftButtonUp += (s, e) =>
                        {
                            if (isCloud) { OpenCloudFile(capturedFileItem); return; }
                            try
                            {
                                if (isFolder)
                                    Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false });
                                else
                                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                            }
                            catch { }
                        };

                        // 右键菜单（云文件用自定义菜单，本地文件调用资源管理器原生右键菜单）
                        iconStack.MouseRightButtonDown += (s, e) =>
                        {
                            if (isCloud)
                            {
                                var cloudMenu = BuildCloudFileContextMenu(capturedFileItem, item);
                                cloudMenu.PlacementTarget = iconStack;
                                cloudMenu.IsOpen = true;
                                e.Handled = true;
                                return;
                            }
                            iconStack.Opacity = 0.7;
                            var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
                            var pt = System.Windows.Forms.Cursor.Position;
                            NativeMethods.ShowExplorerContextMenu(hwnd, filePath, pt.X, pt.Y);
                            iconStack.Opacity = 1.0;
                            e.Handled = true;
                        };

                        // 编辑模式：hover 显示删除
                        if (_isEditMode)
                        {
                            var delIconBtn = new Button
                            {
                                Content = "✕", FontSize = 9,
                                Width = 16, Height = 16,
                                Background = Brushes.IndianRed,
                                Foreground = Brushes.White,
                                BorderThickness = new Thickness(0),
                                Padding = new Thickness(0),
                                Cursor = Cursors.Hand,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Visibility = Visibility.Collapsed,
                                Margin = new Thickness(0, -iconSize - 4, 0, 0)
                            };
                            var delIconBorder = new Border
                            {
                                CornerRadius = new CornerRadius(8),
                                Child = delIconBtn
                            };
                            delIconBtn.Click += (s, e) =>
                            {
                                item.FileItems.Remove(capturedFileItem);
                                _configManager?.Save();
                                RenderPanels();
                            };
                            iconStack.MouseEnter += (s, e) => delIconBorder.Visibility = Visibility.Visible;
                            iconStack.MouseLeave += (s, e) => delIconBorder.Visibility = Visibility.Collapsed;

                            // 用 Grid 叠放图标和删除按钮
                            var iconGrid = new Grid();
                            iconGrid.Children.Add(iconStack);
                            iconGrid.Children.Add(delIconBorder);
                            listContainer.Children.Add(iconGrid);
                        }
                        else
                        {
                            listContainer.Children.Add(iconStack);
                        }

                        // 目录项支持拖拽接收
                        if (isFolder)
                        {
                            iconStack.AllowDrop = true;
                            iconStack.DragEnter += (s, e) =>
                            {
                                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                                {
                                    e.Effects = DragDropEffects.Copy;
                                    iconStack.Opacity = 0.6;
                                }
                                e.Handled = true;
                            };
                            iconStack.DragOver += (s, e) =>
                            {
                                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                                {
                                    e.Effects = DragDropEffects.Copy;
                                }
                                e.Handled = true;
                            };
                            iconStack.DragLeave += (s, e) => iconStack.Opacity = 1.0;
                            iconStack.Drop += (s, e) =>
                            {
                                iconStack.Opacity = 1.0;
                                HandleDropOnFolder(s, e, capturedFileItem, item, cloudIconGrid);
                            };
                        }
                    }
                    else
                    {
                        // ===== 列表布局（支持多列）=====
                        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Tag = isFolder ? "__folder__" : null };

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
                        openBtn.Click += (s, e) =>
                        {
                            if (isCloud) { OpenCloudFile(capturedFileItem); return; }
                            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                            catch { }
                        };

                        // 图标 + 名称（云文件/云目录带云标记）
                        var nameBlock = new Emoji.Wpf.TextBlock
                        {
                            Text = fileItem.Name,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            ToolTip = isCloud ? BuildCloudToolTip(fileItem) : fileItem.Path
                        };
                        var iconNamePanel = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center,
                            Cursor = Cursors.Hand
                        };
                        Grid? cloudIconGrid = isCloud ? BuildCloudIcon(isFolder, 15) : null;
                        FrameworkElement iconEl = cloudIconGrid != null
                            ? cloudIconGrid
                            : new Emoji.Wpf.TextBlock
                            {
                                Text = isFolder ? "📁" : "📄",
                                FontSize = 15,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 5, 0)
                            };
                        iconNamePanel.Children.Add(iconEl);
                        iconNamePanel.Children.Add(nameBlock);

                        iconNamePanel.MouseLeftButtonUp += (s, e) =>
                        {
                            if (isCloud) { OpenCloudFile(capturedFileItem); return; }
                            try
                            {
                                if (isFolder)
                                    Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false });
                                else
                                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
                            }
                            catch { }
                        };

                        DockPanel.SetDock(delBtn, Dock.Right);
                        DockPanel.SetDock(openBtn, Dock.Right);
                        row.Children.Add(delBtn);
                        row.Children.Add(openBtn);
                        row.Children.Add(iconNamePanel);

                        // 右键菜单（云文件用自定义菜单，本地文件调用资源管理器原生右键菜单）
                        var highlightBrush = (Brush)FindResource("BrushListViewItemMouseOver");
                        row.MouseRightButtonDown += (s, e) =>
                        {
                            if (isCloud)
                            {
                                var cloudMenu = BuildCloudFileContextMenu(capturedFileItem, item);
                                cloudMenu.PlacementTarget = row;
                                cloudMenu.IsOpen = true;
                                e.Handled = true;
                                return;
                            }
                            row.Background = highlightBrush;
                            var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
                            var pt = System.Windows.Forms.Cursor.Position;
                            NativeMethods.ShowExplorerContextMenu(hwnd, filePath, pt.X, pt.Y);
                            row.Background = Brushes.Transparent;
                            e.Handled = true;
                        };

                        // hover 显示按钮
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

                        // 目录项支持拖拽接收
                        if (isFolder)
                        {
                            row.AllowDrop = true;
                            row.DragEnter += (s, e) =>
                            {
                                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                                {
                                    e.Effects = DragDropEffects.Copy;
                                    row.Background = (Brush)FindResource("BrushListViewItemMouseOver");
                                }
                                e.Handled = true;
                            };
                            row.DragOver += (s, e) =>
                            {
                                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                                {
                                    e.Effects = DragDropEffects.Copy;
                                }
                                e.Handled = true;
                            };
                            row.DragLeave += (s, e) => row.Background = Brushes.Transparent;
                            row.Drop += (s, e) =>
                            {
                                row.Background = Brushes.Transparent;
                                HandleDropOnFolder(s, e, capturedFileItem, item, cloudIconGrid);
                            };
                        }

                        listContainer.Children.Add(row);
                    }
                }
                container.Children.Add(listBorder);
            }

            // 基于 ModuleControl 基础组件包装
            var module = CreateModuleControl(Framework.Lang.T("📁 文件区"), item);

            var addFileMi = new MenuItem { Header = Framework.Lang.T("添加文件") };
            addFileMi.Click += (s, e) => AddFileToArea(item);
            module.AddCustomMenuItem(addFileMi);

            var addFolderMi = new MenuItem { Header = Framework.Lang.T("添加目录") };
            addFolderMi.Click += (s, e) => AddFolderToArea(item);
            module.AddCustomMenuItem(addFolderMi);

            var addCloudFileMi = new MenuItem { Header = Framework.Lang.T("☁️ 添加云文件") };
            addCloudFileMi.Click += (s, e) => AddCloudFileToArea(item);
            module.AddCustomMenuItem(addCloudFileMi);

            // 布局子菜单
            var layoutMenu = new MenuItem { Header = Framework.Lang.T("布局") };
            var largeIconsMi = new MenuItem { Header = Framework.Lang.T("大图标"), IsCheckable = true, IsChecked = item.FileAreaLayout == "LargeIcons" };
            largeIconsMi.Click += (s, e) => { item.FileAreaLayout = "LargeIcons"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(largeIconsMi);
            var smallIconsMi = new MenuItem { Header = Framework.Lang.T("小图标"), IsCheckable = true, IsChecked = item.FileAreaLayout == "SmallIcons" };
            smallIconsMi.Click += (s, e) => { item.FileAreaLayout = "SmallIcons"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(smallIconsMi);
            var listMi = new MenuItem { Header = Framework.Lang.T("列表"), IsCheckable = true, IsChecked = item.FileAreaLayout == "List" };
            listMi.Click += (s, e) => { item.FileAreaLayout = "List"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(listMi);
            var popupMi = new MenuItem { Header = Framework.Lang.T("弹出显示"), IsCheckable = true, IsChecked = item.FileAreaLayout == "Popup" };
            popupMi.Click += (s, e) => { item.FileAreaLayout = "Popup"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(popupMi);
            module.AddCustomMenuItem(layoutMenu);

            // 列数子菜单
            var columnsMenu = new MenuItem { Header = Framework.Lang.T("列") };
            for (int col = 1; col <= 4; col++)
            {
                var colItem = new MenuItem { Header = Framework.Lang.TF("{0}列", col), IsCheckable = true, IsChecked = item.FileAreaColumns == col };
                int colCount = col;
                colItem.Click += (s, e) => { item.FileAreaColumns = colCount; _configManager?.Save(); RenderPanels(); };
                columnsMenu.Items.Add(colItem);
            }
            module.AddCustomMenuItem(columnsMenu);

            module.SetContent(container);
            return module;
        }

        private void AddFileToArea(PanelItem item)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = Framework.Lang.T("选择文件")
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
                Title = Framework.Lang.T("选择文件夹"),
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

        /// <summary>弹出云文件导入对话框，选择云盘或输入云文件ID导入</summary>
        private void AddCloudFileToArea(PanelItem item)
        {
            var dialog = new AddCloudFileDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.Results is { Count: > 0 })
            {
                item.FileItems ??= new List<FileAreaItem>();
                item.FileItems.AddRange(dialog.Results);
                _configManager?.Save();
                RenderPanels();
            }
        }

        /// <summary>云文件 ToolTip：云盘名 + 文件ID/链接</summary>
        private static string BuildCloudToolTip(FileAreaItem fileItem)
        {
            string space = string.IsNullOrEmpty(fileItem.CloudSpaceName) ? Framework.Lang.T("网络文件") : fileItem.CloudSpaceName;
            string target = fileItem.CloudFileId ?? fileItem.CloudUrl ?? fileItem.Path;
            return $"☁️ {space}：{target}";
        }

        /// <summary>打开云文件：云目录直接用云盘查看器打开并定位；链接用浏览器打开；纯ID（非网址）复制到剪贴板</summary>
        private void OpenCloudFile(FileAreaItem fileItem)
        {
            // 百度网盘：用百度网盘窗口打开
            if (fileItem.CloudSpaceName == "百度网盘")
            {
                if (fileItem.IsFolder && !string.IsNullOrEmpty(fileItem.CloudFileId))
                    new BaiduPanWindow(fileItem.CloudFileId, fileItem.Name).Show();
                else
                    new BaiduPanWindow().Show();
                return;
            }

            // 云目录：直接打开云盘查看器并定位到该目录
            if (fileItem.IsFolder && !string.IsNullOrEmpty(fileItem.CloudFileId))
            {
                new CloudDriveWindow(fileItem.CloudFileId, fileItem.Name).Show();
                return;
            }

            try
            {
                string url = fileItem.CloudUrl ?? "";
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (!string.IsNullOrEmpty(fileItem.CloudFileId))
                {
                    // 纯ID（非链接）：复制到剪贴板，方便粘贴到对应云盘客户端
                    Clipboard.SetText(fileItem.CloudFileId);
                }
            }
            catch { }
        }

        /// <summary>云文件右键菜单：打开 / 复制链接 / 复制文件ID / 删除</summary>
        private ContextMenu BuildCloudFileContextMenu(FileAreaItem fileItem, PanelItem panelItem)
        {
            var ctx = new ContextMenu();

            var openMi = new MenuItem { Header = Framework.Lang.T("打开") };
            openMi.Click += (s, e) => OpenCloudFile(fileItem);
            ctx.Items.Add(openMi);

            var copyLinkMi = new MenuItem { Header = Framework.Lang.T("复制链接") };
            copyLinkMi.Click += (s, e) =>
            {
                try { Clipboard.SetText(fileItem.CloudUrl ?? fileItem.Path); } catch { }
            };
            ctx.Items.Add(copyLinkMi);

            if (!string.IsNullOrEmpty(fileItem.CloudFileId))
            {
                var copyIdMi = new MenuItem { Header = Framework.Lang.T("复制文件ID") };
                copyIdMi.Click += (s, e) =>
                {
                    try { Clipboard.SetText(fileItem.CloudFileId); } catch { }
                };
                ctx.Items.Add(copyIdMi);
            }

            ctx.Items.Add(new Separator());

            var delMi = new MenuItem { Header = Framework.Lang.T("删除") };
            delMi.Click += (s, e) =>
            {
                panelItem.FileItems?.Remove(fileItem);
                _configManager?.Save();
                RenderPanels();
            };
            ctx.Items.Add(delMi);

            return ctx;
        }

        /// <summary>打开文件或文件夹</summary>
        private void OpenFileOrFolder(string path, bool isFolder)
        {
            try
            {
                if (isFolder)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = false });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                }
            }
            catch { }
        }

        /// <summary>构建文件项右键菜单</summary>
        private ContextMenu BuildFileItemContextMenu(FileAreaItem fileItem, PanelItem panelItem, string filePath, bool isFolder)
        {
            var ctx = new ContextMenu();
            var capturedFileItem = fileItem;

            if (isFolder)
            {
                var menuOpen = new MenuItem { Header = Framework.Lang.T("打开") };
                menuOpen.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false }); }
                    catch { }
                };
                ctx.Items.Add(menuOpen);

                var menuEmpty = new MenuItem { Header = Framework.Lang.T("清空文件夹") };
                menuEmpty.Click += (s, e) =>
                {
                    if (MessageBox.Show(Framework.Lang.TF("确定清空\n{0}\n下的所有内容？", filePath), Framework.Lang.T("确认清空"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                    {
                        try
                        {
                            foreach (var f in System.IO.Directory.GetFiles(filePath)) System.IO.File.Delete(f);
                            foreach (var d in System.IO.Directory.GetDirectories(filePath)) System.IO.Directory.Delete(d, true);
                        }
                        catch (Exception ex) { MessageBox.Show(Framework.Lang.TF("清空失败：{0}", ex.Message)); }
                    }
                };
                ctx.Items.Add(menuEmpty);

                var menuDel = new MenuItem { Header = Framework.Lang.T("删除") };
                menuDel.Click += (s, e) =>
                {
                    if (MessageBox.Show(Framework.Lang.TF("确定删除文件夹\n{0}\n及其所有内容？", filePath), Framework.Lang.T("确认删除"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                    {
                        try
                        {
                            System.IO.Directory.Delete(filePath, true);
                            panelItem.FileItems.Remove(capturedFileItem);
                            _configManager?.Save();
                            RenderPanels();
                        }
                        catch (Exception ex) { MessageBox.Show(Framework.Lang.TF("删除失败：{0}", ex.Message)); }
                    }
                };
                ctx.Items.Add(menuDel);

                ctx.Items.Add(new Separator());
                var menuProps = new MenuItem { Header = Framework.Lang.T("属性") };
                menuProps.Click += (s, e) =>
                {
                    try { NativeMethods.ShowFileProperties(filePath); }
                    catch { }
                };
                ctx.Items.Add(menuProps);
            }
            else
            {
                var menuOpen = new MenuItem { Header = Framework.Lang.T("打开") };
                menuOpen.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                    catch { }
                };
                ctx.Items.Add(menuOpen);

                var menuLocate = new MenuItem { Header = Framework.Lang.T("定位") };
                menuLocate.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false }); }
                    catch { }
                };
                ctx.Items.Add(menuLocate);

                var menuDel = new MenuItem { Header = Framework.Lang.T("删除") };
                menuDel.Click += (s, e) =>
                {
                    if (MessageBox.Show(Framework.Lang.TF("确定删除文件\n{0}？", filePath), Framework.Lang.T("确认删除"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                    {
                        try
                        {
                            System.IO.File.Delete(filePath);
                            panelItem.FileItems.Remove(capturedFileItem);
                            _configManager?.Save();
                            RenderPanels();
                        }
                        catch (Exception ex) { MessageBox.Show(Framework.Lang.TF("删除失败：{0}", ex.Message)); }
                    }
                };
                ctx.Items.Add(menuDel);

                ctx.Items.Add(new Separator());
                var menuProps = new MenuItem { Header = Framework.Lang.T("属性") };
                menuProps.Click += (s, e) =>
                {
                    try { NativeMethods.ShowFileProperties(filePath); }
                    catch { }
                };
                ctx.Items.Add(menuProps);
            }

            // 从列表移除（不删除磁盘文件）
            ctx.Items.Add(new Separator());
            var menuRemoveFromList = new MenuItem { Header = Framework.Lang.T("从列表移除") };
            menuRemoveFromList.Click += (s, e) =>
            {
                panelItem.FileItems.Remove(capturedFileItem);
                _configManager?.Save();
                RenderPanels();
            };
            ctx.Items.Add(menuRemoveFromList);

            return ctx;
        }

        /// <summary>云操作登录态校验：未登录时提示用户并返回 false</summary>
        private bool EnsureLoggedInForCloud()
        {
            var app = (App)Application.Current;
            if (!string.IsNullOrEmpty(app.AppSettings.AccountToken)) return true;
            ThemedMessageBox.Show(Window.GetWindow(this), Framework.Lang.T("您当前尚未登录，请登录后操作。"), "提示");
            return false;
        }

        /// <summary>处理文件拖拽到文件夹（本地目录执行复制；云目录则上传到对应云盘目录）</summary>
        private void HandleDropOnFolder(object sender, DragEventArgs e, FileAreaItem folderItem, PanelItem panelItem, Grid? cloudIconGrid = null)
        {
            // 云目录：上传到对应云盘目录（需要登录）
            if (folderItem.IsCloudFile)
            {
                if (!EnsureLoggedInForCloud())
                {
                    e.Handled = true; // 截止冒泡，避免落到外层被当成“添加到文件区”
                    return;
                }
                HandleDropOnCloudFolder(e, folderItem, cloudIconGrid);
                return;
            }

            string folderPath = folderItem.Path;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var path in files)
                {
                    var fileName = System.IO.Path.GetFileName(path);
                    var destPath = System.IO.Path.Combine(folderPath, fileName);

                    try
                    {
                        if (System.IO.File.Exists(path))
                        {
                            // 复制文件到目标目录
                            System.IO.File.Copy(path, destPath, overwrite: true);
                        }
                        else if (System.IO.Directory.Exists(path))
                        {
                            // 复制目录到目标目录
                            CopyDirectory(path, destPath);
                        }

                        // 如果拖入的文件已在文件区列表中，更新其路径为目录内的新位置
                        var existingItem = panelItem.FileItems.FirstOrDefault(f => f.Path == path);
                        if (existingItem != null)
                        {
                            existingItem.Path = destPath;
                            existingItem.Name = fileName;
                        }
                        // 如果文件不在文件区列表中，也添加进去（方便用户看到）
                        else
                        {
                            panelItem.FileItems.Add(new FileAreaItem
                            {
                                Path = destPath,
                                Name = fileName,
                                IsFolder = System.IO.Directory.Exists(destPath)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Framework.Lang.TF("复制失败：{0}", ex.Message));
                    }
                }
                _configManager?.Save();
                RenderPanels();
                e.Handled = true;
            }
        }

        /// <summary>将拖入的文件/目录添加到文件区列表（返回是否已处理）</summary>
        private bool AddDroppedFilesToArea(DragEventArgs e, PanelItem item)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            item.FileItems ??= new List<FileAreaItem>();
            foreach (var path in files)
            {
                var isDir = System.IO.Directory.Exists(path);
                item.FileItems.Add(new FileAreaItem
                {
                    Path = path,
                    Name = System.IO.Path.GetFileName(path),
                    IsFolder = isDir
                });
            }
            _configManager?.Save();
            RenderPanels();
            e.Handled = true;
            return true;
        }

        /// <summary>构建云文件/云目录图标：基础图标 + 右下角云标记</summary>
        private static Grid BuildCloudIcon(bool isFolder, double iconSize)
        {
            var grid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            grid.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = isFolder ? "📁" : "📄",
                FontSize = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            grid.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "☁️",
                FontSize = Math.Max(8, iconSize * 0.45),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            });
            return grid;
        }

        /// <summary>弹出显示布局：文件夹图标+label 触发按钮，点击弹出浮层列出所有文件项（参考快捷组弹出菜单效果）</summary>
        private UIElement BuildFileAreaPopupContent(PanelItem item)
        {
            // label：取模块标题（去掉开头 emoji 避免与文件夹图标重复）并附加项数
            string title = string.IsNullOrEmpty(item.Title) ? Framework.Lang.T("文件区") : item.Title;
            string label = StripLeadingEmoji(title);
            if (label.Length == 0) label = Framework.Lang.T("文件区");
            label = $"{label}（{item.FileItems.Count}）";

            // 竖排：上方大图标 + 下方 label（桌面快捷方式风格）
            var triggerContent = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            triggerContent.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "📁",
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            triggerContent.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = label,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("BrushTextMain")
            });

            var triggerBtn = new Button
            {
                Content = triggerContent,
                Style = (Style)FindResource("GhostButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2),
                Padding = new Thickness(10, 4, 10, 4)
            };

            var itemPopup = new Popup
            {
                PlacementTarget = triggerBtn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var popupStack = new StackPanel { Orientation = Orientation.Vertical };
            if (item.FileItems.Count == 0)
            {
                // 空状态提示：点击直接打开添加文件对话框（并关闭弹窗）
                popupStack.Children.Add(BuildEmptyAddTip(Framework.Lang.T("暂无文件，点击添加"),
                    () => { itemPopup.IsOpen = false; AddFileToArea(item); },
                    new Thickness(10, 8, 10, 8)));
            }
            else
            {
                foreach (var fileItem in item.FileItems.OrderByDescending(f => f.AddedAt))
                    popupStack.Children.Add(BuildFileAreaPopupItem(fileItem, item, itemPopup));
            }

            var popupScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 360,
                Content = popupStack
            };

            var popupBorder = new Border
            {
                Background = (Brush)FindResource("BrushBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 190,
                Child = popupScroll
            };
            itemPopup.Child = popupBorder;

            triggerBtn.Click += (s, e) => { itemPopup.IsOpen = !itemPopup.IsOpen; };

            // 拖拽支持：拖文件悬停到触发图标时自动浮动出列表，便于继续拖入列表内的目录；
            // 直接释放在触发图标上 = 添加到本文件区
            triggerBtn.AllowDrop = true;
            triggerBtn.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    itemPopup.IsOpen = true; // 浮动出列表供选择目标目录
                    e.Effects = DragDropEffects.Copy;
                }
                e.Handled = true;
            };
            triggerBtn.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            triggerBtn.Drop += (s, e) =>
            {
                itemPopup.IsOpen = false;
                AddDroppedFilesToArea(e, item); // 拖到主弹出文件夹 = 添加到本文件区
            };

            // 浮层空白处也可接收拖放：释放 = 添加到本文件区（目录行自己处理并截止冒泡）
            popupBorder.AllowDrop = true;
            popupBorder.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            popupBorder.Drop += (s, e) =>
            {
                itemPopup.IsOpen = false;
                AddDroppedFilesToArea(e, item);
            };

            return triggerBtn;
        }

        /// <summary>弹出显示布局浮层内的文件项：图标+名称，点击打开，云文件支持右键菜单</summary>
        private UIElement BuildFileAreaPopupItem(FileAreaItem fileItem, PanelItem panelItem, Popup popup)
        {
            bool isFolder = fileItem.IsFolder;
            bool isCloud = fileItem.IsCloudFile;
            string filePath = fileItem.Path;

            var row = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Grid? cloudIconGrid = isCloud ? BuildCloudIcon(isFolder, 15) : null;
            FrameworkElement iconEl = cloudIconGrid != null
                ? cloudIconGrid
                : new Emoji.Wpf.TextBlock
                {
                    Text = isFolder ? "📁" : "📄",
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                };
            sp.Children.Add(iconEl);
            sp.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = fileItem.Name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("BrushTextMain"),
                ToolTip = isCloud ? BuildCloudToolTip(fileItem) : fileItem.Path
            });
            row.Child = sp;

            var hoverBrush = (Brush)FindResource("BrushListViewItemMouseOver");
            row.MouseEnter += (s, e) => row.Background = hoverBrush;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;

            // 拖拽接收：目录行→复制到该目录（云目录走上传）；文件行→添加到本文件区
            row.AllowDrop = true;
            row.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    row.Background = hoverBrush;
                }
                e.Handled = true;
            };
            row.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            row.DragLeave += (s, e) => row.Background = Brushes.Transparent;
            row.Drop += (s, e) =>
            {
                row.Background = Brushes.Transparent;
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                popup.IsOpen = false;
                if (isFolder)
                {
                    // 目录行：把文件复制进该目录（云目录走上传流程）
                    HandleDropOnFolder(s, e, fileItem, panelItem, cloudIconGrid);
                }
                else
                {
                    // 文件行：拖到云文件同样需要登录；本地文件则把拖入的文件添加到本文件区
                    if (fileItem.IsCloudFile && !EnsureLoggedInForCloud()) return;
                    AddDroppedFilesToArea(e, panelItem);
                }
            };

            // 点击打开（并关闭浮层）：文件夹用资源管理器打开，文件直接打开，云文件走云逻辑
            row.MouseLeftButtonUp += (s, e) =>
            {
                popup.IsOpen = false;
                if (isCloud) { OpenCloudFile(fileItem); return; }
                try
                {
                    if (isFolder)
                        Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false });
                    else
                        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                }
                catch { }
            };

            // 云文件右键菜单
            row.MouseRightButtonDown += (s, e) =>
            {
                if (isCloud)
                {
                    var cloudMenu = BuildCloudFileContextMenu(fileItem, panelItem);
                    cloudMenu.PlacementTarget = row;
                    cloudMenu.IsOpen = true;
                    e.Handled = true;
                }
            };

            return row;
        }

        /// <summary>去掉标题开头的 emoji/符号（如“📁 文件区”→“文件区”）</summary>
        private static string StripLeadingEmoji(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsSurrogate(c)) { i += 2; continue; }                    // emoji 代理对
                if (c == '\uFE0F' || char.IsWhiteSpace(c)) { i++; continue; }     // 变体选择符/空白
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(s, i);
                if (cat == System.Globalization.UnicodeCategory.OtherSymbol) { i++; continue; }
                break;
            }
            return s.Substring(i).Trim();
        }

        /// <summary>拖拽文件到云目录：上传到对应云盘目录，并在目录图标上显示环形进度条</summary>
        private async void HandleDropOnCloudFolder(DragEventArgs e, FileAreaItem folderItem, Grid? iconGrid)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var localFiles = files.Where(System.IO.File.Exists).ToList();
            if (localFiles.Count == 0) return;
            string parentId = folderItem.CloudFileId ?? "";
            if (string.IsNullOrEmpty(parentId)) return;

            // 在云目录图标上挂载环形进度条
            RingProgress? ring = null;
            if (iconGrid != null)
            {
                double size = iconGrid.ActualWidth > 0 ? Math.Max(iconGrid.ActualWidth, iconGrid.ActualHeight) : 32;
                ring = new RingProgress(size);
                iconGrid.Children.Add(ring.Root);
            }

            int total = localFiles.Count;
            int done = 0;
            foreach (var path in localFiles)
            {
                var ringRef = ring;
                int doneRef = done;
                bool success = await Task.Run(() => CloudDriveService.Instance.UploadFileAsync(path, parentId, pct =>
                {
                    double frac = (doneRef + pct / 100.0) / total;
                    Dispatcher.BeginInvoke(new Action(() => ringRef?.SetProgress(frac)));
                }));
                if (success) done++;
                ring?.SetProgress((double)done / total);
            }

            // 短暂停留满格状态后移除进度条
            if (ring != null && iconGrid != null)
            {
                ring.SetProgress(1);
                await Task.Delay(500);
                iconGrid.Children.Remove(ring.Root);
            }
        }

        /// <summary>环形进度条：叠加在图标上显示上传进度</summary>
        private sealed class RingProgress
        {
            public Grid Root { get; }
            private readonly Path _arc;
            private readonly double _center;
            private readonly double _radius;

            public RingProgress(double size)
            {
                double thickness = Math.Max(2.5, size * 0.09);
                _center = size / 2;
                _radius = (size - thickness) / 2;

                Root = new Grid
                {
                    Width = size,
                    Height = size,
                    IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // 轨道（整环，半透明灰）
                Root.Children.Add(new Ellipse
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                    StrokeThickness = thickness
                });
                // 进度弧（主题色）
                _arc = new Path
                {
                    Stroke = (Application.Current.TryFindResource("BrushAccent") as Brush) ?? Brushes.DodgerBlue,
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                Root.Children.Add(_arc);
            }

            /// <summary>设置进度（0~1）</summary>
            public void SetProgress(double fraction)
            {
                _arc.Data = fraction <= 0.001 ? Geometry.Empty : BuildArc(fraction);
            }

            private Geometry BuildArc(double fraction)
            {
                fraction = Math.Clamp(fraction, 0, 0.9999);
                double angle = fraction * 2 * Math.PI;
                double endX = _center + _radius * Math.Sin(angle);
                double endY = _center - _radius * Math.Cos(angle);
                var figure = new PathFigure { StartPoint = new Point(_center, _center - _radius) };
                figure.Segments.Add(new ArcSegment
                {
                    Point = new Point(endX, endY),
                    Size = new Size(_radius, _radius),
                    IsLargeArc = fraction > 0.5,
                    SweepDirection = SweepDirection.Clockwise
                });
                var geo = new PathGeometry();
                geo.Figures.Add(figure);
                return geo;
            }
        }

        /// <summary>递归复制目录</summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            System.IO.Directory.CreateDirectory(destDir);
            foreach (var file in System.IO.Directory.GetFiles(sourceDir))
            {
                System.IO.File.Copy(file, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file)), true);
            }
            foreach (var dir in System.IO.Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir)));
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

            var module = CreateModuleControl(Framework.Lang.T("📋 剪贴板历史"), item);

            // 标题栏右侧：历史弹窗按钮（点击弹出侧边 popup，列出最近 50 条）
            var expandBtn = new Button
            {
                Content = "⤢",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("查看最近剪贴板历史")
            };
            expandBtn.Click += (s, e) => ShowClipboardHistoryPopup(expandBtn);
            DockPanel.SetDock(expandBtn, Dock.Right);
            module.Header.Children.Add(expandBtn);

            module.SetContent(container);
            return module;
        }

        private void EnsureClipboardServiceStarted()
        {
            if (_clipboardService != null) return;

            // 使用全局单例，与 MCP / AI 工具共享同一份剪贴板历史数据
            _clipboardService = ClipboardService.Instance;
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
                _clipboardHistoryContainer.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无记录，复制内容后自动显示"),
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

            // 右键菜单：从历史删除该条记录
            var histMenu = new ContextMenu();
            var histDelItem = new MenuItem { Header = Framework.Lang.T("从历史删除") };
            histDelItem.Click += (s, e) => _clipboardService?.RemoveEntry(entry);
            histMenu.Items.Add(histDelItem);
            row.ContextMenu = histMenu;

            // 内容类型图标
            string icon = ClipboardService.GetContentTypeIcon(entry.ContentType);
            string timeStr = entry.Time.ToString("HH:mm:ss");
            var iconText = new Emoji.Wpf.TextBlock
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
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("复制到剪贴板"),
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

            var textBlock = new Emoji.Wpf.TextBlock
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

            // 拖拽到「剪贴板收藏」模块：按住移动超过阈值发起拖拽（单击仍为复制）
            Point? dragStart = null;
            row.MouseLeftButtonDown += (s, e) => { dragStart = e.GetPosition(null); };
            row.MouseMove += (s, e) =>
            {
                if (dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragStart = null;
                var data = new DataObject();
                data.SetData(ClipboardDragFormat, entry);
                data.SetText(entry.Text);
                DragDrop.DoDragDrop(row, data, DragDropEffects.Copy);
            };

            return row;
        }

        /// <summary>侧边弹窗：列出最近最多 50 条剪贴板记录（点击复制、右键删除，实时刷新）</summary>
        private void ShowClipboardHistoryPopup(UIElement placementTarget)
        {
            // 已打开则先关闭旧弹窗
            if (_clipboardHistoryPopup != null)
            {
                _clipboardHistoryPopup.IsOpen = false;
                _clipboardHistoryPopup = null;
            }

            EnsureClipboardServiceStarted();

            var listPanel = new StackPanel();

            Action rebuild = () =>
            {
                listPanel.Children.Clear();
                var history = _clipboardService?.ClipboardHistory;
                if (history == null || history.Count == 0)
                {
                    listPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = Framework.Lang.T("暂无记录，复制内容后自动显示"),
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontSize = (double)FindResource("FontSizeTiny"),
                        Margin = new Thickness(4)
                    });
                    return;
                }
                foreach (var entry in history.Take(50))
                    listPanel.Children.Add(BuildClipboardHistoryItem(entry));
            };
            rebuild();

            // 弹窗打开期间实时刷新（剪贴板事件在非 UI 线程触发，需调度）
            Action onChanged = () => Dispatcher.BeginInvoke(rebuild, DispatcherPriority.Background);
            if (_clipboardService != null)
                _clipboardService.ClipboardChanged += onChanged;

            // 停靠左缘→弹窗在右；停靠右缘→弹窗在左
            var placement = _mainWindow != null && _mainWindow.DockSide == DockEdge.Right
                ? PlacementMode.Left : PlacementMode.Right;

            var popup = new Popup
            {
                PlacementTarget = placementTarget,
                Placement = placement,
                StaysOpen = false, // 点击弹窗外自动关闭
                AllowsTransparency = true
            };
            popup.Closed += (s, e) =>
            {
                if (_clipboardService != null)
                    _clipboardService.ClipboardChanged -= onChanged;
                _clipboardHistoryPopup = null;
            };

            var scroll = new ScrollViewer
            {
                Width = 340,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listPanel
            };

            popup.Child = new Border
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Margin = new Thickness(10), // 为阴影留出空间
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.5,
                    Color = Colors.Black
                },
                Child = scroll
            };
            popup.IsOpen = true;
            _clipboardHistoryPopup = popup;
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
            else if (type == PanelType.TodoList)
                newPanel.TodoItems = new List<TodoItem>();

            // 自动开启编辑模式（需在构建 widget 前，确保新模块挂上编辑模式拖拽手柄）
            _isEditMode = true;
            _currentWindowConfig.IsEditMode = true;

            AddTopLevelPanel(newPanel);
        }

        private void AddQuickGroupPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            var menuItem = sender as UIElement ?? this;

            // 先输入分组名称，再创建对应的快捷方式面板（面板即分组，数据为面板自有）
            var name = ShowGroupNameDialog();
            if (string.IsNullOrEmpty(name)) return;

            var newPanel = new PanelItem
            {
                Type = PanelType.QuickGroup,
                QuickGroupName = name,
                QuickColumns = 2,
                QuickItems = new List<QuickItem>()
            };
            _currentWindowConfig.Panels.Add(newPanel);

            _configManager.Save();
            RenderPanels();

            // 延迟弹出添加项菜单（重绘后 _quickGroupUIService 已指向新面板）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _quickGroupUIService?.ShowAddShortcutMenuForGroup(menuItem, newPanel.Id);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>轻量级分组名称输入对话框，返回分组名；取消返回 null</summary>
        private string ShowGroupNameDialog()
        {
            string resultName = null;
            var dialog = new Window
            {
                Title = Framework.Lang.T("新建快捷方式"),
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
            namePanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("分组名:"), Foreground = (Brush)FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center, Width = 60 });
            var nameBox = new TextBox { Width = 220, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            namePanel.Children.Add(nameBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 15, 10, 10) };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show(Framework.Lang.T("请输入分组名称")); return; }
                resultName = name;
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 1);
            grid.Children.Add(btnPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
            return resultName;
        }
        private void AddGroupPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            AddTopLevelPanel(new PanelItem
            {
                Type = PanelType.Group,
                Title = Framework.Lang.T("新分组"),
                ChildPanels = new List<PanelItem>(),
                IsExpanded = true
            });
        }

        private void AddModuleToGroup(PanelItem groupItem, PanelType type, int insertIndex = -1)
        {
            groupItem.ChildPanels ??= new List<PanelItem>();

            PanelItem newPanelItem;
            if (type == PanelType.Timer)
            {
                // 对于计时器，创建空的容器，不添加默认计时器
                newPanelItem = new PanelItem 
                { 
                    Type = PanelType.Timer,
                    TimerItems = new List<TimerItem>(),  // ✅ 空列表，不添加默认项
                    SkipWrapper = true  // ✅ 标记跳过外层容器
                };
            }
            else if (type == PanelType.Countdown)
            {
                newPanelItem = new PanelItem
                {
                    Type = PanelType.Countdown,
                    CountdownItems = new List<CountdownItem>(),
                    SkipWrapper = true
                };
            }
            else if (type == PanelType.Pomodoro)
            {
                newPanelItem = new PanelItem
                {
                    Type = PanelType.Pomodoro,
                    Pomodoro = new PomodoroSettings(),
                    SkipWrapper = true
                };
            }
            else
            {
                newPanelItem = new PanelItem { Type = type };
                // 新面板初始化为空，避免触发全局迁移
                if (type == PanelType.StickyNotes)
                    newPanelItem.StickyNotes = new List<StickyNote>();
                else if (type == PanelType.TodoList)
                    newPanelItem.TodoItems = new List<TodoItem>();
            }

            InsertChildPanel(groupItem, newPanelItem, insertIndex);
            _configManager?.Save();
            RenderPanels();
        }

        /// <summary>向分组插入子面板：insertIndex >= 0 时插入到指定位置，否则追加到末尾</summary>
        private static void InsertChildPanel(PanelItem groupItem, PanelItem child, int insertIndex)
        {
            groupItem.ChildPanels ??= new List<PanelItem>();
            if (insertIndex >= 0 && insertIndex < groupItem.ChildPanels.Count)
                groupItem.ChildPanels.Insert(insertIndex, child);
            else
                groupItem.ChildPanels.Add(child);
        }

        private void AddStickyNotesPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.StickyNotes);
        private void AddTodoPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.TodoList);
        private void AddClipboardPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.Clipboard);
        private void AddSystemMonitorPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.SystemMonitor);
        private void AddUsageStatsPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.UsageStats);
        private void AddAIChatPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.AIChat);
        private void AddFileAreaPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.FileArea);
        private void AddClipboardHistoryPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.ClipboardHistory);
        private void AddImageCarouselPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.ImageCarousel);
        private void AddDailyActivityPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.DailyActivity);
        private void AddCalendarPanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.Calendar);
        private void AddScreenTimePanel_Click(object sender, RoutedEventArgs e) => AddPanel(PanelType.ScreenTime);

        /// <summary>打开团队组件库，选择团队共享模块添加到当前窗口（数据共享互通）</summary>
        private void AddFromTeamLibrary_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            if (string.IsNullOrEmpty(app.AppSettings.AccountToken))
            {
                MessageBox.Show(Framework.Lang.T("请先登录后再使用团队组件库"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new SideDesk.Windows.TeamLibraryWindow { Owner = Window.GetWindow(this) };
            window.OnAddItem = panel =>
            {
                if (_currentWindowConfig == null || _configManager == null) return;
                AddTopLevelPanel(panel);
            };
            window.Show();
        }

        /// <summary>插件子菜单展开时动态填充已加载的插件列表</summary>
        private void AddPluginMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;
            menu.Items.Clear();

            var plugins = PluginLoaderService.Instance.Plugins;
            if (plugins.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Framework.Lang.T("(无已加载插件)"), IsEnabled = false });
                return;
            }

            foreach (var p in plugins)
            {
                var mi = new MenuItem { Header = $"{p.Config.Icon} {p.Config.Name}" };
                var pluginId = p.Config.Id;
                mi.Click += (s, ev) => AddPluginPanel(pluginId);
                menu.Items.Add(mi);
            }
        }

        /// <summary>添加插件面板到当前窗口</summary>
        private void AddPluginPanel(string pluginId)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var plugin = PluginLoaderService.Instance.GetPlugin(pluginId);
            var newPanel = new PanelItem
            {
                Type = PanelType.Plugin,
                PluginId = pluginId,
                Title = plugin != null ? $"{plugin.Config.Icon} {plugin.Config.Name}" : null
            };
            AddTopLevelPanel(newPanel);
        }
        private void AddDataDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.DataDashboard,
                Title = Framework.Lang.T("数据看板"),
                DashboardItems = new List<DataDashboardItem>()
            };
            AddTopLevelPanel(newPanel);
        }

        /// <summary>添加数据块子菜单展开时，加载设置中的数据项（模版）列表</summary>
        private void AddDataBlockMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menu) return;
            PopulateDataBlockMenu(menu, tpl => AddDataBlockPanel(tpl), () =>
            {
                var customItem = ShowAddDashboardItemDialog();
                if (customItem != null)
                {
                    if (_currentWindowConfig == null || _configManager == null) return;
                    var newPanel = new PanelItem
                    {
                        Type = PanelType.DataDashboard,
                        Title = Framework.Lang.T("数据看板"),
                        DashboardItems = new List<DataDashboardItem> { customItem }
                    };
                    AddTopLevelPanel(newPanel);
                    Services.DataPollingService.Instance.SyncFromConfigs(_configManager);
                }
            });
        }

        /// <summary>用设置中的数据模版填充指定子菜单；点击项时回调 onClick；onCustomClick 为「定制数据块」回调</summary>
        private void PopulateDataBlockMenu(MenuItem menu, Action<Models.DataTemplate> onClick, Action? onCustomClick = null)
        {
            menu.Items.Clear();
            var templates = DataTemplateManager.Load().Templates;
            if (templates.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Framework.Lang.T("(无数据项，请在设置中添加)"), IsEnabled = false });
                if (onCustomClick != null)
                {
                    menu.Items.Add(new Separator());
                    var customMi = new MenuItem { Header = Framework.Lang.T("定制数据块") };
                    customMi.Click += (s, ev) => onCustomClick();
                    menu.Items.Add(customMi);
                }
                return;
            }
            foreach (var tpl in templates)
            {
                var header = string.IsNullOrEmpty(tpl.Category) ? tpl.Name : $"{tpl.Name}  [{tpl.Category}]";
                var mi = new MenuItem { Header = header, ToolTip = tpl.Description };
                var captured = tpl;
                mi.Click += (s, ev) => onClick(captured);
                menu.Items.Add(mi);
            }
            if (onCustomClick != null)
            {
                menu.Items.Add(new Separator());
                var customMi = new MenuItem { Header = Framework.Lang.T("定制数据块") };
                customMi.Click += (s, ev) => onCustomClick();
                menu.Items.Add(customMi);
            }
        }

        /// <summary>基于数据模版创建一个数据看板项</summary>
        private static DataDashboardItem BuildDashboardItemFromTemplate(Models.DataTemplate tpl) => new DataDashboardItem
        {
            Label = tpl.Name,
            TemplateId = tpl.Id,
            TargetUrl = tpl.TargetUrl,
            Selector = tpl.Selector,
            RefreshIntervalSeconds = tpl.RefreshIntervalSeconds,
            BrowserId = tpl.DefaultBrowserId,
            ProfileId = tpl.DefaultProfileId,
            Value = "--"
        };

        /// <summary>根据模版新建一个包含单个数据项的数据看板模块</summary>
        private void AddDataBlockPanel(Models.DataTemplate tpl)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.DataDashboard,
                Title = Framework.Lang.T("数据看板"),
                DashboardItems = new List<DataDashboardItem> { BuildDashboardItemFromTemplate(tpl) }
            };
            AddTopLevelPanel(newPanel);
        }

        /// <summary>根据模版向分组内添加一个数据看板模块</summary>
        private void AddDataBlockToGroup(PanelItem groupItem, Models.DataTemplate tpl, int insertIndex = -1)
        {
            InsertChildPanel(groupItem, new PanelItem
            {
                Type = PanelType.DataDashboard,
                DashboardItems = new List<DataDashboardItem> { BuildDashboardItemFromTemplate(tpl) }
            }, insertIndex);
            _configManager?.Save();
            RenderPanels();
        }

        private void AddTimerPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.Timer,
                Title = Framework.Lang.T("⏱ 计时器"),
                TimerItems = new List<TimerItem>()
            };
            AddTopLevelPanel(newPanel);
        }

        private void AddCountdownPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.Countdown,
                Title = Framework.Lang.T("⏳ 倒计时"),
                CountdownItems = new List<CountdownItem>()
            };
            AddTopLevelPanel(newPanel);
        }

        private void AddPomodoroPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.Pomodoro,
                Title = Framework.Lang.T("🍅 番茄闹钟"),
                Pomodoro = new PomodoroSettings()
            };
            AddTopLevelPanel(newPanel);
        }

        /// <summary>顶层「添加模块」新面板添加到顶层（支持按右键位置插入）；分组内部添加请用 AddModuleToGroup。返回插入下标</summary>
        private int AddPanelToGroupOrTop(PanelItem newPanel)
        {
            // 若右键点击位置落在两个顶层模块之间，则插入到该位置
            if (_pendingInsertIndex is int insertIdx)
            {
                _pendingInsertIndex = null;
                int count = _currentWindowConfig!.Panels.Count;
                if (insertIdx > 0 && insertIdx < count)
                {
                    _currentWindowConfig.Panels.Insert(insertIdx, newPanel);
                    return insertIdx;
                }
            }
            // 顶层「添加模块」始终添加到顶层；只有在分组的右键菜单里添加才会进入分组（走 AddModuleToGroup）
            _currentWindowConfig!.Panels.Add(newPanel);
            return _currentWindowConfig.Panels.Count - 1;
        }

        /// <summary>新增顶层模块：保存配置并增量插入 widget，不重建已有模块（避免整 bar 闪烁/丢滚动位置）</summary>
        private void AddTopLevelPanel(PanelItem newPanel)
        {
            int index = AddPanelToGroupOrTop(newPanel);
            _configManager?.Save();
            InsertPanelWidgetIncremental(newPanel, index);
        }

        private void AddSignalLightPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.SignalLight,
                Title = Framework.Lang.T("🚦 信号灯"),
                SignalLights = new List<SignalLightItem>
                {
                    new() { Color = "#FF4444", Size = 36, Icon = "", IsOn = true },
                    new() { Color = "#FFAA00", Size = 36, Icon = "", IsOn = true },
                    new() { Color = "#44CC44", Size = 36, Icon = "", IsOn = true }
                }
            };
            AddTopLevelPanel(newPanel);
        }

        private void AddDigitalEmployeePanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            // 默认展示全局库中前几个员工
            var defaultIds = DigitalEmployees.Employees.Take(3).Select(emp => emp.Id).ToList();
            var newPanel = new PanelItem
            {
                Type = PanelType.DigitalEmployee,
                Title = Framework.Lang.T("👥 数字员工"),
                DigitalEmployeeIds = defaultIds
            };
            AddTopLevelPanel(newPanel);
        }

        #endregion

        #region 数字员工面板

        private UIElement BuildDigitalEmployeeWidget(PanelItem item)
        {
            item.DigitalEmployeeIds ??= new List<string>();
            var columns = Math.Clamp(item.DigitalEmployeeColumns <= 0 ? 3 : item.DigitalEmployeeColumns, 1, 5);

            var container = new StackPanel();

            // 按模块指定的 Id 顺序取出存在的员工
            var shown = item.DigitalEmployeeIds
                .Select(id => DigitalEmployees.GetById(id))
                .Where(emp => emp != null)
                .Cast<DigitalEmployee>()
                .ToList();

            if (shown.Count == 0)
            {
                container.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂未选择展示员工，点击标题栏 ▾ 选择"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 12,
                    Opacity = 0.7,
                    Margin = new Thickness(2, 2, 0, 2)
                });
            }
            else
            {
                var grid = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var emp in shown)
                    grid.Children.Add(CreateEmployeeCard(emp, columns));
                container.Children.Add(grid);
            }

            // 基于 ModuleControl 基础组件包装
            var module = CreateModuleControl(Framework.Lang.T("👥 数字员工"), item);

            // 管理数字员工
            var manageMi = new MenuItem { Header = Framework.Lang.T("管理数字员工") };
            manageMi.Click += (s, e) => OpenDigitalEmployeeManager();
            module.AddCustomMenuItem(manageMi);

            // 选择展示员工（子菜单勾选）
            var selectMi = new MenuItem { Header = Framework.Lang.T("选择展示员工") };
            selectMi.SubmenuOpened += (s, e) => PopulateEmployeeSelectMenu(selectMi, item);
            PopulateEmployeeSelectMenu(selectMi, item);
            module.AddCustomMenuItem(selectMi);

            // 每排列数
            var columnsMi = new MenuItem { Header = Framework.Lang.T("每排列数") };
            for (int c = 1; c <= 5; c++)
            {
                int cc = c;
                var cmi = new MenuItem { Header = cc.ToString(), IsCheckable = true, IsChecked = columns == cc };
                cmi.Click += (s, e) =>
                {
                    item.DigitalEmployeeColumns = cc;
                    _configManager?.Save();
                    RenderPanels();
                };
                columnsMi.Items.Add(cmi);
            }
            module.AddCustomMenuItem(columnsMi);

            module.SetContent(container);
            return module;
        }

        /// <summary>创建单个员工卡片（头像 + 名字），点击弹出详情</summary>
        private FrameworkElement CreateEmployeeCard(DigitalEmployee emp, int columns)
        {
            var card = new StackPanel
            {
                Width = columns switch { 1 => 150, 2 => 88, 3 => 60, 4 => 46, _ => 38 },
                Margin = new Thickness(4, 4, 4, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand
            };

            double avatarSize = columns switch { 1 => 48, 2 => 44, 3 => 40, 4 => 36, _ => 30 };

            var avatarBorder = new Border
            {
                Width = avatarSize,
                Height = avatarSize,
                CornerRadius = new CornerRadius(avatarSize / 2),
                Background = (Brush)FindResource("BrushAccent"),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            if (!string.IsNullOrWhiteSpace(emp.AvatarPath) && System.IO.File.Exists(emp.AvatarPath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(emp.AvatarPath);
                    bmp.EndInit();
                    avatarBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch
                {
                    avatarBorder.Child = CreateAvatarFallbackText(emp, avatarSize);
                }
            }
            else
            {
                avatarBorder.Child = CreateAvatarFallbackText(emp, avatarSize);
            }

            card.Children.Add(avatarBorder);

            var nameBlock = new Emoji.Wpf.TextBlock
            {
                Text = emp.Name,
                FontSize = columns >= 4 ? 10 : 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 140
            };
            card.Children.Add(nameBlock);

            if (columns <= 2 && !string.IsNullOrWhiteSpace(emp.Role))
            {
                card.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = emp.Role,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140
                });
            }

            var tooltip = CreateEmployeeStatusTooltip(emp, out var statusDot, out var statusText);
            card.ToolTip = tooltip;
            card.MouseEnter += async (s, e) =>
            {
                // 悬停时从后端获取最新状态（我的员工列表）
                var remote = await Services.DigitalEmployeeApiService.Instance.GetMyListAsync();
                if (remote == null) return;
                var match = remote.FirstOrDefault(r => r.Name == emp.Name);
                if (match == null) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    var color = match.Status switch
                    {
                        "online" => (Color)ColorConverter.ConvertFromString("#22C55E"),
                        "busy" => (Color)ColorConverter.ConvertFromString("#F59E0B"),
                        _ => (Color)ColorConverter.ConvertFromString("#9CA3AF")
                    };
                    statusDot.Fill = new SolidColorBrush(color);
                    statusText.Text = match.StatusText;
                    statusText.Foreground = new SolidColorBrush(color);
                });
            };
            card.MouseLeftButtonUp += (s, e) => OpenDigitalEmployeeWebPage(emp);

            return card;
        }

        private Emoji.Wpf.TextBlock CreateAvatarFallbackText(DigitalEmployee emp, double avatarSize) => new()
        {
            Text = emp.AvatarFallback,
            Foreground = Brushes.White,
            FontSize = avatarSize * 0.42,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>填充“选择展示员工”子菜单（全局员工列表勾选）</summary>
        private void PopulateEmployeeSelectMenu(MenuItem menu, PanelItem item)
        {
            menu.Items.Clear();
            item.DigitalEmployeeIds ??= new List<string>();
            var all = DigitalEmployees.Employees;
            if (all.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Framework.Lang.T("(暂无员工，请先管理添加)"), IsEnabled = false });
                return;
            }
            foreach (var emp in all)
            {
                var mi = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(emp.Role) ? emp.Name : $"{emp.Name}  [{emp.Role}]",
                    IsCheckable = true,
                    IsChecked = item.DigitalEmployeeIds.Contains(emp.Id),
                    StaysOpenOnClick = true
                };
                var captured = emp;
                mi.Click += (s, e) =>
                {
                    if (mi.IsChecked)
                    {
                        if (!item.DigitalEmployeeIds.Contains(captured.Id))
                            item.DigitalEmployeeIds.Add(captured.Id);
                    }
                    else
                    {
                        item.DigitalEmployeeIds.Remove(captured.Id);
                    }
                    _configManager?.Save();
                    RenderPanels();
                };
                menu.Items.Add(mi);
            }
        }

        /// <summary>打开数字员工管理窗口</summary>
        private void OpenDigitalEmployeeManager()
        {
            var win = new DigitalEmployeeWindow(DigitalEmployees)
            {
                Owner = Window.GetWindow(this),
                OnChanged = () => RenderPanels()
            };
            win.ShowDialog();
            RenderPanels();
        }

        /// <summary>展示员工详情弹窗</summary>
        private void ShowEmployeeInfoDialog(DigitalEmployee emp)
        {
            var dialog = new Window
            {
                Title = emp.Name,
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.NoResize
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };

            // 头像 + 名字 + 职位
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var avatarBorder = new Border
            {
                Width = 56, Height = 56, CornerRadius = new CornerRadius(28),
                Background = (Brush)FindResource("BrushAccent"), Margin = new Thickness(0, 0, 14, 0)
            };
            if (!string.IsNullOrWhiteSpace(emp.AvatarPath) && System.IO.File.Exists(emp.AvatarPath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(emp.AvatarPath);
                    bmp.EndInit();
                    avatarBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch { avatarBorder.Child = CreateAvatarFallbackText(emp, 56); }
            }
            else avatarBorder.Child = CreateAvatarFallbackText(emp, 56);
            top.Children.Add(avatarBorder);

            var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameCol.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = emp.Name, FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain")
            });
            if (!string.IsNullOrWhiteSpace(emp.Role) || !string.IsNullOrWhiteSpace(emp.Department))
            {
                var sub = string.Join(" · ", new[] { emp.Role, emp.Department }.Where(x => !string.IsNullOrWhiteSpace(x)));
                nameCol.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = sub, FontSize = 12, Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            top.Children.Add(nameCol);
            root.Children.Add(top);

            void AddInfoRow(string label, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                row.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextMuted"), Margin = new Thickness(0, 0, 0, 2)
                });
                row.Children.Add(new TextBlock
                {
                    Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("BrushTextMain")
                });
                root.Children.Add(row);
            }

            AddInfoRow(Framework.Lang.T("擅长/技能"), emp.Skills);
            AddInfoRow(Framework.Lang.T("联系方式"), emp.Contact);
            AddInfoRow(Framework.Lang.T("简介/职责"), emp.Description);

            dialog.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
                Content = root
            };
            dialog.ShowDialog();
        }

        /// <summary>创建员工悬停状态浮窗（显示头像、姓名、职位、状态指示）</summary>
        private ToolTip CreateEmployeeStatusTooltip(DigitalEmployee emp, out System.Windows.Shapes.Ellipse statusDot, out TextBlock statusText)
        {
            var statusColor = emp.Status switch
            {
                "online" => (Color)ColorConverter.ConvertFromString("#22C55E"),
                "busy" => (Color)ColorConverter.ConvertFromString("#F59E0B"),
                "offline" => (Color)ColorConverter.ConvertFromString("#9CA3AF"),
                _ => (Color)ColorConverter.ConvertFromString("#22C55E")
            };

            var panel = new StackPanel { Margin = new Thickness(2) };

            // 第一行：头像 + 姓名/职位
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            var avatar = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
                Background = (Brush)FindResource("BrushAccent"),
                Margin = new Thickness(0, 0, 10, 0)
            };
            if (!string.IsNullOrWhiteSpace(emp.AvatarPath) && System.IO.File.Exists(emp.AvatarPath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(emp.AvatarPath);
                    bmp.EndInit();
                    avatar.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch { avatar.Child = CreateAvatarFallbackText(emp, 36); }
            }
            else avatar.Child = CreateAvatarFallbackText(emp, 36);
            top.Children.Add(avatar);

            var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameCol.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = emp.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain")
            });
            if (!string.IsNullOrWhiteSpace(emp.Role) || !string.IsNullOrWhiteSpace(emp.Department))
            {
                var sub = string.Join(" · ", new[] { emp.Role, emp.Department }.Where(x => !string.IsNullOrWhiteSpace(x)));
                nameCol.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = sub, FontSize = 11, Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            top.Children.Add(nameCol);
            panel.Children.Add(top);

            // 第二行：状态指示（悬停时从后端实时刷新）
            var statusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };
            statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(statusColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            statusRow.Children.Add(statusDot);
            statusText = new TextBlock
            {
                Text = emp.StatusText,
                FontSize = 12,
                Foreground = new SolidColorBrush(statusColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusRow.Children.Add(statusText);
            panel.Children.Add(statusRow);

            // 第三行：技能摘要（如有）
            if (!string.IsNullOrWhiteSpace(emp.Skills))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.TF("擅长：{0}", emp.Skills),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 6, 0, 0),
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            // 提示点击打开详情
            panel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("点击查看数字员工后台 →"),
                FontSize = 10,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 0)
            });

            return new ToolTip
            {
                Content = panel,
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                Padding = new Thickness(12, 10, 12, 10),
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                HorizontalOffset = 12,
                VerticalOffset = 12
            };
        }

        /// <summary>打开数字员工 Web 后台页面（WebView2 窗口）</summary>
        private void OpenDigitalEmployeeWebPage(DigitalEmployee emp)
        {
            var win = new SideDesk.Windows.DigitalEmployeeWebWindow(emp);
            win.Show();
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

        #region 每日活跃面板

        /// <summary>活跃分钟数 → 等级 0~4</summary>
        private int GetActivityLevel(double minutes)
        {
            if (minutes <= 0) return 0;
            if (minutes < 30) return 1;
            if (minutes < 120) return 2;
            if (minutes < 240) return 3;
            return 4;
        }

        /// <summary>活跃等级 → 格子颜色（绿色渐变，0 级使用主题边框色以适配亮/暗主题）</summary>
        private Brush GetActivityLevelBrush(int level) => level switch
        {
            1 => new SolidColorBrush(Color.FromRgb(0x0E, 0x44, 0x29)),
            2 => new SolidColorBrush(Color.FromRgb(0x00, 0x6D, 0x32)),
            3 => new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x41)),
            4 => new SolidColorBrush(Color.FromRgb(0x39, 0xD3, 0x53)),
            _ => (Brush)FindResource("BrushBorder")
        };

        /// <summary>视图模式定义：显示名 → 存储键</summary>
        private static readonly (string Label, string Key)[] DailyActivityViews =
        {
            (Framework.Lang.T("本月"), "Month"),
            (Framework.Lang.T("今日"), "Today"),
            (Framework.Lang.T("本周"), "Week")
        };

        /// <summary>每日活跃取数统一入口：screen=本地屏幕使用分钟，其他类型=服务端独立数据（缓存，未缓存返回 null）</summary>
        private double? GetDailyActivityValue(string? corpId, string type, DateTime date)
        {
            if (type == "screen")
                return SideDesk.Windows.WindowUsageStats.GetActiveMinutesForDate(date);
            return Services.DailyActivityService.Instance.TryGetCachedValue(corpId, type, date);
        }

        /// <summary>每日活跃数值展示文字：screen=时长格式，其他类型=纯数字</summary>
        private static string FormatDailyActivityValue(string type, double value)
            => type == "screen"
                ? SideDesk.Windows.WindowUsageStats.FormatDuration(TimeSpan.FromMinutes(value), showSeconds: false)
                : value.ToString("0.##");

        /// <summary>当前窗口的每日活跃作用域（团队窗口=团队，否则个人）</summary>
        private string? GetDailyActivityCorpId()
            => _currentWindowConfig?.OwnerType == "team" ? _currentWindowConfig?.CorpId : null;

        /// <summary>确保服务端每日活跃数据已缓存（异步拉取，到达后重绘）</summary>
        private void EnsureDailyActivityLoaded(string? corpId, string type, DateTime start, DateTime end)
        {
            _ = LoadDailyActivityAsync(corpId, type, start, end);
        }

        private async System.Threading.Tasks.Task LoadDailyActivityAsync(string? corpId, string type, DateTime start, DateTime end)
        {
            try
            {
                bool fetched = await Services.DailyActivityService.Instance.EnsureLoadedAsync(corpId, type, start, end);
                if (fetched) await Dispatcher.InvokeAsync(RenderPanels);
            }
            catch { }
        }

        /// <summary>每日活跃模块：根据 item.DailyActivityView 渲染 本月热力图 / 今日 / 本周(前后5天) 视图；
        /// 数据来源由 item.DailyActivityType 决定（screen=本地屏幕使用，其他=服务端独立数据表类型）</summary>
        private UIElement BuildDailyActivityWidget(PanelItem item)
        {
            string view = string.IsNullOrEmpty(item.DailyActivityView) ? "Month" : item.DailyActivityView;
            string type = string.IsNullOrEmpty(item.DailyActivityType) ? "screen" : item.DailyActivityType;
            string? corpId = GetDailyActivityCorpId();

            // 服务端类型：确保当前视图可见范围的日期数据已缓存（未命中时异步拉取，到达后自动重绘）
            if (type != "screen")
            {
                var today = DateTime.Today;
                (DateTime start, DateTime end) = view switch
                {
                    "Today" => (today, today),
                    "Week" => (today.AddDays(-2), today),
                    _ => (new DateTime(today.Year, today.Month, 1), today)
                };
                EnsureDailyActivityLoaded(corpId, type, start, end);
            }

            UIElement content = view switch
            {
                "Today" => BuildDailyActivityToday(corpId, type),
                "Week" => BuildDailyActivityWeek(corpId, type),
                _ => BuildDailyActivityMonth(corpId, type)
            };

            // ===== 右上角下拉菜单：数据类型 + 视图切换 + 刷新 =====
            var module = CreateModuleControl(Framework.Lang.T("每日活跃"), item);

            var typeMenu = new MenuItem { Header = Framework.Lang.T("数据类型") };
            void AddTypeOption(string label, string key)
            {
                var tmi = new MenuItem { Header = label, IsCheckable = true, IsChecked = key == type };
                tmi.Click += (s, e) =>
                {
                    item.DailyActivityType = key;
                    _configManager?.Save();
                    RenderPanels();
                };
                typeMenu.Items.Add(tmi);
            }
            AddTypeOption(Framework.Lang.T("屏幕使用"), "screen");
            // 内置固定类型（番茄/屏幕时长/健身记录/鼠标键盘活跃）：始终展示，暂无数据时显示 0
            foreach (var builtIn in Services.DailyActivityTypes.BuiltIn)
                if (builtIn != type) AddTypeOption(builtIn, builtIn);
            if (type != "screen") AddTypeOption(type, type);
            // 异步加载作用域内其他类型（未登录/后端不可达时仅保留当前项）
            Dispatcher.InvokeAsync(async () =>
            {
                var types = await Services.DailyActivityService.Instance.ListTypesAsync(corpId);
                if (types == null) return;
                foreach (var t in types)
                {
                    bool exists = false;
                    foreach (MenuItem tmi in typeMenu.Items)
                        if ((tmi.Header as string) == t) { exists = true; break; }
                    if (!exists) AddTypeOption(t, t);
                }
            });
            module.AddCustomMenuItem(typeMenu);

            var viewMenu = new MenuItem { Header = Framework.Lang.T("视图") };
            foreach (var (label, key) in DailyActivityViews)
            {
                string capturedKey = key;
                var mi = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = key == view
                };
                mi.Click += (s, e) =>
                {
                    item.DailyActivityView = capturedKey;
                    _configManager?.Save();
                    RenderPanels();
                };
                viewMenu.Items.Add(mi);
            }
            module.AddCustomMenuItem(viewMenu);

            var refreshMi = new MenuItem { Header = Framework.Lang.T("刷新") };
            refreshMi.Click += (s, e) =>
            {
                if (type != "screen")
                    Services.DailyActivityService.Instance.Invalidate(corpId, type);
                RenderPanels();
            };
            module.AddCustomMenuItem(refreshMi);

            module.SetContent(content);
            return module;
        }

        /// <summary>每日活跃 — 今日视图：只显示当天，大格子 + 活跃时长摘要</summary>
        private UIElement BuildDailyActivityToday(string? corpId, string type)
        {
            var today = DateTime.Today;
            double minutes = GetDailyActivityValue(corpId, type, today) ?? 0;
            int level = GetActivityLevel(minutes);
            var weekNames = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

            var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            // 日期标题
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.TF("{0:M月d日} {1}", today, weekNames[(int)today.DayOfWeek]),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 大格子：活跃度色块 + 时长文字
            var cell = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(8),
                Background = GetActivityLevelBrush(level),
                BorderBrush = (Brush)FindResource("BrushAccent"),
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = minutes > 0
                    ? Framework.Lang.TF("{0:M月d日}  活跃 {1}", today, FormatDailyActivityValue(type, minutes))
                    : Framework.Lang.TF("{0:M月d日}  无活跃记录", today)
            };
            cell.Child = new TextBlock
            {
                Text = minutes > 0
                    ? FormatDailyActivityValue(type, minutes)
                    : "—",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = level > 0 ? Brushes.White : (Brush)FindResource("BrushTextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            root.Children.Add(cell);

            // 摘要文字
            root.Children.Add(new TextBlock
            {
                Text = minutes > 0 ? Framework.Lang.TF("今日活跃 {0}", FormatDailyActivityValue(type, minutes)) : Framework.Lang.T("今日暂无活跃记录"),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            return root;
        }

        /// <summary>每日活跃 — 本周视图：一行一天，显示以今天为中心前后共5天</summary>
        private UIElement BuildDailyActivityWeek(string? corpId, string type)
        {
            var today = DateTime.Today;
            var weekNames = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            var accent = (Brush)FindResource("BrushAccent");

            var root = new StackPanel();

            // 标题：日期范围
            var start = today.AddDays(-2);
            var end = today.AddDays(2);
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.TF("{0:M月d日} ~ {1:M月d日}", start, end),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left
            });

            // 逐日行：日期标签 + 活跃度色条 + 时长
            for (int offset = -2; offset <= 2; offset++)
            {
                var date = today.AddDays(offset);
                bool isToday = date == today;
                bool isFuture = date > today;
                double minutes = isFuture ? 0 : (GetDailyActivityValue(corpId, type, date) ?? 0);
                int level = isFuture ? 0 : GetActivityLevel(minutes);

                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // 日期标签
                var label = new TextBlock
                {
                    Text = isToday ? Framework.Lang.TF("{0:M/d} 今天", date) : $"{date:M/d} {weekNames[(int)date.DayOfWeek]}",
                    FontSize = 10,
                    Foreground = isToday ? accent : (Brush)FindResource("BrushTextMuted"),
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                // 活跃度色块
                var cell = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(3),
                    Background = GetActivityLevelBrush(level),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 6, 0),
                    ToolTip = isFuture
                        ? Framework.Lang.TF("{0:M月d日}", date)
                        : minutes > 0
                            ? Framework.Lang.TF("{0:M月d日}  活跃 {1}", date, FormatDailyActivityValue(type, minutes))
                            : Framework.Lang.TF("{0:M月d日}  无活跃记录", date)
                };
                if (isToday)
                {
                    cell.BorderBrush = accent;
                    cell.BorderThickness = new Thickness(1.5);
                }
                if (isFuture) cell.Opacity = 0.4;
                Grid.SetColumn(cell, 1);
                row.Children.Add(cell);

                // 时长文字
                var duration = new TextBlock
                {
                    Text = isFuture ? "" : minutes > 0
                        ? FormatDailyActivityValue(type, minutes)
                        : Framework.Lang.T("无记录"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("BrushTextSec"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = isFuture ? 0.4 : 1
                };
                Grid.SetColumn(duration, 2);
                row.Children.Add(duration);

                root.Children.Add(row);
            }

            return root;
        }

        /// <summary>每日活跃 — 本月视图：以热力图形式展示当月每天的活跃度（每行 7 个格子 = 一周，周一起）</summary>
        private UIElement BuildDailyActivityMonth(string? corpId, string type)
        {
            const double CellMargin = 2;   // 格子四周间距
            const double MaxCell = 26;      // 格子最大边长（面板过宽时避免格子过大）

            var today = DateTime.Today;
            var first = new DateTime(today.Year, today.Month, 1);
            int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
            // 周一起的前导空格数（DayOfWeek: 周日=0..周六=6 → 周一=0）
            int leading = ((int)first.DayOfWeek + 6) % 7;

            var root = new StackPanel();

            // 月份标题
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.TF("{0}年{1}月", today.Year, today.Month),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left
            });

            // 星期表头（一~日）——与格子同为 7 列，随宽度自适应铺满
            var weekHeader = new UniformGrid
            {
                Columns = 7,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 3)
            };
            foreach (var wd in new[] { "一", "二", "三", "四", "五", "六", "日" })
            {
                weekHeader.Children.Add(new TextBlock
                {
                    Text = wd,
                    FontSize = 9,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Opacity = 0.7
                });
            }
            root.Children.Add(weekHeader);

            // 日历格子：7 列自适应铺满可用宽度（不管模块多宽，每行始终 7 个）
            var grid = new UniformGrid
            {
                Columns = 7,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var accent = (Brush)FindResource("BrushAccent");
            // 前导空格（占位）
            for (int i = 0; i < leading; i++)
                grid.Children.Add(new Border { Margin = new Thickness(CellMargin), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });

            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(today.Year, today.Month, day);
                bool isFuture = date > today;
                var cell = new Border
                {
                    Margin = new Thickness(CellMargin),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(3)
                };

                if (isFuture)
                {
                    cell.Background = GetActivityLevelBrush(0);
                    cell.Opacity = 0.4;
                    cell.ToolTip = Framework.Lang.TF("{0:M月d日}", date);
                }
                else
                {
                    double minutes = GetDailyActivityValue(corpId, type, date) ?? 0;
                    int level = GetActivityLevel(minutes);
                    cell.Background = GetActivityLevelBrush(level);
                    cell.ToolTip = minutes > 0
                        ? Framework.Lang.TF("{0:M月d日}  活跃 {1}", date, FormatDailyActivityValue(type, minutes))
                        : Framework.Lang.TF("{0:M月d日}  无活跃记录", date);
                }

                // 今天：高亮描边
                if (date == today)
                {
                    cell.BorderBrush = accent;
                    cell.BorderThickness = new Thickness(1.5);
                }

                grid.Children.Add(cell);
            }

            // 动态计算：7 列平分可用宽度，格子保持正方形（受 MaxCell 限制）
            grid.SizeChanged += (s, e) =>
            {
                double w = e.NewSize.Width;
                if (w <= 0) return;
                double inner = w / 7.0 - CellMargin * 2;
                if (inner > MaxCell) inner = MaxCell;
                if (inner < 2) inner = 2;
                foreach (var child in grid.Children)
                    if (child is Border b) { b.Width = inner; b.Height = inner; }
            };
            root.Children.Add(grid);

            return root;
        }

        // ==================== 日历模块 ====================

        /// <summary>日历模块：紧凑月历 + 鼠标悬停弹出放大窗口（适配小屏看不清的场景）</summary>
        private UIElement BuildCalendarWidget(PanelItem item)
        {
            var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var today = DateTime.Today;

            // ===== 大日期显示 =====
            // 大日子数字
            var dayText = new TextBlock
            {
                Text = today.Day.ToString(),
                FontSize = 42,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                LineHeight = 46
            };
            root.Children.Add(dayText);

            // 月份 + 星期
            var weekNames = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            var subText = new TextBlock
            {
                Text = Framework.Lang.TF("{0}年{1}月 {2}", today.Year, today.Month, weekNames[(int)today.DayOfWeek]),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextSec"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            root.Children.Add(subText);

            // 活动小点行（预留：后续可绑定当日活动数据）
            var dotsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            root.Children.Add(dotsPanel);

            // TODO: 根据当日活动数据动态添加小点，示例：
            // AddActivityDot(dotsPanel, "BrushAccent");

            // ===== 悬停弹出完整月历 =====
            var popup = new Popup
            {
                PlacementTarget = root,
                Placement = PlacementMode.MousePoint,
                AllowsTransparency = true,
                StaysOpen = true
            };
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            closeTimer.Tick += (s, e) =>
            {
                closeTimer.Stop();
                if (!root.IsMouseOver && !(popup.Child != null && popup.Child.IsMouseOver))
                    popup.IsOpen = false;
            };

            root.MouseEnter += (s, e) => { closeTimer.Stop(); popup.IsOpen = true; };
            root.MouseLeave += (s, e) => closeTimer.Start();
            popup.AddHandler(UIElement.MouseEnterEvent, new MouseEventHandler((s, e) => closeTimer.Stop()), true);
            popup.AddHandler(UIElement.MouseLeaveEvent, new MouseEventHandler((s, e) => closeTimer.Start()), true);

            popup.Child = BuildCalendarPopupContent(today, today);

            return WrapWidget(Framework.Lang.T("📅 日历"), root, item);
        }

        /// <summary>添加活动小点（日历组件用）</summary>
        private void AddActivityDot(StackPanel panel, string brushKey)
        {
            panel.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = (Brush)FindResource(brushKey),
                Margin = new Thickness(2, 0, 2, 0)
            });
        }

        /// <summary>日历导航按钮（‹ / ›）</summary>
        private Button MakeCalendarNavButton(string content)
        {
            return new Button
            {
                Content = content,
                Width = 20,
                Height = 20,
                FontSize = 13,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextSec"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>日历星期表头（Sun~Sat，周日起）</summary>
        private UniformGrid BuildCalendarWeekHeader(double fontSize, double bottomMargin)
        {
            var weekHeader = new UniformGrid
            {
                Columns = 7,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, bottomMargin)
            };
            foreach (var wd in new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" })
            {
                weekHeader.Children.Add(new TextBlock
                {
                    Text = wd,
                    FontSize = fontSize,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Foreground = (Brush)FindResource("BrushTextMuted")
                });
            }
            return weekHeader;
        }

        /// <summary>构建日历网格：日期卡片（数字 + 当年第 Dxx 天），今天高亮描边</summary>
        private void BuildCalendarGrid(UniformGrid grid, DateTime month, DateTime today,
            double dayFontSize, double subFontSize, double radius, double pad, bool interactive)
        {
            var first = new DateTime(month.Year, month.Month, 1);
            int daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            int leading = (int)first.DayOfWeek; // 周日起：前导空格数

            var cellBG = (Brush)FindResource("BrushInputBG");
            var accent = (Brush)FindResource("BrushAccent");
            var textMain = (Brush)FindResource("BrushTextMain");
            var textMuted = (Brush)FindResource("BrushTextMuted");

            for (int i = 0; i < leading; i++)
                grid.Children.Add(new Border { Margin = new Thickness(1.5) });

            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(month.Year, month.Month, day);
                bool isToday = date == today;

                var cell = new Border
                {
                    Background = cellBG,
                    CornerRadius = new CornerRadius(radius),
                    Margin = new Thickness(1.5),
                    Padding = new Thickness(pad),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                if (isToday)
                {
                    cell.BorderBrush = accent;
                    cell.BorderThickness = new Thickness(1.5);
                }

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = day.ToString(),
                    FontSize = dayFontSize,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = textMain,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = $"D{date.DayOfYear}",
                    FontSize = subFontSize,
                    Foreground = textMuted,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                cell.Child = stack;

                if (interactive)
                {
                    var hoverBG = (Brush)FindResource("BrushListViewItemMouseOver");
                    cell.MouseEnter += (s, e) => cell.Background = hoverBG;
                    cell.MouseLeave += (s, e) => cell.Background = cellBG;
                }

                grid.Children.Add(cell);
            }
        }

        /// <summary>构建日历悬停放大弹窗内容（大尺寸月历，便于小屏下查看）</summary>
        private UIElement BuildCalendarPopupContent(DateTime month, DateTime today)
        {
            var popupRoot = new Border
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(10), // 为阴影留出空间
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.5,
                    Color = Colors.Black
                }
            };

            var stack = new StackPanel { Width = 380 };

            // 月份标题
            stack.Children.Add(new TextBlock
            {
                Text = $"{month.Year}/{month.Month:D2}",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // 星期表头
            stack.Children.Add(BuildCalendarWeekHeader(12, 4));

            // 大尺寸日期网格
            var bigGrid = new UniformGrid { Columns = 7 };
            BuildCalendarGrid(bigGrid, month, today, dayFontSize: 15, subFontSize: 9.5, radius: 6, pad: 4, interactive: true);
            stack.Children.Add(bigGrid);

            popupRoot.Child = stack;
            return popupRoot;
        }

        // ==================== 插件模块 ====================

        /// <summary>构建外部插件模块 UI</summary>
        private UIElement BuildPluginWidget(PanelItem item)
        {
            var pluginId = item.PluginId;
            if (string.IsNullOrEmpty(pluginId))
            {
                return WrapWidget(Framework.Lang.T("🧩 插件"), new TextBlock
                {
                    Text = Framework.Lang.T("未指定插件"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Opacity = 0.6,
                    Margin = new Thickness(8)
                }, item);
            }

            var loader = PluginLoaderService.Instance;
            var plugin = loader.GetPlugin(pluginId);
            if (plugin == null)
            {
                return WrapWidget(Framework.Lang.T("🧩 插件"), new TextBlock
                {
                    Text = Framework.Lang.TF("插件未加载: {0}", pluginId),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Opacity = 0.6,
                    Margin = new Thickness(8)
                }, item);
            }

            var ui = loader.CreatePluginUI(pluginId);
            if (ui == null)
            {
                return WrapWidget($"{plugin.Config.Icon} {plugin.Config.Name}", new TextBlock
                {
                    Text = Framework.Lang.T("插件 UI 创建失败"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Opacity = 0.6,
                    Margin = new Thickness(8)
                }, item);
            }

            var title = string.IsNullOrEmpty(item.Title)
                ? $"{plugin.Config.Icon} {plugin.Config.Name}"
                : item.Title;

            return WrapWidget(title, ui, item);
        }

        #endregion

        #region 好友面板

        /// <summary>好友在线状态 → 颜色</summary>
        private Brush GetFriendStatusBrush(string? status) => status switch
        {
            "online" => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), // 绿色 在线
            "away"   => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)), // 橙黄 离开
            "busy"   => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)), // 红色 忙碌
            _        => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // 灰色 离线
        };

        /// <summary>好友在线状态 → 中文标签</summary>
        private string GetFriendStatusLabel(string? status) => status switch
        {
            "online" => Framework.Lang.T("在线"),
            "away"   => Framework.Lang.T("离开"),
            "busy"   => Framework.Lang.T("忙碌"),
            _        => Framework.Lang.T("离线"),
        };

        /// <summary>后端好友列表缓存（供好友模块展示与选择器使用）</summary>
        private List<FriendUser>? _friendListCache;

        /// <summary>若缓存为空则异步加载后端好友列表，完成后刷新面板</summary>
        private void TriggerFriendListLoad()
        {
            if (_friendListCache != null) return;
            var app = (App)Application.Current;
            if (string.IsNullOrEmpty(app.AppSettings.AccountToken)) return; // 未登录不请求
            _ = LoadFriendListAsync();
        }

        private async Task LoadFriendListAsync()
        {
            try
            {
                var list = await FriendService.Instance.GetFriendListAsync();
                if (list == null) return;
                _friendListCache = list;
                await Dispatcher.InvokeAsync(() => RenderPanels());
            }
            catch { /* 忽略加载失败，面板保持占位提示 */ }
        }

        /// <summary>异步加载好友头像（URL 下载 / base64 解码），成功后替换首字占位；失败保留占位</summary>
        private async void ApplyFriendAvatarAsync(Border border, string? avatar)
        {
            if (string.IsNullOrWhiteSpace(avatar)) return;
            try
            {
                byte[] bytes;
                if (avatar.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    bytes = await http.GetByteArrayAsync(avatar);
                }
                else
                {
                    bytes = Convert.FromBase64String(avatar);
                }
                if (bytes.Length == 0) return;

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();

                border.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                border.Child = null;
            }
            catch { /* 加载失败：保留首字占位头像 */ }
        }

        /// <summary>好友资料变更（后端 WS 推送 friend_profile_updated）：
        /// 同步更新缓存中对应好友的昵称与头像并重绘面板；缓存未建立时不处理（下次加载会取到最新数据），
        /// 缓存中找不到该用户（如加密格式升级/新好友）则重拉全量列表兜底</summary>
        public void UpdateFriendProfile(string userId, string? nickName, string? avatar)
        {
            var cache = _friendListCache;
            if (cache == null) return;
            var f = cache.FirstOrDefault(x => x.UserId == userId);
            if (f == null)
            {
                _ = LoadFriendListAsync();
                return;
            }
            if (nickName != null) f.NickName = nickName;
            if (avatar != null) f.Avatar = avatar;
            RenderPanels();
        }

        /// <summary>好友上/下线（后端 WS 推送 friend_online/friend_offline）：
        /// 按 encrypt2 确定性密文匹配缓存好友项并更新在线状态，重绘面板；缓存未建立时不处理</summary>
        public void UpdateFriendOnlineStatus(string userId, bool online)
        {
            var cache = _friendListCache;
            if (cache == null) return;
            var f = cache.FirstOrDefault(x => x.UserId == userId);
            if (f == null) return;
            f.IsOnline = online ? 1 : 0;
            RenderPanels();
        }

        private UIElement BuildFriendWidget(PanelItem item)
        {
            item.FriendUserIds ??= new List<string>();

            // 列表展示已勾选的后端好友；未选择时显示“圆加号”引导占位（点击选择好友）
            UIElement content = BuildFriendListContent(item);

            // 基于 ModuleControl 基础组件包装（直接添加菜单项，保留子菜单）
            var module = CreateModuleControl(Framework.Lang.T("好友"), item);

            // 选择好友（从后端好友列表勾选展示，可多选）
            var selectMi = new MenuItem { Header = Framework.Lang.T("选择好友") };
            selectMi.Click += (s, e) => ShowFriendPickerDialog(item);
            module.AddCustomMenuItem(selectMi);

            // 添加新好友
            var addMi = new MenuItem { Header = Framework.Lang.T("添加新好友") };
            addMi.Click += (s, e) => ShowAddFriendByCodeDialog();
            module.AddCustomMenuItem(addMi);

            module.SetContent(content);
            return module;
        }

        /// <summary>好友列表内容：按 FriendUserIds 顺序渲染已勾选的后端好友；未选择时显示圆加号占位</summary>
        private UIElement BuildFriendListContent(PanelItem item)
        {
            TriggerFriendListLoad(); // 缓存为空时触发异步加载

            var container = new StackPanel();
            var cache = _friendListCache;

            // 按模块指定的 userId 顺序取出存在的好友（缓存未就绪时视为空）
            var shown = cache == null
                ? new List<FriendUser>()
                : item.FriendUserIds!
                    .Select(id => cache.FirstOrDefault(f => f.UserId == id))
                    .Where(f => f != null)
                    .Cast<FriendUser>()
                    .ToList();

            // 未选择好友 → 圆加号占位（点击打开好友选择器）
            if (shown.Count == 0)
            {
                container.Children.Add(BuildFriendEmptyPlaceholder(item));
                return container;
            }

            foreach (var f in shown)
                container.Children.Add(BuildFriendListRow(f));

            return container;
        }

        /// <summary>未选择好友时的占位：圆加号，点击打开好友选择器</summary>
        private UIElement BuildFriendEmptyPlaceholder(PanelItem item)
        {
            double size = 46;
            var circle = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                BorderBrush = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(1.5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("点击选择要展示的好友"),
                Margin = new Thickness(0, 8, 0, 8)
            };
            circle.Child = new TextBlock
            {
                Text = "+",
                FontSize = 28,
                FontWeight = FontWeights.Light,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            circle.MouseLeftButtonUp += (s, e) => { if (!_isEditMode) ShowFriendPickerDialog(item); };
            return circle;
        }

        /// <summary>好友列表中的单行：头像（带在线状态点）+ 昵称 + 状态</summary>
        private UIElement BuildFriendListRow(FriendUser f)
        {
            string status = f.IsOnline == 1 ? "online" : "offline";

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 4),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("点击标题栏 ▾ → 选择好友，可管理展示的好友")
            };

            // 圆形头像 + 右下角在线状态点
            double size = 30;
            var avatarGrid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            var avatarBorder = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = (Brush)FindResource("BrushAccent")
            };
            string fallback = !string.IsNullOrWhiteSpace(f.NickName) ? f.NickName.Substring(0, 1) : "\U0001F642";
            avatarBorder.Child = new Emoji.Wpf.TextBlock
            {
                Text = fallback,
                Foreground = Brushes.White,
                FontSize = size * 0.42,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 异步加载真实头像（URL 下载 / base64 解码），成功后替换首字占位
            ApplyFriendAvatarAsync(avatarBorder, f.Avatar);
            avatarGrid.Children.Add(avatarBorder);
            double dotSize = Math.Max(10, size * 0.28);
            avatarGrid.Children.Add(new Border
            {
                Width = dotSize, Height = dotSize,
                CornerRadius = new CornerRadius(dotSize / 2),
                Background = GetFriendStatusBrush(status),
                BorderBrush = (Brush)FindResource("BrushInputBG"),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = GetFriendStatusLabel(status)
            });
            row.Children.Add(avatarGrid);

            // 昵称 + 在线状态
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            col.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(f.NickName) ? f.UserName : f.NickName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170
            });
            col.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = GetFriendStatusLabel(status),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            row.Children.Add(col);

            return row;
        }

        /// <summary>选择好友弹窗：列出全部后端好友并勾选，勾选者将在模块列表中展示</summary>
        private async void ShowFriendPickerDialog(PanelItem item)
        {
            if (_configManager == null) return;
            item.FriendUserIds ??= new List<string>();

            var app = (App)Application.Current;
            if (string.IsNullOrEmpty(app.AppSettings.AccountToken))
            {
                ThemedMessageBox.Show(Window.GetWindow(this), Framework.Lang.T("请先登录后再选择好友。"), "提示");
                return;
            }

            // 拉取最新好友列表
            var friends = await FriendService.Instance.GetFriendListAsync();
            if (friends == null)
            {
                ThemedMessageBox.Show(Window.GetWindow(this), Framework.Lang.T("获取好友列表失败，请稍后重试。"), "提示");
                return;
            }
            _friendListCache = friends; // 顺便更新缓存

            var dialog = new Window
            {
                Title = Framework.Lang.T("选择好友"),
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("勾选要在模块中展示的好友（可多选）"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var listPanel = new StackPanel();
            var checks = new List<(FriendUser f, CheckBox cb)>();

            if (friends.Count == 0)
            {
                listPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无好友，请先添加好友。"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 12
                });
            }
            else
            {
                foreach (var f in friends)
                {
                    var cb = new CheckBox
                    {
                        IsChecked = item.FriendUserIds.Contains(f.UserId),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    row.Children.Add(cb);

                    // 头像圆
                    var avatar = new Border
                    {
                        Width = 28, Height = 28,
                        CornerRadius = new CornerRadius(14),
                        Background = (Brush)FindResource("BrushAccent"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    string fb = !string.IsNullOrWhiteSpace(f.NickName) ? f.NickName.Substring(0, 1) : "\U0001F642";
                    avatar.Child = new Emoji.Wpf.TextBlock
                    {
                        Text = fb,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    row.Children.Add(avatar);

                    // 昵称 + 在线状态
                    var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                    nameCol.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(f.NickName) ? f.UserName : f.NickName,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        FontSize = 13
                    });
                    nameCol.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = f.IsOnline == 1 ? Framework.Lang.T("在线") : Framework.Lang.T("离线"),
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontSize = 10,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                    row.Children.Add(nameCol);

                    listPanel.Children.Add(row);
                    checks.Add((f, cb));
                }
            }

            // 好友较多时可滚动
            root.Children.Add(new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            var btnPanel = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
            bool goAddFriend = false;
            var addBtn = new Button { Content = Framework.Lang.T("+ 添加好友"), Padding = new Thickness(10, 4, 10, 4), Style = (Style)FindResource("StandardButtonStyle") };
            addBtn.Click += (s, e) => { goAddFriend = true; dialog.DialogResult = false; };
            okBtn.Click += (s, e) =>
            {
                item.FriendUserIds = checks.Where(c => c.cb.IsChecked == true).Select(c => c.f.UserId).ToList();
                _configManager?.Save();
                RenderPanels();
                dialog.DialogResult = true;
            };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            DockPanel.SetDock(addBtn, Dock.Left);
            DockPanel.SetDock(cancelBtn, Dock.Right);
            DockPanel.SetDock(okBtn, Dock.Right);
            btnPanel.Children.Add(addBtn);
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();

            // 关闭后跳转设置页好友管理（搜索/添加好友），添加完成后可再次打开本弹窗勾选
            if (goAddFriend)
                ((App)Application.Current).OpenSettings(sw => sw.SwitchToFriendsPage());
        }

        private void AddFriendPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.Friend,
                Title = Framework.Lang.T("好友")
            };
            AddTopLevelPanel(newPanel);
        }

        /// <summary>通过邀请码添加新好友（暂无后端，先根据邀请码创建一个新的好友模块）</summary>
        private void ShowAddFriendByCodeDialog()
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            var dialog = new Window
            {
                Title = Framework.Lang.T("添加新好友"),
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Owner = Window.GetWindow(this)
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("输入好友邀请码"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var codeBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(codeBox);
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("输入对方的邀请码即可添加为好友"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 16)
            });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("添加"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                var code = codeBox.Text.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    MessageBox.Show(Framework.Lang.T("请输入邀请码"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var newPanel = new PanelItem
                {
                    Type = PanelType.Friend,
                    Title = Framework.Lang.T("好友"),
                    FriendName = Framework.Lang.T("好友 ") + code,
                    FriendEmoji = "\U0001F642",
                    FriendStatusText = Framework.Lang.T("在线"),
                    FriendOnlineStatus = "online"
                };
                AddTopLevelPanel(newPanel);
                dialog.DialogResult = true;
            };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        #endregion

        #region 剪贴板收藏（已移除自动监听）
        #endregion

        #region 键盘/鼠标计数器 UI

        /// <summary>底部状态栏宽度变化：单项（图标+数字）约需 60px，当单项可用宽度低于 60px 时切换为上下两行，避免文字被裁切</summary>
        private void StatusBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            statusBar.Orientation = statusBar.ActualWidth < 120
                ? Orientation.Vertical
                : Orientation.Horizontal;
        }

        private void OnKeyCountChanged(int count)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_keyCountValueButton != null)
                    _keyCountValueButton.Content = KeyboardHookService.FormatCount(count);
                keyCountText.Text = $"⌨ {KeyboardHookService.FormatCount(count)}";
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
                Content = KeyboardHookService.FormatCount(_keyboardHookService?.TodayKeyCount ?? 0),
                Width = 70, Height = 28,
                Margin = new Thickness(2, 0, 4, 0), Padding = new Thickness(2),
                Background = (Brush)FindResource("BrushAccent"),
                Foreground = (Brush)FindResource("BrushTextMain"), BorderThickness = new Thickness(0),
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_keyCountValueButton, Dock.Right);
            panel.Children.Add(_keyCountValueButton);

            var nameText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("今日按键"),
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

            var nameText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("鼠标移动"),
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
                container.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("（暂无便签）"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11,
                    Margin = new Thickness(4)
                });
            }
        }

        /// <summary>渲染面板级便签（per-panel 存储）；onEmptyAdd 非空时空列表提示可点击直接添加</summary>
        private void RefreshPanelStickyNotes(StackPanel container, PanelItem item, Action? onEmptyAdd = null)
        {
            container.Children.Clear();
            var notes = item.StickyNotes ?? new List<StickyNote>();
            foreach (var note in notes.OrderByDescending(n => n.UpdatedAt))
            {
                container.Children.Add(BuildPanelStickyNoteItem(note, container, item));
            }
            if (notes.Count == 0)
            {
                container.Children.Add(onEmptyAdd != null
                    ? BuildEmptyAddTip(Framework.Lang.T("（暂无便签，点击添加）"), onEmptyAdd)
                    : new Emoji.Wpf.TextBlock
                    {
                        Text = Framework.Lang.T("（暂无便签）"),
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

            // 右键菜单：删除便签（无需进入编辑模式）
            var noteMenu = new ContextMenu();
            var deleteNoteItem = new MenuItem { Header = Framework.Lang.T("删除便签") };
            deleteNoteItem.Click += (s, e) =>
            {
                item.StickyNotes?.RemoveAll(n => n.Id == note.Id);
                _configManager?.Save();
                RefreshPanelStickyNotes(container, item);
            };
            noteMenu.Items.Add(deleteNoteItem);
            var noteHideMi = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = item.HideFrame };
            noteHideMi.Click += (s, e) => { item.HideFrame = noteHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            noteMenu.Items.Add(noteHideMi);
            row.ContextMenu = noteMenu;

            var delBtn = new Button
            {
                Content = "✕", Width = 18, Height = 18,
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0), FontSize = 10,
                Padding = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("删除"), Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            if (_isEditMode)
            {
                row.MouseEnter += (s, e) => delBtn.Visibility = Visibility.Visible;
                row.MouseLeave += (s, e) => delBtn.Visibility = Visibility.Collapsed;
            }

            var textBlock = new Emoji.Wpf.TextBlock
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

        private void OpenPanelStickyNoteEditor(StickyNote note, StackPanel container, PanelItem item, Action<StickyNote> onCommitNew = null)
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
                    },
                    onCommitNew);
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
                ToolTip = Framework.Lang.T("删除"), Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            if (_isEditMode)
            {
                row.MouseEnter += (s, e) => delBtn.Visibility = Visibility.Visible;
                row.MouseLeave += (s, e) => delBtn.Visibility = Visibility.Collapsed;
            }

            var textBlock = new Emoji.Wpf.TextBlock
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
            // 顶层面板排序：交由 dynamicContent 的冒泡事件处理，此处不拦截
            if (e.Data.GetDataPresent("PanelItemId")) return;
            // 剪贴板历史→收藏拖拽：由收藏模块自己的 DragOver/Drop 处理，此处不拦截
            if (e.Data.GetDataPresent(ClipboardDragFormat)) return;
            _quickGroupUIService?.HandleDragOver(e);
        }

        private void NativeModePanel_Drop(object sender, DragEventArgs e)
        {
            // 仅当拖放落在快捷分组容器区域内时，才交给快捷分组服务处理（加入分组）。
            // 否则不拦截 e.Handled，交由底层窗口 (MainWindow.Window_Drop) 弹出 FileDropDialog 兜底，
            // 避免拖到面板空白处时文件被静默加入分组而无任何提示。
            if (_quickGroupUIService != null && IsDropOverElement(e, _groupPanel))
            {
                _quickGroupUIService.HandleDrop(e);
            }
        }

        /// <summary>判断当前拖放位置是否落在指定元素的可视范围内</summary>
        private bool IsDropOverElement(DragEventArgs e, FrameworkElement? element)
        {
            if (element == null || !element.IsVisible) return false;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            Point p = e.GetPosition(element);
            return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
        }

        #region 顶层面板拖拽排序（编辑模式）

        /// <summary>为编辑模式下的顶层面板挂载拖拽排序手柄（整卡片拖拽）</summary>
        private void AttachPanelDragHandlers(FrameworkElement fe, PanelItem item)
        {
            fe.Tag = item.Id;
            Point start = default;
            bool ready = false;
            fe.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // 仅当鼠标按在「标题栏」上时才准备拖拽调序，避免拖动滚动条/内容误触发移动
                if (!IsWithinPanelDragHandle(e.OriginalSource, fe)) { ready = false; return; }
                start = e.GetPosition(fe);
                ready = true;
            };
            fe.PreviewMouseMove += (s, e) =>
            {
                if (!ready || e.LeftButton != MouseButtonState.Pressed) return;
                // 若正在拖拽调整模块高度，则不触发面板调序
                if (_resizingModule) { ready = false; return; }
                Point pos = e.GetPosition(fe);
                if (Math.Abs(pos.X - start.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(pos.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    ready = false;
                    var data = new DataObject("PanelItemId", item.Id);
                    try { DragDrop.DoDragDrop(fe, data, DragDropEffects.Move); }
                    finally { HidePanelDragIndicator(); HidePinnedDragIndicator(); }
                }
            };
            fe.PreviewMouseLeftButtonUp += (s, e) => { ready = false; };
        }

        /// <summary>标题栏拖拽手柄标记：仅带此 Tag 的元素（含其子元素）可发起面板调序拖拽</summary>
        private const string PanelDragHandleTag = "panelDragHandle";

        /// <summary>自定义卡片标题栏标记：带此 Uid 的标题栏在可调高包装时保持固定（不参与滚动）</summary>
        private const string PanelHeaderUid = "panelHeader";

        /// <summary>判断鼠标事件源是否位于某个「标题栏拖拽手柄」内（向上遍历至 stopAt 为止）</summary>
        private bool IsWithinPanelDragHandle(object? source, FrameworkElement stopAt)
        {
            DependencyObject? d = source as DependencyObject;
            while (d != null)
            {
                if (d is FrameworkElement f && (f.Tag as string) == PanelDragHandleTag) return true;
                if (ReferenceEquals(d, stopAt)) return false;
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void DynamicContent_DragOver(object sender, DragEventArgs e)
        {
            if (!_isEditMode || !e.Data.GetDataPresent("PanelItemId")) { HidePanelDragIndicator(); return; }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var (_, y) = GetPanelDrop(e);
            ShowPanelDragIndicator(y);
        }

        private void DynamicContent_DragLeave(object sender, DragEventArgs e) => HidePanelDragIndicator();

        private void DynamicContent_Drop(object sender, DragEventArgs e)
        {
            HidePanelDragIndicator();
            if (!_isEditMode || !e.Data.GetDataPresent("PanelItemId")) return;
            string draggedId = (string)e.Data.GetData("PanelItemId");
            MovePanelToIndex(draggedId, GetPanelDropIndex(e));
            e.Handled = true;
        }

        // ===== 置顶区拖放（编辑模式）：拖入置顶区 = 自动置顶，拖回滚动区 = 取消置顶 =====

        private void PinnedContent_DragOver(object sender, DragEventArgs e)
        {
            if (!_isEditMode || !e.Data.GetDataPresent("PanelItemId")) { HidePinnedDragIndicator(); return; }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var (_, y) = GetPinnedDrop(e);
            ShowPinnedDragIndicator(y);
        }

        private void PinnedContent_DragLeave(object sender, DragEventArgs e) => HidePinnedDragIndicator();

        private void PinnedContent_Drop(object sender, DragEventArgs e)
        {
            HidePinnedDragIndicator();
            if (!_isEditMode || !e.Data.GetDataPresent("PanelItemId")) return;
            string draggedId = (string)e.Data.GetData("PanelItemId");
            MovePanelToPinnedIndex(draggedId, GetPinnedDrop(e).index);
            e.Handled = true;
        }

        /// <summary>根据拖放的 Y 坐标计算目标插入位置（数据层面板索引）</summary>
        private int GetPanelDropIndex(DragEventArgs e) => GetPanelDrop(e).index;

        /// <summary>根据 dynamicContent 内容坐标系中的位置计算顶层面板插入位置（以各模块中线为界）。
        /// 返回 Panels 列表索引（供 AddPanelToGroupOrTop 使用）：存在置顶模块时，滚动区可视索引需换算为列表索引。</summary>
        private int GetPanelInsertIndexAt(Point pos)
        {
            double y = 0; int visIdx = 0;
            foreach (UIElement uie in dynamicContent.Children)
            {
                if (uie is not FrameworkElement fe || fe.Tag is not string) continue;
                double h = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                if (pos.Y < y + h / 2) return UnpinnedVisualIndexToPanelIndex(visIdx);
                y += h; visIdx++;
            }
            return UnpinnedVisualIndexToPanelIndex(visIdx);
        }

        /// <summary>滚动区可视索引（未置顶模块子集序号）→ Panels 列表索引</summary>
        private int UnpinnedVisualIndexToPanelIndex(int visIdx)
        {
            if (_currentWindowConfig == null) return visIdx;
            var panels = _currentWindowConfig.Panels;
            var unpinned = panels.Where(p => !p.IsPinned).ToList();
            if (visIdx >= unpinned.Count) return panels.Count;
            return panels.IndexOf(unpinned[visIdx]);
        }

        /// <summary>同时返回插入索引与该边界在 dynamicContent 内容坐标系中的 Y 位置</summary>
        private (int index, double y) GetPanelDrop(DragEventArgs e)
        {
            Point pos = e.GetPosition(dynamicContent);
            double y = 0; int panelIdx = 0;
            foreach (UIElement uie in dynamicContent.Children)
            {
                if (uie is not FrameworkElement fe || fe.Tag is not string) continue;
                double h = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                if (pos.Y < y + h / 2) return (panelIdx, y);
                y += h; panelIdx++;
            }
            return (panelIdx, y);
        }

        /// <summary>同时返回置顶区插入索引与该边界在 pinnedContent 内容坐标系中的 Y 位置</summary>
        private (int index, double y) GetPinnedDrop(DragEventArgs e)
        {
            Point pos = e.GetPosition(pinnedContent);
            double y = 0; int panelIdx = 0;
            foreach (UIElement uie in pinnedContent.Children)
            {
                if (uie is not FrameworkElement fe || fe.Tag is not string) continue;
                double h = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                if (pos.Y < y + h / 2) return (panelIdx, y);
                y += h; panelIdx++;
            }
            return (panelIdx, y);
        }

        /// <summary>将常驻叠加层指示器定位到指定边界并显示（不改变布局）</summary>
        private void ShowPanelDragIndicator(double contentY)
        {
            // dynamicContent 在 Grid 中向下偏移了其 Margin.Top，指示器与其同处一个 Grid，故需叠加该偏移
            double top = dynamicContent.Margin.Top + contentY - panelDropIndicator.Height / 2;
            if (top < 0) top = 0;
            panelDropIndicator.Margin = new Thickness(0, top, 0, 0);
            panelDropIndicator.Visibility = Visibility.Visible;
        }

        private void HidePanelDragIndicator()
        {
            panelDropIndicator.Visibility = Visibility.Collapsed;
        }

        /// <summary>将置顶区指示器定位到指定边界并显示（不改变布局）</summary>
        private void ShowPinnedDragIndicator(double contentY)
        {
            double top = pinnedContent.Margin.Top + contentY - pinnedDropIndicator.Height / 2;
            if (top < 0) top = 0;
            pinnedDropIndicator.Margin = new Thickness(0, top, 0, 0);
            pinnedDropIndicator.Visibility = Visibility.Visible;
        }

        private void HidePinnedDragIndicator()
        {
            pinnedDropIndicator.Visibility = Visibility.Collapsed;
        }

        /// <summary>将指定面板拖入滚动区目标位置并持久化（拖入滚动区即取消置顶）。
        /// insertIdx 为滚动区可视索引（未置顶模块子集序号，含拖拽项自身）。</summary>
        private void MovePanelToIndex(string draggedId, int insertIdx)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var panels = _currentWindowConfig.Panels;
            int oldIdx = panels.FindIndex(p => p.Id == draggedId);
            if (oldIdx < 0) return;
            var item = panels[oldIdx];
            bool wasPinned = item.IsPinned;

            // 拖拽项本身未置顶时，可视索引中包含自身占位，需先扣除；置顶项拖入滚动区则无需扣除
            if (!wasPinned)
            {
                int draggedVisIdx = panels.Where(p => !p.IsPinned).ToList().IndexOf(item);
                if (draggedVisIdx >= 0 && insertIdx > draggedVisIdx) insertIdx--;
            }

            panels.RemoveAt(oldIdx);
            item.IsPinned = false; // 拖入滚动区即取消置顶

            var unpinned = panels.Where(p => !p.IsPinned).ToList();
            if (insertIdx < 0) insertIdx = 0;
            if (insertIdx > unpinned.Count) insertIdx = unpinned.Count;
            // 可视索引 → 列表索引：末尾追加到列表尾部，否则定位到对应未置顶模块之前
            int newIdx = insertIdx == unpinned.Count ? panels.Count : panels.IndexOf(unpinned[insertIdx]);
            // 未置顶且位置未变时无需刷新；置顶项状态已变化，必须保存并重渲染
            if (!wasPinned && newIdx == oldIdx) { panels.Insert(oldIdx, item); return; }
            panels.Insert(newIdx, item);
            _configManager.Save();
            RenderPanels();
            // 排序同步：将当前所有模块的顺序推送到服务端
            _ = PushItemPositionsAsync();
        }

        /// <summary>将指定面板拖入置顶区目标位置并持久化（拖入置顶区即自动置顶）。
        /// insertIdx 为置顶区可视索引（置顶模块子集序号，含拖拽项自身）。</summary>
        private void MovePanelToPinnedIndex(string draggedId, int insertIdx)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var panels = _currentWindowConfig.Panels;
            int oldIdx = panels.FindIndex(p => p.Id == draggedId);
            if (oldIdx < 0) return;
            var item = panels[oldIdx];
            bool wasPinned = item.IsPinned;

            // 拖拽项本身已置顶时，可视索引中包含自身占位，需先扣除
            if (wasPinned)
            {
                int draggedVisIdx = panels.Where(p => p.IsPinned).ToList().IndexOf(item);
                if (draggedVisIdx >= 0 && insertIdx > draggedVisIdx) insertIdx--;
            }

            panels.RemoveAt(oldIdx);
            item.IsPinned = true; // 拖入置顶区即自动置顶

            var pinned = panels.Where(p => p.IsPinned).ToList();
            if (insertIdx < 0) insertIdx = 0;
            if (insertIdx > pinned.Count) insertIdx = pinned.Count;
            // 置顶区末尾 → 插入到最后一个置顶模块之后（无置顶模块时为列表最前）；否则定位到对应置顶模块之前
            int newIdx = insertIdx == pinned.Count
                ? (pinned.Count > 0 ? panels.IndexOf(pinned[pinned.Count - 1]) + 1 : 0)
                : panels.IndexOf(pinned[insertIdx]);
            if (wasPinned && newIdx == oldIdx) { panels.Insert(oldIdx, item); return; }
            panels.Insert(newIdx, item);
            _configManager.Save();
            RenderPanels();
            // 排序同步
            _ = PushItemPositionsAsync();
        }

        /// <summary>将当前窗口内所有模块的当前顺序推送到服务端</summary>
        private async Task PushItemPositionsAsync()
        {
            if (_currentWindowConfig == null) return;
            var serverIds = _currentWindowConfig.Panels
                .Where(p => !string.IsNullOrEmpty(p.ServerId))
                .Select(p => p.ServerId!)
                .ToList();
            if (serverIds.Count == 0) return;

            // 团队窗口的模块排序是团队共享的，透传团队加密串由后端解析
            string? corpId = null;
            if (_currentWindowConfig.OwnerType == "team"
                && !string.IsNullOrEmpty(_currentWindowConfig.CorpId))
            {
                corpId = _currentWindowConfig.CorpId;
            }

            await Services.CloudSyncService.Instance.PushResourcePosAsync("item", serverIds, corpId).ConfigureAwait(false);
        }

        #endregion

        #endregion

        #region 事件处理

        private void AIChatButton_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            var mode = app.AppSettings.AIChatMode ?? "client";
            bool loggedIn = !string.IsNullOrEmpty(app.AppSettings.AccountToken);

            // 默认本地化（保护隐私）；auto：已登录用网页版，未登录用本地化；也可手动强制指定
            bool useWeb = mode == "web" || (mode == "auto" && loggedIn);

            if (useWeb)
            {
                if (_aiChatWebWindow != null && _aiChatWebWindow.IsLoaded)
                {
                    _aiChatWebWindow.Activate();
                    return;
                }

                _aiChatWebWindow = new SideDesk.Windows.AIChatWebWindow();
                if (_mainWindow != null)
                {
                    _aiChatWebWindow.Left = _mainWindow.Left + _mainWindow.Width;
                    _aiChatWebWindow.Top = _mainWindow.Top;
                    _aiChatWebWindow.Width = 420;
                    _aiChatWebWindow.Height = Math.Max(400, _mainWindow.Height);
                }
                _aiChatWebWindow.Show();
                return;
            }

            // 客户端内置模式
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
                    MessageBox.Show(Framework.Lang.T("未找到可用的任务栏窗口"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Window pickerDialog = new Window
                {
                    Title = Framework.Lang.T("选择窗口"),
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
                var leftHeader = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("已锁定窗口列表"),
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
                var deleteRuleBtn = new Button { Content = Framework.Lang.T("删除规则"), Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
                var refreshLockedBtn = new Button { Content = Framework.Lang.T("刷新"), Width = 60, Height = 28 };
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
                        sp.Children.Add(new Emoji.Wpf.TextBlock
                        {
                            Text = head,
                            FontWeight = FontWeights.Bold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        string detail = !string.IsNullOrEmpty(r.MatchPattern)
                            ? $"匹配（{(r.MatchMode == TitleMatchMode.Equals ? "等于" : "包含")}）：{r.MatchPattern}"
                            : Framework.Lang.TF("原标题：{0}", r.OriginalTitle);
                        sp.Children.Add(new Emoji.Wpf.TextBlock
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
                            Content = new Emoji.Wpf.TextBlock
                            {
                                Text = Framework.Lang.T("（暂无锁定规则）"),
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
                        if (MessageBox.Show(Framework.Lang.TF("确定删除规则：[{0}] {1}？\n\n注意：仅删除规则，如果该窗口当前仍被锁定，下次开机将不再自动锁定。", selRule.LockText, selRule.ProcessName),
                            Framework.Lang.T("确认删除"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                        {
                            TitleLockRuleManager.RemoveExact(selRule);
                            reloadLocked();
                        }
                    }
                    else
                    {
                        MessageBox.Show(Framework.Lang.T("请选中要删除的规则"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                };
                refreshLockedBtn.Click += (s, ev) => reloadLocked();

                grid.Children.Add(leftPanel);

                // ===== 右侧：可选窗口 =====
                var rightPanel = new DockPanel();
                Grid.SetColumn(rightPanel, 2);
                var rightHeader = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("选择一个窗口来锁定任务栏标题"),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                DockPanel.SetDock(rightHeader, Dock.Top);
                rightPanel.Children.Add(rightHeader);

                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(btnPanel, Dock.Bottom);
                var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
                cancelBtn.Click += (s, ev) => pickerDialog.Close();
                var okBtn = new Button { Content = Framework.Lang.T("下一步"), Width = 80, Height = 30, IsDefault = true };
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
                    textPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = w.Title,
                        FontWeight = FontWeights.Bold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 420
                    });
                    textPanel.Children.Add(new Emoji.Wpf.TextBlock
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
                        MessageBox.Show(Framework.Lang.T("请选择一个窗口"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show(Framework.Lang.TF("任务栏标记功能出错：{0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowOverlayEditor(TaskbarOverlayManager.WindowInfo window)
        {
            var dialog = new LockTitleDialog(window) { Owner = _mainWindow };
            dialog.ShowDialog();
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
        /// "显示设置"菜单展开时动态填充可切换的显示选项（工具栏显示开关 + 全局显示开关）
        /// </summary>
        private void DisplaySettingsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;

            menu.Items.Clear();

            // 工具显示开关（按窗口配置）：勾选 = 底部工具栏显示对应工具按钮
            if (_currentWindowConfig != null)
            {
                var toolDisplays = new (string Key, string Header, Action Action)[]
                {
                    ("ClipboardHistory", "📋 剪贴板历史", ShowClipboardHistoryTool),
                    ("AIChat",           "🤖 AI 助手",    ShowAIChatTool),
                    ("VoiceInput",       "🎤 语音输入",  ShowVoiceInputTool)
                };
                foreach (var (key, header, action) in toolDisplays)
                {
                    string k = key;
                    Action act = action;
                    var mi = new MenuItem
                    {
                        Header = header,
                        IsCheckable = true,
                        IsChecked = _currentWindowConfig.EnabledTools.Contains(k),
                        Tag = k
                    };
                    mi.Click += (s, ev) => ToggleTool(k, mi.IsChecked, act);
                    menu.Items.Add(mi);
                }
                menu.Items.Add(new Separator());
            }

            // 使用时长分析：全局设置，实际控制后台统计服务的启停
            var settings = AppSettings.Instance;
            var usageMi = new MenuItem
            {
                Header = Framework.Lang.T("使用时长分析"),
                IsCheckable = true,
                IsChecked = settings.ShowUsageStats,
                Tag = "ShowUsageStats"
            };
            usageMi.Click += (s, ev) => ToggleDisplaySetting("ShowUsageStats", usageMi.IsChecked);
            menu.Items.Add(usageMi);
        }

        private void ToggleDisplaySetting(string key, bool value)
        {
            var settings = AppSettings.Instance;
            switch (key)
            {
                case "ShowUsageStats":
                    settings.ShowUsageStats = value;
                    SideDesk.Services.WindowUsageService.Instance.ApplyEnabled(value);
                    break;
            }
            settings.Save();
        }

        private void ToggleHideModuleFrame_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            AppSettings.Instance.HideModuleFrame = menuItem?.IsChecked ?? false;
            AppSettings.Instance.Save();
            RenderPanels();
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
        /// 右键菜单打开时，同步勾选状态（编辑模式、隐藏模块外框）并记录插入位置
        /// </summary>
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // 记录右键点击位置对应的插入索引（用于“添加模块”时插入到两模块之间）
            _pendingInsertIndex = null;
            try { _pendingInsertIndex = GetPanelInsertIndexAt(Mouse.GetPosition(dynamicContent)); }
            catch { _pendingInsertIndex = null; }

            // 同步编辑模式 checkbox 状态
            editModeMenu.IsChecked = _isEditMode;

            // 同步“隐藏模块外框”勾选状态
            hideModuleFrameMenu.IsChecked = AppSettings.Instance.HideModuleFrame;

            // 设置屏幕菜单勾选（已移除）
        }

        #endregion

        #region 数据看板面板

        private UIElement BuildDataDashboardWidget(PanelItem item)
        {
            item.DashboardItems ??= new List<DataDashboardItem>();

            // 数据项容器：根据 DashboardColumns 使用 UniformGrid 实现多列等宽布局
            int cols = Math.Clamp(item.DashboardColumns, 1, 4);
            var contentPanel = new System.Windows.Controls.Primitives.UniformGrid { Columns = cols };

            // 数据项列表
            if (item.DashboardItems != null && item.DashboardItems.Count > 0)
            {
                foreach (var dashItem in item.DashboardItems)
                {
                    var card = new StackPanel
                    {
                        Margin = new Thickness(4, 4, 4, 4),
                        Cursor = Cursors.Hand,
                        Background = Brushes.Transparent,
                    };

                    var labelBlock = new Emoji.Wpf.TextBlock
                    {
                        Text = dashItem.Label,
                        FontSize = dashItem.LabelFontSize > 0 ? dashItem.LabelFontSize : 11,
                        FontWeight = dashItem.LabelBold ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = ApplyDashColor(dashItem.LabelColor, "BrushTextSec")
                    };
                    card.Children.Add(labelBlock);

                    var valueBlock = new Emoji.Wpf.TextBlock
                    {
                        Text = dashItem.Value ?? "--",
                        FontSize = dashItem.ValueFontSize > 0 ? dashItem.ValueFontSize : 16,
                        FontWeight = dashItem.ValueBold ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = ApplyDashColor(dashItem.ValueColor, "BrushTextMain"),
                        Tag = dashItem.Id
                    };
                    card.Children.Add(valueBlock);

                    // 应用水平对齐（标题/值各自对齐）与值格式
                    ApplyDashboardItemStyle(dashItem, card, labelBlock, valueBlock);

                    // 右键菜单：立即更新数据 + 删除数据项
                    var capturedDash = dashItem;
                    var dashItemMenu = new ContextMenu();

                    var refreshItem = new MenuItem { Header = Framework.Lang.T("立即更新数据") };
                    refreshItem.Click += async (s, e) =>
                    {
                        try
                        {
                            // 引用项刷新其指向的实际轮询数据项
                            var target = string.IsNullOrEmpty(capturedDash.RefVarKey)
                                ? capturedDash
                                : Services.DataPollingService.Instance.FindItemByVarKey(capturedDash.RefVarKey) ?? capturedDash;
                            await Services.DataPollingService.Instance.RefreshItemAsync(target);
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DataDashboard] 手动刷新失败 {capturedDash.Label}: {ex.Message}"); }
                    };
                    dashItemMenu.Items.Add(refreshItem);

                    var editStyleItem = new MenuItem { Header = Framework.Lang.T("编辑样式") };
                    editStyleItem.Click += (s, e) =>
                    {
                        if (ShowDashboardItemStyleDialog(capturedDash))
                        {
                            // 定向更新当前数据项卡片的样式，避免 RenderPanels() 全量重建导致整个窗口重载
                            ApplyDashboardItemStyle(capturedDash, card, labelBlock, valueBlock);
                        }
                    };
                    dashItemMenu.Items.Add(editStyleItem);

                    var editItem = new MenuItem { Header = Framework.Lang.T("编辑数据项") };
                    editItem.Click += (s, e) =>
                    {
                        string? oldRefVarKey = capturedDash.RefVarKey;
                        var updated = ShowAddDashboardItemDialog(capturedDash);
                        if (updated != null)
                        {
                            _configManager?.Save();
                            if (_configManager != null) Services.DataPollingService.Instance.SyncFromConfigs(_configManager);
                            // 引用目标变化（含旧式配置项转为引用）需重建 UI 重新订阅数据源更新；
                            // 否则仅定向更新名称，避免 RenderPanels() 全量重建导致浏览器面板 reload、闪烁。
                            if (!string.Equals(oldRefVarKey, updated.RefVarKey, StringComparison.Ordinal))
                            {
                                RenderPanels();
                            }
                            else
                            {
                                labelBlock.Text = updated.Label;
                            }
                        }
                    };
                    dashItemMenu.Items.Add(editItem);

                    var dashItemDel = new MenuItem { Header = Framework.Lang.T("删除数据项") };
                    dashItemDel.Click += (s, e) =>
                    {
                        item.DashboardItems?.Remove(capturedDash);
                        _configManager?.Save();
                        if (_configManager != null) Services.DataPollingService.Instance.SyncFromConfigs(_configManager);
                        RenderPanels();
                    };
                    dashItemMenu.Items.Add(dashItemDel);
                    card.ContextMenu = dashItemMenu;

                    // 点击卡片以 app 模式打开目标网址（干净独立窗口，不在主浏览器开标签页）
                    card.MouseLeftButtonUp += (s, e) =>
                    {
                        Services.DataPollingService.Instance.OpenItemUrlInAppMode(capturedDash);
                    };

                    contentPanel.Children.Add(card);

                    // 启动定时刷新
                    StartDashboardItemRefresh(dashItem, valueBlock);
                }
            }
            else
            {
                contentPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无数据项，请编辑添加"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            // 添加数据项回调
            void AddDashItem()
            {
                var newItem = ShowAddDashboardItemDialog();
                if (newItem != null)
                {
                    item.DashboardItems ??= new List<DataDashboardItem>();
                    item.DashboardItems.Add(newItem);
                    _configManager?.Save();
                    if (_configManager != null) Services.DataPollingService.Instance.SyncFromConfigs(_configManager);
                    RenderPanels();
                }
            }

            // SkipWrapper：分组内直接返回内容
            if (item.SkipWrapper)
            {
                if (_isEditMode)
                {
                    var addBtn = new Button
                    {
                        Content = Framework.Lang.T("+ 添加数据项"),
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushAccent"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 6, 0, 0)
                    };
                    addBtn.Click += (s, e) => AddDashItem();
                    var sp = new StackPanel();
                    sp.Children.Add(contentPanel);
                    sp.Children.Add(addBtn);
                    return sp;
                }
                return contentPanel;
            }

            // 基于 ModuleControl 基础组件包装
            var module = CreateModuleControl(Framework.Lang.T("📊 数据看板"), item);

            var addMenuItem = new MenuItem { Header = Framework.Lang.T("添加数据项") };
            addMenuItem.Click += (s, e) => AddDashItem();
            module.AddCustomMenuItem(addMenuItem);

            // 布局子菜单：几列
            var layoutMenu = new MenuItem { Header = Framework.Lang.T("布局") };
            for (int n = 1; n <= 4; n++)
            {
                var colItem = new MenuItem { Header = Framework.Lang.TF("{0} 列", n), IsCheckable = true, IsChecked = cols == n };
                int colCount = n;
                colItem.Click += (s, e) =>
                {
                    item.DashboardColumns = colCount;
                    _configManager?.Save();
                    RenderPanels();
                };
                layoutMenu.Items.Add(colItem);
            }
            module.AddCustomMenuItem(layoutMenu);

            module.SetContent(contentPanel);
            return module;
        }

        /// <summary>根据自定义颜色字符串或回退到主题资源获取 Brush</summary>
        private Brush ApplyDashColor(string? customColor, string fallbackResourceKey)
        {
            if (!string.IsNullOrEmpty(customColor))
            {
                try { return (SolidColorBrush)new BrushConverter().ConvertFrom(customColor)!; }
                catch { }
            }
            return (Brush)FindResource(fallbackResourceKey);
        }

        /// <summary>
        /// 按格式渲染值文本：{0} 或「变量」代表本项的值；{变量名}（如 {qoder.count}）替换为对应变量的当前值；
        /// 无任何占位符时格式作前缀；留空格式直接显示值。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex _dashFmtPlaceholderRegex =
            new(@"\{([^{}]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string FormatDashValue(string? rawValue, string? format)
        {
            var v = string.IsNullOrEmpty(rawValue) ? "--" : rawValue;
            if (string.IsNullOrWhiteSpace(format)) return v;
            var fmt = format!;
            if (!fmt.Contains('{')) return fmt + v;
            return _dashFmtPlaceholderRegex.Replace(fmt, m =>
            {
                var key = m.Groups[1].Value.Trim();
                if (key == "0" || key == "变量") return v;
                var src = Services.DataPollingService.Instance.FindItemByVarKey(key);
                return string.IsNullOrEmpty(src?.Value) ? "--" : src!.Value;
            });
        }

        /// <summary>格式是否引用了其他变量（{变量名}，排除 {0}/变量 这类自身值占位符）；引用时需在任意数据更新时重算文本</summary>
        private static bool FormatHasVarReference(string? format)
        {
            if (string.IsNullOrWhiteSpace(format) || !format.Contains('{')) return false;
            foreach (System.Text.RegularExpressions.Match m in _dashFmtPlaceholderRegex.Matches(format))
            {
                var key = m.Groups[1].Value.Trim();
                if (key != "0" && key != "变量") return true;
            }
            return false;
        }

        /// <summary>数据项样式编辑弹窗</summary>
        private bool ShowDashboardItemStyleDialog(DataDashboardItem dashItem)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("编辑样式 - ") + dashItem.Label,
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Owner = Window.GetWindow(this)
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(20) };

            // 标题
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("编辑数据块样式"),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = (Brush)FindResource("BrushTextMain")
            });

            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");
            var comboStyle = (Style)FindResource("GlobalComboBoxStyle");

            var presetColors = new[]
            {
                "#FFFFFF", "#CCCCCC", "#999999", "#666666", "#333333", "#000000",
                "#E74C3C", "#E67E22", "#F39C12", "#F1C40F", "#2ECC71", "#1ABC9C",
                "#3498DB", "#2980B9", "#9B59B6", "#8E44AD", "#34495E", "#1A1A2E"
            };

            string? labelSelectedColor = dashItem.LabelColor;
            string? valueSelectedColor = dashItem.ValueColor;

            // 构建颜色选择器控件（色块按钮 + 弹出预设色板 + 手动输入）
            (Border swatchBtn, TextBox hexBox) BuildColorPicker(string? currentColor)
            {
                // 右侧 hex 输入框（先声明，供 popup 回调使用）
                var hexBox = new TextBox { Text = currentColor ?? "", Style = textBoxStyle, Width = 110 };

                // 色块按钮（包一层容器作为 Popup PlacementTarget，避免逻辑树冲突）
                var swatchWrapper = new Border { Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 0) };
                var swatch = new Border
                {
                    Width = 28, Height = 28,
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                swatchWrapper.Child = swatch;

                void UpdateSwatch()
                {
                    if (string.IsNullOrEmpty(currentColor))
                        swatch.Background = (Brush)FindResource("BrushInputBG");
                    else
                    {
                        try { swatch.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(currentColor)!; }
                        catch { swatch.Background = (Brush)FindResource("BrushInputBG"); }
                    }
                }
                UpdateSwatch();

                // 弹出色板
                var popup = new System.Windows.Controls.Primitives.Popup
                {
                    PlacementTarget = swatchWrapper,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    AllowsTransparency = true,
                    StaysOpen = false
                };
                var pickerPanel = new Border
                {
                    Background = (Brush)FindResource("BrushDialogBG"),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8)
                };
                var pickerContent = new StackPanel();
                var colorGrid = new WrapPanel { Width = 180 };
                foreach (var c in presetColors)
                {
                    var cell = new Border
                    {
                        Width = 24, Height = 24,
                        Margin = new Thickness(2),
                        CornerRadius = new CornerRadius(3),
                        Background = (SolidColorBrush)new BrushConverter().ConvertFrom(c)!,
                        BorderBrush = Brushes.Transparent,
                        BorderThickness = new Thickness(2),
                        Cursor = Cursors.Hand
                    };
                    cell.MouseLeftButtonUp += (s, e) =>
                    {
                        currentColor = c;
                        hexBox.Text = c;
                        UpdateSwatch();
                        popup.IsOpen = false;
                    };
                    colorGrid.Children.Add(cell);
                }
                pickerContent.Children.Add(colorGrid);

                // 自定义颜色输入
                var customDock = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var customLabel = new TextBlock { Text = Framework.Lang.T("自定义："), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                DockPanel.SetDock(customLabel, Dock.Left);
                customDock.Children.Add(customLabel);
                var customBox = new TextBox { Width = 100, FontSize = 11, Style = textBoxStyle };
                customDock.Children.Add(customBox);
                var applyBtn = new Button
                {
                    Content = Framework.Lang.T("应用"), FontSize = 10, Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("GhostButtonStyle")
                };
                applyBtn.Click += (s, e) =>
                {
                    var txt = customBox.Text.Trim();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        currentColor = txt;
                        hexBox.Text = txt;
                        UpdateSwatch();
                    }
                };
                DockPanel.SetDock(applyBtn, Dock.Right);
                customDock.Children.Add(applyBtn);
                pickerContent.Children.Add(customDock);
                pickerPanel.Child = pickerContent;

                popup.Child = pickerPanel;

                swatch.MouseLeftButtonUp += (s, e) => { popup.IsOpen = !popup.IsOpen; };

                hexBox.TextChanged += (s, e) =>
                {
                    currentColor = hexBox.Text.Trim();
                    UpdateSwatch();
                };

                // 不添加到任何父容器，由调用方负责布局
                return (swatchWrapper, hexBox);
            }

            // 标题（颜色 + 字号 + 粗体 同一行）
            var labelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var labelRowLabel = new TextBlock { Text = Framework.Lang.T("标题："), Width = 50, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(labelRowLabel, Dock.Left);
            labelRow.Children.Add(labelRowLabel);
            var (labelSwatch, labelHexBox) = BuildColorPicker(labelSelectedColor);
            labelRow.Children.Add(labelSwatch);
            labelRow.Children.Add(labelHexBox);
            var labelSizeBox = new TextBox
            {
                Text = dashItem.LabelFontSize > 0 ? dashItem.LabelFontSize.ToString() : "12",
                Style = textBoxStyle, Width = 70, Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(labelSizeBox, Dock.Right);
            labelRow.Children.Add(labelSizeBox);
            var labelBoldCheck = new CheckBox
            {
                Content = Framework.Lang.T("粗体"), IsChecked = dashItem.LabelBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("DarkCheckBoxStyle"),
                ToolTip = Framework.Lang.T("标题文字粗体")
            };
            DockPanel.SetDock(labelBoldCheck, Dock.Right);
            labelRow.Children.Add(labelBoldCheck);
            root.Children.Add(labelRow);

            // 数据（颜色 + 字号 + 粗体 同一行）
            var valueRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var valueRowLabel = new TextBlock { Text = Framework.Lang.T("数据："), Width = 50, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(valueRowLabel, Dock.Left);
            valueRow.Children.Add(valueRowLabel);
            var (valueSwatch, valueHexBox) = BuildColorPicker(valueSelectedColor);
            valueRow.Children.Add(valueSwatch);
            valueRow.Children.Add(valueHexBox);
            var valueSizeBox = new TextBox
            {
                Text = dashItem.ValueFontSize > 0 ? dashItem.ValueFontSize.ToString() : "16",
                Style = textBoxStyle, Width = 70, Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(valueSizeBox, Dock.Right);
            valueRow.Children.Add(valueSizeBox);
            var valueBoldCheck = new CheckBox
            {
                Content = Framework.Lang.T("粗体"), IsChecked = dashItem.ValueBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("DarkCheckBoxStyle"),
                ToolTip = Framework.Lang.T("数据文字粗体")
            };
            DockPanel.SetDock(valueBoldCheck, Dock.Right);
            valueRow.Children.Add(valueBoldCheck);
            root.Children.Add(valueRow);

            // 水平对齐
            var alignDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var alignLabel = new TextBlock { Text = Framework.Lang.T("水平对齐："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(alignLabel, Dock.Left);
            alignDock.Children.Add(alignLabel);
            var alignCombo = new ComboBox { Style = comboStyle };
            alignCombo.Items.Add(Framework.Lang.T("左对齐"));
            alignCombo.Items.Add(Framework.Lang.T("居中"));
            alignCombo.Items.Add(Framework.Lang.T("右对齐"));
            alignCombo.SelectedIndex = dashItem.HorizontalAlignment switch
            {
                "Center" => 1,
                "Right" => 2,
                _ => 0
            };
            alignDock.Children.Add(alignCombo);
            root.Children.Add(alignDock);

            // 值格式（可选）：{0} 或「变量」代表值，如 你好：{0}
            var fmtDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var fmtLabel = new TextBlock { Text = Framework.Lang.T("格式："), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(fmtLabel, Dock.Left);
            fmtDock.Children.Add(fmtLabel);
            var fmtBox = new TextBox { Text = dashItem.ValueFormat ?? "", Style = textBoxStyle };
            fmtDock.Children.Add(fmtBox);
            root.Children.Add(fmtDock);
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("用 {0} 或 {变量名} 代表值，如：你好：{0}、在线：{qoder.count}；留空则直接显示值"),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(70, 0, 0, 16)
            });

            // 提示
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("点击色块选择预设颜色，也可直接输入 #RRGGBB 色值，留空使用默认主题色"),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                dashItem.LabelColor = labelHexBox.Text.Trim();
                dashItem.ValueColor = valueHexBox.Text.Trim();
                if (double.TryParse(labelSizeBox.Text.Trim(), out double ls) && ls > 0) dashItem.LabelFontSize = ls;
                else dashItem.LabelFontSize = 0;
                if (double.TryParse(valueSizeBox.Text.Trim(), out double vs) && vs > 0) dashItem.ValueFontSize = vs;
                else dashItem.ValueFontSize = 0;
                dashItem.LabelBold = labelBoldCheck.IsChecked ?? false;
                dashItem.ValueBold = valueBoldCheck.IsChecked ?? false;
                dashItem.HorizontalAlignment = alignCombo.SelectedIndex switch
                {
                    1 => "Center",
                    2 => "Right",
                    _ => ""
                };
                dashItem.ValueFormat = fmtBox.Text.Trim();
                _configManager?.Save();
                dialog.DialogResult = true;
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            return dialog.ShowDialog() == true;
        }

        /// <summary>
        /// 将数据项的样式（颜色/字号/粗体/水平对齐）应用到对应卡片控件。
        /// 供「编辑样式」对话框确认后定向刷新使用，避免 RenderPanels() 全量重建整个窗口。
        /// </summary>
        private void ApplyDashboardItemStyle(DataDashboardItem dashItem, StackPanel card, Emoji.Wpf.TextBlock labelBlock, Emoji.Wpf.TextBlock valueBlock)
        {
            labelBlock.FontSize = dashItem.LabelFontSize > 0 ? dashItem.LabelFontSize : 11;
            labelBlock.FontWeight = dashItem.LabelBold ? FontWeights.Bold : FontWeights.Normal;
            labelBlock.Foreground = ApplyDashColor(dashItem.LabelColor, "BrushTextSec");

            valueBlock.FontSize = dashItem.ValueFontSize > 0 ? dashItem.ValueFontSize : 16;
            valueBlock.FontWeight = dashItem.ValueBold ? FontWeights.Bold : FontWeights.Normal;
            valueBlock.Foreground = ApplyDashColor(dashItem.ValueColor, "BrushTextMain");

            // 水平对齐：卡片占满单元格，标题与值各自独立对齐（居中时文字真正居中）
            var hAlign = dashItem.HorizontalAlignment;
            var blockAlign = hAlign == "Center" ? HorizontalAlignment.Center
                           : hAlign == "Right" ? HorizontalAlignment.Right
                           : HorizontalAlignment.Left;
            var textAlign = hAlign == "Center" ? TextAlignment.Center
                          : hAlign == "Right" ? TextAlignment.Right
                          : TextAlignment.Left;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            labelBlock.HorizontalAlignment = blockAlign;
            labelBlock.TextAlignment = textAlign;
            valueBlock.HorizontalAlignment = blockAlign;
            valueBlock.TextAlignment = textAlign;

            // 值格式：按格式重新渲染当前值（引用项取被引用数据项的值）
            var displayItem = dashItem;
            if (!string.IsNullOrEmpty(dashItem.RefVarKey))
            {
                var source = Services.DataPollingService.Instance.FindItemByVarKey(dashItem.RefVarKey);
                if (source != null) displayItem = source;
            }
            valueBlock.Text = FormatDashValue(displayItem.Value, dashItem.ValueFormat);
        }

        /// <summary>
        /// 面板只负责展示：订阅中央轮询服务 DataPollingService 的数据更新事件，不再自行定时抓取。
        /// 引用项（RefVarKey 非空）订阅其引用的实际轮询数据项的更新。
        /// </summary>
        private void StartDashboardItemRefresh(DataDashboardItem dashItem, Emoji.Wpf.TextBlock valueBlock)
        {
            // 引用项：解析被引用的运行中数据项作为实际展示数据源（找不到时展示自身持久化值/--）
            var displayItem = dashItem;
            if (!string.IsNullOrEmpty(dashItem.RefVarKey))
            {
                var source = Services.DataPollingService.Instance.FindItemByVarKey(dashItem.RefVarKey);
                if (source != null) displayItem = source;
            }

            var valueFormat = dashItem.ValueFormat;
            // 格式引用了其他变量（如 {qoder.count}）时，任意数据项更新都需重算文本（变量值可能后到/先更新）
            var hasVarRef = FormatHasVarReference(valueFormat);

            // 展示已持久化的值（按格式渲染）
            valueBlock.Text = FormatDashValue(displayItem.Value, valueFormat);

            var itemId = displayItem.Id;

            // 订阅中央轮询服务的数据更新事件
            Action<string, string> handler = (updatedId, value) =>
            {
                if (!hasVarRef && updatedId != itemId) return;
                Dispatcher.Invoke(() =>
                {
                    // 事件触发前各数据项 Value 已更新，直接按当前值重算格式文本
                    valueBlock.Text = FormatDashValue(displayItem.Value, valueFormat);
                });
            };
            Services.DataPollingService.Instance.DataUpdated += handler;

            // 控件卸载时取消订阅，避免重复订阅与内存泄漏（RenderPanels 会重建 UI）
            valueBlock.Unloaded += (s, e) =>
            {
                Services.DataPollingService.Instance.DataUpdated -= handler;
            };
        }

        #endregion

        #region 数据项配置对话框

        private DataDashboardItem? ShowAddDashboardItemDialog(DataDashboardItem? editItem = null)
        {
            var app = (App)Application.Current;
            var templates = DataTemplateManager.Load().Templates;

            var dialog = new Window
            {
                Title = editItem != null ? Framework.Lang.T("编辑数据项") : Framework.Lang.T("添加数据项"),
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
            var comboStyle = (Style)FindResource("GlobalComboBoxStyle");
            var primaryBtnStyle = (Style)FindResource("PrimaryButtonStyle");
            var standardBtnStyle = (Style)FindResource("StandardButtonStyle");

            // ===== 根布局 =====
            var rootPanel = new StackPanel { Margin = new Thickness(20) };

            // 名称框（各页签共用）：放在对话框最上方，供 ApplyTemplate 预填使用
            var nameBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("数据项名称"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            rootPanel.Children.Add(nameBox);

            // ===== 顶部页签：新建（无模板自定义） / 数据模板 / 我的数据看板 =====
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var tabNew = new Button { Content = Framework.Lang.T("🆕 新建"), Style = primaryBtnStyle, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            var tabTpl = new Button { Content = Framework.Lang.T("📋 数据模板"), Style = standardBtnStyle, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            var tabPick = new Button { Content = Framework.Lang.T("📊 我的数据看板"), Style = standardBtnStyle, Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand };
            tabRow.Children.Add(tabNew);
            tabRow.Children.Add(tabTpl);
            tabRow.Children.Add(tabPick);
            rootPanel.Children.Add(tabRow);

            // ===== 页签：数据模板（模板驱动，URL/选择器/间隔由模板提供） =====
            var tplPanel = new StackPanel();

            tplPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("数据模板"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var templateCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            foreach (var t in templates)
                templateCombo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(t.Category) ? t.Name : $"{t.Name}  [{t.Category}]", Tag = t.Id, ToolTip = t.Description });
            if (templates.Count > 0)
            {
                templateCombo.SelectedIndex = 0;
            }
            else
            {
                templateCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("（暂无数据模板）"), IsEnabled = false });
            }
            tplPanel.Children.Add(templateCombo);
            rootPanel.Children.Add(tplPanel);

            // ===== 页签：新建（无模板，手动配置选择器或 API 请求） =====
            var newPanel = new StackPanel();
            var customPanel = newPanel; // 自定义字段直接放在新建页签内

            customPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("获取方式"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var modeCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            modeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("页面选择器"), Tag = "SEL" });
            modeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("API 请求"), Tag = "API" });
            modeCombo.SelectedIndex = 0;
            customPanel.Children.Add(modeCombo);

            customPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("网址"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var urlBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(urlBox);

            var selectorLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("选择器 (CSS Selector 或 XPath)"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            customPanel.Children.Add(selectorLabel);
            var selectorBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(selectorBox);

            var methodLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("请求方式"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            customPanel.Children.Add(methodLabel);
            var methodCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            methodCombo.Items.Add(new ComboBoxItem { Content = "GET", Tag = "GET" });
            methodCombo.Items.Add(new ComboBoxItem { Content = "POST", Tag = "POST" });
            methodCombo.SelectedIndex = 0;
            customPanel.Children.Add(methodCombo);

            var postBodyLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("POST 内容"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            customPanel.Children.Add(postBodyLabel);
            var postBodyBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            customPanel.Children.Add(postBodyBox);

            var extractLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("响应提取（JSON 路径，如 data.count）"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            customPanel.Children.Add(extractLabel);
            var extractBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(extractBox);

            customPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("刷新间隔（秒）"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var intervalBox = new TextBox { Style = textBoxStyle, Text = "60", Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(intervalBox);

            // ===== 公共区（新建/模板页签共用）：所属团队 / 变量名 / 浏览器 / Profile =====
            var commonPanel = new StackPanel();

            // 所属团队（个人 + 团队列表，异步加载；决定变量名唯一性作用域）
            commonPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("所属团队"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var teamCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            teamCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("个人"), Tag = (string?)null });
            teamCombo.SelectedIndex = 0;
            commonPanel.Children.Add(teamCombo);
            string? pendingTeamId = null; // 模板切换时待恢复的团队选中
            _ = Task.Run(async () =>
            {
                try
                {
                    var teams = await Services.TeamService.Instance.ListTeamsAsync();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (teamCombo.Items.Count > 1) return; // 已加载过，避免重复
                        if (teams != null)
                        {
                            foreach (var t in teams)
                                teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
                        }
                        if (pendingTeamId != null)
                        {
                            for (int i = 1; i < teamCombo.Items.Count; i++)
                            {
                                if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == pendingTeamId)
                                { teamCombo.SelectedIndex = i; break; }
                            }
                        }
                    });
                }
                catch { }
            });

            commonPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("变量名（团队/个人内唯一，如 qcoder.count）"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var varKeyBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            commonPanel.Children.Add(varKeyBox);

            // 浏览器 / Profile（联动）
            var browsers = app.ProgramRegistry.Browsers;
            var browserLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("浏览器"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            commonPanel.Children.Add(browserLabel);
            var browserCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            browserCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("默认浏览器"), Tag = (string?)null });
            foreach (var b in browsers)
                browserCombo.Items.Add(new ComboBoxItem { Content = b.Name, Tag = b.Id });
            browserCombo.SelectedIndex = 0;
            commonPanel.Children.Add(browserCombo);

            var profileLabel = new Emoji.Wpf.TextBlock
            {
                Text = "Profile",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            commonPanel.Children.Add(profileLabel);
            var profileCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            commonPanel.Children.Add(profileCombo);

            Action refreshProfiles = () =>
            {
                profileCombo.Items.Clear();
                if (browserCombo.SelectedItem is ComboBoxItem selectedBrowserItem && selectedBrowserItem.Tag is string browserId)
                {
                    var browser = browsers.FirstOrDefault(b => b.Id == browserId);
                    if (browser != null)
                    {
                        foreach (var p in browser.Profiles)
                            profileCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
                        if (profileCombo.Items.Count > 0)
                            profileCombo.SelectedIndex = 0;
                    }
                }
            };
            browserCombo.SelectionChanged += (s, e) => refreshProfiles();

            // 选中模板时预填名称、模板归属团队与模板默认浏览器/Profile
            void ApplyTemplate()
            {
                if (templateCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string tplId) return;
                var tpl = templates.FirstOrDefault(t => t.Id == tplId);
                if (tpl == null) return;
                nameBox.Text = tpl.Name;
                // 预填模板归属团队（列表未加载完时暂存，加载完成后自动恢复）
                pendingTeamId = tpl.TeamId;
                teamCombo.SelectedIndex = 0;
                for (int i = 1; i < teamCombo.Items.Count; i++)
                {
                    if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == tpl.TeamId)
                    { teamCombo.SelectedIndex = i; break; }
                }
                // 预填模板默认浏览器
                for (int i = 0; i < browserCombo.Items.Count; i++)
                {
                    if (browserCombo.Items[i] is ComboBoxItem bci
                        && (tpl.DefaultBrowserId == null ? bci.Tag == null : (bci.Tag as string) == tpl.DefaultBrowserId))
                    { browserCombo.SelectedIndex = i; break; }
                }
                refreshProfiles();
                if (!string.IsNullOrEmpty(tpl.DefaultProfileId))
                {
                    for (int i = 0; i < profileCombo.Items.Count; i++)
                    {
                        if (profileCombo.Items[i] is ComboBoxItem pci && (pci.Tag as string) == tpl.DefaultProfileId)
                        { profileCombo.SelectedIndex = i; break; }
                    }
                }
            }
            templateCombo.SelectionChanged += (s, e) => ApplyTemplate();
            modeCombo.SelectionChanged += (s, e) => SyncCustomVisibility();
            methodCombo.SelectionChanged += (s, e) => SyncCustomVisibility();

            // 页签/获取方式切换时同步各输入区显隐（API 模式不需要浏览器/Profile）
            void SyncCustomVisibility()
            {
                bool isApi = newPanel.Visibility == Visibility.Visible
                    && (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "API";
                bool isPost = (methodCombo.SelectedItem as ComboBoxItem)?.Tag as string == "POST";
                selectorLabel.Visibility = selectorBox.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
                methodLabel.Visibility = methodCombo.Visibility = isApi ? Visibility.Visible : Visibility.Collapsed;
                postBodyLabel.Visibility = postBodyBox.Visibility = isApi && isPost ? Visibility.Visible : Visibility.Collapsed;
                extractLabel.Visibility = extractBox.Visibility = isApi ? Visibility.Visible : Visibility.Collapsed;
                browserLabel.Visibility = browserCombo.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
                profileLabel.Visibility = profileCombo.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
            }
            SyncCustomVisibility();

            rootPanel.Children.Add(newPanel);
            rootPanel.Children.Add(commonPanel);

            // ===== 页签：我的数据看板（引用式，仅展示运行中数据） =====
            var refPanel = new StackPanel();

            // 后台数据 = 当前运行中的数据模块（轮询实例：面板项 + 后台项，按身份去重）+ 变量集，编辑时排除自身
            var bgSources = Services.DataPollingService.Instance.GetAllItems()
                .Where(d => !string.IsNullOrEmpty(d.VarKey))
                .Where(d => editItem == null || d.Id != editItem.Id)
                .Concat(Services.VariableSetService.Instance.GetItems()
                    .Where(d => editItem == null || d.Id != editItem.Id))
                .ToList();

            refPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("请选择一个运行中的数据源，本数据项仅展示其数据，无需配置网址/选择器。"),
                Foreground = labelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            refPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("从后台数据选择"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var bgCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            if (bgSources.Count == 0)
            {
                bgCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("（暂无运行中的数据项）"), IsEnabled = false });
            }
            else
            {
                foreach (var d in bgSources)
                    bgCombo.Items.Add(new ComboBoxItem { Content = $"{d.Label}（{d.VarKey}）", Tag = d });
            }
            refPanel.Children.Add(bgCombo);

            rootPanel.Children.Add(refPanel);

            // 页签切换：new=新建（自定义） / tpl=数据模板 / pick=我的数据看板
            void SwitchTab(string tab)
            {
                tabNew.Style = tab == "new" ? primaryBtnStyle : standardBtnStyle;
                tabTpl.Style = tab == "tpl" ? primaryBtnStyle : standardBtnStyle;
                tabPick.Style = tab == "pick" ? primaryBtnStyle : standardBtnStyle;
                newPanel.Visibility = tab == "new" ? Visibility.Visible : Visibility.Collapsed;
                tplPanel.Visibility = tab == "tpl" ? Visibility.Visible : Visibility.Collapsed;
                refPanel.Visibility = tab == "pick" ? Visibility.Visible : Visibility.Collapsed;
                commonPanel.Visibility = tab == "pick" ? Visibility.Collapsed : Visibility.Visible;
                SyncCustomVisibility();
            }
            tabNew.Click += (s, e) => SwitchTab("new");
            tabTpl.Click += (s, e) =>
            {
                // 进入模板页签时若名称仍为空，则按模板默认值预填（名称/团队/浏览器）
                if (string.IsNullOrWhiteSpace(nameBox.Text)) ApplyTemplate();
                SwitchTab("tpl");
            };
            tabPick.Click += (s, e) => SwitchTab("pick");

            // 初始页签与预填：添加时默认「新建」；编辑时按项类型进入对应页签
            if (editItem == null)
            {
                SwitchTab("new");
            }
            else
            {
                nameBox.Text = editItem.Label ?? "";
                bool isRef = !string.IsNullOrEmpty(editItem.RefVarKey);
                if (isRef)
                {
                    // 引用项：进入「我的数据看板」页签并恢复已选引用源
                    SwitchTab("pick");
                    for (int i = 0; i < bgCombo.Items.Count; i++)
                    {
                        if (bgCombo.Items[i] is ComboBoxItem ci && ci.Tag is DataDashboardItem d
                            && string.Equals(d.VarKey, editItem.RefVarKey, StringComparison.OrdinalIgnoreCase))
                        { bgCombo.SelectedIndex = i; break; }
                    }
                }
                else if (!string.IsNullOrEmpty(editItem.TemplateId))
                {
                    // 模板项：进入「数据模板」页签并恢复模板选中与变量名
                    SwitchTab("tpl");
                    for (int i = 0; i < templateCombo.Items.Count; i++)
                    {
                        if (templateCombo.Items[i] is ComboBoxItem ci && ci.Tag is string tid && tid == editItem.TemplateId)
                        { templateCombo.SelectedIndex = i; break; }
                    }
                    varKeyBox.Text = editItem.VarKey ?? "";
                }
                else
                {
                    // 无模板自定义项：进入「新建」页签并恢复网址/选择器或 API 配置
                    SwitchTab("new");
                    urlBox.Text = editItem.TargetUrl ?? "";
                    bool isApi = !string.IsNullOrEmpty(editItem.ApiMethod);
                    modeCombo.SelectedIndex = isApi ? 1 : 0;
                    selectorBox.Text = editItem.Selector ?? "";
                    methodCombo.SelectedIndex = string.Equals(editItem.ApiMethod, "POST", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    postBodyBox.Text = editItem.ApiBody ?? "";
                    extractBox.Text = editItem.ResponseExtract ?? "";
                    intervalBox.Text = editItem.RefreshIntervalSeconds > 0 ? editItem.RefreshIntervalSeconds.ToString() : "60";
                    varKeyBox.Text = editItem.VarKey ?? "";
                }
                // 模板选择联动会用模板名/模板团队覆盖，恢复数据项自身名称与团队
                if (!isRef)
                {
                    nameBox.Text = editItem.Label ?? "";
                    pendingTeamId = editItem.TeamId;
                    teamCombo.SelectedIndex = 0;
                    for (int i = 1; i < teamCombo.Items.Count; i++)
                    {
                        if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == editItem.TeamId)
                        { teamCombo.SelectedIndex = i; break; }
                    }
                    if (!string.IsNullOrEmpty(editItem.BrowserId))
                    {
                        for (int i = 0; i < browserCombo.Items.Count; i++)
                        {
                            if (browserCombo.Items[i] is ComboBoxItem ci && ci.Tag is string bid && bid == editItem.BrowserId)
                            { browserCombo.SelectedIndex = i; break; }
                        }
                        refreshProfiles();
                        if (!string.IsNullOrEmpty(editItem.ProfileId))
                        {
                            for (int i = 0; i < profileCombo.Items.Count; i++)
                            {
                                if (profileCombo.Items[i] is ComboBoxItem ci && ci.Tag is string pid && pid == editItem.ProfileId)
                                { profileCombo.SelectedIndex = i; break; }
                            }
                        }
                    }
                }
            }

            // 选中引用源后预填名称
            bgCombo.SelectionChanged += (s, e) =>
            {
                if (bgCombo.SelectedItem is ComboBoxItem ci && ci.Tag is DataDashboardItem src
                    && string.IsNullOrWhiteSpace(nameBox.Text))
                    nameBox.Text = src.Label ?? "";
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
                Content = Framework.Lang.T("取消"),
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
                Content = Framework.Lang.T("确定"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(20, 8, 20, 8)
            };
            confirmBtn.Click += async (s, e) =>
            {
                bool isNewMode = newPanel.Visibility == Visibility.Visible;
                bool isTplMode = tplPanel.Visibility == Visibility.Visible;
                if (isNewMode || isTplMode)
                {
                    // 新建/数据模板：名称 + 变量名（唯一性校验）；模板页签需选中模板，新建页签需网址/选择器
                    if (isTplMode)
                    {
                        var tplId = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                        if (tplId == null || templates.FirstOrDefault(t => t.Id == tplId) == null)
                        {
                            MessageBox.Show(Framework.Lang.T("请选择数据模板"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    else
                    {
                        // 无模板自定义：网址必填；选择器模式下选择器必填
                        if (string.IsNullOrWhiteSpace(urlBox.Text))
                        {
                            MessageBox.Show(Framework.Lang.T("请输入请求网址"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            urlBox.Focus();
                            return;
                        }
                        bool isApi = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "API";
                        if (!isApi && string.IsNullOrWhiteSpace(selectorBox.Text))
                        {
                            MessageBox.Show(Framework.Lang.T("请输入选择器"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            selectorBox.Focus();
                            return;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        MessageBox.Show(Framework.Lang.T("请输入数据项名称"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        nameBox.Focus();
                        return;
                    }
                    string varKey = varKeyBox.Text.Trim();
                    if (string.IsNullOrEmpty(varKey))
                    {
                        MessageBox.Show(Framework.Lang.T("请输入变量名"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        varKeyBox.Focus();
                        return;
                    }
                    var teamId = (teamCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    // 变量名唯一性校验：本地（全部面板 + 后台数据项）+ 后端权威校验
                    if (IsVarKeyDuplicateLocally(varKey, teamId, editItem?.Id))
                    {
                        MessageBox.Show(Framework.Lang.TF("变量名 \"{0}\" 在当前范围（团队/个人）内已存在，请换一个名称。", varKey), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        varKeyBox.Focus();
                        return;
                    }
                    confirmBtn.IsEnabled = false;
                    bool available;
                    try { available = await Services.DataVariableService.Instance.CheckAsync(varKey, teamId); }
                    finally { confirmBtn.IsEnabled = true; }
                    if (!available)
                    {
                        MessageBox.Show(Framework.Lang.TF("变量名 \"{0}\" 在当前范围（团队/个人）内已存在，请换一个名称。", varKey), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        varKeyBox.Focus();
                        return;
                    }
                }
                else
                {
                    // 选择：必须选中一个运行中的数据源
                    var srcBg = (bgCombo.SelectedItem as ComboBoxItem)?.Tag as DataDashboardItem;
                    if (srcBg == null)
                    {
                        MessageBox.Show(Framework.Lang.T("请选择一个数据源（后台数据或变量名）"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        MessageBox.Show(Framework.Lang.T("请输入数据项名称"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        nameBox.Focus();
                        return;
                    }
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
                var item = editItem ?? new DataDashboardItem();
                item.Label = nameBox.Text.Trim();

                if (refPanel.Visibility == Visibility.Visible)
                {
                    // 引用式：仅展示同变量名运行中数据项的值，自身不参与轮询
                    var src = ((bgCombo.SelectedItem as ComboBoxItem)?.Tag as DataDashboardItem)!;
                    item.RefVarKey = src!.VarKey;
                    // 快照源项采集配置（供点击卡片打开 URL 等使用）
                    item.TemplateId = src.TemplateId;
                    item.TargetUrl = src.TargetUrl;
                    item.Selector = src.Selector;
                    item.RefreshIntervalSeconds = src.RefreshIntervalSeconds;
                    item.BrowserId = src.BrowserId;
                    item.ProfileId = src.ProfileId;
                    // 引用项不定义自己的变量与团队归属
                    item.VarKey = null;
                    item.TeamId = null;
                    item.TeamName = null;
                    item.Value = string.IsNullOrEmpty(src.Value) ? "--" : src.Value;
                }
                else if (tplPanel.Visibility == Visibility.Visible)
                {
                    // 数据模板：配置取自模板
                    var tplId = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    var tpl = templates.FirstOrDefault(t => t.Id == tplId);
                    item.TemplateId = tpl?.Id ?? "";
                    item.TargetUrl = tpl?.TargetUrl ?? "";
                    item.Selector = tpl?.Selector ?? "";
                    item.RefreshIntervalSeconds = tpl != null && tpl.RefreshIntervalSeconds > 0 ? tpl.RefreshIntervalSeconds : 60;
                    item.BrowserId = (browserCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    item.ProfileId = (profileCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    // 模板项无 API 配置（编辑时从自定义改回模板需清除）
                    item.ApiMethod = null;
                    item.ApiBody = null;
                    item.ResponseExtract = null;
                    item.TeamId = (teamCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    item.TeamName = teamCombo.SelectedIndex > 0 ? (teamCombo.SelectedItem as ComboBoxItem)?.Content as string : null;
                    item.VarKey = varKeyBox.Text.Trim();
                    item.RefVarKey = null;
                    if (string.IsNullOrEmpty(item.Value)) item.Value = "--";
                }
                else
                {
                    // 新建：选择器 或 API 请求手动配置
                    bool isApi = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "API";
                    item.TemplateId = "";
                    item.TargetUrl = urlBox.Text.Trim();
                    if (isApi)
                    {
                        item.Selector = "";
                        item.ApiMethod = (methodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "GET";
                        item.ApiBody = postBodyBox.Text.Trim();
                        item.ResponseExtract = extractBox.Text.Trim();
                        item.BrowserId = null;
                        item.ProfileId = null;
                    }
                    else
                    {
                        item.Selector = selectorBox.Text.Trim();
                        item.ApiMethod = null;
                        item.ApiBody = null;
                        item.ResponseExtract = null;
                        item.BrowserId = (browserCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                        item.ProfileId = (profileCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    }
                    item.RefreshIntervalSeconds = int.TryParse(intervalBox.Text.Trim(), out var secs) && secs >= 5 ? secs : 60;
                    item.TeamId = (teamCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    item.TeamName = teamCombo.SelectedIndex > 0 ? (teamCombo.SelectedItem as ComboBoxItem)?.Content as string : null;
                    item.VarKey = varKeyBox.Text.Trim();
                    item.RefVarKey = null;
                    if (string.IsNullOrEmpty(item.Value)) item.Value = "--";
                }

                return item;
            }

            return null;
        }

        /// <summary>
        /// 变量名本地唯一性校验：跨所有窗口面板与后台数据项，同作用域（同为个人，或同一团队）内不能有同名变量。
        /// excludeItemId 用于编辑时排除数据项自身。
        /// </summary>
        private bool IsVarKeyDuplicateLocally(string varKey, string? teamId, string? excludeItemId)
        {
            bool SameScope(DataDashboardItem di)
            {
                if (di.Id == excludeItemId) return false;
                if (string.IsNullOrEmpty(di.VarKey)) return false;
                if (!string.Equals(di.VarKey, varKey, StringComparison.OrdinalIgnoreCase)) return false;
                bool diPersonal = string.IsNullOrEmpty(di.TeamId);
                bool targetPersonal = string.IsNullOrEmpty(teamId);
                return (diPersonal && targetPersonal) || (!diPersonal && !targetPersonal && di.TeamId == teamId);
            }
            bool PanelHas(PanelItem panel)
            {
                if (panel.DashboardItems != null && panel.DashboardItems.Any(SameScope)) return true;
                if (panel.ChildPanels != null)
                    foreach (var child in panel.ChildPanels)
                        if (PanelHas(child)) return true;
                return false;
            }

            var app = (App)Application.Current;
            foreach (var win in app.WindowConfigs.Windows)
                foreach (var panel in win.Panels)
                    if (PanelHas(panel)) return true;
            return app.WindowConfigs.BackgroundDashboardItems.Any(SameScope);
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
            PopulateToolsMenu(menu);
        }

        /// <summary>
        /// 填充"工具"菜单项（底部右键菜单与窗口右上角菜单共用，保证两处内容一致）。
        /// 均为直接执行类工具；可勾选的显示开关在“显示设置”中。
        /// </summary>
        public void PopulateToolsMenu(MenuItem menu)
        {
            menu.Items.Clear();

            if (_currentWindowConfig == null)
            {
                menu.Items.Add(new MenuItem { Header = Framework.Lang.T("(未加载窗口配置)"), IsEnabled = false });
                return;
            }

            var availableTools = new (string Header, Action Action)[]
            {
                (Framework.Lang.T("🚀 窗口瞬移"), (Action)ShowWindowTeleportTool),
                (Framework.Lang.T("🔒 窗口标题锁定器"), (Action)ShowTitleLockTool),
                (Framework.Lang.T("🧩 插件编辑器"), (Action)ShowPluginEditorTool),
                // 后续可以在这里添加更多工具
            };

            foreach (var (header, action) in availableTools)
            {
                Action act = action;
                // Header 用纯字符串：Styles.xaml 的全局 MenuItem 隐式样式会通过 HeaderTemplate
                // 自动将字符串渲染为彩色 emoji。不能手动设为 Emoji.Wpf.TextBlock（UIElement），
                // 否则会与 HeaderTemplate 冲突，绑定到该对象并调 ToString 显示类型名
                var mi = new MenuItem { Header = header };
                mi.Click += (s, ev) => act();
                menu.Items.Add(mi);
            }
        }

        /// <summary>
        /// 填充"添加模块"菜单项（窗口右上角菜单使用，与空白处右键的 addModuleMenu 保持一致；
        /// 复用同一组 Click 处理函数，新模块添加到当前窗口顶层）。
        /// </summary>
        public void PopulateAddModuleMenu(MenuItem menu)
        {
            menu.Items.Clear();

            MenuItem Make(string header, RoutedEventHandler click)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += click;
                return mi;
            }

            menu.Items.Add(Make(Framework.Lang.T("分组"), AddGroupPanel_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(Make(Framework.Lang.T("快捷方式"), AddQuickGroupPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("便签"), AddStickyNotesPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("今日待办"), AddTodoPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("剪贴板收藏"), AddClipboardPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("系统信息"), AddSystemMonitorPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("使用统计"), AddUsageStatsPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("屏幕时长"), AddScreenTimePanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("AI 对话"), AddAIChatPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("文件区"), AddFileAreaPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("数据看板"), AddDataDashboard_Click));

            menu.Items.Add(Make(Framework.Lang.T("计时器"), AddTimerPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("倒计时"), AddCountdownPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("番茄闹钟"), AddPomodoroPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("信号灯"), AddSignalLightPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("图片轮播"), AddImageCarouselPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("每日活跃"), AddDailyActivityPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("日历"), AddCalendarPanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("数字员工"), AddDigitalEmployeePanel_Click));
            menu.Items.Add(Make(Framework.Lang.T("好友"), AddFriendPanel_Click));
            menu.Items.Add(new Separator());

            // 添加数据块：子菜单展开时动态加载（与右键菜单共用 AddDataBlockMenu_SubmenuOpened）
            var dataBlockSub = new MenuItem { Header = Framework.Lang.T("添加数据块") };
            dataBlockSub.Items.Add(new MenuItem { Header = Framework.Lang.T("(加载中...)"), IsEnabled = false });
            dataBlockSub.SubmenuOpened += AddDataBlockMenu_SubmenuOpened;
            menu.Items.Add(dataBlockSub);

            // 插件子菜单：展开时动态加载已安装插件（与右键菜单共用 AddPluginMenu_SubmenuOpened）
            var pluginSub = new MenuItem { Header = Framework.Lang.T("插件") };
            pluginSub.Items.Add(new MenuItem { Header = Framework.Lang.T("(加载中...)"), IsEnabled = false });
            pluginSub.SubmenuOpened += AddPluginMenu_SubmenuOpened;
            menu.Items.Add(pluginSub);

            menu.Items.Add(new Separator());
            menu.Items.Add(Make(Framework.Lang.T("从团队组件库添加..."), AddFromTeamLibrary_Click));
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
        /// 显示窗口标题锁定器（选择窗口界面，与顶部菜单 / 设置-工具入口一致）
        /// </summary>
        private void ShowTitleLockTool()
        {
            var window = new TitleLockPickerWindow();
            // 获取主窗口作为Owner
            if (_mainWindow != null)
                window.Owner = _mainWindow;
            window.ShowDialog();
        }

        private void ShowPluginEditorTool()
        {
            var window = new SideDesk.Windows.PluginEditorWindow();
            window.Show();
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
                    Content = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("📋 剪贴板历史") },
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
                    Content = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("🤖 AI 助手") },
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
                    Content = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("🎤 语音输入") },
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
                    if (toolKey == "ClipboardHistory") ShowClipboardHistoryPopup(btn);
                    else if (toolKey == "AIChat") ShowAIChatTool();
                    else if (toolKey == "VoiceInput") ShowVoiceInputTool();
                };
            }

            return btn;
        }

        /// <summary>
        /// 显示剪贴板历史工具：弹出侧边 popup 列出最近 50 条（不再在底部内联展示）
        /// </summary>
        private void ShowClipboardHistoryTool()
        {
            ShowClipboardHistoryPopup(toolsBar);
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
                MessageBox.Show(Framework.Lang.TF("语音输入功能初始化失败：{0}\n\n{1}", ex.Message, ex.StackTrace), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                    Content = Framework.Lang.T("🎤 点击开始"),
                    Padding = new Thickness(12, 5, 12, 5),
                    Foreground = (Brush)FindResource("BrushAccentText"),
                    FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Name = "VoiceInputButton"
                }
            };

            // 状态文本
            var statusText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("就绪"),
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
                        statusText.Text = Framework.Lang.TF("识别：{0}", recognizedText);
                        
                        // 可以将识别结果发送到AI对话或其他处理逻辑
                        HandleVoiceInput(recognizedText);
                    });
                };

                // 识别结束事件
                _speechEngine.RecognizeCompleted += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        voiceBtn.Content = Framework.Lang.T("🎤 点击开始");
                        voiceBtn.Background = Brushes.Transparent;
                        voiceBtnBorder.Background = (Brush)FindResource("BrushAccent");
                        statusText.Text = Framework.Lang.T("就绪");
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
                statusText.Text = Framework.Lang.TF("语音初始化失败：{0}", ex.Message);
            }
        }

        private void StartVoiceRecognition(Button voiceBtn, TextBlock statusText, Border voiceBtnBorder)
        {
            try
            {
                _isVoiceRecording = true;
                voiceBtn.Content = Framework.Lang.T("🎤 正在聆听...");
                voiceBtnBorder.Background = Brushes.Red;
                statusText.Text = Framework.Lang.T("正在聆听...");
                _speechEngine?.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
            }
            catch (Exception ex)
            {
                statusText.Text = Framework.Lang.TF("启动失败：{0}", ex.Message);
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
                statusText.Text = Framework.Lang.TF("停止失败：{0}", ex.Message);
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
                    MessageBox.Show(Framework.Lang.T("未找到可用的任务栏窗口"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 创建窗口选择对话框
                var pickerDialog = new Window
                {
                    Title = Framework.Lang.T("🚀 窗口瞬移 - 选择要移动的窗口"),
                    Width = 600,
                    Height = 500,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = _mainWindow,
                    ResizeMode = ResizeMode.CanResize,
                    Background = (Brush)FindResource("BrushDialogBG")
                };

                var mainPanel = new DockPanel { Margin = new Thickness(20) };
                
                // 顶部说明
                var headerText = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("选择任务栏中的窗口，点击\"瞬移\"将其移动到左侧Dock区域"),
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
                    infoPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = win.Title,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    infoPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = Framework.Lang.TF("进程: {0}", win.ProcessName),
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
                    Content = Framework.Lang.T("🚀 瞬移到左侧"),
                    Width = 140,
                    Height = 35,
                    Margin = new Thickness(0, 0, 10, 0),
                    Background = (Brush)FindResource("BrushAccent"),
                    Foreground = (Brush)FindResource("BrushAccentText"),
                    FontWeight = FontWeights.Bold
                };

                var cancelBtn = new Button
                {
                    Content = Framework.Lang.T("取消"),
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
                        MessageBox.Show(Framework.Lang.T("请先选择一个窗口"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show(Framework.Lang.TF("打开窗口瞬移工具失败：{0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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

                MessageBox.Show(Framework.Lang.TF("已将窗口“{0}”移动到左侧Dock区域", windowInfo.Title), Framework.Lang.T("成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("移动窗口失败：{0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                    var titleText = new Emoji.Wpf.TextBlock
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
                        ToolTip = Framework.Lang.T("还原到任务栏"),
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
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel();

            // 标题栏
            var headerPanel = new DockPanel();
            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("🚀 瞬移窗口"),
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
                
                MessageBox.Show(Framework.Lang.TF("已将窗口“{0}”从瞬移列表中移除\n窗口仍保持工具窗口样式，不会在任务栏显示", windowInfo.Title), Framework.Lang.T("已移除"), MessageBoxButton.OK, MessageBoxImage.Information);
                Debug.WriteLine($"已从瞬移列表移除窗口: {hwnd}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("移除窗口失败：{0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
