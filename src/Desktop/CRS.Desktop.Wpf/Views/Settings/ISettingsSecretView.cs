namespace CRS.DesktopClient.Views.Settings;

/// <summary>
/// 设置子视图仅负责清空由自身名称域持有的密码输入框。
/// 密码不通过接口传递，不暴露为持久化属性；窗口统一调度关闭、锁定与通知的清理。
/// </summary>
public interface ISettingsSecretView
{
    void ClearInputs();
}
