using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SideDesk.Models;

namespace SideDesk
{
    public partial class DigitalEmployeeWindow : Window
    {
        private readonly DigitalEmployeeManager _manager;
        private DigitalEmployee? _current;
        private bool _isLoading;

        /// <summary>窗口关闭后，若员工库有变更可通过该回调通知调用方刷新卡片</summary>
        public Action? OnChanged { get; set; }

        public DigitalEmployeeWindow(DigitalEmployeeManager manager)
        {
            InitializeComponent();
            _manager = manager;
            RefreshList();
            if (_manager.Employees.Count > 0)
                employeeList.SelectedIndex = 0;

            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
        }

        private void RefreshList()
        {
            var keepId = _current?.Id;
            employeeList.ItemsSource = null;
            employeeList.ItemsSource = _manager.Employees;
            if (keepId != null)
            {
                var match = _manager.Employees.FirstOrDefault(emp => emp.Id == keepId);
                if (match != null) employeeList.SelectedItem = match;
            }
        }

        private void EmployeeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _current = employeeList.SelectedItem as DigitalEmployee;
            LoadDetail();
        }

        private void LoadDetail()
        {
            if (_current == null)
            {
                detailScroll.Visibility = Visibility.Collapsed;
                emptyHint.Visibility = Visibility.Visible;
                return;
            }

            _isLoading = true;
            detailScroll.Visibility = Visibility.Visible;
            emptyHint.Visibility = Visibility.Collapsed;

            detailTitle.Text = _current.Name;
            nameBox.Text = _current.Name;
            emojiBox.Text = _current.Emoji;
            roleBox.Text = _current.Role;
            departmentBox.Text = _current.Department;
            skillsBox.Text = _current.Skills;
            contactBox.Text = _current.Contact;
            descriptionBox.Text = _current.Description;

            // 状态下拉框
            var statusIndex = _current.Status switch { "online" => 0, "busy" => 1, "offline" => 2, _ => 0 };
            statusCombo.SelectedIndex = statusIndex;

            UpdateAvatarPreview();
            _isLoading = false;
        }

        private void UpdateAvatarPreview()
        {
            avatarFallbackText.Text = _current?.AvatarFallback ?? "?";
            if (_current != null && !string.IsNullOrWhiteSpace(_current.AvatarPath) && File.Exists(_current.AvatarPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(_current.AvatarPath);
                    bmp.EndInit();
                    avatarImageBrush.ImageSource = bmp;
                    avatarImageHost.Visibility = Visibility.Visible;
                    avatarFallbackText.Visibility = Visibility.Collapsed;
                    return;
                }
                catch { }
            }
            avatarImageBrush.ImageSource = null;
            avatarImageHost.Visibility = Visibility.Collapsed;
            avatarFallbackText.Visibility = Visibility.Visible;
        }

        private void Field_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isLoading || _current == null) return;
            _current.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Framework.Lang.T("digitalEmployee.untitled") : nameBox.Text.Trim();
            _current.Emoji = emojiBox.Text.Trim();
            _current.Role = roleBox.Text.Trim();
            _current.Department = departmentBox.Text.Trim();
            _current.Skills = skillsBox.Text.Trim();
            _current.Contact = contactBox.Text.Trim();
            _current.Description = descriptionBox.Text;

            detailTitle.Text = _current.Name;
            _manager.Save();
            OnChanged?.Invoke();

            // 刷新左侧列表显示（姓名/角色/头像文字）
            RefreshList();
            if (string.IsNullOrWhiteSpace(_current.AvatarPath))
                avatarFallbackText.Text = _current.AvatarFallback;
        }

        private void AddEmployee_Click(object sender, RoutedEventArgs e)
        {
            var emp = _manager.Add(Framework.Lang.T("digitalEmployee.newEmployee"));
            OnChanged?.Invoke();
            RefreshList();
            employeeList.SelectedItem = emp;
            nameBox.Focus();
            nameBox.SelectAll();
        }

        private void DeleteEmployee_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (MessageBox.Show(Framework.Lang.TF("digitalEmployee.deleteEmployee", _current.Name), Framework.Lang.T("cloudDrive.confirmDeletion"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            _manager.Remove(_current.Id);
            _current = null;
            OnChanged?.Invoke();
            RefreshList();
            if (_manager.Employees.Count > 0)
                employeeList.SelectedIndex = 0;
            else
                LoadDetail();
        }

        private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var dlg = new OpenFileDialog
            {
                Title = Framework.Lang.T("settings.chooseAvatarImage"),
                Filter = Framework.Lang.T("digitalEmployee.avatarImageFilter")
            };
            if (dlg.ShowDialog(this) == true)
            {
                _current.AvatarPath = dlg.FileName;
                _manager.Save();
                UpdateAvatarPreview();
                OnChanged?.Invoke();
            }
        }

        private void ClearAvatar_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _current.AvatarPath = "";
            _manager.Save();
            UpdateAvatarPreview();
            OnChanged?.Invoke();
        }

        private void StatusCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || _current == null) return;
            if (statusCombo.SelectedItem is ComboBoxItem item && item.Tag is string status)
            {
                _current.Status = status;
                _manager.Save();
                OnChanged?.Invoke();
            }
        }
    }
}
