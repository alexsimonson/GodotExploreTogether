"""Generates the stand-in sounds in assets/audio/placeholder/ (synthesized, so no licensing questions).

They're crude on purpose: each marks a sound to source properly. A real file in assets/audio/ with the same name
replaces its placeholder (see docs/26-11-16/AUDIO-REPORT.md). Standard library only; rerunnable:

    python -I tools/generate_placeholder_sounds.py
"""
import math
import os
import random
import struct
import wave

RATE = 22050
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "audio", "placeholder")
rnd = random.Random(1234)


def n(seconds):
    return int(seconds * RATE)


def noise(seconds):
    return [rnd.uniform(-1, 1) for _ in range(n(seconds))]


def lowpass(x, cutoff):
    a = 1 - math.exp(-2 * math.pi * cutoff / RATE)
    y, out = 0.0, []
    for v in x:
        y += a * (v - y)
        out.append(y)
    return out


def highpass(x, cutoff):
    low = lowpass(x, cutoff)
    return [a - b for a, b in zip(x, low)]


def tone(seconds, f0, f1=None, shape="sine"):
    f1 = f0 if f1 is None else f1
    out, phase, count = [], 0.0, n(seconds)
    for i in range(count):
        f = f0 + (f1 - f0) * i / max(1, count - 1)
        phase += 2 * math.pi * f / RATE
        if shape == "sine":
            out.append(math.sin(phase))
        elif shape == "saw":
            out.append(((phase / math.pi) % 2) - 1)
        else:  # square
            out.append(1.0 if math.sin(phase) >= 0 else -1.0)
    return out


def env(x, attack=0.005, decay=None, hold=0.0):
    """Linear attack, optional hold, then exponential decay to the end."""
    count = len(x)
    a, h = n(attack), n(hold)
    tail = max(1, count - a - h)
    k = 5.0 / tail if decay is None else 1.0 / max(1, n(decay))
    out = []
    for i, v in enumerate(x):
        if i < a:
            g = i / max(1, a)
        elif i < a + h:
            g = 1
        else:
            g = math.exp(-(i - a - h) * k)
        out.append(v * g)
    return out


def mix(*parts):
    length = max(len(p) for p in parts)
    return [sum(p[i] for p in parts if i < len(p)) for i in range(length)]


def gain(x, g):
    return [v * g for v in x]


def pad(x, before=0.0):
    return [0.0] * n(before) + x


def normalize(x, peak=0.85):
    m = max(1e-9, max(abs(v) for v in x))
    return [v * peak / m for v in x]


def loopable(x, fade=0.5):
    """Crossfades the end into the start so the loop has no click."""
    f = n(fade)
    head, body = x[:f], x[f:]
    for i in range(f):
        t = i / f
        body[len(body) - f + i] = body[len(body) - f + i] * (1 - t) + head[i] * t
    return body


def write(name, x, peak=0.85):
    x = normalize(x, peak)
    path = os.path.join(OUT, name + ".wav")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, v)) * 32767)) for v in x))
    print("wrote", os.path.relpath(path))


def thud(seconds=0.25, f=90, click=0.3, cutoff=900):
    return mix(env(tone(seconds, f * 1.6, f), 0.002), gain(env(lowpass(noise(seconds), cutoff), 0.001, 0.03), click))


def creak(seconds, f0, f1):
    base = tone(seconds, f0, f1, "saw")
    wobble = [v * (0.6 + 0.4 * math.sin(i * 0.004)) for i, v in enumerate(base)]
    return env(lowpass(wobble, 1500), 0.05, hold=seconds * 0.5)


def voice(seconds, f0, f1, rough=0.3, cutoff=2500):
    """A crude vocal sound: a buzzy glide through a formant-ish filter, with breath."""
    buzz = tone(seconds, f0, f1, "saw")
    vib = [v * (1 + 0.15 * math.sin(i * 2 * math.pi * 6 / RATE)) for i, v in enumerate(buzz)]
    return env(mix(lowpass(vib, cutoff), gain(lowpass(noise(seconds), 3000), rough)), 0.03, hold=seconds * 0.3)


def main():
    os.makedirs(OUT, exist_ok=True)

    # Weapons and hits.
    write("dry_fire", mix(env(highpass(noise(0.05), 3000), 0.0005), pad(env(highpass(noise(0.04), 2500), 0.0005), 0.06)), 0.6)
    write("swing", env([v * math.sin(math.pi * i / n(0.3)) for i, v in enumerate(lowpass(highpass(noise(0.3), 400), 2500))], 0.05), 0.6)
    write("hit_flesh", mix(thud(0.2, 70, 0.6, 700), gain(env(lowpass(noise(0.12), 2500), 0.001), 0.4)))
    write("hit_blunt", thud(0.3, 60, 0.8, 1200))
    write("chop", mix(thud(0.25, 140, 0.9, 2500), pad(gain(env(highpass(noise(0.08), 2000), 0.001), 0.3), 0.01)))
    write("magic_ice", mix(env(tone(1.0, 2400, 900), 0.01), gain(env(highpass(noise(1.0), 4000), 0.01), 0.5),
                           gain(env(tone(1.0, 3600, 1300), 0.02), 0.5)), 0.6)
    write("magic_blood", mix(env(tone(1.2, 70, 40, "saw"), 0.05, hold=0.3), gain(env(lowpass(noise(1.2), 400), 0.1, hold=0.4), 0.7)), 0.7)

    # Creatures and people.
    write("enemy_attack", voice(0.5, 110, 70, 0.6, 900))
    write("enemy_hurt", voice(0.3, 160, 110, 0.5, 1200))
    write("enemy_death", voice(1.0, 120, 40, 0.6, 800))
    write("player_hurt", voice(0.25, 220, 170, 0.3))
    write("player_death", voice(0.9, 200, 90, 0.3))
    write("npc_hurt", voice(0.3, 260, 200, 0.3))
    write("npc_death", voice(0.8, 240, 100, 0.3))
    write("scream", voice(1.1, 700, 520, 0.4, 4000), 0.7)
    write("grab", mix(env(lowpass(noise(0.2), 1500), 0.01), gain(voice(0.2, 230, 190, 0.3), 0.6)), 0.6)
    write("break_free", mix(env(lowpass(noise(0.35), 2000), 0.01, hold=0.1), gain(voice(0.35, 200, 260, 0.4), 0.7)), 0.7)

    # Doors and locks.
    write("door_open", mix(creak(0.7, 180, 260), pad(gain(thud(0.1, 300, 0.5, 3000), 0.4))), 0.6)
    write("door_close", mix(gain(creak(0.3, 240, 180), 0.5), pad(thud(0.3, 80, 0.7, 1500), 0.25)), 0.7)
    write("door_lock", mix(env(highpass(noise(0.03), 2500), 0.0005), pad(env(highpass(noise(0.05), 1800), 0.0005), 0.07)), 0.6)
    write("door_kick", mix(thud(0.4, 55, 1.0, 1500), pad(gain(env(lowpass(noise(0.3), 3000), 0.001), 0.4), 0.02)))
    write("door_break", mix(thud(0.5, 50, 1.0, 2000), pad(env(lowpass(noise(0.9), 3500), 0.002, 0.25), 0.03),
                            pad(gain(env(highpass(noise(0.4), 2500), 0.001), 0.5), 0.1)))
    picks = [pad(env(highpass(noise(0.02), 3500), 0.0005), t) for t in (0.0, 0.17, 0.31, 0.52, 0.66)]
    write("lockpick", mix(*picks), 0.4)
    write("lockpick_break", mix(env(highpass(noise(0.04), 4000), 0.0002), gain(env(tone(0.15, 3100), 0.001), 0.5)), 0.7)

    # Furniture: smashed over someone, and picked up.
    write("furniture_break", mix(thud(0.4, 70, 1.0, 2500), pad(env(lowpass(noise(0.7), 4000), 0.002, 0.18), 0.01),
                                 *[pad(gain(env(highpass(noise(0.05), 2500), 0.001), 0.6), t) for t in (0.08, 0.15, 0.27, 0.36)]))
    write("flashlight_click", mix(env(highpass(noise(0.02), 3000), 0.0005), pad(gain(env(highpass(noise(0.015), 4000), 0.0005), 0.6), 0.05)), 0.5)
    # Power and lights.
    write("glass_break", mix(env(highpass(noise(0.5), 3000), 0.001, 0.12),
                             *[pad(gain(env(tone(0.08, f), 0.001), 0.4), t) for t, f in ((0.02, 3400), (0.09, 4100), (0.17, 2900))]), 0.7)
    write("light_switch", env(highpass(noise(0.025), 2500), 0.0005), 0.45)
    write("fusebox_bang", mix(thud(0.3, 180, 0.8, 4000), gain(env(tone(0.4, 900), 0.001), 0.3)), 0.75)
    write("breaker", mix(thud(0.15, 120, 0.7, 3000), pad(env(highpass(noise(0.03), 3000), 0.0005), 0.05)), 0.6)
    write("sparks", mix(*[pad(gain(env(highpass(noise(0.04), 3500), 0.0005), rnd.uniform(0.3, 1)), rnd.uniform(0, 0.9)) for _ in range(14)],
                        env(tone(1.0, 60, 40, "saw"), 0.01)), 0.75)
    write("main_breaker", mix(thud(0.6, 50, 1.0, 1500), pad(env(tone(1.5, 120, 30, "saw"), 0.01), 0.1)), 0.8)
    # Zombies.
    write("zombie_groan", voice(1.6, 95, 70, 0.7, 700), 0.7)
    write("zombie_bite", mix(voice(0.35, 140, 90, 0.8, 1200), pad(gain(env(lowpass(noise(0.25), 2500), 0.002), 0.7), 0.1)), 0.75)
    write("zombie_death", mix(voice(0.9, 110, 45, 0.7, 600), pad(thud(0.4, 60, 0.8, 900), 0.5)), 0.8)
    write("zombie_bash", mix(*[pad(thud(0.25, 70, 0.9, 1400), t) for t in (0.0, 0.32, 0.6)]), 0.8)
    write("furniture_grab", mix(thud(0.15, 160, 0.4, 2000), pad(gain(env(lowpass(noise(0.2), 1500), 0.01), 0.4), 0.05)), 0.5)

    # Items, the exit, travel, UI.
    write("item_pickup", mix(env(tone(0.12, 900, 1400), 0.002), pad(env(tone(0.12, 1400, 1800), 0.002), 0.06)), 0.5)
    write("item_drop", thud(0.2, 120, 0.6, 1800), 0.6)
    write("exit_open", mix(env(tone(2.5, 50, 45, "saw"), 0.2, hold=1.2), gain(env(lowpass(noise(2.5), 250), 0.3, hold=1.0), 0.8),
                           pad(thud(0.6, 40, 1.0, 600), 2.0)))
    write("travel", mix(env(lowpass(noise(1.0), 1200), 0.3, hold=0.2), gain(env(tone(1.0, 300, 600), 0.3), 0.3)), 0.5)
    write("ui_open", env(tone(0.08, 700, 900), 0.002), 0.4)
    write("ui_close", env(tone(0.08, 900, 650), 0.002), 0.4)
    write("alert_suspicious", mix(env(tone(0.25, 660), 0.005), pad(env(tone(0.3, 880), 0.005), 0.18)), 0.5)
    write("alert_alarmed", mix(*[pad(env(tone(0.15, 990, shape="square"), 0.002), t * 0.18) for t in range(3)]), 0.4)

    # Footsteps: four of each.
    for i in range(1, 5):
        write(f"step_stone_{i}", mix(env(highpass(noise(0.09), 1200 + 200 * i), 0.001), gain(thud(0.09, 90 + 10 * i, 0.3, 800), 0.6)), 0.5)
        write(f"step_wood_{i}", thud(0.14, 110 + 15 * i, 0.5, 1400), 0.5)
        write(f"step_dirt_{i}", env(lowpass(noise(0.12), 900 + 100 * i), 0.004), 0.45)

    # Loops (about 8 s, crossfaded end to start).
    write("amb_hub", loopable(mix(gain(lowpass(noise(8.5), 300), 0.8),
                                  *[pad(env(tone(0.12, f, f * 1.3), 0.01), t) for t, f in ((1.2, 2600), (1.4, 2900), (4.8, 3100), (6.3, 2500))])), 0.35)
    write("amb_dungeon", loopable(mix(lowpass(noise(8.5), 120),
                                      *[pad(gain(env(tone(0.05, f, f * 1.8), 0.001), 0.6), t) for t, f in ((2.1, 1400), (5.5, 1700), (7.2, 1200))])), 0.35)
    write("amb_city_day", loopable(mix(gain(lowpass(noise(8.5), 500), 0.9), gain(lowpass(highpass(noise(8.5), 250), 900), 0.4),
                                       *[pad(env(tone(0.1, f, f * 1.2), 0.01), t) for t, f in ((0.8, 3000), (3.9, 2700))])), 0.35)
    crickets = []
    for k in range(18):
        t = rnd.uniform(0, 7.6)
        chirp = [v * (0.5 + 0.5 * math.sin(i * 2 * math.pi * 30 / RATE)) for i, v in enumerate(tone(0.25, 4200))]
        crickets.append(pad(gain(env(chirp, 0.01, hold=0.15), 0.3), t))
    write("amb_city_night", loopable(mix(gain(lowpass(noise(8.5), 200), 0.6), *crickets)), 0.3)
    crackle = [gain(env(highpass(noise(0.02), 2000), 0.0005), rnd.uniform(0.2, 1)) for _ in range(40)]
    write("torch_crackle", loopable(mix(gain(lowpass(noise(4.5), 300), 0.5), *[pad(c, rnd.uniform(0, 4.4)) for c in crackle])), 0.4)

    # Tavern music: a simple tune over a vamp (a stand-in for real music, which should be sourced).
    beat = 0.32
    chords = [(220, 277, 330), (196, 247, 294), (175, 220, 262), (196, 247, 294)]
    melody = [440, 494, 554, 494, 440, 392, 440, 0, 392, 440, 494, 440, 392, 349, 392, 0,
              349, 392, 440, 392, 349, 330, 349, 0, 392, 440, 494, 554, 587, 554, 494, 0]
    parts = []
    for bar, chord in enumerate(chords):
        for b in range(8):
            t = (bar * 8 + b) * beat
            note = chord[0] / 2 if b % 2 == 0 else chord[b // 2 % 3]
            parts.append(pad(gain(env(tone(beat * 0.9, note, shape="saw"), 0.005), 0.25), t))
            m = melody[bar * 8 + b]
            if m:
                parts.append(pad(gain(env(tone(beat * 0.95, m), 0.01), 0.35), t))
    write("tavern_music", mix(*parts), 0.5)


if __name__ == "__main__":
    main()
