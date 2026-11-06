#!/usr/bin/env python3
"""
Sender SIMULEREDE maalepakker til PC-programmet, saa systemet kan proeves af uden hardware.

Scriptet efterligner formatet fra sensor_stream_tracking.py paa Raspberry Pi: én samlet
besked pr. maaling med rotation, kraft og position, ét faelles loebenummer og en fast
sendefrekvens. Kameraet leverer langsommere end sendefrekvensen, saa den samme position
gentages i flere beskeder - praecis som paa Pi'en - og pos_age_ms fortaeller hvor gammel
positionen er.

Eksempler:
  python send_test_packets.py --host 127.0.0.1
  python send_test_packets.py --host 172.20.10.7 --duration 60
  python send_test_packets.py --occlusion 4 --at 10    markoeren daekkes i 4 s efter 10 s
  python send_test_packets.py --loss 0.03              3 % af beskederne naar ikke frem
  python send_test_packets.py --still --noise 3        proben ligger i ro (FR-12)

Dette er testdata. Det er ikke en sensor og ikke en realistisk bevaegelsesmodel, og
resultaterne maa ikke bruges som projektresultater.
"""
import argparse
import json
import math
import random
import socket
import time

parser = argparse.ArgumentParser()
parser.add_argument("--host", default="127.0.0.1",
                    help="PC'ens IPv4-adresse. Brug 127.0.0.1 naar scriptet koerer paa samme pc.")
parser.add_argument("--port", type=int, default=5005)
parser.add_argument("--rate", type=float, default=40.0, help="sendefrekvens, Hz (som RATE_HZ paa Pi'en)")
parser.add_argument("--cam", type=float, default=20.0, help="kameraets billedfrekvens, Hz")
parser.add_argument("--duration", type=float, default=0, help="sekunder. 0 = koer til Ctrl+C.")
parser.add_argument("--shape", choices=["cirkel", "linje", "ottetal"], default="cirkel")
parser.add_argument("--still", action="store_true",
                    help="proben ligger i ro. Det er opstillingen, FR-12 kraever.")
parser.add_argument("--noise", type=float, default=1.0, help="stoej paa positionen i mm (spids til spids)")
parser.add_argument("--occlusion", type=float, default=0, help="sekunder hvor markoeren er daekket")
parser.add_argument("--at", type=float, default=10, help="hvornaar okklusionen begynder")
parser.add_argument("--loss", type=float, default=0.0, help="andel beskeder der kastes vaek, 0.0-1.0")
parser.add_argument("--no-force", action="store_true", help="send ingen kraftmaaling")
args = parser.parse_args()

SIDE_MM = 320.0

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)


def quaternion(elapsed):
    roll = 0.25 * math.sin(elapsed * 0.7)
    pitch = 0.20 * math.sin(elapsed * 0.5)
    yaw = 0.40 * math.sin(elapsed * 0.3)
    cr, sr = math.cos(roll / 2), math.sin(roll / 2)
    cp, sp = math.cos(pitch / 2), math.sin(pitch / 2)
    cy, sy = math.cos(yaw / 2), math.sin(yaw / 2)
    return {
        "w": cr * cp * cy + sr * sp * sy,
        "x": sr * cp * cy - cr * sp * sy,
        "y": cr * sp * cy + sr * cp * sy,
        "z": cr * cp * sy - sr * sp * cy,
    }


def position(elapsed):
    """Position i bordkoordinater, mm. Midten ligger i (160, 160)."""
    jitter = args.noise / 2.0
    if args.still:
        return (160.0 + random.uniform(-jitter, jitter),
                160.0 + random.uniform(-jitter, jitter))

    w = 2 * math.pi * 0.1                       # et gennemloeb pr. 10 sekunder
    if args.shape == "cirkel":
        x = 160 + 70 * math.cos(w * elapsed)
        y = 160 + 70 * math.sin(w * elapsed)
    elif args.shape == "linje":
        x = 50 + 220 * (0.5 + 0.5 * math.sin(w * elapsed))
        y = 160.0
    else:
        x = 160 + 70 * math.sin(w * elapsed)
        y = 160 + 45 * math.sin(2 * w * elapsed)
    return (x + random.uniform(-jitter, jitter), y + random.uniform(-jitter, jitter))


start = time.time()
period = 1.0 / args.rate
cam_period = 1.0 / args.cam
next_send = start
next_frame = start
occlusion_from = start + args.at
occlusion_to = occlusion_from + args.occlusion

seq = 0
sent = 0
dropped = 0
frames = 0

# Seneste faerdigbehandlede kamerabillede, som Pi'en ville have det liggende.
last_pos = None
last_frame_time = None

print(f"Sender SIMULEREDE pakker til {args.host}:{args.port} i Pi-format")
print(f"  {args.rate:.0f} Hz besked, {args.cam:.0f} Hz kamera, "
      + ("proben ligger i ro" if args.still else f"form: {args.shape}"))
if args.occlusion > 0:
    print(f"  okklusion fra {args.at:.0f} s til {args.at + args.occlusion:.0f} s")
if args.loss > 0:
    print(f"  pakketab: {args.loss * 100:.0f} %")
print("  Ctrl+C for at stoppe.")

try:
    while True:
        now = time.time()
        elapsed = now - start
        if args.duration and elapsed >= args.duration:
            break

        occluded = occlusion_from <= now < occlusion_to

        # Kameraet leverer sit eget tempo, uafhaengigt af sendefrekvensen.
        if now >= next_frame:
            next_frame += cam_period
            frames += 1
            if occluded:
                last_pos = None
                last_frame_time = None
            else:
                last_pos = position(elapsed)
                last_frame_time = now

        if now >= next_send:
            next_send += period
            seq += 1

            if last_pos is None:
                pos_msg = None
                pos_age = None
                status = "probe marker (ID 4) not visible"
            else:
                x, y = last_pos
                pos_msg = {"x": round(x, 1), "y": round(y, 1),
                           "inside": 0 <= x <= SIDE_MM and 0 <= y <= SIDE_MM}
                pos_age = int((now - last_frame_time) * 1000)
                status = f"probe:  x = {x:7.1f} mm   y = {y:7.1f} mm"

            message = {
                "seq": seq,
                "t": now,
                "quat": quaternion(elapsed),
                "force_n": None if args.no_force else round(2.5 + 0.8 * math.sin(elapsed), 3),
                "force_counts": None if args.no_force else 5600,
                "pos": pos_msg,
                "track_status": status,
                "pos_age_ms": pos_age,
            }

            if args.loss > 0 and random.random() < args.loss:
                # Beskeden blev produceret, men naar aldrig frem. Loebenummeret springer,
                # og det er netop det, programmet skal kunne opdage.
                dropped += 1
            else:
                try:
                    sock.sendto(json.dumps(message).encode("utf-8"), (args.host, args.port))
                    sent += 1
                except OSError as exc:
                    print(f"UDP-afsendelse fejlede: {exc}", flush=True)

        time.sleep(0.002)
except KeyboardInterrupt:
    pass
finally:
    sock.close()
    seconds = max(time.time() - start, 1e-6)
    print()
    print(f"Stoppet efter {seconds:.1f} s")
    print(f"  beskeder sendt: {sent} ({sent / seconds:.0f} Hz)"
          + (f", {dropped} kastet vaek" if dropped else ""))
    print(f"  kamerabilleder: {frames} ({frames / seconds:.0f} Hz)")
