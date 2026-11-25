# V1.1 协议基线（冻结）

用途：以后验证新版 PC（V1.2）是否仍兼容旧 ESP32（V1.1）。

- `np_ref_v1_1.dll`：V1.1 C 参考实现编出的 DLL（2026-10-02 构建），**不要覆盖**
- `mock_esp32_new.py`：V1.1 的 ESP32 模拟器（本副本的 `DEFAULT_DLL` 已指向本目录的旧 DLL）
- `mock_esp32_gui.py`：同一内核的 GUI 版

运行：

```bat
python mock_esp32_new.py --port 9100
python mock_esp32_gui.py --port 9100
```

兼容性验证建议：

1. 用本目录的旧 mock + 旧 DLL 连接新版 PC：应能正常握手、请求歌词/封面；
2. 旧 ESP32 不会发送 V1.2 的 `KNOWN_CRC32`，新版 PC 必须按 V1.1 完整传输；
3. 旧 ESP32 也不会处理 `ACK(NOT_MODIFIED)`，所以新版 PC 只有在收到 `HAS_KNOWN_CRC32` 时才允许回它。
