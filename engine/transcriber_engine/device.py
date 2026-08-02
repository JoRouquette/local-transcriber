"""Resolution du peripherique de calcul (device configurable : auto / cuda / cpu)."""
from __future__ import annotations


def resolve_device(requested: str) -> str:
    requested = (requested or "auto").lower()
    if requested in ("cuda", "gpu"):
        return "cuda"
    if requested == "cpu":
        return "cpu"
    # auto : CUDA si disponible, sinon CPU
    try:
        import torch

        return "cuda" if torch.cuda.is_available() else "cpu"
    except Exception:
        return "cpu"


def resolve_compute_type(requested: str, device: str) -> str:
    requested = (requested or "auto").lower()
    if requested != "auto":
        return requested
    # Defaut sensé : float16 sur GPU, int8 sur CPU (rapide et leger).
    return "float16" if device == "cuda" else "int8"
