"""Loopback-only MQTT 3.1.1 observer with two explicitly audited queries.

Uses an audited vendor-reserved PluginClient slot, clean session and QoS 0.
Credentials are derived locally from the hash-pinned installed bridge binary;
they are never written to the evidence file or console.
"""
import argparse
import hashlib
import json
import msvcrt
import os
import re
import select
import socket
import struct
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BRIDGE = Path(r'C:\Program Files\OEM\机械革命控制中心\AiStoneService\GCUBridge.exe')
AUDITED_HASH = '7a1405e231a5e8581c4e9fc4fce2302a77578742ac643aa3d42f7dadaca80695'
TOPICS = ['Customize/SupportInfo', 'Fan/Status', 'Tray/Status', 'System/FanInfo',
          'System/CpuInfo', 'System/GpuInfo', 'System/BatteryInfo',
          'System/BatteryProtection', 'Display/Status', 'Setting/Status']


def mqtt_string(value):
    data = value.encode('utf-8')
    return struct.pack('!H', len(data)) + data


def frame(header, body=b''):
    size = len(body)
    encoded = bytearray([header])
    while True:
        digit = size % 128
        size //= 128
        encoded.append(digit | (128 if size else 0))
        if not size:
            break
    return bytes(encoded) + body


def read_exact(client, length):
    data = bytearray()
    while len(data) < length:
        part = client.recv(length - len(data))
        if not part:
            raise EOFError('Bridge closed connection')
        data.extend(part)
    return bytes(data)


def read_packet(client):
    header = read_exact(client, 1)[0]
    size = 0
    for n in range(4):
        digit = read_exact(client, 1)[0]
        size += (digit & 127) * 128 ** n
        if size > 1024 * 1024:
            raise ValueError('MQTT packet exceeds 1 MiB')
        if not digit & 128:
            return header, read_exact(client, size)
    raise ValueError('Malformed MQTT remaining length')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--seconds', type=int, default=20, choices=range(1, 56))
    parser.add_argument('--output', type=Path, default=ROOT / '.local/evidence/live-observation.json')
    parser.add_argument('--query-known-status', action='store_true', help='Send only original tray GETSUPPORT and GETSTATUS queries')
    parser.add_argument('--capture-vendor-queries', action='store_true', help='Subscribe to original UI query topics as evidence; never replay them')
    parser.add_argument('--enable-observed-monitoring', action='store_true', help='Send the observed System_ON telemetry request, never System_OFF')
    args = parser.parse_args()
    topics = TOPICS + (['System/Control', 'Customize/SupportControl', 'Fan/Control', 'Display/Control', 'Setting/Control'] if args.capture_vendor_queries else [])
    data = BRIDGE.read_bytes()
    if hashlib.sha256(data).hexdigest() != AUDITED_HASH:
        raise ValueError('Unreviewed bridge version; observation refused')
    # The ClientManager string is an audited constant, not a personal password.
    suffixes = set()
    for offset in (0, 1):
        suffixes.update(re.findall(r'_Pwd\d{6,24}_', data[offset:len(data) - ((len(data)-offset) % 2)].decode('utf-16le', errors='ignore')))
    if len(suffixes) != 1:
        raise ValueError('Cannot identify audited local credential format')
    family, slot = 'PluginClient', 19
    client_id = f'{family}_{slot}'
    username = f'{family}_User_{slot}'
    password = family + next(iter(suffixes)) + str(slot)
    lock_path = Path(os.environ['LOCALAPPDATA']) / 'OpenRevo/bridge-slot.lock'
    lock_path.parent.mkdir(exist_ok=True, parents=True)
    records = []
    sent_types = []
    published_queries = []
    started = datetime.now(timezone.utc).isoformat()
    with lock_path.open('a+b') as lock:
        lock.seek(0)
        if lock.read(1) == b'':
            lock.write(b'0')
            lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        with socket.create_connection(('127.0.0.1', 13688), timeout=5) as client:
            connect = mqtt_string('MQTT') + bytes([4, 0xc2]) + struct.pack('!H', 30)
            connect += mqtt_string(client_id) + mqtt_string(username) + mqtt_string(password)
            client.sendall(frame(0x10, connect))
            sent_types.append('CONNECT(clean=true,no-will)')
            header, body = read_packet(client)
            if header != 0x20 or len(body) != 2 or body != b'\x00\x00':
                raise ValueError(f'Unexpected CONNACK: {header:#x}, {body.hex()}')
            body = struct.pack('!H', 1) + b''.join(mqtt_string(topic) + b'\x00' for topic in topics)
            client.sendall(frame(0x82, body))
            sent_types.append('SUBSCRIBE(exact-status-topics,qos=0)')
            # A finite observation; no reconnect/retry loop and no application query.
            end = time.monotonic() + args.seconds
            last_ping = time.monotonic()
            suback = None
            while time.monotonic() < end:
                if time.monotonic() - last_ping >= 10:
                    client.sendall(frame(0xc0))
                    sent_types.append('PINGREQ')
                    last_ping = time.monotonic()
                if not select.select([client], [], [], min(1, max(0, end-time.monotonic())))[0]:
                    continue
                client.settimeout(5)
                header, body = read_packet(client)
                if header == 0xd0 and body == b'':
                    continue
                if header >> 4 == 9:
                    if body[:2] != b'\x00\x01' or body[2:] != bytes(len(topics)):
                        raise ValueError('SUBACK did not grant every exact subscription at QoS 0')
                    suback = list(body[2:])
                    if args.query_known_status:
                        for topic, action in [('Customize/SupportControl', 'GETSUPPORT'), ('Fan/Control', 'GETSTATUS')]:
                            payload = json.dumps({'Action': action}, separators=(',', ':')).encode('ascii')
                            client.sendall(frame(0x30, mqtt_string(topic) + payload))
                            sent_types.append('PUBLISH(audited-query,qos=0,retain=false)')
                            published_queries.append({'topic': topic, 'payload': {'Action': action},
                                                      'sentAtUtc': datetime.now(timezone.utc).isoformat()})
                    if args.enable_observed_monitoring:
                        topic, action = 'System/Control', 'System_ON'
                        payload = json.dumps({'Action': action}, separators=(',', ':')).encode('ascii')
                        client.sendall(frame(0x30, mqtt_string(topic) + payload))
                        sent_types.append('PUBLISH(observed-monitor-enable,qos=0,retain=false)')
                        published_queries.append({'topic': topic, 'payload': {'Action': action},
                                                  'sentAtUtc': datetime.now(timezone.utc).isoformat()})
                elif header >> 4 == 3:
                    if (header >> 1) & 3:
                        raise ValueError('Unexpected PUBLISH QoS above subscription maximum')
                    if len(body) < 2:
                        raise ValueError('Invalid PUBLISH')
                    size = struct.unpack('!H', body[:2])[0]
                    if size == 0 or 2 + size > len(body):
                        raise ValueError('Invalid topic length')
                    topic = body[2:2 + size].decode('utf-8')
                    if topic not in topics:
                        raise ValueError('Unexpected topic outside exact allowlist')
                    payload = body[2 + size:].decode('utf-8')
                    records.append({'receivedAtUtc': datetime.now(timezone.utc).isoformat(),
                                    'topic': topic, 'retained': bool(header & 1),
                                    'payload': json.loads(payload)})
                else:
                    raise ValueError(f'Unexpected packet type {header >> 4}')
            client.sendall(frame(0xe0))
            sent_types.append('DISCONNECT')
    result = {'startedAtUtc': started, 'endedAtUtc': datetime.now(timezone.utc).isoformat(),
              'bridgeSha256': AUDITED_HASH, 'clientFamily': family, 'slot': slot,
              'protocol': 'MQTT 3.1.1', 'host': '127.0.0.1', 'port': 13688,
              'sentPackets': sent_types, 'applicationPublishCount': len(published_queries),
              'publishedQueries': published_queries,
              'subscriptions': topics, 'suback': suback, 'messages': records}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    counts = {topic: {'retained': sum(r['topic']==topic and r['retained'] for r in records),
                      'live': sum(r['topic']==topic and not r['retained'] for r in records)} for topic in topics}
    print(json.dumps({'messages': len(records), 'topics': counts, 'applicationPublishCount': len(published_queries)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
