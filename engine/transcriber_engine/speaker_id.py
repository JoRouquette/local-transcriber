"""Identification de locuteurs a partir de snippets de voix (enrollment).

Principe : on calcule un embedding de reference par fichier du dossier `voices/`
(le nom du fichier = nom du locuteur), puis un embedding par cluster diarise, et on
associe chaque cluster au locuteur le plus proche (cosinus) au-dela d'un seuil.

Gate Hugging Face : le modele `pyannote/embedding` doit etre accepte sur le Hub
(comme les modeles de diarisation). Voir README.
"""
from __future__ import annotations

import glob
import os
from typing import Optional

import numpy as np


def _cosine(a: np.ndarray, b: np.ndarray) -> float:
    denom = (np.linalg.norm(a) * np.linalg.norm(b)) + 1e-9
    return float(np.dot(a, b) / denom)


class SpeakerIdentifier:
    """Encapsule le modele d'embedding et le catalogue de voix de reference."""

    def __init__(self, hf_token: Optional[str], device: str):
        from pyannote.audio import Inference, Model  # import tardif (lourd)
        import torch

        model = Model.from_pretrained("pyannote/embedding", use_auth_token=hf_token)
        self._inference = Inference(model, window="whole", device=torch.device(device))
        self._references: dict[str, np.ndarray] = {}

    def load_voices(self, voices_dir: str) -> int:
        """Charge tous les snippets d'un dossier. Retourne le nombre de voix chargees."""
        self._references.clear()
        if not voices_dir or not os.path.isdir(voices_dir):
            return 0
        patterns = ("*.wav", "*.flac", "*.mp3", "*.m4a", "*.ogg")
        for pattern in patterns:
            for path in glob.glob(os.path.join(voices_dir, pattern)):
                name = os.path.splitext(os.path.basename(path))[0]
                try:
                    self._references[name] = np.asarray(self._inference(path)).reshape(-1)
                except Exception:
                    # Un snippet illisible ne doit pas casser tout le traitement.
                    continue
        return len(self._references)

    def _embed_crop(self, audio_path: str, start: float, end: float) -> Optional[np.ndarray]:
        from pyannote.core import Segment

        if end - start < 0.3:  # trop court pour un embedding fiable
            return None
        try:
            emb = self._inference.crop(audio_path, Segment(start, end))
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

        speaker_segments : { "SPEAKER_00": [(start, end), ...], ... }
        Retourne : { "SPEAKER_00": ("Jonathan", 0.72), ... } (labels non resolus omis).
        """
        result: dict[str, tuple[str, float]] = {}
        if not self._references:
            return result

        for label, spans in speaker_segments.items():
            # On embed les segments les plus longs (jusqu'a 5) et on moyenne.
            spans_sorted = sorted(spans, key=lambda s: s[1] - s[0], reverse=True)[:5]
            embeddings = [e for s, e2 in spans_sorted if (e := self._embed_crop(audio_path, s, e2)) is not None]
            if not embeddings:
                continue
            cluster = np.mean(np.vstack(embeddings), axis=0)

            best_name, best_score = None, -1.0
            for name, ref in self._references.items():
                score = _cosine(cluster, ref)
                if score > best_score:
                    best_name, best_score = name, score

            if best_name is not None and best_score >= threshold:
                result[label] = (best_name, best_score)

        return result
