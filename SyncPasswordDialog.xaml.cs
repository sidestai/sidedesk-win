using System.Windows;

namespace SideDesk
{
    public partial class SyncPasswordDialog : Window
    {
        /// <summary>用户设置的同步加密密码（与登录密码无关；调用方用于派生密钥后应立即清空引用）</summary>
        public string Password { get; private set; } = "";

        public SyncPasswordDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // 密码以明文直接显示，用户可即时核对，故不再需要二次确认输入
            string pwd = passwordText.Text;

            if (string.IsNullOrWhiteSpace(pwd))
            {
                ThemedMessageBox.Show(this, Framework.Lang.T("misc.pleaseEnterThePassword"), Framework.Lang.T("common.notice"));
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
