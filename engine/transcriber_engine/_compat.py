"""Correctifs de compatibilité runtime.

speechbrain 1.x (tiré par pyannote.audio 3.3) enregistre des « lazy modules »
(k2_fsa, nlp, ...) qui tentent d'importer des dépendances optionnelles absentes.
Quand pytorch_lightning appelle `inspect.stack()` pendant le chargement d'un modèle,
Python accède à `__file__` de ces modules, ce qui déclenche l'import optionnel et
lève une ImportError qui casse `whisperx.load_model`.

On neutralise ça en faisant lever un AttributeError propre pour l'accès aux dunders
(`__file__`, ...) sur ces lazy modules : `inspect`/`hasattr` l'ignorent alors
gracieusement, sans jamais déclencher l'import optionnel.
"""
from __future__ import annotations


def apply_speechbrain_patch() -> None:
    try:
        from speechbrain.utils import importutils as iu
    except Exception:
        return

    if getattr(iu.LazyModule, "_lt_dunder_patched", False):
        return

    original = iu.LazyModule.__getattr__

    def safe_getattr(self, name):
        if name.startswith("__") and name.endswith("__"):
            raise AttributeError(name)
        return original(self, name)

    iu.LazyModule.__getattr__ = safe_getattr
    iu.LazyModule._lt_dunder_patched = True
