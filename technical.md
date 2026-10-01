# MediaMonitor 技术实现说明

> 本文收录 **代码层面 / 硬件对接层面** 的技术细节：模块分层与关键实现、配置分发链路、全链路延迟测试的算法与统计口径、Python mock 模拟器、STM32 端 `0xAF` 实现，以及延迟测试排障表。
>
> **分工**：[readme.md](readme.md) = 怎么用（核心特性、协议表、配置入口生效时机、快速开始）；**本文 = 怎么实现、怎么对接、怎么排障**。

---

## 1. 项目架构与关键模块

* **Transport 层**：抽象 `IMediaTransport` 接口，解耦业务逻辑与底层物理链路（Serial/UDP）。
* **上行指令处理**：`Services/BackControlService.cs` 解析 `0xAB` 帧（自持接收缓冲：搜 `0xAB` 头 + Len 越界防御 + XOR 校验），再交 `Tools/MediaKeyInvoker.cs` 执行系统媒体键。
* **同步核心**：`Core/PackageMaster.cs` 统筹状态抓取与发包频率控制。
* **配置**：`Services/ConfigService.cs` 逐项容错地读写 `config.json`，启动时注入各消费者；静默项的生效时机见 readme「⚙️ 配置项生效时机」。
* **UI 辅助**：`UI/LatencyTestController.cs`（测延迟交互）、`UI/WindowPlacement.cs`（窗口布局持久化：关闭时写入、启动时校验后应用）。

### 1.1 发送通道：`Send`（入队节流）vs `SendImmediate`（直发）

`TransportManager` 自身实现 `IMediaTransport`，对外统一封装"当前挂载的引擎"（串口 / UDP），业务层只跟它打交道：

| 方法 | 行为 | 用途 |
| :--- | :--- | :--- |
| `Send(byte[])` | 入 `BlockingCollection` 队列后立即返回；常驻后台线程 `SenderLoop` 按 `SendIntervalMs`（默认 `10`，0~200 可调）节流后真实发送 | 常规业务包（`0x10` / `0x11` / `0x12`~`0x15`） |
| `SendImmediate(byte[])` | **不入队**，直连引擎 `Send()` | 探测帧（`0x1F`）等对排队偏置敏感的场景 |

* 切换传输引擎（`SetTransport`）时会**清空积压队列**，避免旧链路的数据发到新链路；
* `ApplyConfig()` 只做引用赋值（原子），发送循环只会读到"旧配置"或"新配置"，不会读到半份配置。

### 1.2 SmtcService：会话管理、进度外推与"残留信息"清理

* **切会话**：`SelectSession()` 先解绑旧会话的三个事件（`MediaPropertiesChanged` / `TimelinePropertiesChanged` / `PlaybackInfoChanged`）并把 `_mediaUpdateSeq` 自增，让旧会话所有在途的 async 回调失效；随后绑定新会话并**立即触发一次**属性读取，避免等到下一次系统事件才刷新。
* **进度外推**：Playing 状态下按 `timeline.LastUpdatedTime + PlaybackRate` 持续外推（Chrome 等不频繁刷新 Timeline 的播放器也能得到连续进度）；进入 Playing 的瞬间用 `_resumeBasePosition/_resumeBaseTime` 作锚点，消除"暂停→恢复"时把整段暂停时长虚增进首个同步包的问题；结果再做 `[0, EndTime]` 钳位。
* **残留清理（重要）**：若当前会话已不在 `SessionManager.GetSessions()` 中（播放器退出 / 会话被关闭），`DetachIfSessionGone()` 会执行 `DetachCurrentSession(notify: true)` —— 解绑事件、重置播放状态与锚点、**清空 `CurrentTitle` / `CurrentArtist` / `CurrentAlbum`**，并触发 `MediaCleared` 事件。
  不清场的话，三个缓存字段会一直保留上一首的值，界面与托盘会"假装还在播放"，而 `GetCurrentProgress()` 其实已经返回 `null`。
* **上层约定**：`PackageMaster` 订阅 `MediaCleared` 后**只清上位机侧状态**（复位统计量 → `LyricService.LoadAndParse("", "")` 发布空歌词 → `Invalidate()` 清账本 → 主动触发一次 `LyricChanged(-1, 空行)` 清掉界面预览），**不向硬件下发任何清场/停止包**，硬件保留最后一帧画面。
* **清屏/复位走既有通路**：无歌曲状态下重新点一次「开始连接」，会补发一次空元数据（`0x10` 三个字段长度均为 0），硬件以该帧作为复位参考信号自行清屏 —— 见 readme 协议说明里的注记。

### 1.3 LyricService：不可变快照、`[offset:N]` 与歌曲信息占位行

* **不可变快照**：解析期间只在局部 `newLines` 上构建，完成后由 `PublishLyrics()` 一次性替换 `Lines` 引用并 `Generation++`。读取方（10ms 帧循环）每帧捕获一次引用，整帧基于同一实例 —— 读方无需加锁，也绝不会读到"新旧混杂"的半成品。
* **`[offset:N]` 头标签**：`ParseHeaderOffset()` 只扫描到**首个带时间戳的行之前**（避免把正文里的 `[offset:..]` 误当标签），取出的**原值**存入 `CurrentOffsetMs`，实际平移量为 `-offset`：
  * `N < 0` → 歌词偏快（唱早了）→ 整行**延后** `|N|`；
  * `N > 0` → 歌词偏慢（唱晚了）→ 整行**提前** `N`。
  即 `新时间 = 标签时间 - offset`（例：`[offset:-2800]` + `[00:00.20]` → `00:03.00`）。
* **平移细节**：`ApplyOffset()` 在排序前对每行（含行内逐字）整体平移；逐字与整行共用同一 delta，保证 `w.Time - line.Time` 的相对节奏不变；若某行会被推到 0ms 之前，则只把**该行**收敛到 0ms 起（否则 `(uint)` 强转会变成约 42.9 亿 ms 污染硬件）。运算全程用 `long` 的 Tick 整数，避免 `-offset` 在 int 域溢出。
* **翻译行配对**依赖**平移前**的时间戳（同时间戳 ±50ms 视为翻译行），所以平移必须发生在解析之后。
* **占位歌词**：`LoadAndParse()` 的收尾统一走 `PublishLyricsOrPlaceholder()` —— 一行都没解析出来（没搜到文件 / 歌词目录不可用 / 文件里没有可解析的时间戳）且能拼出歌曲信息时，发布 **1 行** `Time = 0ms`、`Content = "歌名 - 歌手"` 的占位歌词（`IsPlaceholder = true`、`CurrentLyricPath = null`）。终点时间不写死：它就是末行，由 `GetEndTime()` 返回**曲目总时长**，因此 0ms 显示到曲尾。
* **空属性**（`LoadAndParse("", "")`，会话消失时调用）走闸门 1 → 发布空歌词，**不**产生占位行。

### 1.4 PackageMaster：10ms 帧循环与"账本门控"

* **帧循环**（`_frameInterval = 10ms`）：取 SMTC 进度 → `UpdateStatistics()` → 原子捕获 `Lines` 快照 → `cIdx = FindLastIndex(Time <= position)` → 触发 `LyricChanged` → 按 `SyncIntervalMs`（或 Seek / 切歌）发 `0x11` → `HandleOutput()` 发歌词行。
* **账本门控（差分增量）**：视野槽位 `targetSlots` 与已发账本 `_syncedSlots` 求差集，只发新槽位；`Invalidate()` 清账本并把 `_lastProcessedCIdx` 置 `-2` 强制下一帧整屏重发；歌词代际号 `Generation` 变化（切歌/换歌）同样失效账本。
* **`0x15` 的结束时间是账本门控的**：`GetEndTime()` 中间行取下一行开始时间，末行取曲目总时长（不合理时兜底 `+5s`）。因此 `UpdateStatistics()` 里加了「曲目总时长变化 ≥1s 即 `Invalidate()`」——否则切歌瞬间 SMTC 还没报出 EndTime 时发出去的兜底值（`+5s`）会一直留在硬件上，导致末行/占位行提前清屏。
* **发送路径**：业务包走 `TransportManager.Send()`（入队 + 节流），探测帧走 `SendImmediate()`（直发）。

---

## 2. 配置分发链路与逐项容错

配置只有一个来源、一个分发点：

```text
config.json --(启动 Load，逐项容错)--> ConfigSvc.Current --(启动注入一次)--> TransportMgr / Master / Lyrics / LatencyTestController
                                                          --(界面保存时再注入)--> 同上
```

`ConfigService.Load()` 与"整体反序列化 + 一个 catch 全丢"的区别：

1. 先构建默认配置，再用 `JsonDocument` **逐项**覆盖；
2. 某一项类型/格式非法（如 `"LineLimit": "abc"`、`"TargetSerialMaster": "0xZZ"`）时**只回退该项到默认值**并在控制台说明原因，其余项全部保留；
3. `config.json` 里出现未知键（改名/废弃项）只提示并忽略；
4. 只有 JSON 结构本身损坏（括号不闭合等）才会整体回退默认配置。

> `Save()` 是**整体写回**，这也是 readme「两条使用铁律」里"运行中不要手改 `config.json`"的代码层原因：任意一次界面操作都会把内存配置覆盖写盘。

---

## 3. 全链路延迟测试：算法与实现

> 功能入口、使用步骤与可调项见 [readme.md](readme.md) 的「📶 全链路 UDP/UART 延迟测试」；本节说明算法、精度与统计口径。

### 3.1 RTT 双向测距 + 处理耗时扣除

上位机按固定间隔下发 `0x1F` Ping（携带毫秒时间戳 `T1`），硬件**原样回传 `T1`** 并附上自己"从 DMA 接收中断打点 → 主循环组包完成"的微秒耗时 `proc_us`：

```text
RTT      = T2_Recv - T1_Send
OneWay   = (RTT - proc_us / 1000.0) / 2
```

| 处理 | 作用 |
| :--- | :--- |
| 减去 `proc_us` | 剔除 STM32 主循环排队/组包耗时，只留链路与协议本身的耗时 |
| 除以 2 | 单向 ≈ 往返的一半，同时消除两端晶振不同步与上下行不对称 |

> **精度说明**：协议里的 `T1` 仍是 `uint32` 毫秒（硬件零改动），但上位机用 `Stopwatch` 单调时钟打点，并维护 `T1 → 精确发送时刻` 映射把 RTT 还原到微秒级，
> 从而避开 `Environment.TickCount64` 在 Windows 上约 **15.6ms** 的量化误差（实测 RTT 抖动只有 0.1ms 级）。

### 3.2 统计口径（滑动窗口 30 样本，默认 100ms/包）

| 指标 | 定义 | 用途 |
| :--- | :--- | :--- |
| **Base 固化延迟** | 窗口内**最小值** `min(L)` | 物理极速链路，可作为 `D_base` |
| **Avg 平均延迟** | 窗口内均值 | 观察典型表现 |
| **Jitter 网络抖动** | 窗口内样本偏离基线(Base)的平均偏差 `mean(\|L_i - Base\|)` | 判断链路稳定性 |

- 集满 30 个样本（约 3 秒）自动结束；测量中再点一次按钮可**中途停止并按当前窗口结算**。
- **两段式无回包保护**（都不会误伤"链路慢"的正常情况）：
  - **等待告警**：连续 `LatencyWarnMs`（默认 **3s**）没有任何回包 → 只在日志里提醒"仍在等待…"，**不停止**；
  - **停止超时**：连续 `LatencyTimeoutMs`（默认 **10s**）一个包都没收到 → 才结束并提示排查方向；
  - **单次时长上限**：`max(30s, 超时×3)`，防止"回包很慢但一直有"时窗口迟迟填不满（到点按已采集样本结算）。

> 首包慢的典型来源：ESP32 刚从 Wi-Fi modem-sleep 唤醒 / 首次 ARP 解析 / STM32 主循环正忙（`proc_us` 偏大）/ 串口 TX 排队。这些场景下 3 秒确实会误判，所以停止阈值默认放宽到 10 秒。

### 3.3 为什么探测帧走 `SendImmediate`

> 发帧走 `TransportManager.SendImmediate`（直发）而非 `Send`：后者会进节流队列被 `SendIntervalMs` 拖慢（播放中歌词包排队会带来 10~20ms 偏置），
> 也会把 `0x1F` 当"未知指令"高频写日志刷屏（触发日志框自动清空）。

---

## 4. 延迟测试 C# 接入示例（传输无关）

核心类 `Services/ProtocolLatencyTester.cs` 不持有任何 socket，只依赖"发帧委托 + 喂原始字节"两个注入点，换成 BLE 等新链路同样即插即用；完整按钮交互封装在 `UI/LatencyTestController.cs`。

```csharp
var tester = new ProtocolLatencyTester(frame => App.TransportMgr.SendImmediate(frame))
{
    PingIntervalMs = 100,      // 发包间隔
    ReplyTimeoutMs = 3000,     // 无回包超时
    TargetSerialMaster = 0x01  // 0x1F Ping 的目标设备 ID（来自 config.json）
};

tester.LogMessage   += msg => App.LogSvc?.LogInfo(msg, Brushes.DeepSkyBlue);
tester.StatsUpdated += s   => Console.WriteLine($"测量中 {s.SampleCount}/{tester.WindowSize}");
tester.Completed    += s   => Console.WriteLine($"Base={s.BaseMs:F2}ms Avg={s.AvgMs:F2}ms Jitter={s.JitterMs:F2}ms");

// 底层收到的原始字节喂进来：UDP 是整包、串口是任意切片，内部自行按 0xAB 搜头 + XOR 校验分帧
App.TransportMgr.OnRawDataReceived += tester.FeedRawData;

tester.Start(windowSize: 30);
```

---

## 5. 无硬件调试：Python mock 模拟器

> 脚本位置：`MediaMonitor_App/A_tools/mock_esp32_stm32.py`（readme 只给最常用的一条启动命令，完整开关见下）。

它用标准库模拟 ESP32 + STM32 的整条回包链路（随机 `proc_us` 500~2000us、链路时延 2~10ms），仅需 `socket / struct / time / random / argparse`，无需 pip 安装：

```bash
cd MediaMonitor_App\A_tools

# 【同机调试推荐】监听 127.0.0.1:8080，上位机「远程 IP」填 127.0.0.1、端口 8080
python mock_esp32_stm32.py --ip 127.0.0.1 --port 8080

# 默认监听 0.0.0.0:8080（给"脚本在另一台机器/板子"的场景用；0.0.0.0 只是监听地址，别填进上位机的远程 IP）
python mock_esp32_stm32.py

# 与你的 RemotePort 对齐
python mock_esp32_stm32.py --port 5555

# 固定耗时便于校验公式：proc=1ms、链路往返=4ms → 期望单向 = 2.0ms
python mock_esp32_stm32.py --port 5555 --proc-min 1000 --proc-max 1000 --link-min 4 --link-max 4

# 其它开关：--loss 0.3 模拟 30% 丢包（验证超时保护）、--quiet 不逐帧打印
```

输出示例：

```text
[忽略] 非法/非 Ping 帧 17B: AA 10 00 0D 03 E5 8D 95 E6 9C 88 ...   ← 正常！上位机还会发 0x10/0x11/0x15 等常规帧，mock 只认 0x1F
[0001] <- Ping(T1=12543ms)  回 Pong: Dev=0x01 proc=1312us link=5.31ms -> 192.168.1.20:52341   (累计 1)
```

> 看到 `[忽略] 非法/非 Ping 帧` **不要慌**：那是上位机的常规同步帧（`0x10` 元数据、`0x11` 进度、`0x15` 歌词…），mock 按设计忽略了它们（打印按秒节流，不会淹掉 Ping 行）。
> 真正要看的是有没有 `<- Ping` 行：**有**说明 `0x1F` 已送达；**没有**说明上位机压根没发出来或没送到（见下方排障表）。

> **模拟器的时延是准的**：脚本用 `precise_sleep()`（自旋补偿）而不是裸 `time.sleep()`。Windows 上 `time.sleep` 粒度约 **15.6ms**，
> 直接 sleep 会把"声明 0.5~2ms 的 `proc_us`"实际睡成 8~15.6ms，而 `proc_us` 是按声明值扣除的 → 测得的延迟会**虚高好几倍**；
> 同时 `serve_ping()` 在**独立线程**里模拟 proc+链路时延，收包循环永不阻塞（否则业务流量突发会挤爆接收缓冲 → 丢包 + 积压）。

> **注意 IP 填写方向**：`0.0.0.0` 是脚本这边的**监听地址**，只能出现在脚本参数里；
> 上位机的「远程 IP」必须填 **`127.0.0.1`**（脚本与上位机同机）或**本机局域网 IP**（如 `192.168.214.189`，脚本/板子需要把回包送回本机时用），
> **绝对不能填 `0.0.0.0`** —— 那会让 `sendto` 报 `10049 在其上下文中，该请求的地址无效`（程序会在连接时直接拦下并说明原因）。
> 上位机的「远程端口」填脚本的 `--port`。

> 串口模式暂不提供模拟（`pyserial` 不是标准库）：请用真硬件，或用 **com0com** 等虚拟串口对 + 简单转发脚本。

---

## 6. STM32 端实现说明

```c
/* ---- 1. 使能 DWT 周期计数器（Cortex-M3/M4） ---- */
#define CPU_FREQ_HZ  72000000UL                         /* 按你的主频填 */

static void DWT_Init(void)
{
    CoreDebug->DEMCR |= CoreDebug_DEMCR_TRCENA_Msk;     /* 使能跟踪单元 */
    DWT->CYCCNT = 0;
    DWT->CTRL  |= DWT_CTRL_CYCCNTENA_Msk;               /* 开周期计数器 */
}

/* ---- 2. 串口 DMA + IDLE 中断：抓"帧收完"的时间戳 ---- */
static volatile uint32_t g_rx_cyccnt;                   /* 中断里只存周期数，开销极小 */

void USART1_IRQHandler(void)
{
    if (USART1->SR & USART_SR_IDLE)                     /* 一帧收完（总线空闲） */
    {
        (void)USART1->DR;                               /* 读 DR 清 IDLE 标志 */
        g_rx_cyccnt = DWT->CYCCNT;                      /* ← 延迟测试的关键时间戳 t_rx */
        /* ... 你原有的 DMA 收包处理 / 置标志位 ... */
    }
}

/* ---- 3. 主循环组包：算 proc_us 并回 0xAF ---- */
void HandleLatencyPing(uint8_t target_id, uint32_t t1_ms)
{
    if (target_id != 0x01) return;                      /* 主从过滤：非主设备静默丢弃 */

    uint32_t t_tx_cyc = DWT->CYCCNT;                    /* 组包时刻 */
    uint32_t proc_us  = (uint32_t)((uint64_t)(t_tx_cyc - g_rx_cyccnt) * 1000000ULL / CPU_FREQ_HZ);

    uint8_t  frame[14];
    uint8_t *p = frame;
    *p++ = 0xAB; *p++ = 0xAF; *p++ = 0x00; *p++ = 0x09; /* 头 + 指令 + Len=9 */
    *p++ = 0x01;                                        /* Dev_ID */
    memcpy(p, &t1_ms, 4);   p += 4;                     /* C#_T1_ms 原样透传（小端） */
    memcpy(p, &proc_us, 4); p += 4;                     /* STM32_Proc_us（小端） */

    uint8_t check = 0;
    for (int i = 0; i < 13; i++) check ^= frame[i];     /* 全帧异或（不含校验位本身） */
    frame[13] = check;
    UART_Send(frame, sizeof(frame));                    /* 发回 ESP32 → UDP → PC */
}
```

**注意事项**

- `t_tx_cyc - g_rx_cyccnt` 用**无符号减法**：`DWT->CYCCNT` 回绕（72MHz 约 59.6s）时结果依然正确。
- `proc_us` 只统计"串口收完 → 组包完成"，**不要**把 UART 发送耗时算进去，否则会被上位机当成链路延迟扣掉。
- 主频会变的场景（低功耗/超频）请改用固定 1MHz 的定时器计数替代 DWT 周期数。
- `proc_us` 填 0 时上位机会打**告警**：结果仍可用，但会包含主循环排队耗时（数值偏大）。
- ESP32 侧只做透传时要**逐字节原样转发**，不要重打包、不要过滤 `0xAA/0xAB`，也不要拆分/合并 UDP 报文。
- 串口 115200bps 下一帧 10 字节约 0.87ms，本身就会进入测量值；想看纯 UDP 链路请切到 UDP 模式对比。

---

## 7. 延迟测试排障表

| 现象 | 原因 | 处理 |
| :--- | :--- | :--- |
| 日志 `10049 在其上下文中，该请求的地址无效` | 「远程 IP」填了 `0.0.0.0`（监听地址不能当发送目标） | 填 `127.0.0.1`（同机）或对端局域网 IP |
| 日志 `10054 ... 端口不可达` / 对端只零星收到几帧后**彻底没反应** | 对端（mock/ESP32）当时没在运行，UDP 收到 ICMP 端口不可达；**旧版本会因此把 UDP 链路"哑死"** | 本版本已修复：10054 不再关闭链路，会持续重试并为后续发包正常工作；只需启动对端即可，必要时重连一次 |
| 按钮 3 秒后回到「测延迟」，日志 `未收到 0xAF 回包` | 对端在跑但不认 `0x1F`：Target_ID 不是 `0x01`、ESP32 没逐字节透传、防火墙拦截 | 对照 `0x1F/0xAF` 帧格式核对；先用 mock 脚本自检 |
| 日志 `已等待 3000ms 仍无 0xAF 回包…继续等待到 10000ms` | 对端首包慢（ESP32 唤醒/ARP/主循环忙）或确实没在跑 | 属**提醒不停止**；若确认对端有问题可直接再点一次按钮手动停止，或调大 `LatencyTimeoutMs` |
| 日志 `已达单次测试时长上限 xxx ms，按已采集样本结算` | 回包很慢但一直有，窗口迟迟填不满 | 正常兜底；想更久可调大 `LatencyTimeoutMs`（上限随之×3） |
| 日志 `链路已断开（本机 UDP 客户端已被关闭）` | 本机 UDP socket 已失效 | 重新点击「开始连接」 |
| 日志 `UDP 未连接或链路已断开，数据被丢弃` | 未连接就发包，或链路中途断开 | 重新点击「开始连接」 |
| mock 一直刷 `[忽略] 非法/非 Ping 帧` | 那都是上位机的 `0x10/0x11/0x15` 常规帧 | 正常现象（打印已按秒节流），看有没有 `<- Ping` 行即可 |
| 测得延迟比预期**大好几倍**、mock 一行行 `<- Ping` 很少 | 旧版 mock 的两个缺陷：`time.sleep` 粒度 15.6ms 让 `proc_us` 实际睡成 8~15.6ms；单线程阻塞收包被业务流量挤爆缓冲 | 已修复（`precise_sleep` + 独立线程回包 + 1MB 接收缓冲）；另建议**测延迟时不要同时播放音乐**，或临时把 `SendIntervalMs` 调大到 30~50ms |

---

## 8. 实现笔记

* **UI 刷新不依赖数据绑定**：主窗口通过 `Master.LyricChanged`、`TransportMgr.OnTransportError` 等事件（`RaiseEvent`）主动刷新控件，非绑定模式下同样能正确响应。
* **上行回控**：`0xAB` 帧由 `Services/BackControlService.cs` 解析（自持接收缓冲；`Len` 误码成巨大值时丢头重搜 `0xAB`，避免解析永久卡死），再交 `Tools/MediaKeyInvoker.cs`（内部队列）触发系统媒体键。
* **收包分帧**：`ProtocolLatencyTester.FeedRawData()` 内部按 `0xAB` 搜头 + XOR 校验自行分帧，UDP 整包与串口任意切片都能直接喂入。
* **文本编码**：`PackageBuilder.UpdateEncoding()` 是全局静态开关，UTF-8 / GB2312 影响所有文本包（`0x10`、`0x12`~`0x16`）。
* **`0x15` 是普通行的默认下行包**（带结束时间）；`0x12` 仅为兼容保留、当前代码路径不再启用。

---

## 9. 新协议 V1.1（`5A A5`）与「协议模式」双轨

规范与落地计划在另一个仓库：`RLCCProject/Q_Series/Protocol/protocolDesign_1.1_final.md`（规范）、`protocolImplementation.md`（实施计划）。

### 9.1 分层与依赖方向（Legacy 零侵入）

```
UI/ProtocolStackView.cs  ← 主窗口只认这个接口（当前模式 → 一份实现）
        ├── LegacyStackView → TransportManager / PackageMaster / LatencyTestController（原封不动）
        └── NewStackView    → Protocol/New/Service/NewProtocolStack（装配下面这些）
                                 ├── Transport: TcpTransport / ComTransport（包一层 SerialService）
                                 ├── Codec:     NpEncoder / NpStreamParser / CRC
                                 ├── Session:   SessionManager + NewSendScheduler
                                 └── Service:   MediaPublisher / TimelinePump / ControlReceiver
                                                / ResourceSender / LatencyManager / (Smtc|Offset)TimelineSource
```

* 新协议**只用** `SmtcService`（歌名/进度/封面）与 `LyricService`（歌词解析）当数据源，不反向依赖 Legacy 逻辑；
* 唯一侵入点是 `App.xaml.cs`（组合根）与 `MainWindow`（面板/按钮），Legacy 协议文件一行未改。

### 9.2 配置双轨：`config.json` / `config.new.json`

* `Services/ConfigService.cs` 泛型化为 `ConfigService<T>`，`ConfigService`（非泛型）作为 `ConfigService<PackageConfig>` 的子类保留 —— **Legacy 侧用法与默认值完全不变**；
* 新模式用 `ConfigService<NewProtocolConfig>("config.new.json")`，公共项（歌词目录、`WindowBounds`）**各自留一份**；
* 读写规则只有一条：**从"当前模式的配置文件"读，写回同一个文件**（实测：New 模式下跑一整天，`config.json` 一个字节都没动）；
* **协议模式本身不落盘**（`App.Mode`，启动默认 New）——它是一选择，不是配置。

### 9.3 切模式 = 断开 → 换配置 → 重连

`ProtoMode_Changed` 的顺序是：断开当前栈 → 改 `App.Mode` → 对新栈 `ApplyConfig()` → 刷新面板显隐/专有按钮 → 若原本连着则用新模式重连。连接期间模式单选被禁用，避免"跑着换协议"。

### 9.4 两个必须知道的反直觉点（都踩过）

1. **会话状态是事件驱动的，不是轮询出来的**
   `SessionManager.OnFrame()` 在收到 HELLO / HELLO_ACK / SESSION_START / SESSION_END / ERROR 时才改状态并通知；`Tick()`（10ms）只负责**时间条件**（HELLO 重发、ACK 超时重传、握手判死）。
   最初 `Tick()` 末尾无条件 `RaiseStateChanged()`，上层把"心跳跑了一拍"误当成"刚进入 Active"，每秒重发上百帧 MEDIA/时间轴 —— 对端实测收到 ~50 帧/秒，把 500ms 节拍和接收方状态机一起冲垮。现在 `Tick()` 只在状态**真的变了**时才通知，且 `NewProtocolStack` 只对**状态跃迁**动作（双保险）。

2. **发包节奏要有硬护栏**
   `TimelinePump.MinGapMs = clamp(intervalMs/4, 50, 250)`：**任何**路径（包括"切歌/Seek/播放态变化"的即时帧）都不得击穿这个下限。数据源（SMTC / 播放器）可能抖动，护栏保证对端永远看不到"每 20ms 一帧"。诊断用分因计数：`StatSentDue` / `StatSentPlayingChange` / `StatSentSeek`（正常长连应以 `Due` 为主）。

### 9.5 帧分发链（`NewProtocolStack.OnFrame`，顺序不能换）

```
ResourceSender（Lyrics/AlbumCover 的 REQUEST 与资源 ACK）
  → SessionManager（SYSTEM：HELLO/SESSION_*/ACK/ERROR）
  → LatencyManager（LATENCY_REQUEST/RESPONSE/END）
  → ControlReceiver（CONTROL → 媒体键 0xA1/0xA2/0xA3 + 回 ACK）
  → 剩下真正无主的帧才记 "未处理帧"
```

`ResourceSender.OnFrame` 与 `LatencyManager.OnFrame` 是 `void`（P4/P5 定稿的签名），所以"已消费"只能按类型识别；否则每一帧资源和延迟帧都会被误报成"未处理帧"。

### 9.6 端到端验证方式（无硬件）

```powershell
# 1) 对端：Python mock（协议字节层跑真 C 代码 np_ref.dll，需先 ref_c/tests/build_dll.bat）
python MediaMonitor_App/A_tools/mock_esp32_new.py --port 9100 --run-seconds 60

# 2) 本机：New 模式 + TCP + 127.0.0.1:9100，点「开始连接」
#    期望（20s 长连）：对端收到 ~2 帧/秒 TIMELINE、1 帧 MEDIA、歌词资源 CRC32 校验通过、
#                     延迟 30 样本、断开后对端能立刻重新接受连接
```

**实测基线**（`--caps lyrics,cover,jpeg,rgb565 --max-edge 240`）：帧率 2 帧/秒（`cur` 每 ~500ms 递增）、总帧数 ~78/30s、`crc错=0 重同步=0`、歌词资源 39B（占位歌词）CRC 由 C 侧校验通过、延迟 `Base≈0.1ms Avg≈7.5ms`（loopback + mock 150µs 模拟处理）。

### 9.7 回归自测

| 工程 | 命令 | 覆盖 |
| :--- | :--- | :--- |
| C# 编解码 + 配置容错 | `dotnet run -c Debug`（`Tests/NewProtocolSelfTest`） | R1–R9 向量/粘包/重同步/RGB565 + **R10 同步偏移** + **R11 配置逐项容错**（85 项） |
| C 参考实现 | `ref_c/tests/build_msvc.bat` | C 自检 117 项（含资源接收 R8、时间轴投递 R9/R10） |
| 端到端 | `NewProtocolSelfTest.exe --tcp 127.0.0.1:9100` + mock | P3/P4/P5 验收（实时数据 + 资源 + 回控 + 延迟）一次跑完 |
| 真 SMTC 探针 | `NewProtocolSelfTest.exe --tcp-smtc 127.0.0.1:9100 20` | 用真实播放器数据量"发包节奏"，验证抖动护栏 |




