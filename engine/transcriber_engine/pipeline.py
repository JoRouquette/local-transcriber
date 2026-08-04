"""Orchestration : transcription (WhisperX) -> alignement -> diarisation -> identification."""
from __future__ import annotations

import datetime as _dt
import os
from typing import Any, Optional

from . import __version__, writers
from .device import resolve_compute_type, resolve_device
from .models import EngineRequest, EngineResult, SpeakerInfo


def _load_diarization_pipeline(hf_token: Optional[str], device: str):
    """Charge le pipeline de diarisation en tolerant les evolutions d'API de whisperx."""
    import torch

    torch_device = torch.device(device)
    try:
        from whisperx.diarize import DiarizationPipeline  # whisperx recent
    except Exception:
        from whisperx import DiarizationPipeline  # ancienne position
    try:
        return DiarizationPipeline(use_auth_token=hf_token, device=torch_device)
    except AttributeError as e:
        # Pipeline.from_pretrained renvoie None si le token est invalide ou si les
        # conditions des modeles pyannote ne sont pas acceptees -> .to() casse.
        raise RuntimeError(
            "Diarisation indisponible : token Hugging Face invalide ou conditions non acceptees. "
            "Acceptez-les (une fois) sur https://hf.co/pyannote/speaker-diarization-3.1 et "
            "https://hf.co/pyannote/segmentation-3.0, puis reessayez."
        ) from e


def _normalize_segments(segments: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for seg in segments:
        out.append(
            {
                "start": float(seg.get("start") or 0.0),
                "end": float(seg.get("end") or 0.0),
                "text": (seg.get("text") or "").strip(),
                "speaker_label": seg.get("speaker") or "SPEAKER_?",
                "speaker_name": None,
            }
        )
    return out


def _speaker_spans(segments: list[dict[str, Any]]) -> dict[str, list[tuple[float, float]]]:
    spans: dict[str, list[tuple[float, float]]] = {}
    for seg in segments:
        spans.setdefault(seg["speaker_label"], []).append((seg["start"], seg["end"]))
    return spans


def run(req: EngineRequest, hf_token: Optional[str]) -> EngineResult:
    from . import _compat
    _compat.apply_speechbrain_patch()  # avant tout chargement whisperx/pyannote
    import whisperx

    result = EngineResult(audio_path=req.audio_path, engine_version=__version__)

    device = resolve_device(req.device)
    compute_type = resolve_compute_type(req.compute_type, device)
    cache = req.model_cache_dir or None

    # 1. Transcription
    audio = whisperx.load_audio(req.audio_path)
    duration = len(audio) / 16000.0
    lang = None if req.language == "auto" else req.language
    model = whisperx.load_model(
        req.model_size, device, compute_type=compute_type, language=lang, download_root=cache
    )
    threshold_seconds = float(getattr(req, "chunk_threshold_minutes", 20)) * 60.0
    if getattr(req, "chunking_enabled", False) and duration > threshold_seconds:
        import sys as _sys

        from . import chunking

        tr = chunking.chunked_transcribe(
            model,
            audio,
            batch_size=req.batch_size,
            language=lang,
            target_seconds=float(req.chunk_minutes) * 60.0,
            min_silence_seconds=float(req.chunk_min_silence_seconds),
            log=lambda m: print(m, file=_sys.stderr, flush=True),
        )
    else:
        tr = model.transcribe(audio, batch_size=req.batch_size, language=lang)
    detected_lang = tr.get("language", req.language)

    # 2. Alignement (timestamps au mot)
    try:
        model_a, metadata = whisperx.load_align_model(
            language_code=detected_lang, device=device, model_dir=cache
        )
        tr = whisperx.align(
            tr["segments"], model_a, metadata, audio, device, return_char_alignments=False
        )
    except Exception:
        # L'alignement peut echouer sur certaines langues : on garde les segments bruts.
        pass

    speakers: list[SpeakerInfo] = []

    # 3. Diarisation
    if req.diarization_enabled:
        diarize = _load_diarization_pipeline(hf_token, device)
        diarize_segments = diarize(
            audio, min_speakers=req.min_speakers, max_speakers=req.max_speakers
        )
        tr = whisperx.assign_word_speakers(diarize_segments, tr)

    segments = _normalize_segments(tr.get("segments", []))
    spans = _speaker_spans(segments)

    # 4. Identification par snippets de voix (optionnelle)
    id_map: dict[str, tuple[str, float]] = {}
    if req.speaker_id_enabled and req.voices_dir:
        try:
            import sys

            from .speaker_id import SpeakerIdentifier

            identifier = SpeakerIdentifier(hf_token, device)
            n = identifier.load_voices(req.voices_dir)
            print(f"[engine] snippets de voix charges : {n}", file=sys.stderr, flush=True)
            if n > 0:
                id_map = identifier.identify(req.audio_path, spans, req.speaker_id_threshold)
                print(f"[engine] locuteurs identifies : {len(id_map)}/{len(spans)}", file=sys.stderr, flush=True)
        except Exception as e:  # noqa: BLE001
            import sys as _sys
            import traceback

            print(f"[engine] identification ignoree : {e}", file=_sys.stderr, flush=True)
            traceback.print_exc()
            id_map = {}

    for seg in segments:
        if seg["speaker_label"] in id_map:
            seg["speaker_name"] = id_map[seg["speaker_label"]][0]

    for label in sorted(spans.keys()):
        name, conf = id_map.get(label, (None, None))
        speakers.append(SpeakerInfo(label=label, name=name, confidence=conf))

    # 5. Ecriture des sorties
    os.makedirs(req.output_dir, exist_ok=True)
    # Defensif : on ne garde que le nom de fichier pour empecher qu'un base_name
    # du type "..\..\x" ne fasse ecrire hors de output_dir.
    base = os.path.join(req.output_dir, os.path.basename(req.base_name))
    meta = {
        "source_file": req.audio_path,
        "transcribed_at": _dt.datetime.now().isoformat(timespec="seconds"),
        "language": detected_lang,
        "duration_seconds": duration,
        "speaker_count": len(spans),
        "model_size": req.model_size,
        "speakers": [s.__dict__ for s in speakers],
    }

    if req.output_json:
        result.json_path = base + ".json"
        writers.write_json(result.json_path, {"metadata": meta, "segments": segments})
    if req.output_text:
        result.text_path = base + ".txt"
        writers.write_text(result.text_path, segments)
    if req.output_srt:
        result.srt_path = base + ".srt"
        writers.write_srt(result.srt_path, segments)
    if req.output_markdown:
        result.markdown_path = base + ".md"
        writers.write_markdown(result.markdown_path, segments, meta)

    result.status = "ok"
    result.duration_seconds = duration
    result.language = detected_lang
    result.speaker_count = len(spans)
    result.segment_count = len(segments)
    result.speakers = speakers
    return result
