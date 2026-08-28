using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SideDesk.Controls
{
    /// <summary>
    /// 浮动上下文窗口：在主窗口内浮动显示输入框焦点、剪贴板复制等上下文内容。
    /// 本控件只负责内容渲染、精简模式与关闭通知；垂直拖拽/双击手势由宿主 MainWindow 处理。
    /// </summary>
    public partial class FloatingContextPanel : UserControl
    {
        /// <summary>完整模式下内容区最大高度</summary>
        private const double FullContentHeight = 120;

        /// <summary>精简模式下内容区高度（约 2 行文字）</summary>
        private const double CompactContentHeight = 36;

        /// <summary>关闭按钮点击时触发</summary>
        public event Action? CloseRequested;

        /// <summary>点击“问AI”按钮时触发（携带当前内容文本）</summary>
        public event Action<string>? AskAiRequested;

        /// <summary>当前显示的内容文本</summary>
        public string CurrentContent => contentText.Text ?? "";

        /// <summary>是否处于 2 行精简模式</summary>
        public bool IsCompact { get; private set; }

        public FloatingContextPanel()
        {
            InitializeComponent();
        }

        /// <summary>显示指定上下文内容并置为可见（滚动位置回到顶部）</summary>
        public void Show(string title, string content)
        {
            titleText.Text = title;
            contentText.Text = content;
            contentScroll.ScrollToTop();
            Visibility = Visibility.Visible;
        }

        /// <summary>隐藏面板</summary>
        public void HidePanel() => Visibility = Visibility.Collapsed;

        /// <summary>切换完整/精简（2 行）模式；精简模式下隐藏 action 按钮组</summary>
        public void SetCompact(bool compact)
        {
            IsCompact = compact;
            contentScroll.MaxHeight = compact ? CompactContentHeight : FullContentHeight;
            actionsPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        /// <summary>滚动条已隐藏，鼠标悬停时直接用滚轮滚动内容</summary>
        private void ContentScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (contentScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible) return;
            contentScroll.ScrollToVerticalOffset(contentScroll.VerticalOffset - e.Delta / 120.0 * 16);
            e.Handled = true;
        }

        private void AskAiBtn_Click(object sender, RoutedEventArgs e) => AskAiRequested?.Invoke(CurrentContent);
    }
}
