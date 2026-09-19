using System.Windows;

namespace SideDesk.Windows;

/// <summary>
/// 微信客服二维码弹窗：展示 exe 内嵌客服二维码（入口为 设置→关于→联系客服，仅中文语言显示）
/// </summary>
public partial class WechatQrDialog : Window
{
    public WechatQrDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
