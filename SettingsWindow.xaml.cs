using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SideDesk
{
    public partial class SettingsWindow : Window
    {
        private readonly App _app;
        private bool _isLoading = true;

        public SettingsWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            LoadCurrentSettings();
            _isLoading = false;

            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
        }

        private void LoadCurrentSettings()
        {
            // 主题模式
            themeModeCombo.Items.Add("深色");
            themeModeCombo.Items.Add("浅色");
            themeModeCombo.Items.Add("跟随系统");

            switch (_app.AppSettings.ThemeMode?.ToLower())
            {
                case "light": themeModeCombo.SelectedIndex = 1; break;
                case "system": themeModeCombo.SelectedIndex = 2; break;
                default: themeModeCombo.SelectedIndex = 0; break;
            }

            // 面板宽度
            widthBox.Text = _app.AppSettings.PanelWidth.ToString();

            // 停靠位置
            dockCombo.Items.Add("左侧");
            dockCombo.Items.Add("右侧");
            dockCombo.Items.Add("双侧");

            switch (_app.CurrentLayout)
            {
                case LayoutMode.Right: dockCombo.SelectedIndex = 1; break;
                case LayoutMode.Both: dockCombo.SelectedIndex = 2; break;
                default: dockCombo.SelectedIndex = 0; break;
            }
        }

        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (pageGeneral == null || pageAppearance == null || pageAbout == null) return;

            // 隐藏所有页面
            pageGeneral.Visibility = Visibility.Collapsed;
            pageAppearance.Visibility = Visibility.Collapsed;
            pageAbout.Visibility = Visibility.Collapsed;

            // 显示选中页面
            switch (navList.SelectedIndex)
            {
                case 0: pageGeneral.Visibility = Visibility.Visible; break;
                case 1: pageAppearance.Visibility = Visibility.Visible; break;
                case 2: pageAbout.Visibility = Visibility.Visible; break;
            }
        }

        private void ThemeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            string mode = themeModeCombo.SelectedIndex switch
            {
                1 => "Light",
                2 => "System",
                _ => "Dark"
            };

            _app.AppSettings.ThemeMode = mode;
            _app.AppSettings.Save();
            App.ApplyTheme(mode);
        }

        private void Width_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyWidth();
        }

        private void Width_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyWidth();
            }
        }

        private void ApplyWidth()
        {
            if (_isLoading) return;
            if (!int.TryParse(widthBox.Text, out int width) || width < 50 || width > 500)
            {
                widthBox.Text = _app.AppSettings.PanelWidth.ToString();
                return;
            }

            if (width != _app.AppSettings.PanelWidth)
            {
                _app.AppSettings.PanelWidth = width;
                _app.AppSettings.Save();
                _app.ApplyPanelWidth(width);
            }
        }

        private void Dock_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            LayoutMode newLayout = dockCombo.SelectedIndex switch
            {
                1 => LayoutMode.Right,
                2 => LayoutMode.Both,
                _ => LayoutMode.Left
            };

            if (newLayout != _app.CurrentLayout)
            {
                _app.AppSettings.Layout = newLayout;
                _app.AppSettings.Save();
                _app.SwitchLayout(newLayout);
            }
        }
    }
}
