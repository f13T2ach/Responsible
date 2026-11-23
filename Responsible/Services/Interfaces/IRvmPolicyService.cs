namespace Responsible.Services;

// 全局穿透策略：网络默认关闭；开启后会话只获得出站访问，宿主 loopback 与
// 入站端口保持遮罩。设备列表只保存串口名，引擎在会话启动时按设备逐个授权，
// 不存在的设备会让会话启动失败而不是静默降级。
public sealed record RvmPolicy(
    bool NetworkEnabled,
    bool DevicesEnabled,
    IReadOnlyList<string> Devices,
    // 沙箱强度测试：只在宿主显式打开时切换提示词，隔离策略一条都不放宽
    bool JailbreakTest = false)
{
    public static RvmPolicy Default { get; } = new(false, false, [], false);
}

public interface IRvmPolicyService
{
    RvmPolicy Current { get; }

    string? LoadError { get; }

    void Save(RvmPolicy policy);

    /// <summary>枚举宿主机当前登记的串口（COM1…），按端口号排序。</summary>
    IReadOnlyList<string> EnumerateSerialPorts();
}