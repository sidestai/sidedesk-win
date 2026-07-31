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
    /// 模块基础组件：所有面板模块（含插件）的统一基类。
    /// 提供标准外框、标题栏、右上角菜单（模块设置/隐藏外框/置顶/删除）、
    /// 编辑模式拖拽手柄、内容区可调高等通用能力。
    /// 子类/调用方仅需关注内容逻辑，通过 Content 属性注入即可。
    /// </summary>
    public class ModuleControl : ContentControl
    {
        // ===== 常量 =====
        public const string DragHandleTag = "panelDragHandle";
        public const string HeaderUid = "panelHeader";
        private const double MinContentHeight = 48;

        // ===== 属性 =====
        public PanelItem Item { get; }
        public string DefaultTitle { get; set; }
        public bool IsEditMode { get; set; }
        public WindowConfigManager? ConfigManager { get; set; }
        /// <summary>背景色覆盖（优先于 Item.BGColor，用于 Group 等需要特殊背景色的模块）</summary>
        public string? BGColorOverride { get; set; }

        // ===== 回调（由 NativeModePanel 注入）=====
        /// <summary>请求重新渲染所有面板</summary>
        public Action? OnRenderPanels { get; set; }
        /// <summary>请求删除当前模块</summary>
        public Action<PanelItem>? OnRemovePanel { get; set; }
        /// <summary>请求打开模块设置对话框</summary>
        public Action<PanelItem, string>? OnShowSettings { get; set; }
        /// <summary>请求重命名模块</summary>
        public Action<PanelItem, string>? OnRenamePanel { get; set; }

        // ===== 内部 UI 引用 =====
        private Border _rootBorder = null!;
        private StackPanel _mainStack = null!;
        private DockPanel _header = null!;
        private Button _menuBtn = null!;
        private Emoji.Wpf.TextBlock _titleBlock = null!;
        private ScrollViewer? _contentHost;
        private Border? _resizeGrip;
        private ContextMenu? _moduleMenu;
        private readonly bool _groupMode;

        // 自定义菜单项（在标准菜单项之后、分隔线之前插入）
        private readonly List<UIElement> _customMenuItems = new();

        // 内容区（Content 属性存储在此）
        private UIElement? _moduleContent;

        /// <summary>模块高度拖拽进行中标志</summary>
        private bool _resizingModule;

        public ModuleControl(PanelItem item, string defaultTitle, bool isEditMode, WindowConfigManager? configManager, bool groupMode = false)
        {
            Item = item;
            DefaultTitle = defaultTitle;
            IsEditMode = isEditMode;
            ConfigManager = configManager;
            _groupMode = groupMode;

            // 标记为已处理，避免被 NativeModePanel.WrapResizable 二次包装
            Uid = "wrapHandled";

            BuildVisualTree();
        }

        /// <summary>设置模块内容（替代直接设置 Content，确保内容放入正确的容器）</summary>
        public void SetContent(UIElement content)
        {
            _moduleContent = content;
            if (_groupMode)
            {
                // Group 模式：内容直接放入主容器（无 ScrollViewer）
                _mainStack.Children.Add(content);
                return;
            }
            _contentHost!.Content = content;
            // 固定高度时让内容至少填满可视区域
            if (Item.PanelHeight.HasValue && Item.PanelHeight.Value > 0 && content is FrameworkElement cfe)
            {
                cfe.SetBinding(FrameworkElement.MinHeightProperty,
                    new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = _contentHost });
            }
        }

        /// <summary>添加自定义菜单项（插入到标准菜单项“模块设置”之后）</summary>
        public void AddCustomMenuItem(UIElement menuItem)
        {
            _customMenuItems.Add(menuItem);
            // 直接插入到已有菜单中（位置：模块设置之后、分隔线/标准项之前）
            if (_moduleMenu != null)
            {
                int insertIndex = 1 + _customMenuItems.Count - 1; // 1(模块设置) + 已有自定义项数 - 1
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

        /// <summary>获取显示标题（置顶模块自动添加 📌 前缀）</summary>
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

        /// <summary>设置整个模块的右键菜单（Group 模式使用自定义菜单替代标准菜单）</summary>
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
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GetBrush("BrushTextMain")
            };

            if (IsEditMode)
            {
                _titleBlock.Cursor = Cursors.Hand;
                _titleBlock.ToolTip = "双击编辑名称";
                _titleBlock.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
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

            // 内容区（Group 模式不使用 ScrollViewer，高度由子内容撑开）
            if (!_groupMode)
            {
                _contentHost = CreateContentHost();
            }

            // 主容器
            _mainStack = new StackPanel();
            _mainStack.Children.Add(_header);
            if (_contentHost != null)
                _mainStack.Children.Add(_contentHost);

            // 编辑模式：添加 resize 手柄（Group 模式不需要）
            if (IsEditMode && !_groupMode)
            {
                _resizeGrip = CreateResizeGrip();
                _mainStack.Children.Add(_resizeGrip);
            }

            // 外框
            _rootBorder = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = ApplyDashColor(BGColorOverride ?? Item.BGColor, "BrushInputBG"),
                BorderBrush = GetBrush("BrushBorder"),
                BorderThickness = new Thickness(1),
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
                _contentHost = CreateContentHost();

            var bareStack = new StackPanel();
            if (_contentHost != null)
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

        /// <summary>创建底部拖拽手柄（编辑模式下调整模块高度）</summary>
        private Border CreateResizeGrip()
        {
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
                    _contentHost!.ClearValue(FrameworkElement.HeightProperty);
                    if (_contentHost.Content is FrameworkElement contentFe)
                        BindingOperations.ClearBinding(contentFe, FrameworkElement.MinHeightProperty);
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
                startHeight = (double.IsNaN(_contentHost!.Height) || _contentHost.Height <= 0)
                    ? _contentHost.ActualHeight : _contentHost.Height;
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
                _contentHost!.Height = h;
            };

            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                _resizingModule = false;
                grip.ReleaseMouseCapture();
                if (moved)
                {
                    Item.PanelHeight = _contentHost!.Height;
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

            // 模块设置
            var settingsMi = new MenuItem { Header = "模块设置" };
            settingsMi.Click += (s, e) => OnShowSettings?.Invoke(Item, DefaultTitle);
            _moduleMenu.Items.Add(settingsMi);

            // 自定义菜单项
            foreach (var custom in _customMenuItems)
            {
                _moduleMenu.Items.Add(custom);
            }

            // 分隔线（有自定义项时才显示）
            if (_customMenuItems.Count > 0)
            {
                _moduleMenu.Items.Add(new Separator());
            }

            // 隐藏模块外框
            var hideFrameMi = new MenuItem
            {
                Header = "隐藏模块外框",
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
                Header = "置顶显示",
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

            // 删除模块
            var deleteMi = new MenuItem { Header = "删除模块" };
            deleteMi.Click += (s, e) => OnRemovePanel?.Invoke(Item);
            _moduleMenu.Items.Add(deleteMi);

            // 隐藏外框模式下，内容区也挂菜单（确保右键能弹出）
            if (_moduleContent is FrameworkElement contentFe && contentFe.ContextMenu == null)
            {
                contentFe.ContextMenu = _moduleMenu;
            }
        }

        // ===== 工具方法 =====

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
