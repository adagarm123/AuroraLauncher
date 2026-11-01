import re, datetime

path = r"C:/Users/94232/AppData/Roaming/AuroraLauncher/logs/aurora-2026-11-01.log"
lines = open(path, encoding="utf-8", errors="ignore").read().splitlines()

def ts(l):
    m = re.match(r"\[(\d\d:\d\d:\d\d)\]", l)
    return datetime.datetime.strptime(m.group(1), "%H:%M:%S") if m else None

pairs = []
for i, l in enumerate(lines):
    if "Installing Minecraft" in l and ts(l):
        for j in range(i + 1, len(lines)):
            if "Installation of" in lines[j] and ts(lines[j]):
                pairs.append((int((ts(lines[j]) - ts(l)).total_seconds()), ts(l).strftime("%H:%M:%S")))
                break

for sec, t in pairs:
    print(f"{sec:>4}s  start {t}")

# full-asset download span (first install with real asset download)
for i, l in enumerate(lines):
    if "Downloading asset index" in l and ts(l):
        for j in range(i + 1, len(lines)):
            if "Installation of" in lines[j] and ts(lines[j]):
                print(f"first full install w/ assets: {int((ts(lines[j]) - ts(l)).total_seconds())}s")
                break
        break
