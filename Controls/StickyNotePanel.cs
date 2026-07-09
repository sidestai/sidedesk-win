using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;

namespace SideDesk.Controls
{
    /// <summary>
    /// 便签编辑面板 - 右侧浮出式窗口
    /// </summary>
    public class StickyNotePanel
    {
        private Window _dialog = null;
        private TextBox _contentBox = null;
        private StickyNote _currentNote = null;
        private string _originalContent = null;
        private Window _owner = null;
        private Action _onSaved = null;

        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        public bool IsOpen => _dialog != null && _dialog.IsLoaded;

        /// <summary>
        /// 打开指定便签的编辑面板
        /// </summary>
        /// <param name="onSaved">保存后的回调（用于刷新侧边栏列表）</param>
        public void Show(Window owner, double left, double top, double width, double height,
            StickyNote note, Action onSaved = null)
        {
            _onSaved = onSaved;

            // 如果已打开且是同一条便签，仅激活
            if (IsOpen && _currentNote?.Id == note.Id)
            {
                _dialog.Activate();
                return;
            }

            // 如果打开的是不同便签，先保存旧的再关闭
            if (IsOpen)
                SaveAndClose();

            _owner = owner;
            _currentNote = note;
            _originalContent = note.Content;

            _dialog = new Window
            {
                Title = "便签",
                Width = 420,
                Height = height,
                Left = left,  // left 已经是 sidebar 右侧边缘的 X 坐标，直接使用
                Top = top,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.NoResize,  // 改为不可调整大小
                WindowStyle = WindowStyle.None,     // 移除标题栏
                AllowsTransparency = true,          // 允许透明
                Background = Brushes.Transparent,   // 透明背景
                Owner = owner,
                ShowInTaskbar = false               // 不显示在任务栏
            };
            _dialog.Loaded += (s, e) => HideFromTaskbar(_dialog);
            _dialog.SetBinding(Window.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { Source = owner });

            _dialog.Closing += (s, e) => SaveIfChanged();

            _dialog.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    SaveAndClose();
                }
                else if (e.Key == Key.S &&
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    SaveIfChanged();
                    e.Handled = true;
                }
            };

            BuildUI();

            _dialog.Show();
            _contentBox?.Focus();
        }

        private void BuildUI()
        {
            // 创建带圆角和阴影的外层容器
            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30)), // 深灰色背景
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 0,
                    ShadowDepth = 0,
                    BlurRadius = 20,
                    Opacity = 0.5
                },
                Margin = new Thickness(10) // 为阴影留出空间
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题栏
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容区

            outerBorder.Child = mainGrid;

            // === 标题栏区域 ===
            var titleBar = new DockPanel();
            titleBar.Background = new SolidColorBrush(Color.FromArgb(255, 40, 40, 40));
            titleBar.Margin = new Thickness(0);
            Grid.SetRow(titleBar, 0);

            // 先添加时间信息（左侧）
            var timeText = new TextBlock
            {
                Text = _currentNote.CreatedAt.ToString("MM-dd HH:mm"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            DockPanel.SetDock(timeText, Dock.Left);
            titleBar.Children.Add(timeText);

            // 标题文本（中间，自动填充）
            var titleLabel = new TextBlock
            {
                Text = "便签",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 8, 0, 8)
            };
            DockPanel.SetDock(titleLabel, Dock.Left);
            titleBar.Children.Add(titleLabel);

            // ✅ 最后添加关闭按钮（右侧停靠）
            var closeButton = new Button
            {
                Content = "✕",
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                FontSize = 16,
                Cursor = Cursors.Hand,
                Padding = new Thickness(0)
            };
            // 自定义模板：去掉默认按钮外观，但保证 Content(✕) 正常渲染，
            // 并让 Background 绑定生效以支持悬停变色（Template=null 会导致按钮完全不显示）
            var closeTemplate = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            presenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(presenterFactory);
            closeTemplate.VisualTree = borderFactory;
            closeButton.Style = null;
            closeButton.Template = closeTemplate;
            closeButton.Click += (s, e) => SaveAndClose();

            // 鼠标悬停效果
            closeButton.MouseEnter += (s, e) => 
            {
                closeButton.Background = new SolidColorBrush(Color.FromArgb(255, 255, 50, 50));
            };
            closeButton.MouseLeave += (s, e) => 
            {
                closeButton.Background = Brushes.Transparent;
            };

            DockPanel.SetDock(closeButton, Dock.Right); // ✅ 关键：停靠到右侧
            titleBar.Children.Add(closeButton);

            // 使标题栏可拖动窗口
            titleBar.MouseLeftButtonDown += (s, e) => 
            {
                if (e.ClickCount == 2) return; // 双击不处理
                _dialog.DragMove();
            };

            mainGrid.Children.Add(titleBar);

            // === 内容区域 ===
            var contentPanel = new Grid();
            contentPanel.Margin = new Thickness(12);
            Grid.SetRow(contentPanel, 1);

            // 编辑区（TextBox）
            _contentBox = new TextBox
            {
                Text = _currentNote.Content,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12),
                FontSize = 14,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            contentPanel.Children.Add(_contentBox);

            // 底部按钮栏
            var footerPanel = new DockPanel
            {
                Margin = new Thickness(0, 8, 0, 0),
                LastChildFill = false
            };

            var saveBtn = new Button
            {
                Content = "保存",
                Width = 70,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0)
            };
            saveBtn.Click += (s, e) =>
            {
                SaveIfChanged();
            };
            DockPanel.SetDock(saveBtn, Dock.Left);
            footerPanel.Children.Add(saveBtn);

            var copyBtn = new Button
            {
                Content = "复制全文",
                Width = 70,
                Height = 28
            };
            copyBtn.Click += (s, e) =>
            {
                try { Clipboard.SetText(_contentBox.Text); }
                catch { }
            };
            DockPanel.SetDock(copyBtn, Dock.Left);
            footerPanel.Children.Add(copyBtn);

            contentPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(footerPanel, 1);
            contentPanel.Children.Add(footerPanel);

            mainGrid.Children.Add(contentPanel);
            _dialog.Content = outerBorder;
        }

        private void SaveIfChanged()
        {
            if (_currentNote == null || _contentBox == null) return;
            string newContent = _contentBox.Text;
            if (newContent == _originalContent) return;

            _currentNote.Content = newContent;
            StickyNoteManager.Update(_currentNote);
            _originalContent = newContent;

            _onSaved?.Invoke();
        }

        private void SaveAndClose()
        {
            SaveIfChanged();
            if (IsOpen)
            {
                try { _dialog.Close(); } catch { }
            }
            _dialog = null;
            _currentNote = null;
        }

        public void SyncPosition(double left, double top, double width, double height)
        {
            if (IsOpen)
            {
                _dialog.Left = left;  // left 已经是 sidebar 右侧边缘的 X 坐标，直接使用
                _dialog.Top = top;
                _dialog.Height = height;
            }
        }

        public void Activate()
        {
            if (IsOpen)
                _dialog.Activate();
        }

        public void Close()
        {
            SaveAndClose();
        }

        private void HideFromTaskbar(Window window)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var taskbarList = (ITaskbarList)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090")));
                taskbarList.HrInit();
                taskbarList.DeleteTab(hwnd);
                Marshal.ReleaseComObject(taskbarList);
            }
            catch { }
        }
    }
}
