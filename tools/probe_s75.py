"""Query GET_DEVICE_INFO only, using framing from the official public web driver."""
import sys, os, time, json
from pathlib import Path
sys.path.insert(0, os.path.join(os.environ['LOCALAPPDATA'], 'PeripheralBatteryTools', 'python'))
import hid
out=[]
for d in hid.enumerate(0x0c45,0xfefa):
    if (d['usage_page'],d['usage']) != (0xff60,0x61): continue
    h=hid.device(); item={'path':d['path'].decode(),'frames':[]}
    try:
        h.open_path(d['path'])
        for _ in range(4):
            if not h.read(64,30): break
        # report ID 0 + 64 bytes; AA, GET_DEVICE_INFO, 48 bytes, address 0, last packet.
        packet=[0,0xaa,0x10,48,0,0,0,1,0]+[0]*56
        item['sent']=h.write(packet)
        deadline=time.monotonic()+1
        while time.monotonic()<deadline:
            r=h.read(64,200)
            if not r: continue
            item['frames'].append(bytes(r).hex(' '))
            if len(r)>=56 and r[0:3]==[0x55,0x10,48] and r[3:5]==[0,0]:
                data=r[8:56]
                item.update(vid=hex(data[4]|data[5]<<8),pid=hex(data[6]|data[7]<<8),manufacturer=data[12]|data[13]<<8,product=data[14]|data[15]<<8,work_mode=data[16],battery=data[17],charge_raw=data[18],frame_version=data[30])
                break
    except Exception as e: item['error']=str(e)
    finally: h.close()
    out.append(item)
text=json.dumps(out,ensure_ascii=False,indent=2)
(Path(__file__).resolve().parents[1]/'evidence'/'s75-query.json').write_text(text,encoding='utf-8')
print(text)
