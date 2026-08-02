"""Embeddings multilingues (intfloat/multilingual-e5-small).

e5 exige de prefixer les entrees : "query: ..." pour une question, "passage: ..."
pour un document a indexer. On normalise les vecteurs (recherche par cosinus).
"""
from __future__ import annotations

from typing import Optional

import numpy as np

DEFAULT_MODEL = "intfloat/multilingual-e5-small"


def _apply_prefix(texts: list[str], kind: str) -> list[str]:
    prefix = "query: " if kind == "query" else "passage: "
    return [prefix + (t or "") for t in texts]


class Embedder:
    def __init__(
        self,
        model_name: str = DEFAULT_MODEL,
        cache_dir: Optional[str] = None,
        device: str = "cpu",
    ):
        from sentence_transformers import SentenceTransformer

        self.model_name = model_name
        self.model = SentenceTransformer(model_name, cache_folder=cache_dir, device=device)
        self.dim = int(self.model.get_sentence_embedding_dimension())

    def embed(self, texts: list[str], kind: str = "passage") -> np.ndarray:
        if not texts:
            return np.zeros((0, self.dim), dtype="float32")
        vecs = self.model.encode(
            _apply_prefix(texts, kind),
            normalize_embeddings=True,
            convert_to_numpy=True,
            batch_size=32,
        )
        return np.asarray(vecs, dtype="float32")
