"""Point d'entree CLI du moteur, appele par le service .NET.

Usage :
    transcriber-engine --request <chemin_request.json>
    transcriber-engine --selftest        (verifie l'environnement sans modele)

Le resultat (EngineResult) est ecrit en JSON sur stdout. Les logs vont sur stderr.
Code de sortie 0 si status == "ok", 1 sinon.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from . import __version__
from .models import EngineRequest, EngineResult


def _eprint(*args: object) -> None:
    print(*args, file=sys.stderr, flush=True)


def _start_heartbeat(interval: float = 60.0):
    """Emet une ligne stderr periodique pendant le traitement.

    La transcription WhisperX sur CPU est SILENCIEUSE pendant de longues minutes (un gros
    chunk peut prendre >20 min sans aucune sortie). Le watchdog d'inactivite cote .NET tue
    alors un moteur qui travaille pourtant normalement. Ce battement garantit une trace de
    vie reguliere tant que le process n'est pas reellement fige (un vrai blocage natif fige
    aussi ce thread -> plus de battement -> le watchdog joue son role). Retourne un Event a
    positionner pour arreter le battement.
    """
    import threading
    import time

    stop = threading.Event()
    t0 = time.monotonic()

    def _beat() -> None:
        while not stop.wait(interval):
            _eprint(f"[engine] traitement en cours… ({time.monotonic() - t0:.0f}s ecoulees)")

    threading.Thread(target=_beat, daemon=True).start()
    return stop


def _emit(result: EngineResult, out=None) -> int:
    out = out if out is not None else sys.stdout
    out.write(json.dumps(result.to_dict(), ensure_ascii=False))
    out.flush()
    return 0 if result.status == "ok" else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="transcriber-engine")
    parser.add_argument("--request", help="Chemin d'un fichier JSON EngineRequest.")
    parser.add_argument("--selftest", action="store_true", help="Verifie l'environnement.")
    parser.add_argument("--version", action="store_true")
    parser.add_argument("--serve-embeddings", action="store_true",
                        help="Demarre le sidecar d'embeddings (serveur TCP JSON-lines).")
    parser.add_argument("--port", type=int, default=8766, help="Port du sidecar d'embeddings.")
    parser.add_argument("--model", default=None, help="Modele d'embedding (defaut e5-small).")
    parser.add_argument("--device", default="cpu", help="cpu | cuda pour le sidecar d'embeddings.")
    parser.add_argument("--cache-dir", default=None, help="Cache des modeles (embeddings).")
    parser.add_argument(
        "--auth-token",
        default=None,
        help="Jeton d'acces local exige par le sidecar d'embeddings (facultatif).",
    )
    args = parser.parse_args(argv)

    if args.version:
        print(__version__)
        return 0

    if args.serve_embeddings:
        from .embeddings import DEFAULT_MODEL
        from .serve import serve

        return serve(
            args.port, args.model or DEFAULT_MODEL, args.cache_dir, args.device, args.auth_token
        )

    # Charge le token HF depuis .env si present, sinon variable d'environnement.
    try:
        from dotenv import load_dotenv

        load_dotenv()
    except Exception:
        pass
    hf_token = os.environ.get("HF_TOKEN") or None

    if args.selftest:
        info = {"version": __version__, "hf_token_present": bool(hf_token)}
        try:
            import torch

            info["torch"] = torch.__version__
            info["cuda_available"] = torch.cuda.is_available()
        except Exception as e:  # noqa: BLE001
            info["torch_error"] = str(e)
        try:
            import whisperx  # noqa: F401

            info["whisperx"] = "ok"
        except Exception as e:  # noqa: BLE001
            info["whisperx_error"] = str(e)
        try:
            import sentence_transformers  # noqa: F401

            info["sentence_transformers"] = "ok"
        except Exception as e:  # noqa: BLE001
            info["sentence_transformers_error"] = str(e)
        print(json.dumps(info, ensure_ascii=False, indent=2))
        return 0

    if not args.request:
        parser.error("--request est requis (ou utilisez --selftest).")

    try:
        with open(args.request, "r", encoding="utf-8-sig") as f:
            req = EngineRequest.from_dict(json.load(f))
    except Exception as e:  # noqa: BLE001
        return _emit(EngineResult(status="error", error=f"Requete illisible : {e}"))

    if req.diarization_enabled and not hf_token:
        return _emit(
            EngineResult(
                status="error",
                audio_path=req.audio_path,
                error="Diarisation activee mais HF_TOKEN absent. Voir .env.example.",
            )
        )

    # whisperx / faster-whisper ecrivent des messages sur stdout ("No language...",
    # "Detected language..."). On redirige stdout vers stderr pendant le traitement pour
    # que SEUL le JSON de resultat sorte sur le vrai stdout (lu par le service .NET).
    real_out = sys.stdout
    sys.stdout = sys.stderr
    heartbeat = _start_heartbeat()
    try:
        from . import pipeline

        _eprint(f"[engine] traitement de {req.audio_path}")
        result = pipeline.run(req, hf_token)
        _eprint(f"[engine] termine ({result.segment_count} segments, {result.speaker_count} locuteurs)")
        return _emit(result, real_out)
    except Exception as e:  # noqa: BLE001
        import traceback

        _eprint(traceback.format_exc())
        return _emit(EngineResult(status="error", audio_path=req.audio_path, error=str(e)), real_out)
    finally:
        heartbeat.set()


if __name__ == "__main__":
    raise SystemExit(main())
