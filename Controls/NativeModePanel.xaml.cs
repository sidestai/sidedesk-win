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

            // 顶层面板拖拽排序（编辑模式下上下拖动模块/分组调换位置），只订阅一次
            dynamicContent.AllowDrop = true;
            dynamicContent.DragOver += DynamicContent_DragOver;
            dynamicContent.DragLeave += DynamicContent_DragLeave;
            dynamicContent.Drop += DynamicContent_Drop;

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

        private void RenderPanels()
        {
            dynamicContent.Children.Clear();

            // 重置快捷分组服务引用：避免切换到「无快捷分组面板」的窗口时，
            // 面板级拖放仍使用上一个窗口遗留的服务，导致文件被拖到其他窗口的分组。
            // 同时停掉旧服务的后台计时器，避免定时器泄漏（不关闭已打开的窗口）。
            _quickGroupUIService?.StopWatchers();
            _quickGroupUIService = null;
            _groupPanel = null;

            if (_currentWindowConfig == null) return;

            // 确保每个「快捷方式」面板 1:1 绑定到唯一分组，避免多个面板渲染同一份数据
            EnsureQuickGroupBindings();

            foreach (var panel in _currentWindowConfig.Panels)
            {
                var widget = CreatePanelWidget(panel);
                if (widget != null)
                {
                    // 未走 WrapWidget 的自定义卡片模块，在顶层统一附加可调高能力（编辑模式显示拖拽手柄）
                    if (widget is FrameworkElement wfe && wfe.Uid != "wrapHandled")
                        widget = WrapResizable(wfe, panel);

                    // 编辑模式下，为每个顶层面板挂载拖拽排序手柄
                    if (_isEditMode && widget is FrameworkElement fe)
                        AttachPanelDragHandlers(fe, panel);
                    dynamicContent.Children.Add(widget);
                }
            }

            // 空窗口提示
            if (_currentWindowConfig.Panels.Count == 0)
            {
                dynamicContent.Children.Add(new Emoji.Wpf.TextBlock
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
        /// 确保当前窗口配置内每个「快捷方式」面板都 1:1 绑定到唯一且存在的分组。
        /// 迁移策略：未绑定/绑定失效/重复绑定的面板，按声明顺序依次绑定到尚未被占用的现有分组；
        /// 现有分组用完后，为每个多出的面板新建一个空分组。数据不丢失，各面板数据独立隔离。
        /// </summary>
        private void EnsureQuickGroupBindings()
        {
            if (_currentWindowConfig == null) return;

            // 递归收集所有快捷方式面板（含嵌套分组的子面板），保持声明顺序
            var quickPanels = new List<PanelItem>();
            void Collect(List<PanelItem> panels)
            {
                foreach (var p in panels)
                {
                    if (p.Type == PanelType.QuickGroup) quickPanels.Add(p);
                    if (p.ChildPanels != null) Collect(p.ChildPanels);
                }
            }
            Collect(_currentWindowConfig.Panels);
            if (quickPanels.Count == 0) return;

            var allGroups = QuickGroupManager.GetAll();
            var existingIds = new HashSet<string>(allGroups.Select(g => g.Id));
            var usedIds = new HashSet<string>();
            bool changed = false;

            // 预占：其他窗口配置中已绑定的分组，保证跨窗口也不共享同一分组
            if (_configManager != null)
            {
                void CollectUsed(List<PanelItem> panels)
                {
                    foreach (var p in panels)
                    {
                        if (p.Type == PanelType.QuickGroup && !string.IsNullOrEmpty(p.GroupId) && existingIds.Contains(p.GroupId!))
                            usedIds.Add(p.GroupId!);
                        if (p.ChildPanels != null) CollectUsed(p.ChildPanels);
                    }
                }
                foreach (var win in _configManager.Windows)
                {
                    if (win == _currentWindowConfig) continue; // 当前窗口由下方两轮处理
                    CollectUsed(win.Panels);
                }
            }

            // Pass 1：保留已有效且唯一的绑定
            var pending = new List<PanelItem>();
            foreach (var p in quickPanels)
            {
                if (!string.IsNullOrEmpty(p.GroupId) && existingIds.Contains(p.GroupId!) && usedIds.Add(p.GroupId!))
                    continue; // 有效绑定，保留
                pending.Add(p);
            }

            // Pass 2：为未解析面板分配分组——先用现有未占用分组，用完再新建空分组
            var available = allGroups.Where(g => !usedIds.Contains(g.Id)).ToList();
            int idx = 0;
            foreach (var p in pending)
            {
                string gid;
                if (idx < available.Count)
                {
                    gid = available[idx].Id;
                    idx++;
                }
                else
                {
                    var ng = new QuickGroup { Name = "快捷分组", Columns = 2 };
                    QuickGroupManager.AddGroup(ng);
                    gid = ng.Id;
                }
                p.GroupId = gid;
                usedIds.Add(gid);
                changed = true;
            }

            if (changed)
            {
                QuickGroupManager.Save();
                _configManager?.Save();
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
                PanelType.TodoList => BuildTodoWidget(item),
                PanelType.SignalLight => BuildSignalLightWidget(item),
                PanelType.DigitalEmployee => BuildDigitalEmployeeWidget(item),
                PanelType.Friend => BuildFriendWidget(item),
                PanelType.ImageCarousel => BuildImageCarouselWidget(item),
                PanelType.Group => BuildGroupWidget(item),
                _ => null
            };
        }

        /// <summary>
        /// 通用面板包裹器：带标题和删除按钮
        /// </summary>
        private Border WrapWidget(string defaultTitle, UIElement content, PanelItem item, Action? onAddClick = null, ContextMenu? actionMenu = null)
        {
            // 该模块「隐藏模块外框」可勾选菜单项（独立控制单个模块的外框样式）
            MenuItem BuildHideFrameMenuItem()
            {
                var mi = new MenuItem
                {
                    Header = "隐藏模块外框",
                    IsCheckable = true,
                    IsChecked = item.HideFrame
                };
                mi.Click += (s, e) =>
                {
                    item.HideFrame = mi.IsChecked;
                    _configManager?.Save();
                    RenderPanels();
                };
                return mi;
            }

            // 隐藏模块外框（仅非编辑模式生效）：模块独立开关 或 全局开关，任一为真即隐藏
            if ((item.HideFrame || AppSettings.Instance.HideModuleFrame) && !_isEditMode)
            {
                var bareHost = BuildResizableHost(content, item);
                var bareStack = new StackPanel();
                bareStack.Children.Add(bareHost);
                // 拖拽 resize 手柄仅在编辑模式显示；无框分支仅在非编辑模式渲染，故不添加手柄。

                var bareBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Child = bareStack
                };

                // 右键菜单：隐藏模块外框（可勾选） + 删除模块
                // 外框隐藏后内容会填满容器，右键内容区可能落在子元素上导致弹不出容器菜单，
                // 因此为容器和内容顶层各挂一份独立菜单，确保右键模块主要区域都能恢复外框
                ContextMenu BuildBareMenu()
                {
                    var m = new ContextMenu();
                    m.Items.Add(BuildHideFrameMenuItem());
                    var del = new MenuItem { Header = "删除模块" };
                    del.Click += (s, e) => RemovePanel(item);
                    m.Items.Add(del);
                    return m;
                }

                bareBorder.ContextMenu = BuildBareMenu();
                if (content is FrameworkElement contentFe && contentFe.ContextMenu == null)
                    contentFe.ContextMenu = BuildBareMenu();

                bareBorder.Uid = "wrapHandled";
                return bareBorder;
            }

            var displayTitle = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            // 编辑模式下，标题栏作为拖拽手柄（仅此处可拖拽调整模块位置）
            if (_isEditMode)
            {
                header.Tag = PanelDragHandleTag;
                header.Cursor = Cursors.SizeAll;
            }
            var titleBlock = new Emoji.Wpf.TextBlock
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
                        PromptRenamePanel(item, defaultTitle);
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
            else if (actionMenu != null)
            {
                actionBtn.Content = "\u25BE";
                actionBtn.FontSize = 12;
                actionBtn.Click += (s, e) =>
                {
                    actionMenu.PlacementTarget = actionBtn;
                    actionMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    actionMenu.IsOpen = true;
                };
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
            var contentHost = BuildResizableHost(content, item);
            stack.Children.Add(contentHost);
            // 拖拽 resize 手柄仅在编辑模式显示并允许拖拽
            if (_isEditMode)
                stack.Children.Add(CreateResizeGrip(contentHost, item));

            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
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

            // 右键菜单：隐藏模块外框（可勾选） + 删除模块（无需进入编辑模式）
            var moduleMenu = new ContextMenu();
            moduleMenu.Items.Add(BuildHideFrameMenuItem());
            var deleteModuleItem = new MenuItem { Header = "删除模块" };
            deleteModuleItem.Click += (s, e) => RemovePanel(item);
            moduleMenu.Items.Add(deleteModuleItem);
            border.ContextMenu = moduleMenu;

            border.Uid = "wrapHandled";
            return border;
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
                ToolTip = "拖拽调整模块高度（双击恢复自动高度）"
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

            bool dragging = false;
            double startY = 0, startHeight = 0;
            grip.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    host.ClearValue(FrameworkElement.HeightProperty);
                    item.PanelHeight = null;
                    _configManager?.Save();
                    dragging = false;
                    _resizingModule = false;
                    grip.ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }
                dragging = true;
                _resizingModule = true;
                startY = e.GetPosition(this).Y;
                startHeight = (double.IsNaN(host.Height) || host.Height <= 0) ? host.ActualHeight : host.Height;
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                double h = startHeight + (e.GetPosition(this).Y - startY);
                if (h < MinHeight) h = MinHeight;
                host.Height = h;
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                _resizingModule = false;
                grip.ReleaseMouseCapture();
                item.PanelHeight = host.Height;
                _configManager?.Save();
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

        /// <summary>弹出对话框修改面板/分组标题</summary>
        private void PromptRenamePanel(PanelItem item, string defaultTitle)
        {
            var current = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;
            var dialog = new RenameDialog("修改名称", current)
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

            var container = new StackPanel();

            // 标题栏（展开/收起箭头 + 分组名）
            var titleBar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            titleBar.Uid = PanelHeaderUid;
            // 编辑模式下，标题栏作为拖拽手柄
            if (_isEditMode)
            {
                titleBar.Tag = PanelDragHandleTag;
                titleBar.Cursor = Cursors.SizeAll;
            }
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

            var titleBlock = new Emoji.Wpf.TextBlock
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
                        PromptRenamePanel(item, "分组");
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
                ("今日待办", PanelType.TodoList),
                ("剪贴板收藏", PanelType.Clipboard),
                ("系统信息", PanelType.SystemMonitor),
                ("使用统计", PanelType.UsageStats),
                ("AI 对话", PanelType.AIChat),
                ("文件区", PanelType.FileArea),
                ("数据看板", PanelType.DataDashboard),
                ("计时器", PanelType.Timer),
                ("信号灯", PanelType.SignalLight),
                ("图片轮播", PanelType.ImageCarousel),
                ("好友", PanelType.Friend)
            })
            {
                var mi = new MenuItem { Header = header };
                var panelType = type;
                mi.Click += (s, ev) => AddModuleToGroup(item, panelType);
                addSubMenu.Items.Add(mi);
            }
            // 添加数据块：子菜单展开时加载设置中的数据项（模版）
            var groupDataBlockSub = new MenuItem { Header = "添加数据块" };
            groupDataBlockSub.Items.Add(new MenuItem { Header = "(加载中...)", IsEnabled = false });
            var capturedGroup = item;
            groupDataBlockSub.SubmenuOpened += (s, ev) =>
                PopulateDataBlockMenu(groupDataBlockSub, tpl => AddDataBlockToGroup(capturedGroup, tpl), () =>
                {
                    var customItem = ShowAddDashboardItemDialog();
                    if (customItem != null)
                    {
                        capturedGroup.ChildPanels ??= new List<PanelItem>();
                        capturedGroup.ChildPanels.Add(new PanelItem
                        {
                            Type = PanelType.DataDashboard,
                            DashboardItems = new List<DataDashboardItem> { customItem }
                        });
                        _configManager?.Save();
                        RenderPanels();
                    }
                });
            addSubMenu.Items.Add(groupDataBlockSub);
            groupContextMenu.Items.Add(addSubMenu);
            groupContextMenu.Items.Add(new Separator());
            var deleteGroupItem = new MenuItem { Header = "删除分组" };
            deleteGroupItem.Click += (s, ev) => RemovePanel(item);
            groupContextMenu.Items.Add(deleteGroupItem);
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
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = container,
                ContextMenu = groupContextMenu  // 整个分组区域右键都使用分组菜单
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

            // 初始化 QuickGroupUIService（同窗口存在多个快捷分组面板时，
            // 释放上一个面板服务的计时器，避免泄漏）
            _quickGroupUIService?.StopWatchers();
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

            // 已收藏列表
            if (item.ClipboardItems.Count == 0)
            {
                container.Children.Add(new Emoji.Wpf.TextBlock
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

                    // 右键菜单：删除收藏项（无需进入编辑模式）
                    var clipMenu = new ContextMenu();
                    var clipDelItem = new MenuItem { Header = "删除" };
                    var capturedClip = clip;
                    clipDelItem.Click += (s, ev) =>
                    {
                        item.ClipboardItems.RemoveAll(c => c.Id == capturedClip.Id);
                        _configManager?.Save();
                        RenderPanels();
                    };
                    clipMenu.Items.Add(clipDelItem);
                    var clipHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
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

            return WrapWidget("📋 剪贴板收藏", container, item, () => ShowClipboardPickDialog(item));
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
                        AddCandidate($"[图片 {image.PixelWidth}x{image.PixelHeight}]", "Image");
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

        private UIElement BuildTodoWidget(PanelItem item)
        {
            item.TodoItems ??= new List<TodoItem>();

            var container = new StackPanel();

            // 待办列表：未完成在前，已完成在后
            foreach (var todo in item.TodoItems.OrderBy(t => t.IsDone))
            {
                container.Children.Add(CreateTodoRow(todo, item));
            }

            // 空列表提示
            if (item.TodoItems.Count == 0)
            {
                container.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "暂无待办，点击标题栏 + 添加",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 12,
                    Opacity = 0.7,
                    Margin = new Thickness(2, 2, 0, 2)
                });
            }

            if (item.SkipWrapper)
                return container;

            // 标题栏「+」弹出添加待办对话框
            return WrapWidget("今日待办", container, item, () => ShowTodoDialog(item));
        }

        #region 信号灯面板

        /// <summary>根据样式创建灯形状控件</summary>
        private FrameworkElement CreateLightShape(SignalLightItem light)
        {
            var colorStr = light.IsOn ? light.Color : "#333333";
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
            var brush = new SolidColorBrush(color);
            var size = light.Size;

            FrameworkElement shape = light.LightStyle switch
            {
                "Square" => new System.Windows.Shapes.Rectangle
                {
                    Width = size, Height = size,
                    Fill = brush,
                    RadiusX = size * 0.15, RadiusY = size * 0.15
                },
                "Pixel" => new System.Windows.Shapes.Rectangle
                {
                    Width = size, Height = size,
                    Fill = brush,
                    RadiusX = 0, RadiusY = 0
                },
                _ => (FrameworkElement)new System.Windows.Shapes.Ellipse
                {
                    Width = size, Height = size,
                    Fill = brush
                }
            };

            // 发光效果（亮时）
            if (light.IsOn)
            {
                shape.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = color,
                    BlurRadius = size * 0.4,
                    ShadowDepth = 0,
                    Opacity = 0.7
                };
            }

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
            var editMi = new MenuItem { Header = "编辑信号灯" };
            // 需要找到对应的 PanelItem，通过遍历查找
            editMi.Click += (s, e) =>
            {
                // 从 SignalLights 列表中找到包含此 light 的 PanelItem
                if (_currentWindowConfig != null)
                {
                    foreach (var panel in _currentWindowConfig.Panels)
                    {
                        if (panel.SignalLights != null && panel.SignalLights.Contains(light))
                        {
                            ShowSignalLightEditDialog(panel);
                            return;
                        }
                        if (panel.ChildPanels != null)
                        {
                            foreach (var child in panel.ChildPanels)
                            {
                                if (child.SignalLights != null && child.SignalLights.Contains(light))
                                {
                                    ShowSignalLightEditDialog(child);
                                    return;
                                }
                            }
                        }
                    }
                }
            };
            lightMenu.Items.Add(editMi);
            shape.ContextMenu = lightMenu;

            return shape;
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

            void RenderLights()
            {
                container.Children.Clear();
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

                    lightContainer.Children.Add(CreateLightShape(light));
                    lightsPanel.Children.Add(lightContainer);
                }

                container.Children.Add(lightsPanel);
            }

            RenderLights();

            return WrapWidget("🚦 信号灯", container, item, () => ShowSignalLightEditDialog(item));
        }

        private void ShowSignalLightEditDialog(PanelItem item)
        {
            item.SignalLights ??= new List<SignalLightItem>();

            var dialog = new Window
            {
                Title = "编辑信号灯",
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
            var orientLabel = new TextBlock { Text = "排列方向：", Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(orientLabel, Dock.Left);
            row1.Children.Add(orientLabel);
            var orientCombo = new ComboBox { Style = comboStyle, Width = 100 };
            orientCombo.Items.Add("横向");
            orientCombo.Items.Add("纵向");
            orientCombo.SelectedIndex = item.SignalLightVertical ? 1 : 0;
            row1.Children.Add(orientCombo);

            // Dock.Right 先加控件（最右），再加标签（控件左边）
            var styleCombo = new ComboBox { Style = comboStyle, Width = 100 };
            styleCombo.Items.Add("圆形");
            styleCombo.Items.Add("方形");
            styleCombo.Items.Add("像素");
            var firstLight = item.SignalLights.FirstOrDefault();
            styleCombo.SelectedIndex = firstLight?.LightStyle switch
            {
                "Square" => 1,
                "Pixel" => 2,
                _ => 0
            };
            DockPanel.SetDock(styleCombo, Dock.Right);
            row1.Children.Add(styleCombo);
            var styleLabel = new TextBlock { Text = "  灯样式：", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(styleLabel, Dock.Right);
            row1.Children.Add(styleLabel);
            root.Children.Add(row1);

            // 第二行：大小 + 间距 + 对齐
            var row2 = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var sizeLabel = new TextBlock { Text = "灯大小：", Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sizeLabel, Dock.Left);
            row2.Children.Add(sizeLabel);
            var sizeBox = new TextBox { Text = firstLight?.Size.ToString() ?? "36", Style = textBoxStyle, Width = 70 };
            row2.Children.Add(sizeBox);

            // Dock.Right 从右到左：对齐控件 → 对齐标签 → 间距控件 → 间距标签
            var alignCombo = new ComboBox { Style = comboStyle, Width = 80 };
            alignCombo.Items.Add("居左");
            alignCombo.Items.Add("居中");
            alignCombo.Items.Add("居右");
            alignCombo.SelectedIndex = item.SignalLightAlignment switch
            {
                "Left" => 0,
                "Right" => 2,
                _ => 1
            };
            DockPanel.SetDock(alignCombo, Dock.Right);
            row2.Children.Add(alignCombo);

            var alignLabel = new TextBlock { Text = "  对齐：", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(alignLabel, Dock.Right);
            row2.Children.Add(alignLabel);

            var spacingBox = new TextBox { Text = item.SignalLightSpacing.ToString(), Style = textBoxStyle, Width = 50 };
            DockPanel.SetDock(spacingBox, Dock.Right);
            row2.Children.Add(spacingBox);

            var spacingLabel = new TextBlock { Text = "  间距：", VerticalAlignment = VerticalAlignment.Center };
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

                    // 颜色选择
                    var colorBox = new TextBox { Text = light.Color, Style = textBoxStyle, Width = 80, Margin = new Thickness(0, 0, 4, 0) };
                    colorBox.TextChanged += (s, e) => { try { light.Color = colorBox.Text.Trim(); } catch { } };
                    row.Children.Add(colorBox);

                    // 大小
                    var sizeLabel = new TextBlock { Text = "大小:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
                    row.Children.Add(sizeLabel);
                    var sizeBox = new TextBox { Text = light.Size.ToString(), Style = textBoxStyle, Width = 70, Margin = new Thickness(0, 0, 4, 0) };
                    sizeBox.TextChanged += (s, e) => { if (double.TryParse(sizeBox.Text.Trim(), out double v) && v > 0) light.Size = v; };
                    row.Children.Add(sizeBox);

                    // 图标
                    var iconLabel = new TextBlock { Text = "图标:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
                    row.Children.Add(iconLabel);
                    var iconBox = new TextBox { Text = light.Icon ?? "", Style = textBoxStyle, Width = 60, Margin = new Thickness(0, 0, 4, 0) };
                    iconBox.TextChanged += (s, e) => light.Icon = iconBox.Text.Trim();
                    row.Children.Add(iconBox);

                    // 删除按钮
                    var delBtn = new Button
                    {
                        Content = "✕", Width = 22, Height = 22, FontSize = 10,
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0)
                    };
                    delBtn.Click += (s, e) => { item.SignalLights.RemoveAt(idx); RenderEditList(); };
                    DockPanel.SetDock(delBtn, Dock.Right);
                    row.Children.Add(delBtn);

                    lightsStack.Children.Add(row);
                }
            }
            RenderEditList();
            root.Children.Add(lightsStack);

            // 添加灯按钮
            var addBtn = new Button
            {
                Content = "+ 添加灯",
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
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
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
                    : (todo.RemindBeforeMinutes == 0 ? "，准时提醒" : $"，提前{todo.RemindBeforeMinutes}分钟");
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
            textStack.ToolTip = $"{todo.Text}\n（点击查看详情）";
            row.Children.Add(textStack);

            check.Checked += (s, e) => { todo.IsDone = true; _configManager?.Save(); RenderPanels(); };
            check.Unchecked += (s, e) => { todo.IsDone = false; _configManager?.Save(); RenderPanels(); };

            // 右键菜单：删除
            var menu = new ContextMenu();
            var del = new MenuItem { Header = "删除" };
            del.Click += (s, e) => { panelItem.TodoItems?.Remove(todo); _configManager?.Save(); RenderPanels(); };
            menu.Items.Add(del);
            var todoHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = panelItem.HideFrame };
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
                Title = "待办详情",
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
            var statusText = todo.IsDone ? "✅ 已完成" : "⏳ 未完成";
            var statusBlock = new Emoji.Wpf.TextBlock
            {
                Text = statusText,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(statusBlock);

            // 标题
            var titleHeader = new TextBlock { Text = "标题", FontSize = 12, Foreground = (Brush)FindResource("BrushTextMuted"), Margin = new Thickness(0, 0, 0, 4) };
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
            var descHeader = new TextBlock { Text = "详细内容", FontSize = 12, Foreground = (Brush)FindResource("BrushTextMuted"), Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(descHeader);
            var descScroll = new ScrollViewer
            {
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Emoji.Wpf.TextBlock
                {
                    Text = string.IsNullOrEmpty(todo.Description) ? "（无详细内容）" : todo.Description,
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
                    : (todo.RemindBeforeMinutes == 0 ? "，准时提醒" : $"，提前{todo.RemindBeforeMinutes}分钟提醒");
                var timeBlock = new Emoji.Wpf.TextBlock
                {
                    Text = $"\u23F0 任务时间：{todo.DueTime.Value:yyyy-MM-dd HH:mm}{remindText}",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 14, 0, 0)
                };
                root.Children.Add(timeBlock);
            }

            // 创建时间
            var createdBlock = new TextBlock
            {
                Text = $"创建时间：{todo.CreatedAt:yyyy-MM-dd HH:mm}",
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 6, 0, 0)
            };
            root.Children.Add(createdBlock);

            // 按钮区
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };

            var editBtn = new Button { Content = "编辑", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("StandardButtonStyle") };
            editBtn.Click += (s, e) =>
            {
                detailWin.Close();
                EditTodo(todo, panelItem);
            };
            btnPanel.Children.Add(editBtn);

            var closeBtn = new Button { Content = "关闭", Width = 70, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
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
                                MessageBox.Show($"待办提醒：{todo.Text}\n任务时间：{todo.DueTime.Value:MM-dd HH:mm}",
                                    "今日待办", MessageBoxButton.OK, MessageBoxImage.Information);
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
        
            // 空列表提示
            if (item.TimerItems.Count == 0)
            {
                container.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "暂无计时器，点击标题栏 ▾ 添加",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 12,
                    Opacity = 0.7,
                    Margin = new Thickness(2, 2, 0, 2)
                });
            }
        
            // === 自定义标题栏：标题 + 下拉三角 ===
            var defaultTitle = "⏱ 计时器";
            var displayTitle = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Uid = PanelHeaderUid;
            if (_isEditMode) { header.Tag = PanelDragHandleTag; header.Cursor = Cursors.SizeAll; }
        
            var titleBlock = new Emoji.Wpf.TextBlock
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
                        PromptRenamePanel(item, defaultTitle);
                        e.Handled = true;
                    }
                };
            }
        
            // 下拉三角按钮
            var dropBtn = new Button
            {
                Content = "\u25BE",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = _isEditMode ? Visibility.Collapsed : Visibility.Visible
            };
        
            // 构建下拉菜单
            var dropMenu = new ContextMenu();
        
            var addTimerMi = new MenuItem { Header = "添加计时器" };
            addTimerMi.Click += (s, e) =>
            {
                item.TimerItems.Add(new TimerItem());
                _configManager?.Save();
                RenderPanels();
            };
            dropMenu.Items.Add(addTimerMi);
        
            dropMenu.Items.Add(new Separator());
        
            // 隐藏模块外框
            var hideFrameMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi.Click += (s, e) => { item.HideFrame = hideFrameMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            dropMenu.Items.Add(hideFrameMi);
        
            // 删除模块
            var delModuleMi = new MenuItem { Header = "删除模块" };
            delModuleMi.Click += (s, e) => RemovePanel(item);
            dropMenu.Items.Add(delModuleMi);
        
            dropBtn.Click += (s, e) =>
            {
                dropMenu.IsOpen = true;
            };
        
            DockPanel.SetDock(dropBtn, Dock.Right);
            header.Children.Add(dropBtn);
            header.Children.Add(titleBlock);
        
            var mainStack = new StackPanel();
            mainStack.Children.Add(header);
            mainStack.Children.Add(container);
        
            // 隐藏模块外框判断
            if ((item.HideFrame || AppSettings.Instance.HideModuleFrame) && !_isEditMode)
            {
                // 隐藏外框时，若开启了「同时隐藏模块标题」则移除标题栏
                if (AppSettings.Instance.HideModuleTitle)
                    mainStack.Children.Remove(header);
                var bareBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Child = mainStack
                };
                var bareMenu = new ContextMenu();
                var bareHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
                bareHideMi.Click += (s, e) => { item.HideFrame = bareHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
                bareMenu.Items.Add(bareHideMi);
                var bareDelMi = new MenuItem { Header = "删除模块" };
                bareDelMi.Click += (s, e) => RemovePanel(item);
                bareMenu.Items.Add(bareDelMi);
                bareBorder.ContextMenu = bareMenu;
                return bareBorder;
            }
        
            var wrapper = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = mainStack
            };
        
            if (_isEditMode)
            {
                wrapper.MouseEnter += (s, e) => dropBtn.Visibility = Visibility.Visible;
                wrapper.MouseLeave += (s, e) => dropBtn.Visibility = Visibility.Collapsed;
            }
        
            // 右键菜单
            var moduleMenu = new ContextMenu();
            var hideFrameMi2 = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi2.Click += (s, e) => { item.HideFrame = hideFrameMi2.IsChecked; _configManager?.Save(); RenderPanels(); };
            moduleMenu.Items.Add(hideFrameMi2);
            var delMi2 = new MenuItem { Header = "删除模块" };
            delMi2.Click += (s, e) => RemovePanel(item);
            moduleMenu.Items.Add(delMi2);
            wrapper.ContextMenu = moduleMenu;
        
            return wrapper;
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
                Text = "未设置目录或无图片\n点标题栏 ▾ → 设置",
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

            // === 自定义标题栏：标题 + 下拉三角 ===
            var defaultTitle = "🖼 图片轮播";
            var displayTitle = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Uid = PanelHeaderUid;
            if (_isEditMode) { header.Tag = PanelDragHandleTag; header.Cursor = Cursors.SizeAll; }

            var titleBlock = new Emoji.Wpf.TextBlock
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
                    if (e.ClickCount == 2) { PromptRenamePanel(item, defaultTitle); e.Handled = true; }
                };
            }

            var dropBtn = new Button
            {
                Content = "\u25BE",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = _isEditMode ? Visibility.Collapsed : Visibility.Visible
            };

            var dropMenu = new ContextMenu();
            var settingsMi = new MenuItem { Header = "设置目录/播放规则..." };
            settingsMi.Click += (s, e) => ShowCarouselSettingsDialog(item);
            dropMenu.Items.Add(settingsMi);
            var prevMi = new MenuItem { Header = "上一张" };
            prevMi.Click += (s, e) => Prev();
            dropMenu.Items.Add(prevMi);
            var nextMi = new MenuItem { Header = "下一张" };
            nextMi.Click += (s, e) => Next();
            dropMenu.Items.Add(nextMi);
            var playMi = new MenuItem { Header = item.CarouselAutoPlay ? "暂停自动播放" : "开始自动播放" };
            playMi.Click += (s, e) => { item.CarouselAutoPlay = !item.CarouselAutoPlay; _configManager?.Save(); RenderPanels(); };
            dropMenu.Items.Add(playMi);
            dropMenu.Items.Add(new Separator());
            var hideFrameMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi.Click += (s, e) => { item.HideFrame = hideFrameMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            dropMenu.Items.Add(hideFrameMi);
            var delModuleMi = new MenuItem { Header = "删除模块" };
            delModuleMi.Click += (s, e) => RemovePanel(item);
            dropMenu.Items.Add(delModuleMi);
            dropBtn.Click += (s, e) => dropMenu.IsOpen = true;

            DockPanel.SetDock(dropBtn, Dock.Right);
            header.Children.Add(dropBtn);
            header.Children.Add(titleBlock);

            var mainStack = new StackPanel();
            mainStack.Children.Add(header);
            mainStack.Children.Add(displayGrid);

            // 隐藏模块外框判断
            if ((item.HideFrame || AppSettings.Instance.HideModuleFrame) && !_isEditMode)
            {
                if (AppSettings.Instance.HideModuleTitle)
                    mainStack.Children.Remove(header);
                var bareBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Child = mainStack
                };
                var bareMenu = new ContextMenu();
                var bareSettingsMi = new MenuItem { Header = "设置目录/播放规则..." };
                bareSettingsMi.Click += (s, e) => ShowCarouselSettingsDialog(item);
                bareMenu.Items.Add(bareSettingsMi);
                var bareHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
                bareHideMi.Click += (s, e) => { item.HideFrame = bareHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
                bareMenu.Items.Add(bareHideMi);
                var bareDelMi = new MenuItem { Header = "删除模块" };
                bareDelMi.Click += (s, e) => RemovePanel(item);
                bareMenu.Items.Add(bareDelMi);
                bareBorder.ContextMenu = bareMenu;
                return bareBorder;
            }

            var wrapper = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = mainStack
            };
            if (_isEditMode)
            {
                wrapper.MouseEnter += (s, e) => dropBtn.Visibility = Visibility.Visible;
                wrapper.MouseLeave += (s, e) => dropBtn.Visibility = Visibility.Collapsed;
            }
            var moduleMenu = new ContextMenu();
            var mSettingsMi = new MenuItem { Header = "设置目录/播放规则..." };
            mSettingsMi.Click += (s, e) => ShowCarouselSettingsDialog(item);
            moduleMenu.Items.Add(mSettingsMi);
            var mHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            mHideMi.Click += (s, e) => { item.HideFrame = mHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            moduleMenu.Items.Add(mHideMi);
            var mDelMi = new MenuItem { Header = "删除模块" };
            mDelMi.Click += (s, e) => RemovePanel(item);
            moduleMenu.Items.Add(mDelMi);
            wrapper.ContextMenu = moduleMenu;

            return wrapper;
        }

        /// <summary>图片轮播设置对话框：目录 + 播放规则</summary>
        private void ShowCarouselSettingsDialog(PanelItem item)
        {
            var dialog = new Window
            {
                Title = "图片轮播设置",
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
            root.Children.Add(MakeLabel("图片目录"));
            var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var browseBtn = new Button { Content = "浏览", Width = 60, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            DockPanel.SetDock(browseBtn, Dock.Right);
            var folderBox = new TextBox { Text = item.CarouselFolder ?? "", IsReadOnly = true, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            browseBtn.Click += (s, e) =>
            {
                var fd = new Microsoft.Win32.OpenFolderDialog { Title = "选择图片目录" };
                if (!string.IsNullOrWhiteSpace(item.CarouselFolder) && System.IO.Directory.Exists(item.CarouselFolder))
                    fd.InitialDirectory = item.CarouselFolder;
                if (fd.ShowDialog() == true) folderBox.Text = fd.FolderName;
            };
            folderRow.Children.Add(browseBtn);
            folderRow.Children.Add(folderBox);
            root.Children.Add(folderRow);

            // 切换间隔
            root.Children.Add(MakeLabel("切换间隔（秒）"));
            var intervalBox = new TextBox { Text = item.CarouselIntervalSeconds.ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(intervalBox);

            // 显示高度
            root.Children.Add(MakeLabel("显示高度（px）"));
            var heightBox = new TextBox { Text = ((int)item.CarouselHeight).ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(heightBox);

            // 播放顺序
            root.Children.Add(MakeLabel("播放顺序"));
            var orderCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            orderCombo.Items.Add("顺序播放");
            orderCombo.Items.Add("随机播放");
            orderCombo.SelectedIndex = item.CarouselRandom ? 1 : 0;
            root.Children.Add(orderCombo);

            // 填充方式
            root.Children.Add(MakeLabel("填充方式"));
            var fitCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            fitCombo.Items.Add("适应（完整显示）");
            fitCombo.Items.Add("填满（裁剪铺满）");
            fitCombo.Items.Add("拉伸（变形铺满）");
            fitCombo.SelectedIndex = item.CarouselStretch switch { "UniformToFill" => 1, "Fill" => 2, _ => 0 };
            root.Children.Add(fitCombo);

            // 开关项
            var subChk = new CheckBox { Content = "包含子目录", IsChecked = item.CarouselIncludeSubfolders, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(subChk);
            var autoChk = new CheckBox { Content = "自动播放", IsChecked = item.CarouselAutoPlay, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 16) };
            root.Children.Add(autoChk);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
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
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Background = (Brush)FindResource("BrushBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };

            // 右键菜单：删除计时器（无需进入编辑模式）
            var cardMenu = new ContextMenu();
            var deleteMenuItem = new MenuItem { Header = "删除计时器" };
            deleteMenuItem.Click += (s, e) => DeleteTimer(timer, panelItem);
            cardMenu.Items.Add(deleteMenuItem);
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
                Margin = new Thickness(0, 8, 0, 8)
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
            var toggleBtn = new Button
            {
                Content = timer.IsRunning ? "⏹" : "▶",
                Width = 22,
                Height = 22,
                FontSize = 10,
                Background = timer.IsRunning ? Brushes.IndianRed : new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0)
            };
            var toggleBtnBorder = new Border
            {
                CornerRadius = new CornerRadius(11),
                Child = toggleBtn
            };
            DockPanel.SetDock(toggleBtnBorder, Dock.Right);

            // 如果计时器正在运行，启动更新
            if (timer.IsRunning)
            {
                StartTimerUpdate(timer, timeDisplay, panelItem, toggleBtn);
            }

            toggleBtn.Click += (s, e) =>
            {
                if (!timer.IsRunning)
                {
                    timer.IsRunning = true;
                    timer.StartTime = DateTime.Now;
                    _configManager?.Save();
                    StartTimerUpdate(timer, timeDisplay, panelItem, toggleBtn);
                    toggleBtn.Content = "";
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
            headerPanel.Children.Add(toggleBtnBorder);
            headerPanel.Children.Add(nameBlock);

            mainStack.Children.Add(headerPanel);
            mainStack.Children.Add(timeDisplay);

            // 今日统计
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (timer.DailyRecords.TryGetValue(today, out int todaySeconds) && todaySeconds > 0)
            {
                var todayStat = new Emoji.Wpf.TextBlock
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
                    "该计时器模块已没有计时项，是否删除整个模块？",
                    "确认",
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
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = "名称：", Margin = new Thickness(0, 0, 0, 5), Foreground = (Brush)FindResource("BrushTextMain") });
            var nameBox = new TextBox { Text = timer.Name, Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(nameBox);

            // 超时时间
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = "最大时长（秒）：", Margin = new Thickness(0, 0, 0, 5), Foreground = (Brush)FindResource("BrushTextMain") });
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
                Foreground = (Brush)FindResource("BrushAccentText")
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

            if (item.FileItems.Count == 0)
            {
                dropZone.Child = new Emoji.Wpf.TextBlock
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

                        // 图标
                        var iconText = new Emoji.Wpf.TextBlock
                        {
                            Text = isFolder ? "📁" : "📄",
                            FontSize = iconSize,
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        iconStack.Children.Add(iconText);

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
                            ToolTip = fileItem.Path,
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        iconStack.Children.Add(nameText);

                        // 点击打开
                        iconStack.MouseLeftButtonUp += (s, e) =>
                        {
                            try
                            {
                                if (isFolder)
                                    Process.Start(new ProcessStartInfo("explorer.exe", filePath) { UseShellExecute = false });
                                else
                                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                            }
                            catch { }
                        };

                        // 右键菜单（调用资源管理器原生右键菜单）
                        iconStack.MouseRightButtonDown += (s, e) =>
                        {
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
                                HandleDropOnFolder(s, e, filePath, item);
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
                            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                            catch { }
                        };

                        // 图标 + 名称
                        var icon = isFolder ? "📁" : "📄";
                        var nameBlock = new Emoji.Wpf.TextBlock
                        {
                            Text = $"{icon} {fileItem.Name}",
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            ToolTip = fileItem.Path,
                            Cursor = Cursors.Hand
                        };

                        nameBlock.MouseLeftButtonUp += (s, e) =>
                        {
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
                        row.Children.Add(nameBlock);

                        // 右键菜单（调用资源管理器原生右键菜单）
                        var highlightBrush = (Brush)FindResource("BrushListViewItemMouseOver");
                        row.MouseRightButtonDown += (s, e) =>
                        {
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
                                HandleDropOnFolder(s, e, filePath, item);
                            };
                        }

                        listContainer.Children.Add(row);
                    }
                }
                container.Children.Add(listBorder);
            }

            // === 自定义标题栏：标题 + 下拉三角 ===
            var defaultTitle = "📁 文件区";
            var displayTitle = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Uid = PanelHeaderUid;
            if (_isEditMode) { header.Tag = PanelDragHandleTag; header.Cursor = Cursors.SizeAll; }

            var titleBlock = new Emoji.Wpf.TextBlock
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
                        PromptRenamePanel(item, defaultTitle);
                        e.Handled = true;
                    }
                };
            }

            // 下拉三角按钮
            var dropBtn = new Button
            {
                Content = "\u25BE",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = _isEditMode ? Visibility.Collapsed : Visibility.Visible
            };

            // 构建下拉菜单
            var dropMenu = new ContextMenu();

            var addFileMi = new MenuItem { Header = "添加文件" };
            addFileMi.Click += (s, e) => AddFileToArea(item);
            dropMenu.Items.Add(addFileMi);

            var addFolderMi = new MenuItem { Header = "添加目录" };
            addFolderMi.Click += (s, e) => AddFolderToArea(item);
            dropMenu.Items.Add(addFolderMi);

            dropMenu.Items.Add(new Separator());

            // 布局子菜单
            var layoutMenu = new MenuItem { Header = "布局" };
            var largeIconsMi = new MenuItem { Header = "大图标", IsCheckable = true, IsChecked = item.FileAreaLayout == "LargeIcons" };
            largeIconsMi.Click += (s, e) => { item.FileAreaLayout = "LargeIcons"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(largeIconsMi);
            var smallIconsMi = new MenuItem { Header = "小图标", IsCheckable = true, IsChecked = item.FileAreaLayout == "SmallIcons" };
            smallIconsMi.Click += (s, e) => { item.FileAreaLayout = "SmallIcons"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(smallIconsMi);
            var listMi = new MenuItem { Header = "列表", IsCheckable = true, IsChecked = item.FileAreaLayout == "List" };
            listMi.Click += (s, e) => { item.FileAreaLayout = "List"; _configManager?.Save(); RenderPanels(); };
            layoutMenu.Items.Add(listMi);
            dropMenu.Items.Add(layoutMenu);

            // 列数子菜单
            var columnsMenu = new MenuItem { Header = "列" };
            for (int col = 1; col <= 4; col++)
            {
                var colItem = new MenuItem { Header = $"{col}列", IsCheckable = true, IsChecked = item.FileAreaColumns == col };
                int colCount = col;
                colItem.Click += (s, e) => { item.FileAreaColumns = colCount; _configManager?.Save(); RenderPanels(); };
                columnsMenu.Items.Add(colItem);
            }
            dropMenu.Items.Add(columnsMenu);

            dropMenu.Items.Add(new Separator());

            // 隐藏模块外框
            var hideFrameMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi.Click += (s, e) => { item.HideFrame = hideFrameMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            dropMenu.Items.Add(hideFrameMi);

            // 删除模块
            var delModuleMi = new MenuItem { Header = "删除模块" };
            delModuleMi.Click += (s, e) => RemovePanel(item);
            dropMenu.Items.Add(delModuleMi);

            dropBtn.Click += (s, e) =>
            {
                dropMenu.IsOpen = true;
            };

            DockPanel.SetDock(dropBtn, Dock.Right);
            header.Children.Add(dropBtn);
            header.Children.Add(titleBlock);

            var mainStack = new StackPanel();
            mainStack.Children.Add(header);
            mainStack.Children.Add(container);

            // 隐藏模块外框判断
            if ((item.HideFrame || AppSettings.Instance.HideModuleFrame) && !_isEditMode)
            {
                // 隐藏外框时，若开启了「同时隐藏模块标题」则移除标题栏
                if (AppSettings.Instance.HideModuleTitle)
                    mainStack.Children.Remove(header);
                var bareBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Child = mainStack
                };
                var bareMenu = new ContextMenu();
                var bareHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
                bareHideMi.Click += (s, e) => { item.HideFrame = bareHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
                bareMenu.Items.Add(bareHideMi);
                var bareDelMi = new MenuItem { Header = "删除模块" };
                bareDelMi.Click += (s, e) => RemovePanel(item);
                bareMenu.Items.Add(bareDelMi);
                bareBorder.ContextMenu = bareMenu;
                return bareBorder;
            }

            var wrapper = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = mainStack
            };

            if (_isEditMode)
            {
                wrapper.MouseEnter += (s, e) => dropBtn.Visibility = Visibility.Visible;
                wrapper.MouseLeave += (s, e) => dropBtn.Visibility = Visibility.Collapsed;
            }

            // 右键菜单
            var moduleMenu = new ContextMenu();
            var hideFrameMi2 = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi2.Click += (s, e) => { item.HideFrame = hideFrameMi2.IsChecked; _configManager?.Save(); RenderPanels(); };
            moduleMenu.Items.Add(hideFrameMi2);
            var delMi2 = new MenuItem { Header = "删除模块" };
            delMi2.Click += (s, e) => RemovePanel(item);
            moduleMenu.Items.Add(delMi2);
            wrapper.ContextMenu = moduleMenu;

            return wrapper;
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
                        catch (Exception ex) { MessageBox.Show($"清空失败：{ex.Message}"); }
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
                            panelItem.FileItems.Remove(capturedFileItem);
                            _configManager?.Save();
                            RenderPanels();
                        }
                        catch (Exception ex) { MessageBox.Show($"删除失败：{ex.Message}"); }
                    }
                };
                ctx.Items.Add(menuDel);

                ctx.Items.Add(new Separator());
                var menuProps = new MenuItem { Header = "属性" };
                menuProps.Click += (s, e) =>
                {
                    try { NativeMethods.ShowFileProperties(filePath); }
                    catch { }
                };
                ctx.Items.Add(menuProps);
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
                            panelItem.FileItems.Remove(capturedFileItem);
                            _configManager?.Save();
                            RenderPanels();
                        }
                        catch (Exception ex) { MessageBox.Show($"删除失败：{ex.Message}"); }
                    }
                };
                ctx.Items.Add(menuDel);

                ctx.Items.Add(new Separator());
                var menuProps = new MenuItem { Header = "属性" };
                menuProps.Click += (s, e) =>
                {
                    try { NativeMethods.ShowFileProperties(filePath); }
                    catch { }
                };
                ctx.Items.Add(menuProps);
            }

            // 从列表移除（不删除磁盘文件）
            ctx.Items.Add(new Separator());
            var menuRemoveFromList = new MenuItem { Header = "从列表移除" };
            menuRemoveFromList.Click += (s, e) =>
            {
                panelItem.FileItems.Remove(capturedFileItem);
                _configManager?.Save();
                RenderPanels();
            };
            ctx.Items.Add(menuRemoveFromList);

            return ctx;
        }

        /// <summary>处理文件拖拽到文件夹</summary>
        private void HandleDropOnFolder(object sender, DragEventArgs e, string folderPath, PanelItem panelItem)
        {
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
                        MessageBox.Show($"复制失败：{ex.Message}");
                    }
                }
                _configManager?.Save();
                RenderPanels();
                e.Handled = true;
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
                _clipboardHistoryContainer.Children.Add(new Emoji.Wpf.TextBlock
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

            // 右键菜单：从历史删除该条记录
            var histMenu = new ContextMenu();
            var histDelItem = new MenuItem { Header = "从历史删除" };
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
            else if (type == PanelType.TodoList)
                newPanel.TodoItems = new List<TodoItem>();

            // 优先添加到最后一个分组内，使其跟随分组收起/展开
            AddPanelToGroupOrTop(newPanel);

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
            namePanel.Children.Add(new Emoji.Wpf.TextBlock { Text = "分组名:", Foreground = (Brush)FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center, Width = 60 });
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
                else if (type == PanelType.TodoList)
                    newPanel.TodoItems = new List<TodoItem>();
                groupItem.ChildPanels.Add(newPanel);
            }
            
            _configManager?.Save();
            RenderPanels();
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
        private void AddDataDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.DataDashboard,
                Title = "数据看板",
                DashboardItems = new List<DataDashboardItem>()
            };
            AddPanelToGroupOrTop(newPanel);
            _configManager.Save();
            RenderPanels();
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
                        Title = "数据看板",
                        DashboardItems = new List<DataDashboardItem> { customItem }
                    };
                    AddPanelToGroupOrTop(newPanel);
                    _configManager.Save();
                    RenderPanels();
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
                menu.Items.Add(new MenuItem { Header = "(无数据项，请在设置中添加)", IsEnabled = false });
                if (onCustomClick != null)
                {
                    menu.Items.Add(new Separator());
                    var customMi = new MenuItem { Header = "定制数据块" };
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
                var customMi = new MenuItem { Header = "定制数据块" };
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
                Title = "数据看板",
                DashboardItems = new List<DataDashboardItem> { BuildDashboardItemFromTemplate(tpl) }
            };
            AddPanelToGroupOrTop(newPanel);
            _configManager.Save();
            RenderPanels();
        }

        /// <summary>根据模版向分组内添加一个数据看板模块</summary>
        private void AddDataBlockToGroup(PanelItem groupItem, Models.DataTemplate tpl)
        {
            groupItem.ChildPanels ??= new List<PanelItem>();
            groupItem.ChildPanels.Add(new PanelItem
            {
                Type = PanelType.DataDashboard,
                DashboardItems = new List<DataDashboardItem> { BuildDashboardItemFromTemplate(tpl) }
            });
            _configManager?.Save();
            RenderPanels();
        }

        private void AddTimerPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.Timer,
                Title = "⏱ 计时器",
                TimerItems = new List<TimerItem>()
            };
            AddPanelToGroupOrTop(newPanel);
            _configManager.Save();
            RenderPanels();
        }

        /// <summary>将新面板添加到最后一个分组内（如有），否则添加到顶层</summary>
        private void AddPanelToGroupOrTop(PanelItem newPanel)
        {
            // 若右键点击位置落在两个顶层模块之间，则插入到该位置（优先于分组追加行为）
            if (_pendingInsertIndex is int insertIdx)
            {
                _pendingInsertIndex = null;
                int count = _currentWindowConfig!.Panels.Count;
                if (insertIdx > 0 && insertIdx < count)
                {
                    _currentWindowConfig.Panels.Insert(insertIdx, newPanel);
                    return;
                }
            }
            var lastGroup = _currentWindowConfig!.Panels.LastOrDefault(p => p.Type == PanelType.Group);
            if (lastGroup != null)
            {
                lastGroup.ChildPanels ??= new List<PanelItem>();
                lastGroup.ChildPanels.Add(newPanel);
            }
            else
            {
                _currentWindowConfig.Panels.Add(newPanel);
            }
        }

        private void AddSignalLightPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.SignalLight,
                Title = "🚦 信号灯",
                SignalLights = new List<SignalLightItem>
                {
                    new() { Color = "#FF4444", Size = 36, Icon = "", IsOn = true },
                    new() { Color = "#FFAA00", Size = 36, Icon = "", IsOn = true },
                    new() { Color = "#44CC44", Size = 36, Icon = "", IsOn = true }
                }
            };
            AddPanelToGroupOrTop(newPanel);
            _configManager.Save();
            RenderPanels();
        }

        private void AddDigitalEmployeePanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            // 默认展示全局库中前几个员工
            var defaultIds = DigitalEmployees.Employees.Take(3).Select(emp => emp.Id).ToList();
            var newPanel = new PanelItem
            {
                Type = PanelType.DigitalEmployee,
                Title = "👥 数字员工",
                DigitalEmployeeIds = defaultIds
            };
            AddPanelToGroupOrTop(newPanel);
            _configManager.Save();
            RenderPanels();
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
                    Text = "暂未选择展示员工，点击标题栏 ▾ 选择",
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

            // === 自定义标题栏：标题 + 下拉三角 ===
            var defaultTitle = "👥 数字员工";
            var displayTitle = string.IsNullOrEmpty(item.Title) ? defaultTitle : item.Title;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Uid = PanelHeaderUid;
            if (_isEditMode) { header.Tag = PanelDragHandleTag; header.Cursor = Cursors.SizeAll; }

            var titleBlock = new Emoji.Wpf.TextBlock
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
                        PromptRenamePanel(item, defaultTitle);
                        e.Handled = true;
                    }
                };
            }

            var dropBtn = new Button
            {
                Content = "\u25BE",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = _isEditMode ? Visibility.Collapsed : Visibility.Visible
            };

            var dropMenu = new ContextMenu();

            // 管理数字员工
            var manageMi = new MenuItem { Header = "管理数字员工" };
            manageMi.Click += (s, e) => OpenDigitalEmployeeManager();
            dropMenu.Items.Add(manageMi);

            // 选择展示员工（子菜单勾选）
            var selectMi = new MenuItem { Header = "选择展示员工" };
            selectMi.SubmenuOpened += (s, e) => PopulateEmployeeSelectMenu(selectMi, item);
            PopulateEmployeeSelectMenu(selectMi, item);
            dropMenu.Items.Add(selectMi);

            // 每排列数
            var columnsMi = new MenuItem { Header = "每排列数" };
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
            dropMenu.Items.Add(columnsMi);

            dropMenu.Items.Add(new Separator());

            var hideFrameMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi.Click += (s, e) => { item.HideFrame = hideFrameMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            dropMenu.Items.Add(hideFrameMi);

            var delModuleMi = new MenuItem { Header = "删除模块" };
            delModuleMi.Click += (s, e) => RemovePanel(item);
            dropMenu.Items.Add(delModuleMi);

            dropBtn.Click += (s, e) => dropMenu.IsOpen = true;

            DockPanel.SetDock(dropBtn, Dock.Right);
            header.Children.Add(dropBtn);
            header.Children.Add(titleBlock);

            var mainStack = new StackPanel();
            mainStack.Children.Add(header);
            mainStack.Children.Add(container);

            // 隐藏模块外框
            if ((item.HideFrame || AppSettings.Instance.HideModuleFrame) && !_isEditMode)
            {
                // 隐藏外框时，若开启了「同时隐藏模块标题」则移除标题栏
                if (AppSettings.Instance.HideModuleTitle)
                    mainStack.Children.Remove(header);
                var bareBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Child = mainStack
                };
                var bareMenu = new ContextMenu();
                var bareHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
                bareHideMi.Click += (s, e) => { item.HideFrame = bareHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
                bareMenu.Items.Add(bareHideMi);
                var bareDelMi = new MenuItem { Header = "删除模块" };
                bareDelMi.Click += (s, e) => RemovePanel(item);
                bareMenu.Items.Add(bareDelMi);
                bareBorder.ContextMenu = bareMenu;
                return bareBorder;
            }

            var wrapper = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = mainStack
            };

            if (_isEditMode)
            {
                wrapper.MouseEnter += (s, e) => dropBtn.Visibility = Visibility.Visible;
                wrapper.MouseLeave += (s, e) => dropBtn.Visibility = Visibility.Collapsed;
            }

            var moduleMenu = new ContextMenu();
            var hideFrameMi2 = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameMi2.Click += (s, e) => { item.HideFrame = hideFrameMi2.IsChecked; _configManager?.Save(); RenderPanels(); };
            moduleMenu.Items.Add(hideFrameMi2);
            var delMi2 = new MenuItem { Header = "删除模块" };
            delMi2.Click += (s, e) => RemovePanel(item);
            moduleMenu.Items.Add(delMi2);
            wrapper.ContextMenu = moduleMenu;

            return wrapper;
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

            card.ToolTip = string.IsNullOrWhiteSpace(emp.Role) ? emp.Name : $"{emp.Name} · {emp.Role}";
            card.MouseLeftButtonUp += (s, e) => ShowEmployeeInfoDialog(emp);

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
                menu.Items.Add(new MenuItem { Header = "(暂无员工，请先管理添加)", IsEnabled = false });
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

            AddInfoRow("擅长/技能", emp.Skills);
            AddInfoRow("联系方式", emp.Contact);
            AddInfoRow("简介/职责", emp.Description);

            dialog.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
                Content = root
            };
            dialog.ShowDialog();
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
            "online" => "在线",
            "away"   => "离开",
            "busy"   => "忙碌",
            _        => "离线",
        };

        /// <summary>好友头像兜底文字（Emoji 优先，否则名字首字）</summary>
        private Emoji.Wpf.TextBlock CreateFriendAvatarFallback(PanelItem item, double size)
        {
            string fallback;
            if (!string.IsNullOrWhiteSpace(item.FriendEmoji)) fallback = item.FriendEmoji!;
            else if (!string.IsNullOrWhiteSpace(item.FriendName)) fallback = item.FriendName!.Substring(0, 1);
            else fallback = "\U0001F642";
            return new Emoji.Wpf.TextBlock
            {
                Text = fallback,
                Foreground = Brushes.White,
                FontSize = size * 0.42,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>好友头像：圆形头像 + 右下角在线状态圆点</summary>
        private FrameworkElement BuildFriendAvatar(PanelItem item, double size)
        {
            var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };

            var avatarBorder = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = (Brush)FindResource("BrushAccent")
            };
            if (!string.IsNullOrWhiteSpace(item.FriendAvatarPath) && System.IO.File.Exists(item.FriendAvatarPath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(item.FriendAvatarPath);
                    bmp.EndInit();
                    avatarBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch { avatarBorder.Child = CreateFriendAvatarFallback(item, size); }
            }
            else avatarBorder.Child = CreateFriendAvatarFallback(item, size);
            grid.Children.Add(avatarBorder);

            // 右下角在线状态圆点（带描边，与头像分离）
            double dotSize = Math.Max(10, size * 0.28);
            var dot = new Border
            {
                Width = dotSize, Height = dotSize,
                CornerRadius = new CornerRadius(dotSize / 2),
                Background = GetFriendStatusBrush(item.FriendOnlineStatus),
                BorderBrush = (Brush)FindResource("BrushInputBG"),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = GetFriendStatusLabel(item.FriendOnlineStatus)
            };
            grid.Children.Add(dot);

            return grid;
        }

        private UIElement BuildFriendWidget(PanelItem item)
        {
            bool vertical = item.FriendDisplayMode == "VerticalLarge";

            var nameBlock = new Emoji.Wpf.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.FriendName) ? "未命名好友" : item.FriendName!,
                FontSize = vertical ? 15 : 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = vertical ? 170 : 170,
                HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left
            };

            // 状态行：小圆点 + 状态文字
            var statusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left
            };
            statusRow.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = GetFriendStatusBrush(item.FriendOnlineStatus),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            statusRow.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.FriendStatusText) ? GetFriendStatusLabel(item.FriendOnlineStatus) : item.FriendStatusText!,
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160
            });

            UIElement content;
            if (vertical)
            {
                // 垂直大头像：头像居中在上，名称/状态在下
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand };
                var avatar = BuildFriendAvatar(item, 72);
                avatar.HorizontalAlignment = HorizontalAlignment.Center;
                avatar.Margin = new Thickness(0, 0, 0, 8);
                stack.Children.Add(avatar);
                nameBlock.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(nameBlock);
                stack.Children.Add(statusRow);
                content = stack;
            }
            else
            {
                // 水平小头像：头像在左，名称/状态在右
                var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand };
                var avatar = BuildFriendAvatar(item, 30);
                avatar.Margin = new Thickness(0, 0, 10, 0);
                avatar.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(avatar);
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                col.Children.Add(nameBlock);
                col.Children.Add(statusRow);
                row.Children.Add(col);
                content = row;
            }

            if (content is FrameworkElement cfe)
            {
                cfe.MouseLeftButtonUp += (s, e) => { if (!_isEditMode) ShowFriendEditDialog(item); };
                cfe.ToolTip = "点击编辑好友信息";
            }

            // 右上角按钮的下拉菜单
            var actionMenu = new ContextMenu();
            var editMi = new MenuItem { Header = "编辑" };
            editMi.Click += (s, e) => ShowFriendEditDialog(item);
            actionMenu.Items.Add(editMi);

            var displayMi = new MenuItem { Header = "显示方式" };
            var modeHorizontal = new MenuItem { Header = "水平小头像", IsCheckable = true, IsChecked = !vertical };
            modeHorizontal.Click += (s, e) => { item.FriendDisplayMode = "HorizontalSmall"; _configManager?.Save(); RenderPanels(); };
            displayMi.Items.Add(modeHorizontal);
            var modeVertical = new MenuItem { Header = "垂直大头像", IsCheckable = true, IsChecked = vertical };
            modeVertical.Click += (s, e) => { item.FriendDisplayMode = "VerticalLarge"; _configManager?.Save(); RenderPanels(); };
            displayMi.Items.Add(modeVertical);
            actionMenu.Items.Add(displayMi);

            actionMenu.Items.Add(new Separator());
            var addMi = new MenuItem { Header = "添加新好友" };
            addMi.Click += (s, e) => ShowAddFriendByCodeDialog();
            actionMenu.Items.Add(addMi);

            return WrapWidget("好友", content, item, actionMenu: actionMenu);
        }

        private void AddFriendPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var newPanel = new PanelItem
            {
                Type = PanelType.Friend,
                Title = "好友",
                FriendName = "好友",
                FriendEmoji = "\U0001F642",
                FriendStatusText = "在线",
                FriendOnlineStatus = "online"
            };
            AddPanelToGroupOrTop(newPanel);
            _configManager.Save();
            RenderPanels();
        }

        /// <summary>通过邀请码添加新好友（暂无后端，先根据邀请码创建一个新的好友模块）</summary>
        private void ShowAddFriendByCodeDialog()
        {
            if (_currentWindowConfig == null || _configManager == null) return;

            var dialog = new Window
            {
                Title = "添加新好友",
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
                Text = "输入好友邀请码",
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var codeBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(codeBox);
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "输入对方的邀请码即可添加为好友",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 16)
            });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "添加", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                var code = codeBox.Text.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    MessageBox.Show("请输入邀请码", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var newPanel = new PanelItem
                {
                    Type = PanelType.Friend,
                    Title = "好友",
                    FriendName = "好友 " + code,
                    FriendEmoji = "\U0001F642",
                    FriendStatusText = "在线",
                    FriendOnlineStatus = "online"
                };
                AddPanelToGroupOrTop(newPanel);
                _configManager.Save();
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

        /// <summary>编辑好友信息对话框</summary>
        private void ShowFriendEditDialog(PanelItem item)
        {
            var dialog = new Window
            {
                Title = "编辑好友",
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

            string? avatarPath = item.FriendAvatarPath;

            // 头像预览
            var previewBorder = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(32),
                Background = (Brush)FindResource("BrushAccent"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12)
            };

            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Text = item.FriendName ?? "" };
            var emojiBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Text = item.FriendEmoji ?? "" };

            void RefreshPreview()
            {
                previewBorder.Child = null;
                if (!string.IsNullOrWhiteSpace(avatarPath) && System.IO.File.Exists(avatarPath))
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(avatarPath);
                        bmp.EndInit();
                        previewBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                        return;
                    }
                    catch { }
                }
                previewBorder.Background = (Brush)FindResource("BrushAccent");
                string fallback = !string.IsNullOrWhiteSpace(emojiBox.Text) ? emojiBox.Text
                    : (!string.IsNullOrWhiteSpace(nameBox.Text) ? nameBox.Text.Substring(0, 1) : "\U0001F642");
                previewBorder.Child = new Emoji.Wpf.TextBlock
                {
                    Text = fallback, Foreground = Brushes.White, FontSize = 27,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
            }
            RefreshPreview();
            root.Children.Add(previewBorder);

            var avatarBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var chooseBtn = new Button { Content = "选择头像", Style = (Style)FindResource("GhostButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var clearBtn = new Button { Content = "清除头像", Style = (Style)FindResource("GhostButtonStyle") };
            chooseBtn.Click += (s, e) =>
            {
                var ofd = new Microsoft.Win32.OpenFileDialog { Title = "选择头像", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
                if (ofd.ShowDialog() == true) { avatarPath = ofd.FileName; RefreshPreview(); }
            };
            clearBtn.Click += (s, e) => { avatarPath = null; RefreshPreview(); };
            avatarBtns.Children.Add(chooseBtn);
            avatarBtns.Children.Add(clearBtn);
            root.Children.Add(avatarBtns);

            TextBlock Label(string t) => new TextBlock
            {
                Text = t, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = (Brush)FindResource("BrushTextMuted")
            };

            root.Children.Add(Label("名字"));
            nameBox.TextChanged += (s, e) => RefreshPreview();
            root.Children.Add(nameBox);

            root.Children.Add(new TextBlock
            {
                Text = "Emoji/文字头像（无图片时使用）", FontSize = 12,
                Margin = new Thickness(0, 12, 0, 4), Foreground = (Brush)FindResource("BrushTextMuted")
            });
            emojiBox.TextChanged += (s, e) => RefreshPreview();
            root.Children.Add(emojiBox);

            root.Children.Add(Label("状态文字"));
            var statusBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Text = item.FriendStatusText ?? "" };
            root.Children.Add(statusBox);

            root.Children.Add(new TextBlock
            {
                Text = "在线状态", FontSize = 12,
                Margin = new Thickness(0, 12, 0, 4), Foreground = (Brush)FindResource("BrushTextMuted")
            });
            var statusCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle") };
            foreach (var (key, label) in new[] { ("online", "在线"), ("away", "离开"), ("busy", "忙碌"), ("offline", "离线") })
                statusCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            statusCombo.SelectedIndex = (item.FriendOnlineStatus) switch { "away" => 1, "busy" => 2, "offline" => 3, _ => 0 };
            root.Children.Add(statusCombo);

            // 按钮行
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("StandardButtonStyle"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 72 };
            var okBtn = new Button { Content = "确定", Style = (Style)FindResource("PrimaryButtonStyle"), MinWidth = 72 };
            cancelBtn.Click += (s, e) => dialog.Close();
            okBtn.Click += (s, e) =>
            {
                item.FriendName = nameBox.Text.Trim();
                item.FriendEmoji = emojiBox.Text.Trim();
                item.FriendStatusText = statusBox.Text.Trim();
                item.FriendAvatarPath = avatarPath;
                item.FriendOnlineStatus = (statusCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "online";
                _configManager?.Save();
                RenderPanels();
                dialog.Close();
            };
            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            root.Children.Add(btnRow);

            dialog.Content = root;
            dialog.ShowDialog();
        }

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

            var nameText = new Emoji.Wpf.TextBlock
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

            var nameText = new Emoji.Wpf.TextBlock
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
                container.Children.Add(new Emoji.Wpf.TextBlock
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
                container.Children.Add(new Emoji.Wpf.TextBlock
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

            // 右键菜单：删除便签（无需进入编辑模式）
            var noteMenu = new ContextMenu();
            var deleteNoteItem = new MenuItem { Header = "删除便签" };
            deleteNoteItem.Click += (s, e) =>
            {
                item.StickyNotes?.RemoveAll(n => n.Id == note.Id);
                _configManager?.Save();
                RefreshPanelStickyNotes(container, item);
            };
            noteMenu.Items.Add(deleteNoteItem);
            var noteHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
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
                ToolTip = "删除", Visibility = Visibility.Collapsed
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
                    finally { HidePanelDragIndicator(); }
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

        /// <summary>根据拖放的 Y 坐标计算目标插入位置（数据层面板索引）</summary>
        private int GetPanelDropIndex(DragEventArgs e) => GetPanelDrop(e).index;

        /// <summary>根据 dynamicContent 内容坐标系中的位置计算顶层面板插入索引（以各模块中线为界）</summary>
        private int GetPanelInsertIndexAt(Point pos)
        {
            double y = 0; int panelIdx = 0;
            foreach (UIElement uie in dynamicContent.Children)
            {
                if (uie is not FrameworkElement fe || fe.Tag is not string) continue;
                double h = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                if (pos.Y < y + h / 2) return panelIdx;
                y += h; panelIdx++;
            }
            return panelIdx;
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

        /// <summary>将指定面板移动到目标位置并持久化</summary>
        private void MovePanelToIndex(string draggedId, int insertIdx)
        {
            if (_currentWindowConfig == null || _configManager == null) return;
            var panels = _currentWindowConfig.Panels;
            int oldIdx = panels.FindIndex(p => p.Id == draggedId);
            if (oldIdx < 0) return;
            var item = panels[oldIdx];
            panels.RemoveAt(oldIdx);
            int newIdx = insertIdx > oldIdx ? insertIdx - 1 : insertIdx;
            if (newIdx < 0) newIdx = 0;
            if (newIdx > panels.Count) newIdx = panels.Count;
            if (newIdx == oldIdx) { panels.Insert(oldIdx, item); return; }
            panels.Insert(newIdx, item);
            _configManager.Save();
            RenderPanels();
        }

        #endregion

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
                var leftHeader = new Emoji.Wpf.TextBlock
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
                        sp.Children.Add(new Emoji.Wpf.TextBlock
                        {
                            Text = head,
                            FontWeight = FontWeights.Bold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        string detail = !string.IsNullOrEmpty(r.MatchPattern)
                            ? $"匹配（{(r.MatchMode == TitleMatchMode.Equals ? "等于" : "包含")}）：{r.MatchPattern}"
                            : $"原标题：{r.OriginalTitle}";
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
                var rightHeader = new Emoji.Wpf.TextBlock
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
        /// 右键菜单打开时，动态设置布局和屏幕的勾选状态
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

                    // 应用水平对齐
                    var hAlign = dashItem.HorizontalAlignment;
                    if (hAlign == "Center") card.HorizontalAlignment = HorizontalAlignment.Center;
                    else if (hAlign == "Right") card.HorizontalAlignment = HorizontalAlignment.Right;

                    // 右键菜单：立即更新数据 + 删除数据项
                    var capturedDash = dashItem;
                    var dashItemMenu = new ContextMenu();

                    var refreshItem = new MenuItem { Header = "立即更新数据" };
                    refreshItem.Click += async (s, e) =>
                    {
                        try { await BroadcastWsFetch(capturedDash, valueBlock); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DataDashboard] 手动刷新失败 {capturedDash.Label}: {ex.Message}"); }
                    };
                    dashItemMenu.Items.Add(refreshItem);

                    var editStyleItem = new MenuItem { Header = "编辑样式" };
                    editStyleItem.Click += (s, e) => ShowDashboardItemStyleDialog(capturedDash);
                    dashItemMenu.Items.Add(editStyleItem);

                    var editItem = new MenuItem { Header = "编辑数据项" };
                    editItem.Click += (s, e) =>
                    {
                        var updated = ShowAddDashboardItemDialog(capturedDash);
                        if (updated != null)
                        {
                            _configManager?.Save();
                            RenderPanels();
                        }
                    };
                    dashItemMenu.Items.Add(editItem);

                    var dashItemDel = new MenuItem { Header = "删除数据项" };
                    dashItemDel.Click += (s, e) =>
                    {
                        item.DashboardItems?.Remove(capturedDash);
                        _configManager?.Save();
                        RenderPanels();
                    };
                    dashItemMenu.Items.Add(dashItemDel);
                    card.ContextMenu = dashItemMenu;

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

                    contentPanel.Children.Add(card);

                    // 启动定时刷新
                    StartDashboardItemRefresh(dashItem, valueBlock);
                }
            }
            else
            {
                contentPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "暂无数据项，请编辑添加",
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
                        Content = "+ 添加数据项",
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

            // 隐藏模块外框（全局开关或模块独立开关，非编辑模式生效）
            if ((item.HideFrame || AppSettings.Instance.HideModuleFrame) && !_isEditMode)
            {
                // 无框态也提供右键菜单，确保能恢复外框
                var bareMenu = new ContextMenu();
                var bareHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
                bareHideMi.Click += (s, e) => { item.HideFrame = bareHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
                bareMenu.Items.Add(bareHideMi);
                var bareDelMi = new MenuItem { Header = "删除模块" };
                bareDelMi.Click += (s, e) => RemovePanel(item);
                bareMenu.Items.Add(bareDelMi);

                var bareBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Child = contentPanel,
                    ContextMenu = bareMenu
                };
                return bareBorder;
            }

            // === 自定义标题栏：标题 + 下拉三角 ===
            var displayTitle = string.IsNullOrEmpty(item.Title) ? "📊 数据看板" : item.Title;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Uid = PanelHeaderUid;
            if (_isEditMode) { header.Tag = PanelDragHandleTag; header.Cursor = Cursors.SizeAll; }

            var titleBlock = new Emoji.Wpf.TextBlock
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
                        PromptRenamePanel(item, "📊 数据看板");
                        e.Handled = true;
                    }
                };
            }

            // 下拉三角按钮
            var dropBtn = new Button
            {
                Content = "\u25BE",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = _isEditMode ? Visibility.Collapsed : Visibility.Visible
            };

            // 构建下拉菜单
            var dropMenu = new ContextMenu();

            var addMenuItem = new MenuItem { Header = "添加数据项" };
            addMenuItem.Click += (s, e) => AddDashItem();
            dropMenu.Items.Add(addMenuItem);

            dropMenu.Items.Add(new Separator());

            // 布局子菜单：几列
            var layoutMenu = new MenuItem { Header = "布局" };
            for (int n = 1; n <= 4; n++)
            {
                var colItem = new MenuItem { Header = $"{n} 列", IsCheckable = true, IsChecked = cols == n };
                int colCount = n;
                colItem.Click += (s, e) =>
                {
                    item.DashboardColumns = colCount;
                    _configManager?.Save();
                    RenderPanels();
                };
                layoutMenu.Items.Add(colItem);
            }
            dropMenu.Items.Add(layoutMenu);

            dropMenu.Items.Add(new Separator());

            // 隐藏模块外框
            var hideFrameItem = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            hideFrameItem.Click += (s, e) =>
            {
                item.HideFrame = hideFrameItem.IsChecked;
                _configManager?.Save();
                RenderPanels();
            };
            dropMenu.Items.Add(hideFrameItem);

            // 删除模块
            var delModuleItem = new MenuItem { Header = "删除模块" };
            delModuleItem.Click += (s, e) => RemovePanel(item);
            dropMenu.Items.Add(delModuleItem);

            dropBtn.Click += (s, e) =>
            {
                dropMenu.IsOpen = true;
            };

            DockPanel.SetDock(dropBtn, Dock.Right);
            header.Children.Add(dropBtn);
            header.Children.Add(titleBlock);

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(contentPanel);

            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Child = stack
            };

            if (_isEditMode)
            {
                border.MouseEnter += (s, e) => dropBtn.Visibility = Visibility.Visible;
                border.MouseLeave += (s, e) => dropBtn.Visibility = Visibility.Collapsed;
            }

            // 右键菜单：隐藏模块外框（可勾选） + 删除模块（无需进入编辑模式也能隐藏/恢复）
            var dashMenu = new ContextMenu();
            var dashHideMi = new MenuItem { Header = "隐藏模块外框", IsCheckable = true, IsChecked = item.HideFrame };
            dashHideMi.Click += (s, e) => { item.HideFrame = dashHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            dashMenu.Items.Add(dashHideMi);
            var dashDelMi = new MenuItem { Header = "删除模块" };
            dashDelMi.Click += (s, e) => RemovePanel(item);
            dashMenu.Items.Add(dashDelMi);
            border.ContextMenu = dashMenu;

            return border;
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

        /// <summary>数据项样式编辑弹窗</summary>
        private void ShowDashboardItemStyleDialog(DataDashboardItem dashItem)
        {
            var dialog = new Window
            {
                Title = "编辑样式 - " + dashItem.Label,
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
                Text = "编辑数据块样式",
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
                var customLabel = new TextBlock { Text = "自定义：", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                DockPanel.SetDock(customLabel, Dock.Left);
                customDock.Children.Add(customLabel);
                var customBox = new TextBox { Width = 100, FontSize = 11, Style = textBoxStyle };
                customDock.Children.Add(customBox);
                var applyBtn = new Button
                {
                    Content = "应用", FontSize = 10, Padding = new Thickness(6, 2, 6, 2),
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
            var labelRowLabel = new TextBlock { Text = "标题：", Width = 50, VerticalAlignment = VerticalAlignment.Center };
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
                Content = "粗体", IsChecked = dashItem.LabelBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("DarkCheckBoxStyle")
            };
            DockPanel.SetDock(labelBoldCheck, Dock.Right);
            labelRow.Children.Add(labelBoldCheck);
            root.Children.Add(labelRow);

            // 数据（颜色 + 字号 + 粗体 同一行）
            var valueRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var valueRowLabel = new TextBlock { Text = "数据：", Width = 50, VerticalAlignment = VerticalAlignment.Center };
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
                Content = "粗体", IsChecked = dashItem.ValueBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("DarkCheckBoxStyle")
            };
            DockPanel.SetDock(valueBoldCheck, Dock.Right);
            valueRow.Children.Add(valueBoldCheck);
            root.Children.Add(valueRow);

            // 水平对齐
            var alignDock = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var alignLabel = new TextBlock { Text = "水平对齐：", Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(alignLabel, Dock.Left);
            alignDock.Children.Add(alignLabel);
            var alignCombo = new ComboBox { Style = comboStyle };
            alignCombo.Items.Add("左对齐");
            alignCombo.Items.Add("居中");
            alignCombo.Items.Add("右对齐");
            alignCombo.SelectedIndex = dashItem.HorizontalAlignment switch
            {
                "Center" => 1,
                "Right" => 2,
                _ => 0
            };
            alignDock.Children.Add(alignCombo);
            root.Children.Add(alignDock);

            // 提示
            root.Children.Add(new TextBlock
            {
                Text = "点击色块选择预设颜色，也可直接输入 #RRGGBB 色值，留空使用默认主题色",
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
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

        private void StartDashboardItemRefresh(DataDashboardItem dashItem, Emoji.Wpf.TextBlock valueBlock)
        {
            var interval = Math.Max(dashItem.RefreshIntervalSeconds, 10);
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(interval)
            };

            timer.Tick += async (s, e) =>
            {
                try { await BroadcastWsFetch(dashItem, valueBlock); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DataDashboard] 定时刷新失败 {dashItem.Label}: {ex.Message}"); }
            };

            timer.Start();

            // 首次延迟3秒获取一次
            _ = Task.Delay(3000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(async () =>
                {
                    try { await BroadcastWsFetch(dashItem, valueBlock); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DataDashboard] 首次获取失败 {dashItem.Label}: {ex.Message}"); }
                });
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// 通过 WebSocket 广播抓取请求并注册回调更新 UI（定时刷新和手动抓取共用）
        /// </summary>
        private async Task BroadcastWsFetch(DataDashboardItem dashItem, Emoji.Wpf.TextBlock valueBlock)
        {
            var server = Services.WebSocketServerService.Instance;
            if (!server.IsRunning || server.ClientCount == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[DataDashboard] WS无客户端，跳过: {dashItem.Label}");
                return;
            }

            var requestId = Guid.NewGuid().ToString();
            var app = (App)Application.Current;
            app.RegisterFetchCallback(requestId, (success, value, error) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (success)
                    {
                        valueBlock.Text = value;
                        dashItem.Value = value;
                    }
                    else
                    {
                        valueBlock.Text = "--";
                    }
                    System.Diagnostics.Debug.WriteLine($"[DataDashboard] 回调更新: {dashItem.Label} = {(success ? value : error)}");
                });
            });

            var payload = new { type = "fetch", requestId, label = dashItem.Label, url = dashItem.TargetUrl, selector = dashItem.Selector };
            await server.BroadcastAsync(Newtonsoft.Json.JsonConvert.SerializeObject(payload));
        }

        #endregion

        #region 数据项配置对话框

        private DataDashboardItem? ShowAddDashboardItemDialog(DataDashboardItem? editItem = null)
        {
            var dialog = new Window
            {
                Title = editItem != null ? "编辑数据项" : "添加数据项",
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "模板选择",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var templateCombo = new ComboBox
            {
                Style = (Style)FindResource("GlobalComboBoxStyle"),
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "── 基本信息 ──",
                Foreground = labelBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 数据项名称
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "── 数据源配置 ──",
                Foreground = labelBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 目标网址
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "── 浏览器配置 ──",
                Foreground = labelBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 浏览器
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "浏览器",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var browserCombo = new ComboBox
            {
                Style = (Style)FindResource("GlobalComboBoxStyle"),
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
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "Profile",
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var profileCombo = new ComboBox
            {
                Style = (Style)FindResource("GlobalComboBoxStyle"),
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

            // 编辑模式：预填已有数据（在所有控件创建之后）
            if (editItem != null)
            {
                nameBox.Text = editItem.Label ?? "";
                urlBox.Text = editItem.TargetUrl ?? "";
                selectorBox.Text = editItem.Selector ?? "";
                intervalBox.Text = editItem.RefreshIntervalSeconds > 0 ? editItem.RefreshIntervalSeconds.ToString() : "60";
                if (!string.IsNullOrEmpty(editItem.TemplateId))
                {
                    for (int i = 0; i < templateCombo.Items.Count; i++)
                    {
                        if (templateCombo.Items[i] is ComboBoxItem ci && ci.Tag is string tid && tid == editItem.TemplateId)
                        {
                            templateCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(editItem.BrowserId))
                {
                    for (int i = 0; i < browserCombo.Items.Count; i++)
                    {
                        if (browserCombo.Items[i] is ComboBoxItem ci && ci.Tag is string bid && bid == editItem.BrowserId)
                        {
                            browserCombo.SelectedIndex = i;
                            break;
                        }
                    }
                    refreshProfiles();
                    if (!string.IsNullOrEmpty(editItem.ProfileId))
                    {
                        for (int i = 0; i < profileCombo.Items.Count; i++)
                        {
                            if (profileCombo.Items[i] is ComboBoxItem ci && ci.Tag is string pid && pid == editItem.ProfileId)
                            {
                                profileCombo.SelectedIndex = i;
                                break;
                            }
                        }
                    }
                }
            }

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
                var item = editItem ?? new DataDashboardItem();
                item.Label = nameBox.Text.Trim();
                item.TargetUrl = urlBox.Text.Trim();
                item.Selector = selectorBox.Text.Trim();
                item.RefreshIntervalSeconds = int.TryParse(intervalBox.Text.Trim(), out var interval) && interval > 0 ? interval : 60;

                // 记录模板ID
                if (templateCombo.SelectedItem is ComboBoxItem selItem && selItem.Tag is string tmplId)
                {
                    item.TemplateId = tmplId;
                }
                else
                {
                    item.TemplateId = null;
                }

                // 记录浏览器和Profile
                if (browserCombo.SelectedItem is ComboBoxItem browserItem && browserItem.Tag is string bId)
                {
                    item.BrowserId = bId;
                }
                else
                {
                    item.BrowserId = null;
                }
                if (profileCombo.SelectedItem is ComboBoxItem profileItem && profileItem.Tag is string pId)
                {
                    item.ProfileId = pId;
                }
                else
                {
                    item.ProfileId = null;
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
                var headerText = new Emoji.Wpf.TextBlock
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
                    infoPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = win.Title,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    infoPanel.Children.Add(new Emoji.Wpf.TextBlock
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
                    Foreground = (Brush)FindResource("BrushAccentText"),
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
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0, 10, 0, 10),
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
