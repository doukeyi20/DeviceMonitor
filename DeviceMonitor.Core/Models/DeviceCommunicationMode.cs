namespace DeviceMonitor.Core.Models;

// 一条设备配置使用哪种连接；不是靠通信方式当作设备的唯一编号。
public enum DeviceCommunicationMode { Tcp = 0, Rtu = 1 }
