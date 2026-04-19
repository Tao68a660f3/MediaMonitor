import socket

# 配置与你 C# 界面中一致的 IP 和端口
LISTEN_IP = "127.0.0.1"
LISTEN_PORT = 8090

def start_listener():
    # 创建 UDP 套接字
    # AF_INET 表示 IPv4, SOCK_DGRAM 表示 UDP
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    
    try:
        # 绑定端口
        sock.bind((LISTEN_IP, LISTEN_PORT))
        print(f"🚀 监听器已启动: {LISTEN_IP}:{LISTEN_PORT}")
        print("等待接收数据... (按 Ctrl+C 停止)\n")
        
        while True:
            # 接收数据，缓冲区大小为 1024 字节
            data, addr = sock.recvfrom(1024)
            
            # 打印发送方的地址
            # 将原始字节转换为十六进制显示，方便调试你的协议包
            hex_data = data.hex(' ').upper()
            try:
                text_data = data.decode('gb2312') # 尝试用你之前的 GB2312 解码
            except:
                text_data = "[无法解码的文本]"

            print(f"来自 {addr}:")
            print(f"  [HEX]:  {hex_data}")
            print(f"  [TEXT]: {text_data}")
            print("-" * 30)
            
    except KeyboardInterrupt:
        print("\n监听器已关闭。")
    except Exception as e:
        print(f"发生错误: {e}")
    finally:
        sock.close()

if __name__ == "__main__":
    start_listener()