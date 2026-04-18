using System;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 媒体通信传输接口：定义了所有通信方式（串口、UDP、蓝牙等）必须遵循的标准
    /// </summary>
    public interface IMediaTransport
    {
        // 1. 状态管理：UI 或 Helper 只需要看这个状态来决定是否允许发送
        bool IsConnected
        {
            get;
        }

        // 2. 连接管理：具体实现类会在构造函数或属性中保存自己的连接参数（如 IP 或 COM口）
        void Connect();
        void Disconnect();

        // 3. 数据发送：不管底层怎么发，业务层只丢给它一个字节数组
        void Send(byte[] data);

        // 4. 数据接收回调：底层物理驱动收到任何数据，都通过这个事件向上抛给 PackageHelper
        event Action<byte[]> OnRawDataReceived;

        // 5. 传输出问题
        event Action<string> OnTransportError;
    }
}