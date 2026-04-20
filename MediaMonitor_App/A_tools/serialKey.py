import serial
import time

# 替换成你的串口号和波特率
ser = serial.Serial('COM23', 115200) 
# 发送下一曲指令
ser.write(bytes([0xAB, 0xA1, 0x00, 0x00])) 
ser.close()