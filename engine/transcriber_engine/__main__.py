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


def _emit(result: EngineResult) -> int:
    sys.stdout.write(json.dumps(result.to_dict(), ensure_ascii=False))
    sys.stdout.flush()
    return 0 if result.status == "ok" else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="transcriber-engine")
    parser.add_argument("--request", help="Chemin d'un fichier JSON EngineRequest.")
    parser.add_argument("--selftest", action="store_true", help="Verifie l'environnement.")
    parser.add_argument("--version", action="store_true")
    args = parser.parse_args(argv)

    if args.version:
        print(__version__)
        return 0

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
        print(json.dumps(info, ensure_ascii=False, indent=2))
        return 0

    if not args.request:
        parser.error("--request est requis (ou utilisez --selftest).")

    try:
        with open(args.request, "r", encoding="utf-8") as f:
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

    try:
        from . import pipeline

        _eprint(f"[engine] traitement de {req.audio_path}")
        result = pipeline.run(req, hf_token)
        _eprint(f"[engine] termine ({result.segment_count} segments, {result.speaker_count} locuteurs)")
        return _emit(result)
    except Exception as e:  # noqa: BLE001
        import traceback

        _eprint(traceback.format_exc())
        return _emit(EngineResult(status="error", audio_path=req.audio_path, error=str(e)))


if __name__ == "__main__":
    raise SystemExit(main())
