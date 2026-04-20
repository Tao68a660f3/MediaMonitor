import socket
import re
import json
import os
import threading
import time

# 配置文件路径
CONFIG_FILE = "config.json"

# 全局变量
send_addr = None  # 自动发现的发送目标地址 (ip, port)
listen_sock = None
listening = True
config = {}

def load_or_create_config():
    """加载或创建配置文件"""
    global config
    
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
                config = json.load(f)
            print("已加载配置文件 config.json")
            return True
        except Exception as e:
            print(f"配置文件读取失败: {e}，将重新创建")
    
    # 交互式配置
    print("\n首次运行，请配置：")
    print("-" * 40)
    
    listen_ip = input("监听IP [默认 127.0.0.1]: ").strip()
    if not listen_ip:
        listen_ip = "127.0.0.1"
    
    listen_port = input("监听端口 [默认 8090]: ").strip()
    if not listen_port:
        listen_port = 8090
    else:
        listen_port = int(listen_port)
    
    while True:
        charset = input("解码字符集 (gb2312/utf8) [默认 gb2312]: ").strip().lower()
        if not charset:
            charset = "gb2312"
        if charset in ["gb2312", "utf8"]:
            break
        print("请输入 gb2312 或 utf8")
    
    config = {
        "listen_ip": listen_ip,
        "listen_port": listen_port,
        "decode_charset": charset
    }
    
    try:
        with open(CONFIG_FILE, 'w', encoding='utf-8') as f:
            json.dump(config, f, indent=4, ensure_ascii=False)
        print(f"配置已保存到 {CONFIG_FILE}")
    except Exception as e:
        print(f"保存配置文件失败: {e}")
    
    return True

def show_config():
    """显示当前配置"""
    print("\n当前配置:")
    print(f"  监听地址: {config['listen_ip']}:{config['listen_port']}")
    print(f"  解码字符集: {config['decode_charset']}")
    print(f"  发送目标: {'未发现' if send_addr is None else f'{send_addr[0]}:{send_addr[1]}'}")

def listen_udp():
    """UDP监听线程，接收C#服务端的数据并自动发现发送目标"""
    global listen_sock, listening, send_addr
    
    try:
        listen_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        listen_sock.bind((config['listen_ip'], config['listen_port']))
        listen_sock.settimeout(1.0)  # 设置超时，便于检查 listening 标志
        print(f"UDP监听器已启动: {config['listen_ip']}:{config['listen_port']}")
    except Exception as e:
        print(f"监听启动失败: {e}，将无法接收数据")
        return
    
    while listening:
        try:
            data, addr = listen_sock.recvfrom(2048)
            
            # 自动发现发送目标（从收到的第一个包获取）
            # 自动发现/更新发送目标（每次收到包都更新）
            old_addr = send_addr
            send_addr = (addr[0], addr[1])

            if old_addr is None:
                print(f"\n[自动发现] 检测到C#服务端: {send_addr[0]}:{send_addr[1]}")
            elif old_addr != send_addr:
                print(f"\n[自动发现] C#服务端地址已更新: {old_addr[0]}:{old_addr[1]} -> {send_addr[0]}:{send_addr[1]}")
                print(f"后续指令将发送到此地址")
                show_config()
                print()
            
            # 打印收到的数据
            hex_data = ' '.join(f'{b:02X}' for b in data)
            try:
                charset = config.get('decode_charset', 'gb2312')
                if charset == 'utf8':
                    text_data = data.decode('utf-8')
                else:
                    text_data = data.decode('gb2312')
            except:
                text_data = "[无法解码]"
            
            print(f"\n[收到] 来自 {addr[0]}:{addr[1]}")
            print(f"  HEX:  {hex_data}")
            print(f"  TEXT: {text_data}")
            print()
            
        except socket.timeout:
            continue
        except Exception as e:
            if listening:
                print(f"监听错误: {e}")
            break
    
    if listen_sock:
        listen_sock.close()

def send_custom_packet(cmd_hex, payload_hex=[]):
    """发送UDP控制包"""
    global send_addr
    
    if send_addr is None:
        print("未发现C#服务端，请确保C#程序已启动并向本机发送数据")
        return False
    
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
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.sendto(packet, send_addr)
            print(f"[发送] 到 {send_addr[0]}:{send_addr[1]}: {packet.hex().upper()} [指令: {hex(cmd_hex)}]")
            return True
    except Exception as e:
        print(f"发送失败: {e}")
        return False

# 指令映射表
CMD_MAPPING = [
    (r"(n|next|下一|下首|下曲|下个|skip|>>|>\))", 0xA1, "下一曲"),
    (r"(prev|pp|previous|上一|上首|上曲|上个|back|<<|<\[)", 0xA2, "上一曲"),
    (r"(p|play|pause|播放|暂停|切换|toggle|space)", 0xA3, "播放/暂停"),
    (r"(forward|ff|快进|前进|跳过|>>\||\+\d*s|\+5)", 0xA5, "快进 +5s"),
    (r"(backward|rewind|rew|快退|后退|<<\|-\d*s|-5)", 0xA6, "快退 -5s"),
]

def match_command(user_input):
    """匹配用户输入的指令"""
    user_input_lower = user_input.lower().strip()
    
    # 精确匹配 hex 指令
    hex_match = re.search(r"0x[0-9A-Fa-f]{2}", user_input)
    if hex_match:
        try:
            cmd_code = int(hex_match.group(0), 16)
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
    print("\n" + "=" * 50)
    print("媒体控制客户端 (自动发现 + UDP监听)")
    print("=" * 50)
    print("支持的关键词：")
    print("  下一曲: n, next, 下一, skip, >>")
    print("  上一曲: pp, prev, 上一, back, <<")
    print("  播放/暂停: p, play, pause, 播放, 暂停, space")
    print("  快进+5s: ff, forward, 快进, >>|")
    print("  快退-5s: rew, rewind, 快退, <<|")
    print("\n快捷操作：")
    print("  输入 hex 指令: 0xA1, 0xA2, 0xA3, 0xA5, 0xA6")
    print("  输入 'show' 查看当前配置和发现状态")
    print("  输入 'reset' 重置发现的目标地址（等待重新发现）")
    print("  输入 'help' 查看本帮助")
    print("  输入 'exit' 退出程序")
    print("=" * 50)

def main():
    global listening, send_addr
    
    # 加载配置
    load_or_create_config()
    
    # 启动监听线程
    listen_thread = threading.Thread(target=listen_udp, daemon=True)
    listen_thread.start()
    
    # 等待一下让监听线程启动
    time.sleep(0.5)
    
    # 显示界面
    show_config()
    print_help()
    
    # 主循环
    while True:
        try:
            user_input = input("\n> ").strip()
            
            if user_input.lower() in ['exit', 'quit', 'q', '退出']:
                print("正在退出...")
                break
            
            if user_input.lower() in ['help', '?', '帮助']:
                print_help()
                continue
            
            if user_input.lower() == 'show':
                show_config()
                continue
            
            if user_input.lower() == 'reset':
                send_addr = None
                print("已重置发送目标，等待C#服务端重新发送数据...")
                continue
            
            if not user_input:
                continue
            
            cmd_code, desc = match_command(user_input)
            
            if cmd_code:
                send_custom_packet(cmd_code)
                if desc:
                    print(f"  -> {desc}")
            else:
                print(f"无法识别: '{user_input}'")
                print("   输入 'help' 查看支持的指令")
                
        except KeyboardInterrupt:
            print("\n正在退出...")
            break
        except Exception as e:
            print(f"错误: {e}")
    
    # 清理
    listening = False
    if listen_sock:
        listen_sock.close()
    print("程序已退出")

if __name__ == "__main__":
    main()