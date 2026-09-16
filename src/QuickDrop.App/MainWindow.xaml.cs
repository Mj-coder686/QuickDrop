using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using QuickDrop.App.ViewModels;
using QuickDrop.Core.Models;

namespace QuickDrop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel
        {
            ConfirmDeviceAsync = ConfirmDeviceAsync
        };
        DataContext = _viewModel;
    }

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要发送的文件",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddSelectedItems(dialog.FileNames);
        }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要发送的文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddSelectedItems([dialog.FolderName]);
        }
    }

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择文件保存位置",
            InitialDirectory = Directory.Exists(_viewModel.DestinationFolder)
                ? _viewModel.DestinationFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.DestinationFolder = dialog.FolderName;
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _viewModel.ClearSelectedItems();

    private async void StartSending_Click(object sender, RoutedEventArgs e) => await _viewModel.StartSendingAsync();

    private async void StartReceiving_Click(object sender, RoutedEventArgs e) => await _viewModel.StartReceivingAsync();

    private async void Retry_Click(object sender, RoutedEventArgs e) => await _viewModel.RetryAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _viewModel.Cancel();

    private Task<bool> ConfirmDeviceAsync(DeviceIdentity device)
    {
        var result = MessageBox.Show(
            this,
            $"{device.Name} 请求接收文件\n\n设备：{device.Platform}\n连接：局域网直连\n\n只有在你认识这台设备时才允许。",
            "确认接收设备",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Cancel();
        base.OnClosing(e);
    }
}
