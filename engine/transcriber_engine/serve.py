"""Sidecar d'embeddings resident : serveur TCP JSON-lines sur 127.0.0.1.

Protocole (une requete/reponse par ligne UTF-8 terminee par \\n) :
    -> {"texts": ["..."], "kind": "query"|"passage"}
    <- {"vectors": [[...]], "dim": 384, "model": "..."}
       ou {"error": "..."}

Le modele reste charge en memoire entre les requetes (latence basse). Le service
.NET pilote le cycle de vie de ce process.
"""
from __future__ import annotations

import json
import socket
import sys
from typing import Optional

from .embeddings import DEFAULT_MODEL, Embedder


def _eprint(*args: object) -> None:
    print(*args, file=sys.stderr, flush=True)


def serve(port: int, model_name: str, cache_dir: Optional[str], device: str = "cpu") -> int:
    _eprint(f"[embeddings] chargement du modele {model_name} ({device})...")
    embedder = Embedder(model_name, cache_dir, device)
    _eprint(f"[embeddings] pret (dim={embedder.dim})")

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", port))
    srv.listen(8)
    # Marqueur de disponibilite lisible par le service .NET.
    print(json.dumps({"ready": True, "dim": embedder.dim, "model": model_name}), flush=True)
    _eprint(f"[embeddings] a l'ecoute sur 127.0.0.1:{port}")

    try:
        while True:
            conn, _ = srv.accept()
            with conn, conn.makefile("rwb", buffering=0) as stream:
                for raw in stream:
                    line = raw.decode("utf-8").strip()
                    if not line:
                        continue
                    resp = _handle(embedder, line)
                    stream.write((json.dumps(resp) + "\n").encode("utf-8"))
    except KeyboardInterrupt:
        return 0
    finally:
        srv.close()


def _handle(embedder: Embedder, line: str) -> dict:
    try:
        req = json.loads(line)
        texts = req.get("texts", [])
        kind = req.get("kind", "passage")
        vecs = embedder.embed(texts, kind)
        return {"vectors": vecs.tolist(), "dim": embedder.dim, "model": embedder.model_name}
    except Exception as e:  # noqa: BLE001
        return {"error": str(e)}
