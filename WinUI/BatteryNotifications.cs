using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.AppLifecycle;

namespace BatteryDisplay;

internal sealed class BatteryNotifications(Action openDevices) : IDisposable
{
    private AppNotificationManager? manager;

    public void Register()
    {
        if (manager is not null) return;
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            var candidate = AppNotificationManager.Default;
            candidate.NotificationInvoked += NotificationInvoked;
            try { candidate.Register(); }
            catch { candidate.NotificationInvoked -= NotificationInvoked; throw; }
            manager = candidate;
        }
        catch (Exception ex) { LogFailure("register", ex); }
    }

    public (uint Id, string? Error) Send(string title, string message, string tag)
    {
        Register();
        if (manager is null) return (0, "无法初始化 Windows 通知。请以普通用户身份运行环电后重试。");
        try
        {
            var setting = manager.Setting;
            if (setting != AppNotificationSetting.Enabled)
            {
                DebugService.Log("notification", "disabled", new { Setting = setting.ToString() });
                return (0, "Windows 已限制环电通知。请检查 Windows 设置 → 系统 → 通知。");
            }

            var notification = new AppNotificationBuilder()
                .AddArgument("action", "devices")
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            notification.Group = "battery";
            notification.Tag = tag;
            notification.Expiration = DateTimeOffset.Now.AddDays(1);
            manager.Show(notification);
            DebugService.Log("notification", "submitted", new { notification.Id, notification.Tag });
            return notification.Id != 0 ? (notification.Id, null) : (0, "Windows 未接收通知，请稍后重试。");
        }
        catch (Exception ex)
        {
            LogFailure("send", ex);
            return (0, "无法发送 Windows 通知，请检查系统通知设置后重试。");
        }
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => openDevices();

    public bool WasActivated()
    {
        try { return manager is not null && AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.AppNotification; }
        catch (Exception ex) { LogFailure("activation", ex); return false; }
    }

    private static void LogFailure(string operation, Exception ex) => DebugService.Log("notification", "failed",
        new { Operation = operation, Type = ex.GetType().Name, ex.HResult });

    public void Dispose()
    {
        if (manager is null) return;
        manager.NotificationInvoked -= NotificationInvoked;
        try { manager.Unregister(); }
        catch (Exception ex) { LogFailure("unregister", ex); }
        manager = null;
    }
}
