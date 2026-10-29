"""Read-only client identification; unknown binaries never receive native hooks."""
import hashlib
import json
import struct
from pathlib import Path


def executable_sections(data):
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[:2] != b'MZ' or data[pe:pe+4] != b'PE\0\0':
        raise ValueError('不是有效的 PE 文件')
    machine, count = struct.unpack_from('<HH', data, pe+4)
    if machine != 0x8664:
        raise ValueError('仅支持 x64 客户端')
    optional_size = struct.unpack_from('<H', data, pe+20)[0]
    sections = []
    for i in range(count):
        offset = pe+24+optional_size+i*40
        _, rva, size, raw = struct.unpack_from('<IIII', data, offset+8)
        flags = struct.unpack_from('<I', data, offset+36)[0]
        if raw+size > len(data):
            raise ValueError('PE 节区越界')
        if flags & 0x20000000:
            sections.append((rva, data[raw:raw+size]))
    return sections


def inspect_client(dll, registry):
    data = Path(dll).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    sections = executable_sections(data)
    profiles = json.loads(Path(registry).read_text(encoding='utf-8'))['profiles']
    report = {'sha256': digest, 'status': 'unsupported', 'candidates': []}
    for profile in profiles:
        matches = {}
        for name, signature in profile['signatures'].items():
            needle = bytes.fromhex(signature)
            hits = []
            for rva, block in sections:
                start = 0
                while True:
                    pos = block.find(needle, start)
                    if pos < 0:
                        break
                    hits.append(rva+pos)
                    start = pos+1
            matches[name] = hits
        exact = all(address in matches[name] for name, address in profile['addresses'].items()
                    if name in profile['signatures'])
        if digest == profile['sha256'] and exact:
            report.update(status='verified', profile=profile['id'])
            return profile, report
        report['candidates'].append({'profile': profile['id'], 'matches': matches,
                                     'unique': all(len(hits) == 1 for hits in matches.values())})
    report['reason'] = '未知版本尚未验证数据结构和渲染流程；候选函数仅供适配分析，不允许加载补丁。'
    return None, report
