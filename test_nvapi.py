"""
NVAPI 调用测试 v3 — 最小化测试: 用 V2 + TARGET_GPU 预初始化
"""
import ctypes
import struct

nvapi = ctypes.windll.LoadLibrary("nvapi64.dll")
nvapi_QI = nvapi.nvapi_QueryInterface
nvapi_QI.restype = ctypes.c_void_p
nvapi_QI.argtypes = [ctypes.c_uint32]

MAGIC = {'Init': 0x0150E828, 'Enum': 0xE5AC921F, 'Thermal': 0xE3640A56,
         'Pstates': 0x843C0256, 'Memory': 0x774AA982, 'Name': 0xCEEE8E9F}
ptrs = {k: nvapi_QI(v) for k, v in MAGIC.items()}

# Init + Enum
ctypes.CFUNCTYPE(ctypes.c_int)(ptrs['Init'])()
EnumFn = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p * 64, ctypes.POINTER(ctypes.c_int))
handles = (ctypes.c_void_p * 64)()
count = ctypes.c_int(64)
EnumFn(ptrs['Enum'])(handles, count)
h = handles[0]

NameFn = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_char_p)
nb = ctypes.create_string_buffer(64)
NameFn(ptrs['Name'])(h, nb)
print(f"GPU: {nb.value.decode().strip(chr(0))}")

# === Thermal Settings: V2 ===
# Forum says: NV_GPU_THERMAL_SETTINGS_VER_2 with sensor[i].target = NVAPI_THERMAL_TARGET_GPU
ThermalFn = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p)
get_thermal = ThermalFn(ptrs['Thermal'])

# V2: 64 sensors, each 7 ints (controller, min, max, current, target, res[2]) = 28 bytes
# Total: 4(ver)+4(cnt)+64*28 = 8+1792 = 1800
V2_SIZE = 1800
V2_STRIDE = 28
TARGET_GPU = 2
CTRL_GPU = 100

buf = ctypes.create_string_buffer(V2_SIZE)
# version field
ver_val = ctypes.c_uint32(V2_SIZE | (2 << 16))  # 1800 | 0x20000
ctypes.memmove(buf, ctypes.byref(ver_val), 4)
# count field - pre-set to 64
cnt_val = ctypes.c_uint32(64)
ctypes.memmove(ctypes.byref(buf, 4), ctypes.byref(cnt_val), 4)
# Pre-init sensor[0] only: controller + target
s0 = 8
t1 = ctypes.c_uint32(CTRL_GPU)
t2 = ctypes.c_uint32(TARGET_GPU)
ctypes.memmove(ctypes.byref(buf, s0), ctypes.byref(t1), 4)       # ctrl
ctypes.memmove(ctypes.byref(buf, s0 + 16), ctypes.byref(t2), 4)  # target

print(f"\nCalling GetThermalSettings V2 (size={V2_SIZE}, ver=2)...")
ret = get_thermal(h, 0, buf)
cnt = ctypes.c_uint32.from_buffer(buf, 4).value
print(f"  ret={ret}, count={cnt}")

if ret == 0 and cnt > 0:
    for si in range(min(cnt, 3)):
        base = 8 + si * V2_STRIDE
        ctrl = ctypes.c_uint32.from_buffer(buf, base).value
        curr = ctypes.c_uint32.from_buffer(buf, base + 12).value  # currentTemp at offset 12
        targ = ctypes.c_uint32.from_buffer(buf, base + 16).value
        print(f"  Sensor[{si}]: ctrl={ctrl}, target={targ}, temp={curr}C")

# Also try V3 without pre-init
V3_SIZE = 2312
buf3 = ctypes.create_string_buffer(V3_SIZE)
vv3 = ctypes.c_uint32(V3_SIZE | (3 << 16))
ctypes.memmove(buf3, ctypes.byref(vv3), 4)
ctypes.memmove(ctypes.byref(buf3, 4), ctypes.byref(ctypes.c_uint32(0)), 4)
ret3 = get_thermal(h, 0, buf3)
cnt3 = ctypes.c_uint32.from_buffer(buf3, 4).value
print(f"\nV3 (no pre-init): ret={ret3}, count={cnt3}")

# === P-States ===
PstatesFn = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
get_pst = PstatesFn(ptrs['Pstates'])
V3_PST = 776
buf_p = ctypes.create_string_buffer(V3_PST)
ctypes.memmove(buf_p, ctypes.byref(ctypes.c_uint32(V3_PST | (3 << 16))), 4)
ret_p = get_pst(h, buf_p)
print(f"\nP-States V3: ret={ret_p}")
if ret_p == 0:
    for di in range(8):
        off = 8 + di * 24
        if ctypes.c_uint32.from_buffer(buf_p, off).value:
            pct = ctypes.c_uint32.from_buffer(buf_p, off + 4).value
            print(f"  Dom[{di}]: usage={pct}%")

# === Memory ===
MemFn = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
get_mem = MemFn(ptrs['Memory'])
for vn, sz, v in [("V3", 36, 3), ("V2", 28, 2), ("V1", 20, 1)]:
    buf_m = ctypes.create_string_buffer(sz)
    ctypes.memmove(buf_m, ctypes.byref(ctypes.c_uint32(sz | (v << 16))), 4)
    ret_m = get_mem(h, buf_m)
    print(f"Memory {vn}: ret={ret_m}")
    if ret_m == 0:
        p = struct.unpack_from('I'*(sz//4), buf_m, 0)
        print(f"  Dedicated={p[1]/1024:.0f}MB, CurAvail={p[6]/1024:.0f}MB" if len(p)>6 else f"  Dedicated={p[1]/1024:.0f}MB")

print("\nDone.")
