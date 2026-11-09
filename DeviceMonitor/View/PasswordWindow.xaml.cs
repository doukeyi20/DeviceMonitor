// 处理密码控件和密码表单窗口关闭。
using DeviceMonitor.ViewModel;
using System;
using System.ComponentModel;
using System.Windows;

namespace DeviceMonitor.View;

public partial class PasswordWindow : Window
{
    private readonly PasswordEditViewModel _viewModel;

    public PasswordWindow(PasswordEditViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _viewModel.PasswordsClearRequested += ClearPasswords;
        _viewModel.SaveSucceeded += CompleteSave;
        DataContext = viewModel;
    }

    /// <summary>直接读取三个密码框，验证和保存仍由 ViewModel 完成。</summary>
    private async void SavePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAsync(OldPasswordInput.Password, NewPasswordInput.Password,
            ConfirmPasswordInput.Password);
    }

    /// <summary>只清控件，不修改数据库密码。</summary>
    private void ClearPasswords()
    {
        OldPasswordInput.Clear();
        NewPasswordInput.Clear();
        ConfirmPasswordInput.Clear();
    }

    /// <summary>保存成功后设结果为 true，WPF 会关闭弹窗。</summary>
    private void CompleteSave() => DialogResult = true;

    /// <summary>保存还没结束时先不让关窗，避免误以为点关闭就取消了写入。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel.IsSaving)
            e.Cancel = true;
    }

    /// <summary>关窗后清密码、解除订阅；保存期间已由 OnClosing 阻止关闭。</summary>
    protected override void OnClosed(EventArgs e)
    {
        ClearPasswords();
        _viewModel.PasswordsClearRequested -= ClearPasswords;
        _viewModel.SaveSucceeded -= CompleteSave;
        base.OnClosed(e);
    }
}
