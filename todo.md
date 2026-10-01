# MediaMonitor 升级项目1：网络

## 1. 硬件端开发 (后期)
* [ ] **ESP32 固件**: 实现 Wi-Fi 连接、UDP 监听转发至串口、串口接收转发至 UDP。
* [ ] **STM32 固件**: 升级红外接收逻辑，当收到遥控信号时，封装 `0xAB` 包通过串口发给 ESP32。

# MediaMonitor 升级项目2：界面与功能

## 🛠️ 第一阶段：地基加固 (Bug 修复与基础重构)
- [ ] **路径访问防御**：
    - [ ] 在访问歌词目录前，增加对驱动器号（盘符）存在性的校验（防止盘符不存在导致的弹窗）。
    - [ ] 优化路径不存在时的处理逻辑（静默回退到程序目录，不再弹窗）。
- [ ] **主界面 UI 调整**：
    - [ ] 将主界面布局调整为自适应（Auto Size）。
    - [ ] 实现主界面位置与尺寸记忆（保存 `Left`, `Top`, `Width`, `Height` 至 `config.ini`）。
    - [ ] 增加启动时的屏幕有效性检查（防止坐标在屏幕外导致窗口丢失）。

## 🖼️ 第二阶段：桌面 Widget 开发 (极简/精准/独立)
- [ ] **逐步转向 Avalonia UI**
- [ ] **Widget 窗口基础**：
    - [ ] 创建独立窗口：无边框、AllowsTransparency、Topmost、ShowInTaskbar=False。
    - [ ] 实现 `DragMove()` 功能，支持桌面自由拖动。
    - [ ] 实现“不抗锯齿”文字渲染 (`TextOptions.TextRenderingMode="Aliased"`)。
- [ ] **精确尺寸控制**：
    - [ ] 固定 Widget 尺寸，设置长歌词直接截断 (`NoWrap` + `ClipToBounds`)。
    - [ ] 双行逻辑：第一行主词，第二行翻译（无翻译时 Collapse）。
- [ ] **独立配置系统**：
    - [ ] 为 Widget 建立独立配置段落，存储坐标、固定尺寸和显隐开关。

## 🎨 第三阶段：交互与美学优化 (进阶功能)
- [ ] **时间轴平滑滚动**：
    - [ ] 根据当前行剩余时间，计算位移量并应用 `RenderTransform`，实现长歌词在固定窗口内平动。
    - [ ] （可选）增加行切换时的平滑淡入或位移动画。
- [ ] **用户体验优化**：
    - [ ] 增加配置项，允许用户在“极简像素”和“抗锯齿”间切换。

- [ ] **📝 滚动算法全家桶 (Algorithm Collection)**

    - [ ] **算法1 比例停顿流 (Steady Breathe)**：进度 0%-15% 锁定开头，15%-85% 线性滚动，85%-100% 锁定末尾，确保长词首尾可见。
    - [ ] **算法2 阻尼追踪流 (VU Momentum)**：计算当前字/词的实时位置作为目标，应用阻尼公式（Damping）让视口追随。实现唱快则文字疾走，唱慢则优雅漂移的“物理重量感”。
    - [ ] **算法3 变速自适应流 (Dynamic Speed)**：动态调整滚动速度，确保“当前正在唱的字”始终处于视口的 1/3 黄金位置。
    - [ ] **算法4 复古跑马灯 (Cyber Neon)**：无视进度，恒定速度循环滚动。搭配你的“不抗锯齿”像素风，实现最纯正的工业电子味。

- [ ] **[高帧率渲染引擎]**：`CompositionTarget.Rendering` 驱动

    * **滚动位移**：挂载渲染钩子，在每一帧重绘前对应的滚动算法，确保歌词滚动重量感与丝滑度。
    * **逐字染色**：通过渲染循环实时读取音频毫秒级绝对时间戳，直接映射双层 TextBlock 的裁剪区域（Clip），彻底消除 50ms 采样带来的滞后与跳变感。

## 📡 第四阶段：硬件协同与发布
- [ ] **长期运行测试**：验证新锁逻辑下的串口/UDP 持续发包稳定性。
- [ ] **文档完善**：在 README 中推荐 MusicPlayer2 和 foobar2000，指引 SMTC 设置。
- [ ] **逐步支持 webbeef（foobar2000 和 deadbeef 插件）**：如果选择了使用此媒体来源，除非播放器被关闭或者手动切换了媒体来源，否则不应当自动改变媒体来源。

## 🖼️ 封面支持（面向新协议；与 legacy 的 0xAA/0xAB 无关）
- [x] **抓取与缓存**：`SmtcService.CurrentThumbnail` / `OnThumbnailUpdated`（SHA256 内容去重 + 切歌/切会话时丢弃过期结果）。
- [x] **压缩到指定分辨率**：`Tools/ArtworkProcessor.ProcessToJpeg/ProcessToPng/ProcessToRgb565`（等比填满 + 居中裁剪，输出尺寸严格等于目标，不引入新依赖）。
- [x] **验证手段**：主界面「Show Artwork」按钮按需弹窗看 600×600 压缩结果（`UI/ArtworkPreviewWindow`）。
- [x] **下发**：新协议 V1.1 的 `ALBUMCOVER` 资源下发已接线（尺寸/格式由对端 `REQUEST` 决定；无封面回 `FORMAT=NONE` 空资源；未就绪回 `ACK(NOT_READY)`）。

## 📡 新协议 V1.1（`5A A5`，与 Legacy 双轨共存）
实施计划与规范：`RLCCProject/Q_Series/Protocol/protocolImplementation.md` / `protocolDesign_1.1_final.md`。

- [x] **P0–P2** 准备 + 编解码层 + 会话层（C# `Protocol/New/**`；C `ref_c/**`；两端向量自检逐字节一致）
- [x] **P3** 实时数据：MEDIA / TIMELINE / CONTROL（回控映射 0xA1/0xA2/0xA3）
- [x] **P4** 资源传输：歌词池 + 封面（JPEG/PNG/RGB565），单资源互斥 + ABORT + 对端 `MAX_RESOURCE_SIZE` 预校验
- [x] **P5** 延迟测量 + 时间轴投递层（`Sync_OnPacketAt` 对接点）
- [x] **P6** UI 与模式切换：
    - [x] `UI/ProtocolStackView.cs`（`IProtocolStackView` + Legacy/New 两个实现）
    - [x] `Protocol/New/Service/NewProtocolStack.cs` 装配（Transport→Codec→Session→各 Service）
    - [x] `Protocol/New/Transport/ComTransport.cs`（USB CDC 复用 `SerialService`）
    - [x] 新面板 + 协议模式单选 + 共用按钮行（连接/测延迟/Show Artwork；对时按钮仅 Legacy 显示）
    - [x] `config.new.json`（与 `config.json` 各存一份，公共项各留一份；`ConfigService<T>` 泛型化，Legacy 用法零改动）
    - [x] 只读区：会话状态 / 对端能力（来自 `HELLO_ACK`）/ 延迟
- [ ] **P7 收尾**：`technical.md` / `readme.md` 的新协议章节细化；`np_vectors.json` 单一事实源 + 生成脚本（目前两侧各手抄同一份附录 A，互为验证）
- [ ] **ESP32 固件**：把 `ref_c/` 接进真实固件并做硬件联调（当前只到 Python mock：mock 通过 ctypes 跑**真 C 代码**）

---
**当前状态：**
- [x] 串口缓冲区阻塞死锁已解决 (不再导致 UI 挂起)
- [x] 锁内 I/O 逻辑已剥离
- [x] 连续运行稳定性验证通过
