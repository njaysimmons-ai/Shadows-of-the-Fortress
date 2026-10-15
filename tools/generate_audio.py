"""Synthesizes the Hollow Fog sound effects. All output is original and released as CC0.

Usage: python3 tools/generate_audio.py <output dir>
Needs numpy and ffmpeg (with libvorbis) on PATH.
"""
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import numpy as np

RATE = 44100
rng = np.random.default_rng(1983)


def write_ogg(path: Path, samples: np.ndarray) -> None:
    samples = np.clip(samples, -1.0, 1.0)
    pcm = (samples * 32767).astype("<i2")
    with tempfile.NamedTemporaryFile(suffix=".wav") as tmp:
        with wave.open(tmp.name, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes(pcm.tobytes())
        subprocess.run(
            ["ffmpeg", "-y", "-loglevel", "error", "-i", tmp.name,
             "-c:a", "libvorbis", "-q:a", "4", str(path)],
            check=True,
        )


def one_pole_lowpass(x: np.ndarray, cutoff: float) -> np.ndarray:
    a = np.exp(-2 * np.pi * cutoff / RATE)
    y = np.empty_like(x)
    acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc
        y[i] = acc
    return y


def siren() -> np.ndarray:
    """A hand-cranked air raid siren: two slow wind-ups and wind-downs, ~22 seconds."""
    # Pitch envelope as (time, frequency) keyframes.
    keys = [
        (0.0, 90), (4.5, 610), (7.5, 640), (12.0, 160),
        (15.5, 600), (18.0, 630), (22.0, 80),
    ]
    duration = keys[-1][0]
    t = np.arange(int(duration * RATE)) / RATE
    freq = np.interp(t, [k[0] for k in keys], [k[1] for k in keys])
    # Slight wobble, like the rotor is not perfectly steady.
    freq *= 1 + 0.012 * np.sin(2 * np.pi * 5.3 * t)
    phase = 2 * np.pi * np.cumsum(freq) / RATE

    # Mechanical sirens are rich in harmonics; two rotor ports a fifth apart give it its chord.
    tone = np.zeros_like(t)
    for ratio, gain in ((1.0, 1.0), (1.5, 0.55)):
        p = phase * ratio
        tone += gain * (np.sin(p) + 0.35 * np.sin(2 * p) + 0.15 * np.sin(3 * p))

    # Louder as it winds up, and a fade at the very start and end.
    loud = np.clip((freq - 80) / 500, 0.0, 1.0) ** 0.6
    fade = np.minimum(1.0, np.minimum(t / 0.5, (duration - t) / 1.5))
    out = tone * loud * fade

    # Distant, outdoor sound: a touch of echo.
    delay = int(0.23 * RATE)
    echo = np.zeros_like(out)
    echo[delay:] = out[:-delay] * 0.35
    out = out + echo
    return out / np.max(np.abs(out)) * 0.8


def static_burst(length: float) -> np.ndarray:
    """A short crackle of radio static with stuttering amplitude."""
    n = int(length * RATE)
    t = np.arange(n) / RATE
    noise = rng.uniform(-1, 1, n)
    # Band-limit it so it sounds like a little speaker, not pure hiss.
    noise = one_pole_lowpass(noise, 3800) - one_pole_lowpass(noise, 400)

    # Random choppy gating, like a signal fading in and out.
    chunk = int(0.03 * RATE)
    gates = rng.uniform(0.15, 1.0, n // chunk + 1) ** 2
    gate = np.repeat(gates, chunk)[:n]
    gate = one_pole_lowpass(gate, 60)

    # Occasional sharp crackles.
    crackle = np.zeros(n)
    for pos in rng.integers(0, n - 200, int(length * 25)):
        crackle[pos:pos + 120] += rng.uniform(-1, 1, 120) * rng.uniform(0.5, 1.0)

    envelope = np.minimum(1.0, np.minimum(t / 0.02, (length - t) / 0.08))
    out = (noise * gate * 3 + crackle * 0.5) * envelope
    return out / np.max(np.abs(out)) * 0.7


def main() -> None:
    out_dir = Path(sys.argv[1])
    out_dir.mkdir(parents=True, exist_ok=True)
    write_ogg(out_dir / "siren.ogg", siren())
    for i, length in enumerate((0.45, 0.6, 0.75), start=1):
        write_ogg(out_dir / f"static{i}.ogg", static_burst(length))


if __name__ == "__main__":
    main()
