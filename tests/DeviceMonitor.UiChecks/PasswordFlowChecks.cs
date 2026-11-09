// 只用临时数据库验证直接传密码和按钮接线；不显示登录、密码或提示窗口。
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeviceMonitor.Model;
using DeviceMonitor.Services;
using DeviceMonitor.View;
using DeviceMonitor.ViewModel;

internal static partial class Program
{
    private static async Task CheckDirectPasswordFlowsAsync()
    {
        string directory = Path.Combine(Output, "password-flows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "checks.db");
        new DatabaseInitializer(path).Initialize();
        var users = new UserStore(path);
        var devices = new DeviceStore(path);
        var history = new HistoryStore(path);

        // 不调用 App.Run；窗口只创建控件，不 Show，不会触发正式库的启动初始化。
        var loginWindow = new LoginWindow(users, devices, history);
        var login = (LoginViewModel)loginWindow.DataContext;
        login.MessageRequested -= WindowHandler<Action<string, string>>(loginWindow, "ShowMessage");
        login.LoginSucceeded -= WindowHandler<Action<UserAccount>>(loginWindow, "OpenMainWindow");
        string message = "";
        UserAccount? account = null;
        login.MessageRequested += (text, _) => message = text;
        login.LoginSucceeded += user => account = user;
        await login.InitializeAsync();
        login.ShowAdminSetupCommand.Execute(null);
        login.AdminUserName = "direct-admin";

        await login.CreateFirstAdminAsync("Admin123!", "Mismatch!");
        Check(!users.HasAnyUser() && message.Contains("不一致") && login.CanEdit,
            "首次管理员两次密码不一致时不写库，输入仍可改");
        await login.CreateFirstAdminAsync("      ", "      ");
        Check(!users.HasAnyUser() && message.Contains("至少"), "首次管理员拒绝全空白密码");

        ((PasswordBox)loginWindow.FindName("AdminPasswordInput")).Password = "Admin123!";
        ((PasswordBox)loginWindow.FindName("ConfirmPasswordInput")).Password = "Admin123!";
        ((Button)loginWindow.FindName("CreateAdminButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(login.IsWorking && !login.CanEdit, "管理员按钮直接调用带参数方法，并在保存期间禁用输入");
        await login.CreateFirstAdminAsync("Other123!", "Other123!");
        Check(login.IsWorking, "重复创建提交直接返回，不解除第一笔保存的忙状态");
        await WaitForAsync(() => !login.IsWorking);
        Check(users.HasAnyUser() && login.IsLoginVisible && login.UserName == "direct-admin"
            && ((PasswordBox)loginWindow.FindName("AdminPasswordInput")).Password == ""
            && ((PasswordBox)loginWindow.FindName("ConfirmPasswordInput")).Password == "",
            "创建管理员按钮传入真实控件值，成功后回登录并清空两个密码框");

        await login.LoginAsync("wrong-password");
        Check(account is null && message.Contains("错误") && login.CanEdit, "错误登录密码不进入主界面，可重新提交");
        var loginPassword = (PasswordBox)loginWindow.FindName("PasswordInput");
        loginPassword.Password = "Admin123!";
        ((Button)loginWindow.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(login.IsWorking, "登录按钮直接传入密码，并等待后台验证");
        await login.LoginAsync("ignored-second-submit");
        Check(login.IsWorking, "重复登录不清掉第一笔验证的忙状态");
        await WaitForAsync(() => !login.IsWorking);
        Check(account is not null && loginPassword.Password == "", "登录按钮拿到正确账户，验证后清空控件密码");
        loginWindow.Close();

        UserAccount admin = account!;
        var edit = new PasswordEditViewModel(admin, users);
        await edit.SaveAsync("Admin123!", "New123!", "different");
        Check(!edit.IsSaved && edit.PageMessage.Contains("不一致") && edit.CanSave,
            "修改密码两次输入不一致时不保存，仍可重试");
        await edit.SaveAsync("incorrect", "New123!", "New123!");
        Check(!edit.IsSaved && edit.PageMessage.Contains("原密码") && users.CheckLogin("direct-admin", "Admin123!") is not null,
            "原密码错误不会覆盖数据库密码");

        var passwordWindow = new PasswordWindow(edit);
        // 非模态检查不设置 DialogResult，改为记录成功通知。
        edit.SaveSucceeded -= WindowHandler<Action>(passwordWindow, "CompleteSave");
        bool passwordSaved = false;
        edit.SaveSucceeded += () => passwordSaved = !edit.IsSaving;
        ((PasswordBox)passwordWindow.FindName("OldPasswordInput")).Password = "Admin123!";
        ((PasswordBox)passwordWindow.FindName("NewPasswordInput")).Password = "New123!";
        ((PasswordBox)passwordWindow.FindName("ConfirmPasswordInput")).Password = "New123!";
        ((Button)passwordWindow.FindName("SavePasswordButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(edit.IsSaving && !edit.CanSave, "密码保存按钮直接传入三段输入，保存期间禁用重复提交");
        await edit.SaveAsync("Admin123!", "Ignored123!", "Ignored123!");
        Check(edit.IsSaving, "重复保存密码不会提前解除忙状态");
        await WaitForAsync(() => !edit.IsSaving);
        Check(passwordSaved && edit.IsSaved && !edit.CanSave
            && users.CheckLogin("direct-admin", "New123!") is not null
            && ((PasswordBox)passwordWindow.FindName("NewPasswordInput")).Password == "",
            "密码按钮传参保存成功，清空控件，成功通知时已经结束保存");
        passwordWindow.Close();

        var userPage = new UserManagementViewModel(admin, users) { NewUserName = "direct-worker" };
        var userView = new UserManagementView { DataContext = userPage };
        userView.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await WaitForAsync(() => userPage.CanEditNewUser && userPage.Users.Count > 0);
        userPage.MessageRequested -= WindowHandler<Action<string, string>>(userView, "ShowMessage");
        userPage.MessageRequested += (text, _) => message = text;
        await userPage.CreateOperatorAsync("Worker123!", "different");
        Check(message.Contains("不一致") && users.GetUsers(admin).Count == 1 && userPage.CanCreateOperator,
            "创建操作员先校验两次密码，不一致时不写库");
        // 错误仍需提示；接下来成功创建只显示页面消息，不再弹一遍。
        message = string.Empty;
        ((PasswordBox)userView.FindName("NewPasswordInput")).Password = "Worker123!";
        ((PasswordBox)userView.FindName("ConfirmPasswordInput")).Password = "Worker123!";
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check(((Button)userView.FindName("CreateOperatorButton")).IsEnabled, "创建操作员按钮仍随表单状态启用");
        ((Button)userView.FindName("CreateOperatorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!userPage.CanCreateOperator, "操作员按钮直接传入密码，保存开始立即禁用");
        await WaitForAsync(() => userPage.CanEditNewUser);
        UserAccount worker = users.GetUsers(admin).Single(user => user.Role == UserRole.Operator);
        Check(worker.UserName == "direct-worker" && userPage.NewUserName == ""
            && ((PasswordBox)userView.FindName("NewPasswordInput")).Password == "",
            "操作员按钮使用真实控件输入，保存成功清空姓名与密码");
        Check(message == "" && userPage.PageMessage == "操作员创建成功。"
            && userPage.SelectedUser?.Id == worker.Id,
            "创建操作员成功只显示页面结果，仍选中新账户，不重复弹成功消息");
        userPage.NewUserName = worker.UserName;
        await userPage.CreateOperatorAsync("Worker123!", "Worker123!");
        Check(message.Contains("用户名已存在") && users.GetUsers(admin).Count == 2,
            "去掉成功弹窗不影响重复用户名的错误提示与保存保护");
        userView.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        await userPage.StopOperationsAsync();

        var reset = new PasswordEditViewModel(admin, users, worker);
        await reset.SaveAsync("", "Reset123!", "Reset123!");
        Check(reset.IsSaved && users.CheckLogin("direct-worker", "Reset123!") is not null,
            "管理员重置操作员仍不要求原密码，账户权限检查保留");
    }

    // 只在检查里解除真正开窗、弹消息框的方法，避免自动检查等待人工点击。
    private static T WindowHandler<T>(object view, string method) where T : Delegate =>
        (T)Delegate.CreateDelegate(typeof(T), view,
            view.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!);
}
