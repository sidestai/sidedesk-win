using System.Windows.Controls;
using System.Windows.Input;

namespace SideDesk.Framework
{
    /// <summary>
    /// 为 TextBox 启用"三连击选中整行"行为（对齐浏览器 textarea 的三连击选段习惯）。
    /// WPF TextBox 原生只有双击选词；三连击时按点击位置选中所在逻辑行，
    /// 并捕获鼠标防止按住拖动破坏已选中的整行。
    /// </summary>
    public static class TripleClickSelect
    {
        public static void Enable(TextBox tb)
        {
            bool suppressDrag = false;

            tb.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 3) return;

                int charIndex = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), true);
                if (charIndex < 0) return;

                int lineIndex = tb.GetLineIndexFromCharacterIndex(charIndex);
                if (lineIndex < 0) return;

                int lineStart = tb.GetCharacterIndexFromLineIndex(lineIndex);
                int lineLen = tb.GetLineLength(lineIndex);

                tb.Select(lineStart, lineLen);
                tb.Focus();
                e.Handled = true;

                // 三连击后按住拖动会收缩选区：捕获鼠标直到抬起，保持整行选中
                suppressDrag = true;
                tb.CaptureMouse();
            };

            tb.PreviewMouseMove += (s, e) =>
            {
                if (suppressDrag && e.LeftButton == MouseButtonState.Pressed)
                    e.Handled = true;
            };

            tb.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!suppressDrag) return;
                suppressDrag = false;
                if (tb.IsMouseCaptured) tb.ReleaseMouseCapture();
            };
        }
    }
}
