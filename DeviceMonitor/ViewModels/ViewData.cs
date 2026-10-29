// 定义界面请求、读取状态和曲线样本。
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>设备和用户操作共用的确认信息；界面弹框后把选择写回 Confirmed。</summary>
public sealed class ConfirmationRequest(string message, string title)
{
    public string Message { get; } = message;
    public string Title { get; } = title;
    public bool Confirmed
    {
        get; set;
    }
}

/// <summary>固定本次删除的设备和点位，避免弹框后又取到另一个选中项。</summary>
public sealed class PointDeleteRequest
{
    public DeviceInfo Device { get; }
    public PointInfo Point { get; }
    public bool Confirmed { get; set; }

    public PointDeleteRequest(DeviceInfo device, PointInfo point)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Point = point ?? throw new ArgumentNullException(nameof(point));
        if (point.DeviceId != device.Id)
            throw new ArgumentException("这个采集点不属于当前设备。", nameof(point));
    }
}

/// <summary>本次修改密码要用的输入；窗口读取密码框后填入，操作结束就清空。</summary>
public sealed class PasswordInputRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string Confirmation { get; set; } = string.Empty;
}

/// <summary>本次创建账户要用的密码与确认密码；不参与长期界面绑定。</summary>
public sealed class UserPasswordsRequest
{
    public string Password { get; set; } = string.Empty;
    public string Confirmation { get; set; } = string.Empty;
}

/// <summary>一次读取的显示状态，单次测试和监控行共用；不表示设备在线，也不控制采集循环。</summary>
public enum SingleReadState
{
    NotRead,
    Reading,
    Succeeded,
    TimedOut,
    Failed,
    Canceled
}

public sealed record LiveTrendSample(DateTimeOffset ReadAt, double? Value);
