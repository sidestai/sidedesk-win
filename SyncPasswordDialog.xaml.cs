using System.Windows;

namespace SideDesk
{
    public partial class SyncPasswordDialog : Window
    {
        /// <summary>用户输入的密码（调用方用于派生密钥后应立即清空引用）</summary>
        public string Password { get; private set; } = "";

        public SyncPasswordDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string pwd = passwordBox.Password;
            string confirm = confirmBox.Password;

            if (string.IsNullOrEmpty(pwd))
            {
                ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("misc.pleaseEnterThePassword")), Framework.Lang.T("common.notice"));
                return;
            }

            if (pwd != confirm)
            {
                ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("common.passwordMismatch")), Framework.Lang.T("common.notice"));
                return;
            }

            Password = pwd;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
