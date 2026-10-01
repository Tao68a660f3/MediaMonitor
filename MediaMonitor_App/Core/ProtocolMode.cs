using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaMonitor.Core
{
    /// <summary>
    /// 协议模式（**运行期选择，不落盘**）。
    ///
    /// <para><b>New</b> = 新协议（`5A A5`，规范 `protocolDesign_1.1_final.md`）；
    /// <b>Legacy</b> = 现有 `0xAA/0xAB`（已封存，只维护不改动）。</para>
    /// <para>启动默认 New；两种模式各自读写自己的配置文件（`config.new.json` / `config.json`），
    /// 切模式时"断开链路 → 重新注入该模式配置 → 重连"。</para>
    /// </summary>
    public enum ProtocolMode
    {
        Legacy = 0,
        New = 1
    }

    /// <summary>新协议的传输方式（规范 §19.1：USB CDC 在 PC 侧即 COM 口）</summary>
    public enum NewTransportType
    {
        Com = 0,
        Tcp = 1
    }
}
