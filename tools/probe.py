"""阶段二实验工具。依赖 hidapi，仅发送有公开依据的 A950 电池查询。"""
import sys, os, json, time
sys.path.insert(0, os.path.join(os.environ['LOCALAPPDATA'], 'PeripheralBatteryTools', 'python'))
import hid
from pathlib import Path

root = Path(__file__).resolve().parents[1]
devices = hid.enumerate()
results = []
for pid in (0x1074, 0x1084):
    candidates = [d for d in devices if d['vendor_id'] == 0x260d and d['product_id'] == pid]
    for cmd in [d for d in candidates if (d['usage_page'], d['usage']) == (0xff02, 2)]:
        # Match collections within the SAME USB interface (avoid cross-device replies).
        prefix = cmd['path'].lower().split(b'&col')[0]
        replies = [d for d in candidates if (d['usage_page'], d['usage']) == (0xff01, 0)
                   and d['path'].lower().split(b'&col')[0] == prefix]
        if len(replies) != 1:
            results.append({'pid': hex(pid), 'error': 'No unique paired reply collection'})
            continue
        r, c = hid.device(), hid.device()
        item = {'pid': hex(pid), 'command_path': cmd['path'], 'reply_path': replies[0]['path'], 'frames': []}
        try:
            r.open_path(replies[0]['path']); c.open_path(cmd['path'])
            for _ in range(4):
                if not r.read(17, 30): break
            request = [8, 4] + [0]*14 + [0x49]
            item['sent'] = c.send_feature_report(request)
            deadline = time.monotonic()+1.2
            while time.monotonic() < deadline:
                frame = r.read(17, 200)
                if not frame: continue
                item['frames'].append(bytes(frame).hex(' '))
                if len(frame) == 17 and frame[:3] == [9,4,0] and frame[5] == 2 and frame[16] == (0x55-sum(frame[:16])) & 255 and frame[6] <= 100:
                    item['percent'] = frame[6]
                    break
            if 'percent' not in item: item['error'] = 'No valid power reply; sleep/offline/protocol reason undetermined'
        except Exception as e: item['error'] = str(e)
        finally: r.close(); c.close()
        results.append(item)
output = {'time': time.strftime('%Y-%m-%dT%H:%M:%S%z'), 'devices': devices, 'dareu_queries': results}
content = json.dumps(output, ensure_ascii=False, indent=2, default=lambda x: x.decode(errors='replace') if isinstance(x, bytes) else str(x))
(root/'evidence'/'hardware-probe.json').write_text(content, encoding='utf-8')
print(content)
