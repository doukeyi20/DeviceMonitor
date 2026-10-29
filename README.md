# DeviceMonitor · 设备监控与数据追溯系统

面向 Modbus TCP / RTU 设备的 Windows 桌面应用，提供设备与点位配置、多设备轮询采集、实时曲线、阈值报警、历史查询及 CSV 导出。

项目使用 C#、WPF / MVVM、NModbus 和 EF Core + SQLite，已完成真实温湿度传感器的 RTU 接入与读数验证。

![实时监控与点位曲线](docs/images/monitoring.png)

## 核心功能

| 模块 | 功能 |
| --- | --- |
| 设备接入 | 配置 TCP 地址或串口参数，管理设备启停与采集点，支持单点测试 |
| 数据采集 | 按已保存点位配置批量读取连续寄存器，支持多设备轮询和数值换算 |
| 任务管理 | 正常轮次复用连接，通信故障延迟重连，支持取消与停止收尾 |
| 实时监控 | 集中显示各点读数、采集状态及当前报警，切换点位查看实时曲线 |
| 报警记录 | 上下限报警、采集异常、持续事件合并，以及恢复和停止监测状态 |
| 历史追溯 | 按设备、点位名称和时间筛选，支持分页、点位趋势及 CSV 导出 |
| 账户与日志 | 管理员 / 操作员权限、密码管理，以及本地操作与异常日志 |

## 更多界面

<details>
<summary>点位配置与单点测试</summary>

配置寄存器类型、协议地址、数值类型、倍率、偏移量和单位；单点测试显示实际值与原始读数。

![采集点配置](docs/images/point-config.png)
![单点读取结果](docs/images/point-test.png)

</details>

<details>
<summary>历史查询与报警记录</summary>

历史记录支持组合筛选和分页，选中记录后可查看该点的历史趋势；报警历史单独保留事件状态与原因。

![历史查询](docs/images/history.png)
![报警历史](docs/images/alarm-history.png)

</details>

<details>
<summary>账户管理与角色权限</summary>

管理员负责配置、历史查询和账户管理；操作员仅进入实时监控，可手动启停采集。

![创建操作员](docs/images/user-management.png)
![操作员监控界面](docs/images/operator-view.png)

</details>

## 技术栈

| 部分 | 技术 |
| --- | --- |
| 桌面界面 | C#、.NET 10、WPF、XAML |
| 界面组织 | MVVM、CommunityToolkit.Mvvm |
| 设备通信 | NModbus、Modbus TCP / RTU、System.IO.Ports |
| 数据访问 | EF Core 10、LINQ、SQLite、EF Migrations |
| 任务协作 | Task、async/await、CancellationToken、SemaphoreSlim |

## 快速开始

### 运行环境

- Windows，安装 .NET 10 SDK。
- 使用 Visual Studio 时，需支持 .NET 10 并安装“.NET 桌面开发”工作负载。
- 实物 RTU 通信需准备 USB 转 RS-485 适配器、驱动，以及符合设备要求的供电与接线。

在仓库根目录执行：

```powershell
dotnet restore DeviceMonitor.slnx
dotnet build DeviceMonitor.slnx -c Release
dotnet run --project DeviceMonitor/DeviceMonitor.Desktop.csproj -c Release
```

也可以打开 `DeviceMonitor.slnx`，将 `DeviceMonitor.Desktop` 设置为启动项目。

### 首次使用

1. 程序启动时自动应用已有数据库迁移；空库通过登录页创建首个管理员，没有预置密码。
2. 新增设备并启用，填写 IP / 端口或 COM / 波特率 / 校验位 / 停止位及站号。
3. 按设备手册配置点位，执行“读取一次”，核对地址、数据类型与倍率。
4. 在实时监控中勾选设备并开始采集，选中点位查看曲线。
5. 在历史记录中查询读数、查看趋势或导出 CSV。修改正在使用的配置前，先停止对应设备。

设备“启用”不等于已开始采集。连接前应核对设备供电、接线和通信参数；协议地址以设备手册为准，不能直接把 `40001` 一类显示地址当成报文地址。

### 数据位置

```text
%LOCALAPPDATA%/DeviceMonitor/device-monitor.db
%LOCALAPPDATA%/DeviceMonitor/Logs/
```

数据库、日志和导出文件不随源码分发。正常启动不清库；CSV 是历史数据导出，不是完整数据库备份。

## 实现要点

- **连接与调度分开**：各设备维护采集任务；健康连接跨轮次复用。共用 COM 口的 RTU 设备共享串口，并串行完成每次请求响应。
- **按错误类型恢复**：连接和收发故障清理失效连接后等待重试；设备忙保留连接；配置类异常结束任务并提示。
- **统一数值转换**：寄存器先按 UInt16 / Int16 解释，再应用倍率和偏移；显示、报警、历史与导出使用同一实际值。
- **异步界面与安全停止**：同步库调用放在线程池；取消后等待已开始的读取和保存完成，再释放连接。
- **数据一致性**：每轮成功读数批量提交，每次数据库操作独立创建上下文；失败读数不以旧值或零值冒充成功。
- **历史查询与导出**：筛选和分页在数据库执行；翻页、趋势、导出沿用已应用条件，CSV 分批写入临时文件，成功后替换目标。

核心实现：[采集任务](DeviceMonitor/ViewModels/DeviceSamplingViewModel.cs) · [通信读取](DeviceMonitor.Core/Communication/ModbusPointReader.cs) · [报警处理](DeviceMonitor/ViewModels/AlarmMonitor.cs) · [历史存取](DeviceMonitor.Core/Persistence/HistoryStore.cs)

## 回归检查

检查程序使用模拟串口、本机 TCP 回环和临时数据库，不连接真实设备，也不访问应用的实际数据库。

```powershell
# RTU：批量读取、同口多站、取消及资源释放
dotnet run --project tests/DeviceMonitor.RtuChecks/DeviceMonitor.RtuChecks.csproj -c Release

# TCP：分段响应、连接复用与故障恢复
dotnet run --project tests/DeviceMonitor.RtuChecks/DeviceMonitor.RtuChecks.csproj -c Release -- --tcp

# 数据库：迁移、账户、配置、历史查询与批量事务
dotnet run --project tests/DeviceMonitor.PersistenceChecks/DeviceMonitor.PersistenceChecks.csproj -c Release

# WPF：绑定、命令、选择、布局与退出收尾
dotnet run --project tests/DeviceMonitor.UiChecks/DeviceMonitor.UiChecks.csproj -c Release
```

## 项目结构

```text
DeviceMonitor.slnx
DeviceMonitor/             # WPF 应用、页面、ViewModel 与曲线绘制
DeviceMonitor.Core/        # 通信、数据模型、EF 数据访问及运行日志
tests/                     # 通信、数据库和界面回归检查
docs/images/               # 界面截图
```

## 支持范围

- 读取保持寄存器 `03` / 输入寄存器 `04`；每个点占一个寄存器，支持 `UInt16` / `Int16`。
- 连续采集要求同一设备的点位使用同类寄存器、地址连续且不重复，共 1～125 个点；暂不自动分组不连续地址，不含浮点多寄存器解析或控制写入。
- 当前为交互式桌面程序，不是 Windows 后台服务，也不承担 PLC 安全联锁职责。
- 取消会等待已开始的同步库调用完成或超时，不保证立即中断底层驱动。
- 实物验证为单台 RTU 温湿度传感器；模拟回归不等同于工业现场长期稳定性验证。

第三方组件许可与来源见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
