using System.Linq;
using System.Windows;
using SideDesk.Theming;

namespace SideDesk
{
    public partial class SettingsWindow : Window
    {
        private readonly App _app;

        public SettingsWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            // 面板宽度
            widthBox.Text = _app.AppSettings.PanelWidth.ToString();

            // 停靠位置
            switch (_app.CurrentLayout)
            {
                case LayoutMode.Left:
                    dockLeftRadio.IsChecked = true;
                    break;
                case LayoutMode.Right:
                    dockRightRadio.IsChecked = true;
                    break;
                case LayoutMode.Both:
                    dockBothRadio.IsChecked = true;
                    break;
            }

            // 主题列表
            var themes = ThemeManager.GetAvailableThemes().ToList();
            themeCombo.ItemsSource = themes;

            string currentTheme = _app.AppSettings.ThemeName;
            if (!string.IsNullOrEmpty(currentTheme) && themes.Contains(currentTheme))
            {
                themeCombo.SelectedItem = currentTheme;
            }
            else if (themes.Count > 0)
            {
                themeCombo.SelectedIndex = 0;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // 验证宽度
            if (!int.TryParse(widthBox.Text, out int width) || width < 50 || width > 500)
            {
                MessageBox.Show("面板宽度必须在 50~500 之间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存面板宽度
            _app.AppSettings.PanelWidth = width;

            // 保存停靠位置
            LayoutMode newLayout;
            if (dockRightRadio.IsChecked == true)
                newLayout = LayoutMode.Right;
            else if (dockBothRadio.IsChecked == true)
                newLayout = LayoutMode.Both;
            else
                newLayout = LayoutMode.Left;

            // 保存主题
            string selectedTheme = themeCombo.SelectedItem as string ?? "";
            _app.AppSettings.ThemeName = selectedTheme;

            // 持久化
            _app.AppSettings.Save();

            // 应用主题
            if (!string.IsNullOrEmpty(selectedTheme))
            {
                try
                {
                    ThemeManager.ApplyByName(selectedTheme);
                }
                catch { /* 主题加载失败忽略 */ }
            }

            // 应用布局变更（会重新注册 AppBar）
            if (newLayout != _app.CurrentLayout)
            {
                _app.SwitchLayout(newLayout);
            }

            // 如果宽度变了，提示重启生效
            if (width != 105) // 对比初始默认值不够准确，这里只提示
            {
                // 宽度变更需要重新注册 AppBar，通过 SwitchLayout 即可触发
                if (newLayout == _app.CurrentLayout)
                {
                    // 布局没变但宽度变了，需要重新应用
                    _app.ApplyPanelWidth(width);
                }
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
