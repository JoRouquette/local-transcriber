"""Orchestration : transcription (WhisperX) -> alignement -> diarisation -> identification."""
from __future__ import annotations

import datetime as _dt
import os
import sys
import time
from typing import Any, Optional

from . import __version__, writers
from .device import resolve_compute_type, resolve_device
from .models import EngineRequest, EngineResult, SpeakerInfo


def _log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


class _Stopwatch:
    """Chronometre les etapes du pipeline sur stderr.

    Sans cette mesure, tout arbitrage sur le cout des etapes (alignement, batch, moteur
    resident) se fait a l'aveugle : on ne sait pas ou passe le temps mural. Les lignes
    produites sont recuperees par le service (EngineLogSink) et visibles dans la GUI.
    """

    def __init__(self) -> None:
        self._t0 = time.monotonic()
        self._last = self._t0

    def mark(self, label: str) -> None:
        now = time.monotonic()
        _log(
            f"[engine] etape {label} : {now - self._last:.1f}s "
            f"(cumul {now - self._t0:.1f}s)"
        )
        self._last = now


def _free_memory(device: str) -> None:
    """Rend effectivement la memoire des modeles liberes (gc + cache CUDA)."""
    import gc

    gc.collect()
    if device == "cuda":
        try:
            import torch

            torch.cuda.empty_cache()
        except Exception:  # noqa: BLE001
            pass


def _resolve_cpu_threads(requested: int) -> tuple[int, int]:
    """Nombre de threads CPU pour l'ASR (CTranslate2), et nombre de processeurs logiques.

    whisperx.load_model a un defaut code en dur de `threads=4`, propage en `cpu_threads` vers
    CTranslate2 -- et faster-whisper documente qu'une valeur non nulle ECRASE OMP_NUM_THREADS.
    Sans passer ce parametre, l'ASR (l'etape dominante sur CPU) reste donc a 4 threads quelle
    que soit la machine, sans aucun moyen de le corriger par l'environnement.

    Auto (0) = moitie des processeurs logiques, avec un plancher a 4 : sur une machine avec
    hyperthreading cela approche le nombre de coeurs physiques (les GEMM int8 ne gagnent rien a
    saturer les jumeaux logiques), et le plancher garantit de ne jamais faire PIRE qu'avant.
    Sur une machine sans hyperthreading, fixer explicitement cpu_threads au nombre de coeurs.
    """
    logical = os.cpu_count() or 4
    if requested and requested > 0:
        return max(1, requested), logical
    return max(4, logical // 2), logical


def _probe_duration_seconds(path: str) -> Optional[float]:
    """Sonde legere de la duree audio (lecture d'en-tete via PyAV, sans decoder tout le flux)."""
    try:
        import av  # dependance de faster-whisper

        with av.open(path) as container:
            if container.duration is not None:
                return float(container.duration) / 1_000_000.0  # AV_TIME_BASE = 1e6 us
            for stream in container.streams:
                if stream.duration is not None and stream.time_base is not None:
                    return float(stream.duration * stream.time_base)
    except Exception:
        return None
    return None


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
    except Exception as e:  # noqa: BLE001
        # On distingue un echec reseau (telechargement du modele pyannote) d'un autre echec
        # de chargement, pour un message actionnable cote utilisateur.
        msg = str(e).lower()
        network = ("connection", "timed out", "timeout", "network", "resolve", "getaddr",
                   "temporarily", "ssl", "max retries", "connexion")
        if any(tok in msg for tok in network):
            raise RuntimeError(
                "Diarisation indisponible : echec reseau lors du telechargement du modele "
                "pyannote. Verifiez la connexion internet, puis reessayez."
            ) from e
        raise RuntimeError(
            f"Diarisation indisponible : echec du chargement du modele pyannote ({e})."
        ) from e


def _normalize_segments(segments: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for seg in segments:
        start = float(seg.get("start") or 0.0)
        end = float(seg.get("end") or 0.0)
        # Garantit end >= start : un segment mal aligne a duree negative casserait certains
        # lecteurs SRT et l'ordre d'affichage.
        if end < start:
            end = start
        out.append(
            {
                "start": start,
                "end": end,
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

    # Bornage defensif : une requete malformee ne doit pas produire un comportement aberrant
    # (chunk_minutes<=0 -> sur-decoupage en micro-chunks ; min>max transmis a pyannote ; seuil
    # hors [0,1]).
    if req.chunk_minutes < 1:
        req.chunk_minutes = 1
    if req.chunk_threshold_minutes < 1:
        req.chunk_threshold_minutes = 1
    if req.batch_size < 1:
        req.batch_size = 1
    req.speaker_id_threshold = min(1.0, max(0.0, float(req.speaker_id_threshold)))
    if (
        req.min_speakers is not None
        and req.max_speakers is not None
        and req.min_speakers > req.max_speakers
    ):
        req.min_speakers, req.max_speakers = req.max_speakers, req.min_speakers

    device = resolve_device(req.device)
    compute_type = resolve_compute_type(req.compute_type, device)
    cache = req.model_cache_dir or None
    watch = _Stopwatch()

    # 1. Transcription
    # Garde-fou memoire : on refuse d'emblee un fichier trop long AVANT de charger tout l'audio
    # en RAM (whisperx.load_audio decode le flux entier en float32 16 kHz), via une sonde legere.
    max_minutes = getattr(req, "max_audio_minutes", 0) or 0
    if max_minutes > 0:
        probed = _probe_duration_seconds(req.audio_path)
        if probed is not None and probed > max_minutes * 60.0:
            result.status = "error"
            result.duration_seconds = probed
            result.error = (
                f"Fichier trop long ({probed / 60:.0f} min > limite {max_minutes} min). "
                "Augmentez max_audio_minutes ou decoupez le fichier."
            )
            return result

    try:
        audio = whisperx.load_audio(req.audio_path)
    except Exception as e:  # noqa: BLE001
        result.status = "error"
        result.error = (
            f"Fichier audio illisible ou format non supporte : "
            f"{os.path.basename(req.audio_path)} ({e})"
        )
        return result
    duration = len(audio) / 16000.0

    # Fichier vide/silencieux : inutile d'engager le modele, message clair.
    if duration <= 0.0 or len(audio) == 0:
        result.status = "error"
        result.error = (
            f"Fichier audio vide ou sans piste exploitable : {os.path.basename(req.audio_path)}."
        )
        return result
    watch.mark("decodage audio")
    lang = None if req.language == "auto" else req.language
    cpu_threads, logical_cpus = _resolve_cpu_threads(getattr(req, "cpu_threads", 0) or 0)
    if device == "cpu":
        _log(f"[engine] threads ASR : {cpu_threads} (processeurs logiques : {logical_cpus})")
    model = whisperx.load_model(
        req.model_size,
        device,
        compute_type=compute_type,
        language=lang,
        download_root=cache,
        threads=cpu_threads,
    )
    watch.mark(f"chargement modele ASR ({req.model_size}/{compute_type})")
    # Sur CPU, un batch_size eleve consomme beaucoup de RAM (cause de « mkl_malloc: failed to
    # allocate memory ») pour un gain de vitesse faible : on le plafonne. On raccourcit aussi la
    # taille de chunk pour reduire le pic memoire par appel de transcription.
    effective_batch = req.batch_size
    chunk_target = float(req.chunk_minutes) * 60.0
    if device == "cpu":
        effective_batch = max(1, min(req.batch_size, 4))
        chunk_target = min(chunk_target, 300.0)  # 5 min max par chunk sur CPU
        # Ces plafonds ecrasaient les reglages en silence : le batch_size choisi dans la GUI
        # devenait 4 sans un mot, ce qui rend tout diagnostic de lenteur impossible.
        if effective_batch != req.batch_size:
            _log(
                f"[engine] batch_size {req.batch_size} -> {effective_batch} (plafond CPU memoire)"
            )

    threshold_seconds = float(getattr(req, "chunk_threshold_minutes", 20)) * 60.0
    if getattr(req, "chunking_enabled", False) and duration > threshold_seconds:
        import sys as _sys

        from . import chunking

        tr = chunking.chunked_transcribe(
            model,
            audio,
            batch_size=effective_batch,
            language=lang,
            target_seconds=chunk_target,
            min_silence_seconds=float(req.chunk_min_silence_seconds),
            log=lambda m: print(m, file=_sys.stderr, flush=True),
        )
    else:
        tr = model.transcribe(audio, batch_size=effective_batch, language=lang)
    detected_lang = tr.get("language", req.language)
    watch.mark("transcription")

    # L'ASR ne sert plus : on libere ses poids (~1,5 Go en int8 pour large-v3) AVANT de charger
    # le modele d'alignement puis pyannote. Aucune liberation n'existait : les cinq modeles
    # restaient vivants jusqu'au retour de run(), ce qui est la cause la plus probable des
    # « mkl_malloc: failed to allocate memory » qui ont motive le plafonnement du batch a 4.
    del model
    _free_memory(device)

    # 2. Alignement (timestamps au mot)
    try:
        model_a, metadata = whisperx.load_align_model(
            language_code=detected_lang, device=device, model_dir=cache
        )
        tr = whisperx.align(
            tr["segments"], model_a, metadata, audio, device, return_char_alignments=False
        )
        del model_a, metadata
        _free_memory(device)
        watch.mark("alignement")
    except Exception as e:  # noqa: BLE001
        # L'alignement peut echouer sur certaines langues : on garde les segments bruts, mais on
        # trace (un echec systematique doit rester visible dans les logs, pas muet).
        _log(f"[engine] alignement ignore : {e}")
        # Le modele d'alignement a pu etre charge avant l'echec : on ne le laisse pas en memoire
        # pendant la diarisation.
        if "model_a" in locals():
            del model_a
        _free_memory(device)
        watch.mark("alignement (echoue)")

    speakers: list[SpeakerInfo] = []

    # Nombre de voix de reference : calcule UNE seule fois, utilise deux fois (indice du nombre
    # de locuteurs pour pyannote, et decision de charger ou non le modele d'embedding vocal).
    ref_voice_count = 0
    if req.speaker_id_enabled and req.voices_dir:
        from .speaker_id import count_reference_voices

        ref_voice_count = count_reference_voices(req.voices_dir)

    # 3. Diarisation
    if req.diarization_enabled:
        min_spk, max_spk = req.min_speakers, req.max_speakers
        # Si aucun nombre n'est fixe et que des voix de reference existent, on deduit le
        # nombre de locuteurs du dossier voices/ (evite la sur-segmentation de pyannote).
        if min_spk is None and max_spk is None and ref_voice_count > 0:
            min_spk = max_spk = ref_voice_count
            _log(f"[engine] {ref_voice_count} locuteur(s) deduit(s) du dossier voices/")

        diarize = _load_diarization_pipeline(hf_token, device)
        diarize_segments = diarize(audio, min_speakers=min_spk, max_speakers=max_spk)
        # assign_word_speakers assigne le locuteur au niveau segment PUIS au niveau de chaque mot
        # (4 operations pandas + un groupby PAR MOT). Or rien en aval ne lit les locuteurs par
        # mot : _normalize_segments ne conserve que start/end/text/speaker. On retire donc les
        # mots avant l'appel -- l'assignation par segment, la seule utilisee, reste calculee par
        # le code de whisperx lui-meme (il ne lit que seg['start'] et seg['end']), donc a
        # l'identique. Aucune sortie ne change.
        for seg in tr.get("segments", []):
            seg.pop("words", None)
        tr = whisperx.assign_word_speakers(diarize_segments, tr)
        del diarize, diarize_segments
        _free_memory(device)
        watch.mark("diarisation")

    segments = _normalize_segments(tr.get("segments", []))
    spans = _speaker_spans(segments)

    # Diagnostic diarisation : combien de clusters et quelle duree de parole chacun. Permet de
    # distinguer une erreur de diarisation (mauvais clusters) d'une erreur d'identification (mauvais
    # nom colle sur un bon cluster) en lisant simplement les logs du traitement.
    if req.diarization_enabled and spans:
        diag = ", ".join(
            f"{lbl}={sum(e - s for s, e in sp):.0f}s"
            for lbl, sp in sorted(spans.items())
        )
        _log(f"[engine] diarisation : {len(spans)} cluster(s) -> {diag}")

    # 4. Identification par snippets de voix (optionnelle)
    id_map: dict[str, tuple[str, float]] = {}
    # Le constructeur de SpeakerIdentifier charge pyannote/embedding (avec un aller-retour vers
    # le Hub Hugging Face, qui peut partir en timeout hors ligne). On ne le fait donc que s'il y
    # a effectivement des voix de reference a comparer -- l'option cochee avec un dossier
    # voices/ vide est un cas courant : enrolement pas encore fait.
    if req.speaker_id_enabled and req.voices_dir and ref_voice_count > 0:
        try:
            from .speaker_id import SpeakerIdentifier

            identifier = SpeakerIdentifier(hf_token, device)
            n = identifier.load_voices(req.voices_dir)
            _log(f"[engine] snippets de voix charges : {n}")
            if n > 0:
                # La forme d'onde est deja en memoire : la repasser telle quelle evite un SECOND
                # decodage ffmpeg complet du fichier source (~230 Mo de float32 par heure, plus
                # le spawn du process).
                id_map = identifier.identify(audio, spans, req.speaker_id_threshold)
                _log(f"[engine] locuteurs identifies : {len(id_map)}/{len(spans)}")
            watch.mark("identification")
        except Exception as e:  # noqa: BLE001
            import traceback

            _log(f"[engine] identification ignoree : {e}")
            traceback.print_exc()
            id_map = {}

    for seg in segments:
        if seg["speaker_label"] in id_map:
            seg["speaker_name"] = id_map[seg["speaker_label"]][0]

    for label in sorted(spans.keys()):
        name, conf = id_map.get(label, (None, None))
        speakers.append(SpeakerInfo(label=label, name=name, confidence=conf))

    # 5. Ecriture des sorties
    try:
        os.makedirs(req.output_dir, exist_ok=True)
    except OSError as e:
        result.status = "error"
        result.error = f"Impossible de creer le dossier de sortie ({req.output_dir}) : {e}"
        return result
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
    watch.mark("ecriture des sorties")

    result.status = "ok"
    result.duration_seconds = duration
    result.language = detected_lang
    result.speaker_count = len(spans)
    result.segment_count = len(segments)
    result.speakers = speakers
    return result
