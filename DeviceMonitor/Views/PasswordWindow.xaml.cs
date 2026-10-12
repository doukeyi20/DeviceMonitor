using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

/// <summary>
/// 修改/重置密码的同一个窗口：沿用传来的 PasswordEditViewModel，不再创建第二份。
/// 这里按事件读/清密码框，保存后关闭；不验证旧密码，也不直接改数据库。
/// </summary>
public partial class PasswordWindow : Window
{
    private readonly PasswordEditViewModel _viewModel;

    /// <summary>
    /// MainWindow 或 UserManagementView 开窗时传入数据源。订阅读密码、清密码、保存成功事件，再设置 DataContext。
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
    /// SaveAsync 触发 PasswordsRequested 后调用，把当前三个密码框填入同一个临时对象，返回后继续验证。
    /// </summary>
    private void ReadPasswords(PasswordInputRequest request)
    {
        request.OldPassword = OldPasswordInput.Password;
        request.NewPassword = NewPasswordInput.Password;
        request.Confirmation = ConfirmPasswordInput.Password;
    }

    /// <summary>
    /// 保存成功或关窗时调用，只清控件，不修改数据库密码。
    /// </summary>
    private void ClearPasswords()
    {
        OldPasswordInput.Clear();
        NewPasswordInput.Clear();
        ConfirmPasswordInput.Clear();
    }

    /// <summary>
    /// SaveSucceeded 事件调用，DialogResult=true 让 ShowDialog 结束；不在这里重复保存。
    /// </summary>
    private void CompleteSave() => DialogResult = true; // ShowDialog 随后返回 true。

    /// <summary>
    /// 点取消/×或保存成功要关窗时，WPF 调用这里。仍在写入则暂时拦住关闭，避免误以为取消了保存。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel.IsSaving)
            e.Cancel = true; // 保存只需短暂等待，不允许点 × 后误以为这次没保存。
    }

    /// <summary>
    /// 真正关闭后清空密码框、解除订阅，再让这个旧表单的数据源不再接受保存。
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
