#!/usr/bin/env python3
"""
Sender SIMULEREDE maalepakker til PC-programmet, saa systemet kan proeves af uden hardware.

Scriptet efterligner de to rigtige datastroemme hver for sig, praecis som proben og
kameraet goer det:

  * orientering  type="orientation"  quaternion            ca. 100 Hz
  * position     type="position"     x, y i mm             ca. 30 Hz

Hver stroem har sin egen loebenummerserie. Det er vigtigt: programmet opgoer datagab
og pakketab pr. stroem, og en faelles taeller ville give et misvisende resultat.

Eksempler:
  python send_test_packets.py --host 127.0.0.1
  python send_test_packets.py --host 172.20.10.7 --duration 60
  python send_test_packets.py --occlusion 4 --at 10      okklusion i 4 s efter 10 s
  python send_test_packets.py --loss 0.03                3 % tabte positionspakker

Dette er testdata. Det er ikke en sensor og ikke en realistisk bevaegelsesmodel, og
resultaterne maa ikke bruges som projektresultater.
"""
import argparse
import json
import math
import random
import socket
import time
from datetime import datetime, timezone

parser = argparse.ArgumentParser()
parser.add_argument("--host", default="127.0.0.1",
                    help="PC'ens IPv4-adresse. Brug 127.0.0.1 naar scriptet koerer paa samme pc.")
parser.add_argument("--port", type=int, default=5005)
parser.add_argument("--duration", type=float, default=0,
                    help="Sekunder. 0 = koer til Ctrl+C.")
parser.add_argument("--shape", choices=["cirkel", "linje", "ottetal"], default="cirkel",
                    help="Formen paa den simulerede rute.")
parser.add_argument("--still", action="store_true",
                    help="Proben ligger i ro. Det er opstillingen, FR-12 kraever for at "
                         "dokumentere stoejreduktionen.")
parser.add_argument("--noise", type=float, default=1.0,
                    help="Stoej paa positionen i mm (spids til spids).")
parser.add_argument("--occlusion", type=float, default=0,
                    help="Sekunder hvor positionen holder op med at blive sendt (markoeren daekkes).")
parser.add_argument("--at", type=float, default=10,
                    help="Hvornaar okklusionen begynder, sekunder efter start.")
parser.add_argument("--loss", type=float, default=0.0,
                    help="Andel positionspakker der kastes vaek, 0.0-1.0.")
parser.add_argument("--no-orientation", action="store_true", help="Send kun position.")
parser.add_argument("--no-position", action="store_true", help="Send kun orientering.")
args = parser.parse_args()

ORIENTATION_HZ = 100.0
POSITION_HZ = 30.0

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def quaternion(elapsed):
    roll = 0.25 * math.sin(elapsed * 0.7)
    pitch = 0.20 * math.sin(elapsed * 0.5)
    yaw = 0.40 * math.sin(elapsed * 0.3)
    cr, sr = math.cos(roll / 2), math.sin(roll / 2)
    cp, sp = math.cos(pitch / 2), math.sin(pitch / 2)
    cy, sy = math.cos(yaw / 2), math.sin(yaw / 2)
    return (sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy)


def position(elapsed):
    """Rute i bordets plan, mm. Midten ligger i (150, 150)."""
    if args.still:
        return (150.0 + random.uniform(-args.noise / 2, args.noise / 2),
                150.0 + random.uniform(-args.noise / 2, args.noise / 2))

    w = 2 * math.pi * 0.1                      # et gennemloeb pr. 10 sekunder
    if args.shape == "cirkel":
        x = 150 + 60 * math.cos(w * elapsed)
        y = 150 + 60 * math.sin(w * elapsed)
    elif args.shape == "linje":
        x = 60 + 180 * (0.5 + 0.5 * math.sin(w * elapsed))
        y = 150.0
    else:                                       # ottetal
        x = 150 + 60 * math.sin(w * elapsed)
        y = 150 + 40 * math.sin(2 * w * elapsed)
    jitter = args.noise / 2.0
    return (x + random.uniform(-jitter, jitter),
            y + random.uniform(-jitter, jitter))


def send(packet):
    try:
        sock.sendto(json.dumps(packet).encode("utf-8"), (args.host, args.port))
        return True
    except OSError as exc:
        print(f"UDP-afsendelse fejlede: {exc}", flush=True)
        return False


orientation_seq = 0
position_seq = 0
sent_orientation = 0
sent_position = 0
skipped_position = 0

start = time.time()
next_orientation = start
next_position = start
occlusion_from = start + args.at
occlusion_to = occlusion_from + args.occlusion

print(f"Sender SIMULEREDE pakker til {args.host}:{args.port}")
print(f"  orientering {ORIENTATION_HZ:.0f} Hz, position {POSITION_HZ:.0f} Hz, "
      + ("proben ligger i ro" if args.still else f"form: {args.shape}"))
if args.occlusion > 0:
    print(f"  okklusion: positionen holder op fra {args.at:.0f} s til {args.at + args.occlusion:.0f} s")
if args.loss > 0:
    print(f"  pakketab: {args.loss * 100:.0f} % af positionspakkerne kastes vaek")
print("  Ctrl+C for at stoppe.")

try:
    while True:
        now = time.time()
        elapsed = now - start
        if args.duration and elapsed >= args.duration:
            break

        if not args.no_orientation and now >= next_orientation:
            orientation_seq += 1
            qx, qy, qz, qw = quaternion(elapsed)
            send({
                "type": "orientation",
                "timestampUtc": now_iso(),
                "sequenceNumber": orientation_seq,
                "qx": qx, "qy": qy, "qz": qz, "qw": qw,
            })
            sent_orientation += 1
            next_orientation += 1.0 / ORIENTATION_HZ

        if not args.no_position and now >= next_position:
            next_position += 1.0 / POSITION_HZ
            occluded = occlusion_from <= now < occlusion_to
            if occluded:
                # Markoeren er daekket: der sendes intet. Loebenummeret taeller heller ikke
                # op, for afsenderen producerede aldrig en maaling.
                pass
            else:
                position_seq += 1
                if args.loss > 0 and random.random() < args.loss:
                    # Pakken produceres, men naar aldrig frem. Loebenummeret springer,
                    # og det er netop det, programmet skal kunne opdage.
                    skipped_position += 1
                else:
                    x, y = position(elapsed)
                    send({
                        "type": "position",
                        "timestampUtc": now_iso(),
                        "sequenceNumber": position_seq,
                        "x": x, "y": y,
                    })
                    sent_position += 1

        time.sleep(0.002)
except KeyboardInterrupt:
    pass
finally:
    sock.close()
    seconds = max(time.time() - start, 1e-6)
    print()
    print(f"Stoppet efter {seconds:.1f} s")
    print(f"  orientering: {sent_orientation} pakker ({sent_orientation / seconds:.0f} Hz)")
    print(f"  position:    {sent_position} pakker ({sent_position / seconds:.0f} Hz)"
          + (f", {skipped_position} kastet vaek" if skipped_position else ""))
