"""Découpe par silence pour la transcription des fichiers longs.

On ne coupe qu'aux silences (jamais en plein mot), donc pas besoin de chevauchement :
chaque chunk est transcrit séparément, ses timestamps sont ré-offsetés puis concaténés.
L'alignement et la diarisation restent faits sur le fichier ENTIER en aval.
"""
from __future__ import annotations

from typing import Any, Callable, Optional

import numpy as np

SR = 16000  # whisperx.load_audio renvoie du 16 kHz mono float32


def silence_split_points(
    audio: np.ndarray,
    sr: int = SR,
    target_seconds: float = 600.0,
    min_silence_seconds: float = 0.5,
    frame_ms: float = 20.0,
    silence_db: float = -35.0,
) -> list[tuple[int, int]]:
    """Retourne des plages (start_sample, end_sample) coupées à des silences.

    On accumule l'audio jusqu'à `target_seconds`, puis on coupe au prochain silence
    suffisamment long. S'il n'y a aucun silence exploitable, on renvoie un seul chunk.
    """
    n = len(audio)
    if n == 0:
        return [(0, 0)]
    frame = max(1, int(sr * frame_ms / 1000.0))
    pad = (-n) % frame
    a = np.concatenate([audio, np.zeros(pad, dtype=audio.dtype)]) if pad else audio
    frames = a.reshape(-1, frame)
    # Calcul en float32 : evite le pic memoire d'une copie float64 des frames
    # sur les longs audios, le RMS reste correct a cette precision.
    frames32 = frames.astype(np.float32, copy=False)
    rms = np.sqrt(np.mean(frames32 * frames32, axis=1, dtype=np.float32) + 1e-12)
    ref = float(rms.max()) + 1e-12
    db = 20.0 * np.log10(rms / ref + 1e-12)
    silent = db < silence_db

    min_sil_frames = max(1, int(min_silence_seconds * 1000.0 / frame_ms))

    # Milieu de chaque région silencieuse assez longue (en samples).
    candidates: list[int] = []
    i, F = 0, len(silent)
    while i < F:
        if silent[i]:
            j = i
            while j < F and silent[j]:
                j += 1
            if (j - i) >= min_sil_frames:
                candidates.append(((i + j) // 2) * frame)
            i = j
        else:
            i += 1

    target = int(target_seconds * sr)
    points = [0]
    last = 0
    for c in candidates:
        if c - last >= target:
            points.append(c)
            last = c
    if points[-1] < n:
        points.append(n)
    points = sorted({p for p in points if 0 <= p <= n})
    ranges = [(points[k], points[k + 1]) for k in range(len(points) - 1)]
    return ranges or [(0, n)]


def chunked_transcribe(
    model: Any,
    audio: np.ndarray,
    batch_size: int,
    language: Optional[str],
    sr: int = SR,
    target_seconds: float = 600.0,
    min_silence_seconds: float = 0.5,
    log: Optional[Callable[[str], None]] = None,
) -> dict[str, Any]:
    """Transcrit un long audio par chunks (coupés au silence) et fusionne les segments."""
    ranges = silence_split_points(audio, sr, target_seconds, min_silence_seconds)
    if log:
        log(f"[engine] découpage en {len(ranges)} chunk(s) (par silence)")
    all_segments: list[dict[str, Any]] = []
    detected = language
    for idx, (start, end) in enumerate(ranges):
        if end <= start:
            continue
        offset = start / float(sr)
        if log:
            log(f"[engine] chunk {idx + 1}/{len(ranges)} : {offset:.0f}s → {end / float(sr):.0f}s")
        sub_tr = model.transcribe(audio[start:end], batch_size=batch_size, language=language)
        if detected is None:
            detected = sub_tr.get("language")
        for seg in sub_tr.get("segments", []):
            seg = dict(seg)
            # `or 0.0` : tolere un start/end absent OU None (whisperx peut renvoyer None sur un
            # segment degenere ; float(None) planterait tout le fichier). Pattern deja utilise
            # dans pipeline._normalize_segments.
            seg["start"] = (float(seg.get("start") or 0.0)) + offset
            seg["end"] = (float(seg.get("end") or 0.0)) + offset
            all_segments.append(seg)
        # Libere le pic memoire du chunk avant le suivant (limite « mkl_malloc failed »).
        del sub_tr
        import gc

        gc.collect()
    return {"segments": all_segments, "language": detected}
