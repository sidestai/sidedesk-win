using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Framework;
using SideDesk.Theming;

namespace SideDesk
{
    /// <summary>
    /// 主题编辑器 - 3-Tab（颜色/字体/布局）+ 主题选择器
    /// </summary>
    public partial class ThemeEditor : Window
    {
        private bool _suppressThemeChange = false;

        public ThemeEditor()
        {
            InitializeComponent();

            LoadThemeList();
            LoadFontFamilies();
            LoadFontSizes();
        }

        public ThemeEditor(Window owner) : this()
        {
            if (owner != null)
            {
                Owner = owner;
            }
        }

        /// <summary>
        /// 加载可用主题列表到 ComboBox
        /// </summary>
        private void LoadThemeList()
        {
            _suppressThemeChange = true;

            var themes = ThemeManager.GetAvailableThemes().ToList();
            themes.Insert(0, "(自定义)");

            ThemeComboBox.ItemsSource = themes;

            string current = AppSettings.Instance.CurrentTheme;
            if (!string.IsNullOrEmpty(current) && themes.Contains(current))
            {
                ThemeComboBox.SelectedItem = current;
            }
            else
            {
                ThemeComboBox.SelectedIndex = 0;
            }

            _suppressThemeChange = false;
        }

        /// <summary>
        /// 加载常用字体列表
        /// </summary>
        private void LoadFontFamilies()
        {
            var fonts = new List<string>
            {
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimSun",
                "SimHei",
                "KaiTi",
                "Consolas",
                "Segoe UI",
                "Arial",
                "Verdana",
                "Courier New"
            };

            // 追加系统字体中未包含的
            foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
            {
                if (!fonts.Contains(family.Source))
                {
                    fonts.Add(family.Source);
                }
            }

            FontFamilyComboBox.ItemsSource = fonts;

            string currentFont = AppSettings.Instance.FontFamily;
            if (fonts.Contains(currentFont))
            {
                FontFamilyComboBox.SelectedItem = currentFont;
            }
            else
            {
                FontFamilyComboBox.Text = currentFont;
            }
        }

        /// <summary>
        /// 加载字号选项
        /// </summary>
        private void LoadFontSizes()
        {
            var sizes = new[] { 10, 12, 14, 16, 18 };
            FontSizeComboBox.ItemsSource = sizes;

            var currentSetting = AppSettings.Instance.FontSetting;
            if (currentSetting != null)
            {
                int currentSize = currentSetting.FontSize;
                if (sizes.Contains(currentSize))
                {
                    FontSizeComboBox.SelectedItem = currentSize;
                }
            }
            else
            {
                FontSizeComboBox.SelectedItem = 14;
            }
        }

        /// <summary>
        /// 切换主题
        /// </summary>
        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressThemeChange) return;

            string selected = ThemeComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || selected == "(自定义)") return;

            try
            {
                ThemeManager.ApplyByName(selected);
                // 同步字号 ComboBox
                var setting = AppSettings.Instance.FontSetting;
                if (setting != null)
                {
                    FontSizeComboBox.SelectedItem = setting.FontSize;
                }
                // 同步字体 ComboBox
                FontFamilyComboBox.Text = AppSettings.Instance.FontFamily;
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载主题失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 字号更改
        /// </summary>
        private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontSizeComboBox.SelectedItem == null) return;

            int size = (int)FontSizeComboBox.SelectedItem;
            AppSettings.Instance.FontSetting = GetFontSetting(size);
        }

        /// <summary>
        /// 保存当前设置
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppSettings.Instance.Save();
                // 如果当前选了主题，也保存主题文件
                string current = AppSettings.Instance.CurrentTheme;
                if (!string.IsNullOrEmpty(current))
                {
                    var theme = ThemeManager.CreateFromCurrentSettings();
                    ThemeManager.Save(current, theme);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Close();
        }

        /// <summary>
        /// 另存为新主题
        /// </summary>
        private void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            string name = ShowInputDialog("请输入新主题名称：", "另存主题", "MyTheme");
            if (string.IsNullOrWhiteSpace(name)) return;

            // 清理非法字符
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            try
            {
                var theme = ThemeManager.CreateFromCurrentSettings();
                theme.Name = name;
                ThemeManager.Save(name, theme);
                AppSettings.Instance.CurrentTheme = name;
                AppSettings.Instance.Save();

                // 刷新列表
                LoadThemeList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存主题失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static FontSetting GetFontSetting(int size)
        {
            if (size <= 10) return FontSetting.x10;
            if (size <= 12) return FontSetting.x12;
            if (size <= 14) return FontSetting.x14;
            if (size <= 16) return FontSetting.x16;
            return FontSetting.x18;
        }

        /// <summary>
        /// 简易输入对话框
        /// </summary>
        private string ShowInputDialog(string prompt, string title, string defaultValue)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 350,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(15) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });

            var textBox = new TextBox { Text = defaultValue };
            panel.Children.Add(textBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "取消", Width = 70, IsCancel = true };

            string result = null;
            okBtn.Click += (s, ev) => { result = textBox.Text; dlg.Close(); };
            cancelBtn.Click += (s, ev) => { dlg.Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;
            dlg.ShowDialog();
            return result;
        }
    }
}
