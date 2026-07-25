using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SideDesk.Controls
{
    /// <summary>
    /// 带 emoji 插入按钮的输入框组件。
    /// 按钮内嵌于输入框右侧，点击弹出 emoji 网格，选中后插入到当前光标位置。
    /// </summary>
    public partial class EmojiTextBox : UserControl
    {
        /// <summary>双向绑定的文本内容。</summary>
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmojiTextBox),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextPropertyChanged));

        private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (EmojiTextBox)d;
            var newVal = (string)e.NewValue;
            if (self.textBox.Text != newVal)
                self.textBox.Text = newVal;
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>文本变化事件（供外部联动，例如图标预览）。</summary>
        public event TextChangedEventHandler? TextChanged;

        private Popup? _emojiPopup;

        /// <summary>Emoji.Wpf.TextBox 内部用于彩色渲染的 RichTextBox（模板部件 RichTextBox_INTERNAL）。</summary>
        private System.Windows.Controls.RichTextBox? _internalRtb;

        public EmojiTextBox()
        {
            InitializeComponent();
            textBox.Loaded += (s, e) => ThemeInternalRtb();
        }

        /// <summary>
        /// Emoji.Wpf.TextBox 自带模板只包含一个 RichTextBox_INTERNAL，且不做任何主题绑定，
        /// 内部 RichTextBox 默认是白底黑字。这里手动给它套上主题外观，
        /// 并预留右侧 34px 给 emoji 按钮，避免文字被按钮遮住。
        /// </summary>
        private void ThemeInternalRtb()
        {
            if (_internalRtb == null && textBox.Template != null)
                _internalRtb = textBox.Template.FindName("RichTextBox_INTERNAL", textBox) as System.Windows.Controls.RichTextBox;
            if (_internalRtb == null) return;

            _internalRtb.Background = Brushes.Transparent;
            _internalRtb.BorderThickness = new Thickness(0);
            _internalRtb.Foreground = (Brush)FindResource("BrushTextMain");
            _internalRtb.CaretBrush = (Brush)FindResource("BrushTextMain");
            // RichTextBox 是文档控件，不支持 VerticalContentAlignment，内容默认顶部对齐。
            // 这里按字体度量算出单行高，用顶部内边距把它推到垂直居中。
            double lineH = _internalRtb.FontFamily.LineSpacing * _internalRtb.FontSize;
            double innerH = wrapperBorder.Height - wrapperBorder.BorderThickness.Top - wrapperBorder.BorderThickness.Bottom;
            double vPad = Math.Max(0, (innerH - lineH) / 2);
            _internalRtb.Padding = new Thickness(6, vPad, 34, 0);
            _internalRtb.VerticalContentAlignment = VerticalAlignment.Center;

            // 防御：模板应用后若内部 RTB 为空、而外层已有初始文本，手动回填一次
            // （防止控件未做初始 outer→inner 同步导致编辑框不显示已有标题）
            var innerText = new TextRange(_internalRtb.Document.ContentStart, _internalRtb.Document.ContentEnd).Text.TrimEnd('\r', '\n');
            if (string.IsNullOrEmpty(innerText) && !string.IsNullOrEmpty(textBox.Text))
                new TextRange(_internalRtb.Document.ContentStart, _internalRtb.Document.ContentEnd).Text = textBox.Text;

            // 聚焦时外框高亮（模拟 GlobalTextBoxStyle 的聚焦效果）
            _internalRtb.GotFocus += (s, e) => wrapperBorder.BorderBrush = (Brush)FindResource("BrushAccent");
            _internalRtb.LostFocus += (s, e) => wrapperBorder.BorderBrush = (Brush)FindResource("BrushBorder");
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Text != textBox.Text)
                Text = textBox.Text;
            TextChanged?.Invoke(this, e);
        }

        private void EmojiBtn_Click(object sender, RoutedEventArgs e)
        {
            // 延迟构建弹层，确保此时控件已挂载到逻辑树（FindResource 可用）
            _emojiPopup ??= BuildEmojiPopup();
            _emojiPopup.IsOpen = !_emojiPopup.IsOpen;
        }

        /// <summary>emoji 分类数据。“全部”为所有分类去重拼接。</summary>
        private static readonly (string Name, string[] Emojis)[] EmojiCategories =
        {
            ("娱乐", new[]
            {
                "😀","😁","😂","🤣","😊","😍","🥰","😎","🤩","🥳",
                "😇","🤔","🤗","😴","🥺","😭","😤","😅","😉","😋",
                "😜","🤪","😝","🥲","😌","😏","😒","😞","😟","😠",
                "😡","🤯","😱","😨","😰","😥","😢","🤠","🤡","🤢",
                "🤮","🤧","😷","🤒","🤕","👻","💀","👽","🤖","💩",
                "🎉","🎊","🎮","🎯","🎲","🎳","🎵","🎶","🎨","🎬",
                "🎤","🎧","🎸","🎹","🎺","🎻","🥁","🎭","🎪","🎫",
                "❤️","🧡","💛","💚","💙","💜","🖤","🤍","💔","💕",
                "💖","💗","💘","💝","💞","💯","🔥","⭐","🌟","✨"
            }),
            ("办公", new[]
            {
                "💼","📁","📂","📅","📆","📇","📈","📉","📊","📋",
                "📌","📍","📎","📏","📐","✂️","✏️","📝","💡","🔔",
                "📢","📣","💻","🖥️","🖨️","⌨️","🖱️","💾","💿","📞",
                "📱","📲","📧","📩","📨","📯","📜","📃","📄","📑",
                "🗒️","🗓️","🗂️","🗃️","🗄️","🗑️","🔒","🔓","🔑","🔨",
                "🛠️","⚙️","🧰","✅","❌","⚠️","❗","❓","🚀","⏰",
                "⏱️","⏳","🏆","🥇","🥈","🥉","🏅","🎖️"
            }),
            ("生活", new[]
            {
                "☀️","🌙","⛅","🌤️","🌥️","🌦️","🌧️","⛈️","🌩️","🌨️",
                "❄️","☃️","🌈","🌊","💧","🌸","🌺","🌻","🌹","🌷",
                "🌼","🍀","🌿","🍁","🍂","🍃","🌴","🌵","🍎","🍊",
                "🍋","🍌","🍉","🍇","🍓","🍒","🍑","🥭","🍍","🥝",
                "🍅","🥑","🥦","🥕","🌽","🍞","🧀","🍳","🥓","🍔",
                "🍟","🍕","🌭","🥪","🌮","🌯","🥗","🍜","🍣","🍱",
                "🍲","🍛","🍚","🍩","🍪","🎂","🍰","🧁","🍫","🍿",
                "☕","🍵","🧃","🥤","🍺","🍷","🥂","🏠","🏡","🏢",
                "🏬","🏭","🏯","🏰","🚗","🚕","🚙","🚌","🏎️","✈️",
                "🚁","⛵","🚢","🐶","🐱","🐭","🐹","🐰","🦊","🐻",
                "🐼","🐨","🐯","🦁","🐮","🐷","🐸","🐵","🐔","🐧",
                "🐦","🐤","🦆","🦉","🦄","🐴","🐝","🦋","🐌","🐞"
            }),
            ("运动", new[]
            {
                "⚽","🏀","🏈","⚾","🎾","🏐","🏉","🎱","🏓","🏸",
                "🥊","🥋","⛳","🏹","🎣","🤿","🏊","🏄","🚣","🧗",
                "🚴","🚵","🤸","🤺","🤾","🏋️","🤼","🤽","⛷️","🏂",
                "🏆","🥇","🥈","🥉","🏅","🎖️","🏵️","🎽","🛹","🥌",
                "🛷","💪","🦵","🦶","👏","🙏","🤝","👍","✌️","🤞",
                "🤟","🤘","👊","✊","🖐️","🖖","👋","🤙"
            })
        };

        private Popup BuildEmojiPopup()
        {
            var popup = new Popup
            {
                PlacementTarget = wrapperBorder,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true,
                StaysOpen = false
            };

            // 右侧 emoji 网格（可滚动）
            var wrap = new WrapPanel { Width = 240 };
            var scroll = new ScrollViewer
            {
                Content = wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 285,
                // 覆盖式细滚动条：不占布局宽度，出现时不会挤压 emoji 网格
                Style = (Style)FindResource("OverlayScrollStyle")
            };

            // 分类：全部（去重） + 各分类
            var allEmojis = EmojiCategories.SelectMany(c => c.Emojis).Distinct().ToArray();
            var categories = new List<(string Name, string[] Emojis)> { ("全部", allEmojis) };
            categories.AddRange(EmojiCategories);

            // 左侧分类栏
            var catPanel = new StackPanel { Width = 52, Margin = new Thickness(0, 0, 8, 0) };
            var catItems = new List<(string Name, string[] Emojis, Border Bg, TextBlock Label)>();
            foreach (var cat in categories)
            {
                var label = new TextBlock
                {
                    Text = cat.Name,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)FindResource("BrushTextMain")
                };
                var bg = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4, 5, 4, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = label
                };
                var item = (cat.Name, cat.Emojis, bg, label);
                bg.MouseLeftButtonUp += (s, e) => Select(item.Name);
                catItems.Add(item);
                catPanel.Children.Add(bg);
            }

            void Select(string name)
            {
                var target = catItems.First(c => c.Name == name);
                wrap.Children.Clear();
                foreach (var em in target.Emojis)
                    wrap.Children.Add(BuildEmojiCell(em, popup));
                foreach (var c in catItems)
                {
                    bool sel = c.Name == name;
                    c.Bg.Background = sel ? (Brush)FindResource("BrushAccent") : Brushes.Transparent;
                    c.Label.Foreground = sel ? (Brush)FindResource("BrushAccentText") : (Brush)FindResource("BrushTextMain");
                }
            }

            Select("全部");

            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(catPanel);
            body.Children.Add(scroll);

            popup.Child = new Border
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = body
            };
            return popup;
        }

        private Button BuildEmojiCell(string em, Popup popup)
        {
            var cell = new Button
            {
                Content = new Emoji.Wpf.TextBlock { Text = em, FontSize = 16 },
                Width = 30, Height = 30,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            cell.Click += (s, e) =>
            {
                InsertAtCaret(em);
                popup.IsOpen = false;
            };
            return cell;
        }

        /// <summary>
        /// 在真实光标处插入 emoji。
        /// 实际编辑发生在内部 RichTextBox，外层 TextBox.CaretIndex 不可靠，
        /// 所以优先用内部 RTB 的 CaretPosition；拿不到时退化为追加到末尾。
        /// </summary>
        private void InsertAtCaret(string text)
        {
            ThemeInternalRtb();
            if (_internalRtb != null)
            {
                var caret = _internalRtb.CaretPosition;
                if (caret.Parent != null)
                    caret.InsertTextInRun(text);
                else
                    new TextRange(_internalRtb.Document.ContentEnd, _internalRtb.Document.ContentEnd).Text = text;
                _internalRtb.Focus();
            }
            else
            {
                // 退化路径：内部 RTB 不可用时仍用外层 Text（emoji 会黑白，但不丢功能）
                int idx = Math.Min(textBox.CaretIndex, textBox.Text.Length);
                textBox.Text = textBox.Text.Insert(idx, text);
                textBox.CaretIndex = idx + text.Length;
                textBox.Focus();
            }
        }
    }
}
