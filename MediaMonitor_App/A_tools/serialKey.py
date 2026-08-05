import serial
import time

def build_packet(cmd, payload=[]):
    """构建控制包：0xAB + Cmd + LenH + LenL + Payload + 全帧异或校验"""
    body = [0xAB, cmd, (len(payload) >> 8) & 0xFF, len(payload) & 0xFF] + list(payload)
    check = 0
    for b in body:
        check ^= b
    return bytearray(body + [check])

# 替换成你的串口号和波特率
ser = serial.Serial('COM23', 115200) 

# 发送下一曲指令 (0xA1)
ser.write(build_packet(0xA1))
ser.close()
