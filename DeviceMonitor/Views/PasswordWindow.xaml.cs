using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

/// <summary>
/// 修改和重置密码共用的窗口，沿用传来的 PasswordEditViewModel。
/// 后台只取密码、清空控件和关窗，检查与保存由命令处理。
/// </summary>
public partial class PasswordWindow : Window
{
    private readonly PasswordEditViewModel _viewModel; // 表单绑定的数据源，同时决定当前是修改还是重置密码。

    /// <summary>
    /// 接住数据源，订阅界面请求，再设置 DataContext。
    /// </summary>
    public PasswordWindow(PasswordEditViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _viewModel.PasswordsRequested += ReadPasswords;
        _viewModel.PasswordsClearRequested += ClearPasswords;
        _viewModel.SaveSucceeded += CompleteSave;
        DataContext = viewModel; // 用传进来的同一个对象，不再 new 第二份。
    }

    /// <summary>
    /// 由 PasswordEditViewModel 的 PasswordsRequested 事件调用。
    /// 把三个密码框的值写回 request，SaveAsync 返回后继续验证。
    /// </summary>
    private void ReadPasswords(PasswordInputRequest request)
    {
        request.OldPassword = OldPasswordInput.Password;
        request.NewPassword = NewPasswordInput.Password;
        request.Confirmation = ConfirmPasswordInput.Password;
    }

    /// <summary>
    /// 由 PasswordEditViewModel 的 PasswordsClearRequested 事件或本窗口 OnClosed 调用。
    /// 保存成功或关窗时调用，只清控件，不修改数据库密码。
    /// </summary>
    private void ClearPasswords()
    {
        OldPasswordInput.Clear();
        NewPasswordInput.Clear();
        ConfirmPasswordInput.Clear();
    }

    /// <summary>
    /// 由 PasswordEditViewModel 的 SaveSucceeded 事件调用。
    /// 保存成功后设结果为 true，WPF 会关闭弹窗。
    /// </summary>
    private void CompleteSave() => DialogResult = true; // ShowDialog 随后返回 true。

    /// <summary>
    /// 保存还没结束时先不让关窗，避免误以为点关闭就取消了写入。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel.IsSaving)
            e.Cancel = true;
    }

    /// <summary>
    /// 关窗后清密码、解除订阅，并让旧数据源不再接受保存。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ClearPasswords();
        _viewModel.PasswordsRequested -= ReadPasswords;
        _viewModel.PasswordsClearRequested -= ClearPasswords;
        _viewModel.SaveSucceeded -= CompleteSave;
        _viewModel.MarkClosed();
        base.OnClosed(e);
    }
}
