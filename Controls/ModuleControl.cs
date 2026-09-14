using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Framework;

namespace SideDesk.Controls
{
    /// <summary>
    /// 卡片基础组件：所有面板卡片（含插件）的统一基类。
    /// 提供标准外框、标题栏、右上角菜单（卡片设置/隐藏外框/置顶/删除）、
    /// 编辑模式拖拽手柄、内容区可调高等通用能力。
    /// 子类/调用方仅需关注内容逻辑，通过 Content 属性注入即可。
    /// </summary>
    public class ModuleControl : ContentControl
    {
        // ===== 常量 =====
        public const string DragHandleTag = "panelDragHandle";
        public const string HeaderUid = "panelHeader";
        private const double MinContentHeight = 48;
        /// <summary>填充模式未设置固定高度时的默认内容区高度</summary>
        private const double FillDefaultMinHeight = 400;

        // ===== 属性 =====
        public PanelItem Item { get; }
        public string DefaultTitle { get; set; }
        public bool IsEditMode { get; set; }
        public WindowConfigManager? ConfigManager { get; set; }
        /// <summary>背景色覆盖（优先于 Item.BGColor，用于 Group 等需要特殊背景色的卡片）</summary>
        public string? BGColorOverride { get; set; }
        /// <summary>窗口级限制：引用导入的窗口不允许编辑（由 NativeModePanel 注入），与卡片级限制叠加</summary>
        public bool WindowEditBlocked { get; set; }

        /// <summary>编辑受限（卡片自身或所属窗口的引用分享不允许编辑）→ 内容区只读，拦截行数据增删改</summary>
        private bool EditBlocked => Item.IsShareReadOnly || WindowEditBlocked;
        // 注：删除卡片=取消引用，始终允许（不受 AllowDelete 限制）；AllowDelete 仅控制卡片内行数据删除，
        // 由后端 item-row.push 鉴权保障（allowDelete=false 时静默跳过删除），客户端不单独拦截行删除入口。

        // ===== 回调（由 NativeModePanel 注入）=====
        /// <summary>请求重新渲染所有面板</summary>
        public Action? OnRenderPanels { get; set; }
        /// <summary>请求删除当前卡片</summary>
        public Action<PanelItem>? OnRemovePanel { get; set; }
        /// <summary>请求打开卡片设置对话框</summary>
        public Action<PanelItem, string>? OnShowSettings { get; set; }
        /// <summary>请求重命名卡片</summary>
        public Action<PanelItem, string>? OnRenamePanel { get; set; }

        // ===== 内部 UI 引用 =====
        private Border _rootBorder = null!;
        private StackPanel _mainStack = null!;
        private DockPanel _header = null!;
        private Button _menuBtn = null!;
        private Emoji.Wpf.TextBlock _titleBlock = null!;
        private ScrollViewer? _contentHost;
        private Border? _fillHost; // 填充模式内容宿主（无滚动条，内容 100% 铺满，如内嵌网页）
        /// <summary>当前生效的内容宿主（填充模式为 _fillHost，否则为 _contentHost），供 resize 手柄统一操作高度</summary>
        private FrameworkElement HostElement => _fillHost != null ? _fillHost : (FrameworkElement)_contentHost!;
        private Border? _resizeGrip;
        private ContextMenu? _moduleMenu;
        private readonly bool _groupMode;
        private readonly bool _fillMode;

        // 自定义菜单项（在标准菜单项之后、分隔线之前插入）
        private readonly List<UIElement> _customMenuItems = new();

        // 内容区（Content 属性存储在此）
        private UIElement? _moduleContent;

        /// <summary>卡片高度拖拽进行中标志</summary>
        private bool _resizingModule;

        public ModuleControl(PanelItem item, string defaultTitle, bool isEditMode, WindowConfigManager? configManager, bool groupMode = false, bool fillMode = false)
        {
            Item = item;
            DefaultTitle = defaultTitle;
            IsEditMode = isEditMode;
            ConfigManager = configManager;
            _groupMode = groupMode;
            _fillMode = fillMode;

            // 标记为已处理，避免被 NativeModePanel.WrapResizable 二次包装
            Uid = "wrapHandled";

            BuildVisualTree();
        }

        /// <summary>设置卡片内容（替代直接设置 Content，确保内容放入正确的容器）</summary>
        public void SetContent(UIElement content)
        {
            _moduleContent = content;
            // 引用分享只读：包一层拦截层，阻断内容区一切点击操作（行数据的增删改），保留浏览与滚动
            var effective = EditBlocked ? WrapShareReadOnly(content) : content;
            if (_groupMode)
            {
                // Group 模式：内容直接放入主容器（无 ScrollViewer）
                _mainStack.Children.Add(effective);
                return;
            }
            if (_fillHost != null)
            {
                // 填充模式：内容直接拉伸铺满内容区（无滚动条，如内嵌网页）
                _fillHost.Child = effective;
                if (effective is FrameworkElement fillFe)
                {
                    fillFe.HorizontalAlignment = HorizontalAlignment.Stretch;
                    fillFe.VerticalAlignment = VerticalAlignment.Stretch;
                }
                // 未设置固定高度时给默认显示高度（网页等无固有高度的内容）
                if (!Item.PanelHeight.HasValue || Item.PanelHeight.Value <= 0)
                    _fillHost.MinHeight = FillDefaultMinHeight;
                return;
            }
            _contentHost!.Content = effective;
            // 固定高度时让内容至少填满可视区域
            if (Item.PanelHeight.HasValue && Item.PanelHeight.Value > 0 && content is FrameworkElement cfe)
            {
                cfe.SetBinding(FrameworkElement.MinHeightProperty,
                    new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = _contentHost });
            }
        }

        /// <summary>添加自定义菜单项（插入到标准菜单项“卡片设置”之后）</summary>
        public void AddCustomMenuItem(UIElement menuItem)
        {
            _customMenuItems.Add(menuItem);
            // 直接插入到已有菜单中（位置：卡片设置之后、分隔线/标准项之前）
            if (_moduleMenu != null)
            {
                int insertIndex = 1 + _customMenuItems.Count - 1; // 1(卡片设置) + 已有自定义项数 - 1
                _moduleMenu.Items.Insert(insertIndex, menuItem);
                // 确保分隔线存在（第一个自定义项插入时添加）
                if (_customMenuItems.Count == 1)
                {
                    _moduleMenu.Items.Insert(insertIndex + 1, new Separator());
                }
            }
        }
        
        /// <summary>批量添加自定义菜单项</summary>
        public void AddCustomMenuItems(IEnumerable<UIElement> menuItems)
        {
            foreach (var mi in menuItems)
                AddCustomMenuItem(mi);
        }

        /// <summary>获取显示标题（置顶卡片自动添加 📌 前缀）</summary>
        public virtual string GetDisplayTitle()
        {
            var title = string.IsNullOrEmpty(Item.Title) ? DefaultTitle : Item.Title;
            return Item.IsPinned ? $"\U0001F4CC {title}" : title;
        }

        /// <summary>在标题栏左侧插入元素（如展开/收起箭头），Dock.Left</summary>
        public void InsertHeaderLeft(UIElement element)
        {
            DockPanel.SetDock(element, Dock.Left);
            _header.Children.Insert(0, element);
        }

        /// <summary>设置标题栏 Margin</summary>
        public void SetHeaderMargin(Thickness margin)
        {
            _header.Margin = margin;
        }

        /// <summary>设置整个卡片的右键菜单（Group 模式使用自定义菜单替代标准菜单）</summary>
        public void SetModuleContextMenu(ContextMenu menu)
        {
            _rootBorder.ContextMenu = menu;
        }

        /// <summary>设置菜单按钮显示内容</summary>
        public void SetMenuButtonContent(string content)
        {
            _menuBtn.Content = content;
        }

        /// <summary>获取标题栏 DockPanel 引用（供外部设置 ContextMenu 等）</summary>
        public DockPanel Header => _header;

        // ===== 视觉树构建 =====

        private void BuildVisualTree()
        {
            bool hideFrame = (Item.HideFrame || AppSettings.Instance.HideModuleFrame) && !IsEditMode;

            if (hideFrame)
            {
                BuildHideFrameLayout();
            }
            else
            {
                BuildNormalLayout();
            }
        }

        /// <summary>正常模式：带外框、标题栏、菜单按钮</summary>
        private void BuildNormalLayout()
        {
            // 标题栏
            _header = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            _header.Uid = HeaderUid;
            if (IsEditMode)
            {
                _header.Tag = DragHandleTag;
                _header.Cursor = Cursors.SizeAll;
            }

            // 标题文字
            _titleBlock = new Emoji.Wpf.TextBlock
            {
                Text = GetDisplayTitle(),
                FontSize = Framework.AppSettings.Instance.FS(13), // 卡片标题自动比卡片内字体（基准 12）大 1 号
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GetBrush("BrushTextMain")
            };

            if (IsEditMode)
            {
                _titleBlock.Cursor = Cursors.Hand;
                _titleBlock.ToolTip = Framework.Lang.T("nativePanel.doubleClickToRename");
                _titleBlock.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        // 引用分享只读：禁止重命名
                        if (EditBlocked) { ShowReadOnlyHint(_titleBlock); e.Handled = true; return; }
                        OnRenamePanel?.Invoke(Item, DefaultTitle);
                        e.Handled = true;
                    }
                };
            }

            // 右上角菜单按钮
            _menuBtn = new Button
            {
                Content = "\u25BE",
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = GetBrush("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = IsEditMode ? Visibility.Collapsed : Visibility.Visible
            };
            _menuBtn.Click += (s, e) =>
            {
                var menu = _moduleMenu ?? _rootBorder.ContextMenu;
                if (menu == null) return;
                menu.PlacementTarget = _menuBtn;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };

            DockPanel.SetDock(_menuBtn, Dock.Right);
            _header.Children.Add(_menuBtn);
            _header.Children.Add(_titleBlock);

            // 内容区（Group 模式不使用 ScrollViewer，高度由子内容撑开；填充模式无滚动条，内容铺满）
            if (!_groupMode)
            {
                if (_fillMode)
                    _fillHost = CreateFillHost();
                else
                    _contentHost = CreateContentHost();
            }

            // 主容器
            _mainStack = new StackPanel();
            _mainStack.Children.Add(_header);
            if (_fillHost != null)
                _mainStack.Children.Add(_fillHost);
            else if (_contentHost != null)
                _mainStack.Children.Add(_contentHost);

            // 编辑模式：添加 resize 手柄（Group 模式不需要）
            if (IsEditMode && !_groupMode)
            {
                _resizeGrip = CreateResizeGrip();
                _mainStack.Children.Add(_resizeGrip);
            }

            // 外框（分享导入/团队归属卡片使用设置中的标识边框色）
            var (specialBrush, specialThickness) = ResolveSpecialBorder();
            _rootBorder = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = ApplyDashColor(BGColorOverride ?? Item.BGColor, "BrushInputBG"),
                BorderBrush = specialBrush ?? GetBrush("BrushBorder"),
                BorderThickness = new Thickness(specialThickness),
                Child = _mainStack
            };

            // 编辑模式：悬停显示菜单按钮
            if (IsEditMode)
            {
                _rootBorder.MouseEnter += (s, e) => _menuBtn.Visibility = Visibility.Visible;
                _rootBorder.MouseLeave += (s, e) => _menuBtn.Visibility = Visibility.Collapsed;
            }

            // 构建右键菜单（Group 模式由外部设置自定义菜单）
            if (!_groupMode)
            {
                RebuildMenu();
                _rootBorder.ContextMenu = _moduleMenu;
            }

            // 设置为控件内容
            Content = _rootBorder;
        }

        /// <summary>隐藏外框模式：无边框/标题，仅保留内容 + 右键菜单</summary>
        private void BuildHideFrameLayout()
        {
            if (!_groupMode)
            {
                if (_fillMode)
                    _fillHost = CreateFillHost();
                else
                    _contentHost = CreateContentHost();
            }

            var bareStack = new StackPanel();
            if (_fillHost != null)
                bareStack.Children.Add(_fillHost);
            else if (_contentHost != null)
                bareStack.Children.Add(_contentHost);

            _rootBorder = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(0),
                Background = ApplyDashColor(BGColorOverride ?? Item.BGColor, "BrushInputBG"),
                BorderThickness = new Thickness(0),
                Child = bareStack
            };

            // 构建右键菜单（隐藏外框模式下也需要能恢复；Group 模式由外部设置）
            if (!_groupMode)
            {
                RebuildMenu();
                _rootBorder.ContextMenu = _moduleMenu;
            }

            // 初始化 header/titleBlock 为 dummy（避免空引用）
            _header = new DockPanel();
            _titleBlock = new Emoji.Wpf.TextBlock();
            _menuBtn = new Button();
            _mainStack = bareStack;

            Content = _rootBorder;
        }

        /// <summary>填充模式内容宿主：无滚动条，子内容拉伸铺满；高度跟随 PanelHeight 或默认值</summary>
        private Border CreateFillHost()
        {
            var host = new Border { Background = Brushes.Transparent };
            if (Item.PanelHeight.HasValue && Item.PanelHeight.Value > 0)
                host.Height = Item.PanelHeight.Value;
            return host;
        }

        /// <summary>创建内容区 ScrollViewer（支持固定高度 + 滚轮穿透）</summary>
        private ScrollViewer CreateContentHost()
        {
            var host = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            if (Item.PanelHeight.HasValue && Item.PanelHeight.Value > 0)
            {
                host.Height = Item.PanelHeight.Value;
            }

            // 滚轮穿透：内容未溢出时转发给外层
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

        /// <summary>创建底部拖拽手柄（编辑模式下调整卡片高度）</summary>
        private Border CreateResizeGrip()
        {
            var grip = new Border
            {
                Height = 6,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = Framework.Lang.T("nativePanel.dragToAdjustHeightHint")
            };
            var line = new Border
            {
                Width = 34,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = GetBrush("BrushTextMuted"),
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
                    var host = HostElement;
                    host.ClearValue(HeightProperty);
                    if (_contentHost?.Content is FrameworkElement contentFe)
                        BindingOperations.ClearBinding(contentFe, FrameworkElement.MinHeightProperty);
                    if (_fillHost != null)
                        _fillHost.MinHeight = FillDefaultMinHeight; // 恢复默认高度
                    Item.PanelHeight = null;
                    ConfigManager?.Save();
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
                var hostEl = HostElement;
                startHeight = (double.IsNaN(hostEl.Height) || hostEl.Height <= 0)
                    ? hostEl.ActualHeight : hostEl.Height;
                grip.CaptureMouse();
                e.Handled = true;
            };

            grip.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                double dy = e.GetPosition(this).Y - startY;
                if (!moved && Math.Abs(dy) < 3) return;
                moved = true;
                double h = startHeight + dy;
                if (h < MinContentHeight) h = MinContentHeight;
                HostElement.Height = h;
            };

            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                _resizingModule = false;
                grip.ReleaseMouseCapture();
                if (moved)
                {
                    Item.PanelHeight = HostElement.Height;
                    ConfigManager?.Save();
                }
                e.Handled = true;
            };

            grip.LostMouseCapture += (s, e) => { _resizingModule = false; };

            return grip;
        }

        // ===== 右键菜单 =====

        /// <summary>重建右键菜单（标准项 + 自定义项）</summary>
        private void RebuildMenu()
        {
            _moduleMenu = new ContextMenu();

            // 卡片设置（引用分享只读时点击拦截提醒，不禁用——WPF 禁用菜单项 ToolTip 不显示，用户无感知）
            var settingsMi = new MenuItem { Header = Framework.Lang.T("common.moduleSettings") };
            if (EditBlocked)
            {
                var hint = Framework.Lang.T("nativePanel.sharedModuleNoChangeSettings");
                settingsMi.ToolTip = hint;
                AttachBlockedGuard(settingsMi, this, hint);
            }
            settingsMi.Click += (s, e) => OnShowSettings?.Invoke(Item, DefaultTitle);
            _moduleMenu.Items.Add(settingsMi);

            // 自定义菜单项（引用分享只读时拦截，避免绕过拦截层修改数据；Tag="keepWhenReadOnly" 的除外，如再分享）
            foreach (var custom in _customMenuItems)
            {
                if (EditBlocked && custom is MenuItem cmi && !Equals(cmi.Tag, "keepWhenReadOnly"))
                {
                    var hint = Framework.Lang.T("nativePanel.sharedModuleNoModifyContent");
                    cmi.ToolTip = hint;
                    if (cmi.Items.Count > 0)
                    {
                        // 子菜单父项：禁用防止悬停展开后子项绕过拦截（子项无法触达）
                        cmi.IsEnabled = false;
                    }
                    else
                    {
                        // 叶子项：保持可点击，点击时拦截并提醒
                        AttachBlockedGuard(cmi, this, hint);
                    }
                }
                _moduleMenu.Items.Add(custom);
            }

            // 分隔线（有自定义项时才显示）
            if (_customMenuItems.Count > 0)
            {
                _moduleMenu.Items.Add(new Separator());
            }

            // 隐藏卡片外框
            var hideFrameMi = new MenuItem
            {
                Header = Framework.Lang.T("common.hideModuleFrame"),
                IsCheckable = true,
                IsChecked = Item.HideFrame
            };
            hideFrameMi.Click += (s, e) =>
            {
                Item.HideFrame = hideFrameMi.IsChecked;
                ConfigManager?.Save();
                OnRenderPanels?.Invoke();
            };
            _moduleMenu.Items.Add(hideFrameMi);

            // 置顶显示
            var pinMi = new MenuItem
            {
                Header = Framework.Lang.T("nativePanel.pinOnTop"),
                IsCheckable = true,
                IsChecked = Item.IsPinned
            };
            pinMi.Click += (s, e) =>
            {
                Item.IsPinned = pinMi.IsChecked;
                ConfigManager?.Save();
                OnRenderPanels?.Invoke();
            };
            _moduleMenu.Items.Add(pinMi);

            // 删除卡片：引用分享时语义为“取消引用/移除共享卡片”，始终允许——接收方有权移除自己侧边栏的共享卡片，
            // 后端 sync.remove 对非属主仅解除引用并删除 share_import 记录、永不软删源卡片。
            // AllowDelete 只控制卡片内行数据的删除（由内容区拦截层与后端 item-row 鉴权保障），不控制取消引用。
            var deleteMi = new MenuItem { Header = Framework.Lang.T("nativePanel.deleteModule") };
            deleteMi.Click += (s, e) => OnRemovePanel?.Invoke(Item);
            _moduleMenu.Items.Add(deleteMi);

            // 隐藏外框模式下，内容区也挂菜单（确保右键能弹出）
            if (_moduleContent is FrameworkElement contentFe && contentFe.ContextMenu == null)
            {
                contentFe.ContextMenu = _moduleMenu;
            }
        }

        // ===== 分享只读拦截 =====

        private DateTime _lastReadOnlyHintAt = DateTime.MinValue;

        /// <summary>分享只读拦截层：包裹内容区，阻断鼠标按下/抬起（按钮点击、双击编辑、复选框、右键行菜单等），不影响浏览与滚动</summary>
        private UIElement WrapShareReadOnly(UIElement content)
        {
            var wrapper = new Grid
            {
                Background = Brushes.Transparent,
                ToolTip = Framework.Lang.T("nativePanel.sharedModuleNoModifyContent")
            };
            wrapper.Children.Add(content);
            MouseButtonEventHandler block = (s, e) =>
            {
                e.Handled = true;
                ShowReadOnlyHint(wrapper);
            };
            wrapper.PreviewMouseLeftButtonDown += block;
            wrapper.PreviewMouseLeftButtonUp += block;
            wrapper.PreviewMouseRightButtonDown += block;
            wrapper.PreviewMouseRightButtonUp += block;
            return wrapper;
        }

        /// <summary>点击被拦截时短暂弹出提示（3 秒节流，避免刷屏）</summary>
        private void ShowReadOnlyHint(FrameworkElement host, string? message = null)
        {
            if ((DateTime.Now - _lastReadOnlyHintAt).TotalSeconds < 3) return;
            _lastReadOnlyHintAt = DateTime.Now;
            var tip = new ToolTip
            {
                Content = message ?? Framework.Lang.T("nativePanel.sharedModuleNoEditDelete"),
                PlacementTarget = host,
                Placement = PlacementMode.MousePoint,
                IsOpen = true
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (s, e) => { tip.IsOpen = false; timer.Stop(); };
            timer.Start();
        }

        /// <summary>
        /// 为权限受限的菜单项挂点击拦截：点击时关闭菜单并弹出提醒。
        /// 替代 IsEnabled=false——WPF 禁用的 MenuItem 不响应鼠标、ToolTip 不显示，用户点击无感知。
        /// </summary>
        private void AttachBlockedGuard(MenuItem mi, FrameworkElement hintHost, string? message = null)
        {
            mi.PreviewMouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_moduleMenu != null) _moduleMenu.IsOpen = false;
                ShowReadOnlyHint(hintHost, message);
            };
        }

        // ===== 工具方法 =====

        /// <summary>分享/团队卡片的标识边框：分享导入优先于团队归属；无标识时返回 null（用默认边框）</summary>
        private (Brush? brush, double thickness) ResolveSpecialBorder()
        {
            var s = AppSettings.Instance;
            if (!string.IsNullOrEmpty(Item.ShareId))
                return (TryParseBrush(s.ShareBorderColor) ?? GetBrush("BrushBorder"), 1.5);
            if (Item.OwnerType == "team")
                return (TryParseBrush(s.TeamBorderColor) ?? GetBrush("BrushBorder"), 1.5);
            return (null, 1);
        }

        private static Brush? TryParseBrush(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            try { return (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!; }
            catch { return null; }
        }

        private Brush GetBrush(string resourceKey)
        {
            try { return (Brush)FindResource(resourceKey); }
            catch { return Brushes.Gray; }
        }

        private Brush ApplyDashColor(string? customColor, string fallbackResourceKey)
        {
            if (!string.IsNullOrEmpty(customColor))
            {
                try { return (SolidColorBrush)new BrushConverter().ConvertFrom(customColor)!; }
                catch { }
            }
            return GetBrush(fallbackResourceKey);
        }
    }
}
