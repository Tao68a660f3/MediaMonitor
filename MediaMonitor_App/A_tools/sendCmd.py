import socket
import re

# 配置信息
PC_IP = "127.0.0.1"  # 调试阶段先发给自己
PC_PORT = 49669      # 对应你 C# 监听的端口

# 指令映射表：关键词 -> (指令码, 描述)
# 支持中英文、模糊匹配（只要输入包含关键词即可触发）
CMD_MAPPING = [
    # 下一曲
    (r"(n|next|下一|下首|下曲|下个|skip|>>|>\))", 0xA1, "下一曲"),
    # 上一曲
    (r"(prev|pp|previous|上一|上首|上曲|上个|back|<<|<\[)", 0xA2, "上一曲"),
    # 播放/暂停
    (r"(p|play|pause|播放|暂停|切换|toggle|pp|space)", 0xA3, "播放/暂停"),
    # 快进 +5s
    (r"(forward|ff|快进|前进|跳过|>>\||\+\d*s|\+5)", 0xA5, "快进 +5s"),
    # 快退 -5s
    (r"(backward|rewind|rew|快退|后退|<<\|-\d*s|-5)", 0xA6, "快退 -5s"),
]

def send_custom_packet(cmd_hex, payload_hex=[]):
    """发送UDP控制包"""
    # 1. 构建基础包头 (0xAB 为回控标识)
    header = 0xAB
    cmd = cmd_hex
    length = len(payload_hex)
    
    # 2. 计算校验和 (异或逻辑)
    check_sum = 0
    for b in payload_hex:
        check_sum ^= b
        
    # 3. 组合完整数据包
    packet = bytearray([header, cmd, length] + payload_hex + [check_sum])
    
    # 4. UDP 发送
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.sendto(packet, (PC_IP, PC_PORT))
        print(f"已发送: {packet.hex().upper()} [指令: {hex(cmd_hex)}]")

def match_command(user_input):
    """根据用户输入匹配对应的指令（模糊匹配）"""
    user_input_lower = user_input.lower().strip()
    
    # 精确匹配一些快捷指令（如输入 "0xA1" 直接发送）
    hex_match = re.search(r"0x[0-9A-Fa-f]{2}", user_input)
    if hex_match:
        try:
            cmd_code = int(hex_match.group(0), 16)
            # 检查是否在有效指令范围内
            if cmd_code in [0xA1, 0xA2, 0xA3, 0xA5, 0xA6]:
                return cmd_code, f"直接指令 {hex(cmd_code)}"
        except:
            pass
    
    # 模糊匹配关键词
    for pattern, cmd_code, desc in CMD_MAPPING:
        if re.search(pattern, user_input_lower):
            return cmd_code, desc
    
    return None, None

def print_help():
    """打印帮助信息"""
    print("\n" + "="*50)
    print("媒体控制客户端 (模糊匹配)")
    print("="*50)
    print("支持的关键词（中英文均可）：")
    print("  • 下一曲: n, next, 下一, 下首, skip, >>")
    print("  • 上一曲: pp, prev, 上一, 上首, back, <<")
    print("  • 播放/暂停: p, play, pause, 播放, 暂停, toggle, pp")
    print("  • 快进+5s: forward, ff, 快进, >>|")
    print("  • 快退-5s: backward, rew, rewind, 快退, <<|")
    print("\n快捷操作：")
    print("  • 输入 hex 指令: 0xA1, 0xA2, 0xA3, 0xA5, 0xA6")
    print("  • 输入 'help' 或 '?' 查看本帮助")
    print("  • 输入 'exit' 或 'quit' 或 'q' 退出程序")
    print("="*50)

def main():
    print_help()
    
    while True:
        try:
            # 获取用户输入
            user_input = input("\n请输入指令: ").strip()
            
            # 退出条件
            if user_input.lower() in ['exit', 'quit', 'q', '退出']:
                print("程序退出")
                break
            
            # 帮助
            if user_input.lower() in ['help', '?', '帮助']:
                print_help()
                continue
            
            # 空输入跳过
            if not user_input:
                continue
            
            # 匹配指令
            cmd_code, desc = match_command(user_input)
            
            if cmd_code:
                # 发送指令
                send_custom_packet(cmd_code)
                print(f"触发: {desc}")
            else:
                print(f"无法识别: '{user_input}'")
                print("   输入 'help' 查看支持的指令")
                
        except KeyboardInterrupt:
            print("\n\n程序退出 (Ctrl+C)")
            break
        except Exception as e:
            print(f"发生错误: {e}")

if __name__ == "__main__":
    main()