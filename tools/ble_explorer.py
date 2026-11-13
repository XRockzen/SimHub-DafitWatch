"""
Explorateur BLE pour montre Da Fit (protocole MoYoung).

Usage :
  python tools/ble_explorer.py scan [--time 10]
  python tools/ble_explorer.py dump <ADRESSE>
  python tools/ble_explorer.py listen <ADRESSE> [--trigger-hr] [--duration 60]

Avant de lancer : fermer l'app Da Fit sur le téléphone (ou couper son Bluetooth),
sinon la montre reste connectée au téléphone et n'est pas visible / connectable.
"""
import argparse
import asyncio
import sys
from datetime import datetime

from bleak import BleakClient, BleakScanner

sys.stdout.reconfigure(encoding="utf-8")

def u16(short: int) -> str:
    return f"0000{short:04x}-0000-1000-8000-00805f9b34fb"

UUID_SVC_MOYOUNG = u16(0xFEEA)
UUID_MOYOUNG_WRITE = u16(0xFEE2)      # commandes vers la montre (d'après Gadgetbridge)
UUID_SVC_HEART_RATE = u16(0x180D)
UUID_HR_MEASUREMENT = u16(0x2A37)

INTERESTING = {
    UUID_SVC_MOYOUNG: "MoYoung / Da Fit",
    UUID_SVC_HEART_RATE: "Heart Rate (standard)",
    u16(0x180F): "Battery",
    u16(0x180A): "Device Information",
}

# Commande MoYoung de mesure cardiaque (Gadgetbridge : CMD_TRIGGER_MEASURE_HEARTRATE = 109)
CMD_TRIGGER_MEASURE_HEARTRATE = 0x6D


def ts() -> str:
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def moyoung_packet(cmd: int, payload: bytes, hdr: int) -> bytes:
    """Trame MoYoung : FE EA <hdr|len_hi> <len_lo> <cmd> <payload...>"""
    length = len(payload) + 5
    return bytes([0xFE, 0xEA, (hdr + (length >> 8)) & 0xFF, length & 0xFF, cmd]) + payload


def parse_hr_measurement(data: bytes) -> int:
    """Caractéristique standard 0x2A37."""
    flags = data[0]
    return int.from_bytes(data[1:3], "little") if flags & 0x01 else data[1]


async def cmd_scan(args):
    print(f"Scan BLE pendant {args.time}s...")
    found = await BleakScanner.discover(timeout=args.time, return_adv=True)
    rows = sorted(found.values(), key=lambda da: -da[1].rssi)
    for dev, adv in rows:
        uuids = [u.lower() for u in adv.service_uuids]
        tags = [INTERESTING[u] for u in uuids if u in INTERESTING]
        name = adv.local_name or dev.name or "?"
        mark = "  <== candidat" if tags else ""
        print(f"{dev.address}  RSSI {adv.rssi:>4}  {name:<28} {', '.join(tags)}{mark}")
    print(f"\n{len(rows)} appareil(s). Relance avec : dump <ADRESSE>")


async def cmd_dump(args):
    async with BleakClient(args.address, timeout=20) as client:
        print(f"Connecté à {args.address}\n")
        for svc in client.services:
            label = INTERESTING.get(svc.uuid.lower(), svc.description)
            print(f"[SERVICE] {svc.uuid}  {label}")
            for ch in svc.characteristics:
                props = ",".join(ch.properties)
                line = f"   [CHAR] {ch.uuid}  ({props})  {ch.description}"
                if "read" in ch.properties:
                    try:
                        val = await client.read_gatt_char(ch)
                        txt = val.decode("utf-8", "replace") if all(32 <= b < 127 for b in val) and val else ""
                        line += f"\n          = {val.hex(' ')}" + (f"  '{txt}'" if txt else "")
                    except Exception as e:  # noqa: BLE001
                        line += f"\n          (lecture impossible : {e})"
                print(line)
            print()


async def cmd_listen(args):
    async with BleakClient(args.address, timeout=20) as client:
        print(f"Connecté à {args.address} — MTU {client.mtu_size}")

        feef_stats = {"acc": 0, "ppg": 0, "other": 0, "last_ppg": ""}

        async def feef_summary():
            while True:
                await asyncio.sleep(5)
                print(f"{ts()}  feef  [5s] acc={feef_stats['acc']} ppg={feef_stats['ppg']} "
                      f"autres={feef_stats['other']}  dernier {feef_stats['last_ppg']}")
                feef_stats.update(acc=0, ppg=0, other=0)

        def make_handler(uuid: str):
            def handler(_sender, data: bytearray):
                if uuid == u16(0xFEEF) and not args.raw_feef:
                    text = bytes(data).decode("ascii", "replace").strip()
                    kind = text[:3] if text[:3] in ("acc", "ppg") else "other"
                    feef_stats[kind] += 1
                    if kind == "ppg":
                        feef_stats["last_ppg"] = text
                    elif kind == "other":
                        print(f"{ts()}  feef  TEXTE: {text}")
                    return
                extra = ""
                if uuid == UUID_HR_MEASUREMENT:
                    extra = f"  -> HR = {parse_hr_measurement(data)} bpm"
                elif len(data) >= 5 and data[0] == 0xFE and data[1] == 0xEA:
                    extra = f"  -> MoYoung cmd 0x{data[4]:02X} payload {bytes(data[5:]).hex(' ')}"
                print(f"{ts()}  {uuid[4:8]}  {bytes(data).hex(' ')}{extra}")
            return handler

        subscribed = []
        for svc in client.services:
            for ch in svc.characteristics:
                if "notify" in ch.properties or "indicate" in ch.properties:
                    try:
                        await client.start_notify(ch, make_handler(ch.uuid.lower()))
                        subscribed.append(ch.uuid[4:8])
                    except Exception as e:  # noqa: BLE001
                        print(f"  abonnement {ch.uuid[4:8]} impossible : {e}")
        print(f"Abonné à : {', '.join(subscribed)}")

        if args.trigger_hr:
            pkt = moyoung_packet(CMD_TRIGGER_MEASURE_HEARTRATE, bytes([0x00]), args.hdr)
            print(f"{ts()}  >> FEE2 {pkt.hex(' ')}  (démarrage mesure cardiaque)")
            await client.write_gatt_char(UUID_MOYOUNG_WRITE, pkt, response=False)

        async def send_specs(specs):
            for spec in specs:
                cmd_s, _, payload_s = spec.partition(":")
                pkt = moyoung_packet(int(cmd_s, 0), bytes.fromhex(payload_s), args.hdr)
                print(f"{ts()}  >> FEE2 {pkt.hex(' ')}")
                await client.write_gatt_char(UUID_MOYOUNG_WRITE, pkt, response=False)
                await asyncio.sleep(1)

        print(f"Écoute pendant {args.duration}s (Ctrl+C pour arrêter)...\n")
        summary_task = asyncio.create_task(feef_summary())
        try:
            await send_specs(args.send)
            await asyncio.sleep(args.duration)
        finally:
            summary_task.cancel()
            if client.is_connected:
                await send_specs(args.send_end)
            if args.trigger_hr and client.is_connected:
                pkt = moyoung_packet(CMD_TRIGGER_MEASURE_HEARTRATE, bytes([0xFF]), args.hdr)
                print(f"{ts()}  >> FEE2 {pkt.hex(' ')}  (arrêt mesure)")
                await client.write_gatt_char(UUID_MOYOUNG_WRITE, pkt, response=False)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("scan"); s.add_argument("--time", type=float, default=10)
    d = sub.add_parser("dump"); d.add_argument("address")
    l = sub.add_parser("listen"); l.add_argument("address")
    l.add_argument("--trigger-hr", action="store_true", help="envoie la commande MoYoung de mesure cardiaque")
    l.add_argument("--duration", type=float, default=60)
    l.add_argument("--raw-feef", action="store_true", help="affiche le flux debug FEEF brut au lieu d'un résumé")
    l.add_argument("--hdr", type=lambda x: int(x, 0), default=0x20,
                   help="octet d'en-tête MoYoung (Gadgetbridge : 0x10 si MTU=20, sinon 0x20)")
    l.add_argument("--send", action="append", default=[], metavar="CMD:HEX",
                   help="commande MoYoung à envoyer au début, ex. 0x68:11 (répétable)")
    l.add_argument("--send-end", action="append", default=[], metavar="CMD:HEX",
                   help="commande MoYoung à envoyer à la fin, ex. 0x68:ff")
    args = p.parse_args()
    asyncio.run({"scan": cmd_scan, "dump": cmd_dump, "listen": cmd_listen}[args.cmd](args))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
