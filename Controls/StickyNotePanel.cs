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
                Left = left + width,
                Top = top,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.CanResize,
                Owner = owner
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
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 标题栏
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 编辑区
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 底部按钮

            // 标题栏：时间信息
            var headerPanel = new DockPanel { Margin = new Thickness(12, 8, 12, 6) };

            var titleText = new TextBlock
            {
                Text = "便签",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(titleText, Dock.Left);
            headerPanel.Children.Add(titleText);

            var timeText = new TextBlock
            {
                Text = _currentNote.CreatedAt.ToString("MM-dd HH:mm"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            headerPanel.Children.Add(timeText);

            Grid.SetRow(headerPanel, 0);
            grid.Children.Add(headerPanel);

            // 编辑区
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
            Grid.SetRow(_contentBox, 1);
            grid.Children.Add(_contentBox);

            // 底部按钮栏
            var footerPanel = new DockPanel
            {
                Margin = new Thickness(12, 6, 12, 10),
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

            Grid.SetRow(footerPanel, 2);
            grid.Children.Add(footerPanel);

            _dialog.Content = grid;
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
                _dialog.Left = left + width;
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
