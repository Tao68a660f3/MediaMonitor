import socket

# 配置信息
PC_IP = "127.0.0.1"  # 调试阶段先发给自己
PC_PORT = 12345      # 对应你 C# 监听的端口

def send_custom_packet(cmd_hex, payload_hex=[]):
    # 1. 构建基础包头 (0xAB 为回控标识)
    header = 0xAB
    cmd = cmd_hex
    length = len(payload_hex)
    
    # 2. 计算校验和 (异或逻辑，参考你的 SerialService.cs)
    check_sum = 0
    for b in payload_hex:
        check_sum ^= b
        
    # 3. 组合完整数据包
    packet = bytearray([header, cmd, length] + payload_hex + [check_sum])
    
    # 4. UDP 发送
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.sendto(packet, (PC_IP, PC_PORT))
        print(f"已发送指令: {packet.hex().upper()}")

# 示例：发送“下一曲” (假设指令码为 0xA1)
# 对应你定义的 0xAB A1 00 00
send_custom_packet(0xA1)