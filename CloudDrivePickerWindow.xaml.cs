using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>树节点：包装 CloudDriveFile，附加勾选/展开状态，文件夹懒加载子项</summary>
    public class PickerTreeNode : INotifyPropertyChanged
    {
        public CloudDriveFile File { get; }
        public bool IsDummy { get; }
        public bool IsFolder => !IsDummy && File.IsFolder;
        public ObservableCollection<PickerTreeNode> Children { get; } = new();

        // ===== 显示用包装属性（避免 dummy 节点空引用）=====
        public string DisplayName => IsDummy ? "正在加载..." : File.Name;
        public string Icon => IsDummy ? "" : File.TypeIcon;
        public string SizeText => IsDummy ? "" : File.SizeText;

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPC();
                CheckChanged?.Invoke(this);
            }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPC();
                if (value) ExpandRequested?.Invoke(this);
            }
        }

        /// <summary>是否仍持有占位子节点（尚未加载真实子项）</summary>
        public bool HasDummyChild => Children.Count == 1 && Children[0].IsDummy;

        /// <summary>请求展开（由窗口订阅以触发懒加载）</summary>
        public event Action<PickerTreeNode>? ExpandRequested;
        /// <summary>勾选状态变化（由窗口订阅以刷新计数）</summary>
        public event Action<PickerTreeNode>? CheckChanged;

        public PickerTreeNode(CloudDriveFile file, bool isDummy = false)
        {
            File = file;
            IsDummy = isDummy;
            // 文件夹预置一个占位子节点，使展开箭头可见
            if (!isDummy && file.IsFolder) AddDummy();
        }

        public void AddDummy() => Children.Add(new PickerTreeNode(null!, isDummy: true));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>云盘文件树形多选浏览器：勾选文件/目录后返回选中项</summary>
    public partial class CloudDrivePickerWindow : Window
    {
        private readonly ObservableCollection<PickerTreeNode> _roots = new();

        /// <summary>确认选择的结果（未确认为 null）</summary>
        public List<CloudDriveFile>? SelectedFiles { get; private set; }

        public CloudDrivePickerWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
            treeView.ItemsSource = _roots;
            Loaded += async (_, _) => await LoadRootAsync();
        }

        /// <summary>加载根目录</summary>
        private async System.Threading.Tasks.Task LoadRootAsync()
        {
            ShowState(loadingText);
            var files = await CloudDriveService.Instance.ListFilesAsync("0");
            if (files == null)
            {
                ShowState(errorText);
                return;
            }

            _roots.Clear();
            foreach (var f in files) _roots.Add(CreateNode(f));

            ShowState(_roots.Count == 0 ? emptyText : treeView);
            UpdateSelectedCount();
        }

        /// <summary>创建节点并挂接懒加载/勾选事件</summary>
        private PickerTreeNode CreateNode(CloudDriveFile file)
        {
            var node = new PickerTreeNode(file);
            node.ExpandRequested += OnExpandRequested;
            node.CheckChanged += _ => UpdateSelectedCount();
            return node;
        }

        /// <summary>文件夹首次展开时懒加载子项</summary>
        private async void OnExpandRequested(PickerTreeNode node)
        {
            if (!node.HasDummyChild) return; // 已加载过

            var files = await CloudDriveService.Instance.ListFilesAsync(node.File.Id);
            node.Children.Clear();
            if (files == null)
            {
                node.AddDummy(); // 加载失败：保留占位，折叠后再展开可重试
                return;
            }
            foreach (var f in files) node.Children.Add(CreateNode(f));
        }

        private void ShowState(UIElement target)
        {
            loadingText.Visibility = ReferenceEquals(target, loadingText) ? Visibility.Visible : Visibility.Collapsed;
            emptyText.Visibility = ReferenceEquals(target, emptyText) ? Visibility.Visible : Visibility.Collapsed;
            errorText.Visibility = ReferenceEquals(target, errorText) ? Visibility.Visible : Visibility.Collapsed;
            treeView.Visibility = ReferenceEquals(target, treeView) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Selection_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            int count = CountChecked(_roots);
            selectedCountText.Text = $"已选 {count} 项";
            okBtn.IsEnabled = count > 0;
        }

        private static int CountChecked(IEnumerable<PickerTreeNode> nodes) =>
            nodes.Sum(n => (n.IsChecked && !n.IsDummy ? 1 : 0) + CountChecked(n.Children));

        private static void CollectChecked(IEnumerable<PickerTreeNode> nodes, List<CloudDriveFile> result)
        {
            foreach (var n in nodes)
            {
                if (n.IsChecked && !n.IsDummy) result.Add(n.File);
                CollectChecked(n.Children, result);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var result = new List<CloudDriveFile>();
            CollectChecked(_roots, result);
            SelectedFiles = result;
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
