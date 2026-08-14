"""Identification de locuteurs a partir de snippets de voix (enrollment).

Principe : on calcule un embedding de reference par fichier du dossier `voices/`
(le nom du fichier = nom du locuteur), puis un embedding par cluster diarise, et on
associe chaque cluster au locuteur le plus proche (cosinus) au-dela d'un seuil.

Robustesse format : pyannote lit l'audio via torchaudio, qui NE decode PAS le m4a
(entre autres). On contourne en chargeant tout audio via ffmpeg (whisperx.load_audio,
16 kHz mono) et en le passant a pyannote sous forme de forme d'onde en memoire. De plus,
au premier passage, les snippets dans un format non-WAV sont convertis en WAV
directement dans le dossier `voices/` (normalisation persistante).

Gate Hugging Face : le modele `pyannote/embedding` doit etre accepte sur le Hub.
"""
from __future__ import annotations

import glob
import os
import sys
from typing import Any, Optional

import numpy as np

SR = 16000
# Formats d'entree acceptes pour les snippets (seront normalises en WAV).
_SNIPPET_EXTS = (".wav", ".flac", ".mp3", ".m4a", ".ogg", ".aac", ".wma", ".opus")


def _log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def _load_wave(path: str) -> dict[str, Any]:
    """Charge un fichier audio via ffmpeg (whisperx) en forme d'onde memoire pour pyannote."""
    import torch
    import whisperx

    audio = whisperx.load_audio(path)  # np.float32 mono @ 16 kHz
    waveform = torch.from_numpy(audio).unsqueeze(0)  # (channel=1, time)
    return {"waveform": waveform, "sample_rate": SR}


def normalize_voices_dir(voices_dir: str) -> int:
    """Convertit en WAV, dans `voices/`, les snippets d'un format non lisible par pyannote.

    Idempotent : ne reconvertit pas si le `.wav` existe deja. Retourne le nombre de
    fichiers convertis. L'original est conserve (le chargement privilegie le .wav).
    """
    if not voices_dir or not os.path.isdir(voices_dir):
        return 0
    import shutil

    import soundfile as sf
    import whisperx

    src_bak = os.path.join(voices_dir, "_source")
    converted = 0
    for name in os.listdir(voices_dir):
        src = os.path.join(voices_dir, name)
        if not os.path.isfile(src):
            continue
        base, ext = os.path.splitext(name)
        ext = ext.lower()
        if ext == ".wav" or ext not in _SNIPPET_EXTS:
            continue
        dst = os.path.join(voices_dir, base + ".wav")
        if not os.path.exists(dst):
            try:
                audio = whisperx.load_audio(src)  # ffmpeg -> 16 kHz mono
                sf.write(dst, audio, SR, subtype="PCM_16")
                converted += 1
                _log(f"[engine] snippet normalise en WAV : {name} -> {base}.wav")
            except Exception as e:  # noqa: BLE001
                _log(f"[engine] echec conversion snippet {name} : {e}")
                continue  # on garde l'original si la conversion echoue
        # /voices ne conserve qu'un seul .wav propre par locuteur : l'original part dans _source/.
        try:
            os.makedirs(src_bak, exist_ok=True)
            shutil.move(src, os.path.join(src_bak, name))
        except Exception:
            pass
    return converted


def count_reference_voices(voices_dir: str) -> int:
    """Nombre de locuteurs de reference (noms de fichiers audio distincts dans voices/).

    Ignore le sous-dossier _source/ (originaux archives). Sert a deduire automatiquement
    le nombre de locuteurs pour la diarisation quand l'utilisateur ne l'a pas fixe.
    """
    if not voices_dir or not os.path.isdir(voices_dir):
        return 0
    names: set[str] = set()
    for f in os.listdir(voices_dir):
        p = os.path.join(voices_dir, f)
        if not os.path.isfile(p):
            continue
        base, ext = os.path.splitext(f)
        if ext.lower() in _SNIPPET_EXTS:
            names.add(base.lower())
    return len(names)


def _cosine(a: np.ndarray, b: np.ndarray) -> float:
    denom = (np.linalg.norm(a) * np.linalg.norm(b)) + 1e-9
    return float(np.dot(a, b) / denom)


def _l2(v: np.ndarray) -> np.ndarray:
    """Normalise un vecteur (norme 1). Averager des embeddings L2-normalises evite que les
    crops de forte norme dominent la moyenne du cluster."""
    n = float(np.linalg.norm(v))
    return v / n if n > 1e-9 else v


# Plancher de similarite : meme en appariement 1:1 (ou l'on nomme tout le monde), un score
# clairement absurde laisse le cluster anonyme plutot que d'y coller un nom faux avec aplomb.
# Volontairement TRES bas (les scores utiles observes vont de ~0.25 a ~0.6) : ne defait pas
# le nommage, ne bloque que le grotesque.
_ASSIGN_FLOOR = 0.10

# Duree minimale d'un extrait pour un embedding fiable. 0.3 s etait trop court (embeddings
# bruites) : on releve a 1.0 s.
_MIN_CROP_SECONDS = 1.0


class SpeakerIdentifier:
    """Encapsule le modele d'embedding et le catalogue de voix de reference."""

    def __init__(self, hf_token: Optional[str], device: str):
        from . import _compat

        _compat.apply_speechbrain_patch()
        import torch
        from pyannote.audio import Inference, Model  # import tardif (lourd)

        model = Model.from_pretrained("pyannote/embedding", use_auth_token=hf_token)
        self._inference = Inference(model, window="whole", device=torch.device(device))
        self._references: dict[str, np.ndarray] = {}

    def load_voices(self, voices_dir: str) -> int:
        """Normalise puis charge tous les snippets d'un dossier. Retourne le nombre de voix."""
        self._references.clear()
        if not voices_dir or not os.path.isdir(voices_dir):
            return 0

        # Premier passage : convertit les formats non-WAV en WAV, dans le dossier voices/.
        normalize_voices_dir(voices_dir)

        # On charge en priorite les .wav (normalises) ; tout autre format restant est tente
        # via ffmpeg en memoire pour robustesse.
        seen: set[str] = set()
        for path in sorted(glob.glob(os.path.join(voices_dir, "*.wav"))):
            name = os.path.splitext(os.path.basename(path))[0]
            if name in seen:
                continue
            try:
                self._references[name] = np.asarray(self._inference(_load_wave(path))).reshape(-1)
                seen.add(name)
            except Exception as e:  # noqa: BLE001
                _log(f"[engine] snippet illisible {os.path.basename(path)} : {e}")

        for ext in _SNIPPET_EXTS:
            if ext == ".wav":
                continue
            for path in glob.glob(os.path.join(voices_dir, "*" + ext)):
                name = os.path.splitext(os.path.basename(path))[0]
                if name in seen:
                    continue
                try:
                    self._references[name] = np.asarray(self._inference(_load_wave(path))).reshape(-1)
                    seen.add(name)
                except Exception as e:  # noqa: BLE001
                    # Coherent avec la boucle .wav : on trace pourquoi une voix de reference n'a
                    # pas pu etre chargee, au lieu d'un abandon silencieux.
                    _log(f"[engine] snippet illisible {os.path.basename(path)} : {e}")
                    continue
        return len(self._references)

    def _embed_crop(self, file: dict[str, Any], start: float, end: float) -> Optional[np.ndarray]:
        from pyannote.core import Segment

        if end - start < _MIN_CROP_SECONDS:  # trop court pour un embedding fiable
            return None
        try:
            emb = self._inference.crop(file, Segment(start, end))
            return np.asarray(emb).reshape(-1)
        except Exception:
            return None

    def identify(
        self,
        audio_path: str,
        speaker_segments: dict[str, list[tuple[float, float]]],
        threshold: float,
    ) -> dict[str, tuple[str, float]]:
        """Associe chaque label diarise (SPEAKER_xx) a un nom + score.

        Le fichier source est charge une seule fois via ffmpeg (gere le m4a et consorts).
        """
        result: dict[str, tuple[str, float]] = {}
        if not self._references:
            return result

        try:
            main = _load_wave(audio_path)  # chargement ffmpeg unique (robuste au format)
        except Exception as e:  # noqa: BLE001
            _log(f"[engine] identification : audio source illisible : {e}")
            return result

        # Embedding moyen par cluster diarise (on ignore le bucket "SPEAKER_?" = mots non attribues).
        # Chaque crop est L2-normalise avant moyenne, et on prend jusqu'a 8 extraits les plus longs.
        clusters: dict[str, np.ndarray] = {}
        durations: dict[str, float] = {}
        for label, spans in speaker_segments.items():
            if label == "SPEAKER_?":
                continue
            durations[label] = sum(e2 - s for s, e2 in spans)
            spans_sorted = sorted(spans, key=lambda s: s[1] - s[0], reverse=True)[:8]
            embeddings = [
                _l2(e) for s, e2 in spans_sorted if (e := self._embed_crop(main, s, e2)) is not None
            ]
            if embeddings:
                clusters[label] = _l2(np.mean(np.vstack(embeddings), axis=0))
        if not clusters:
            return result

        labels = list(clusters.keys())
        ref_names = list(self._references.keys())
        # Matrice de similarite cosinus [clusters x voix de reference].
        scores = np.array(
            [[_cosine(clusters[lbl], self._references[rn]) for rn in ref_names] for lbl in labels]
        )

        # --- Diagnostic : matrice complete cluster -> (voix=score), pour comprendre les erreurs.
        for i, lbl in enumerate(labels):
            parts = ", ".join(f"{ref_names[j]}={scores[i, j]:.2f}" for j in range(len(ref_names)))
            _log(f"[engine] id-scores {lbl} ({durations.get(lbl, 0.0):.0f}s) : {parts}")

        if len(labels) <= len(ref_names):
            # On connait les locuteurs (autant ou moins de clusters que de voix) : appariement
            # optimal 1:1 (chaque cluster recoit sa voix la plus proche, globalement). On nomme
            # tout le monde SAUF si le meilleur score est sous le plancher (match grotesque).
            try:
                from scipy.optimize import linear_sum_assignment

                rows, cols = linear_sum_assignment(-scores)
                pairs = list(zip(rows.tolist(), cols.tolist()))
            except Exception:  # noqa: BLE001
                pairs, used = [], set()
                order = sorted(
                    ((scores[i, j], i, j) for i in range(len(labels)) for j in range(len(ref_names))),
                    reverse=True,
                )
                taken_lbl: set[int] = set()
                for _s, i, j in order:
                    if i in taken_lbl or j in used:
                        continue
                    pairs.append((i, j))
                    taken_lbl.add(i)
                    used.add(j)
            for i, j in pairs:
                sc = float(scores[i, j])
                if sc >= _ASSIGN_FLOOR:
                    result[labels[i]] = (ref_names[j], sc)
                    _log(f"[engine] id {labels[i]} -> {ref_names[j]} ({sc:.2f})")
                else:
                    _log(f"[engine] id {labels[i]} -> ANONYME (meilleur {ref_names[j]} {sc:.2f} < {_ASSIGN_FLOOR})")
        else:
            # Plus de clusters que de voix connues (invite non enrole) : argmax + seuil,
            # les clusters sous le seuil restent anonymes.
            for i, lbl in enumerate(labels):
                j = int(np.argmax(scores[i]))
                sc = float(scores[i, j])
                if sc >= threshold:
                    result[lbl] = (ref_names[j], sc)
                    _log(f"[engine] id {lbl} -> {ref_names[j]} ({sc:.2f})")
                else:
                    _log(f"[engine] id {lbl} -> ANONYME (meilleur {ref_names[j]} {sc:.2f} < seuil {threshold:.2f})")

        return result
