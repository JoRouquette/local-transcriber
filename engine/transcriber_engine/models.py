"""Contrats echanges avec le service .NET (miroir de LocalTranscriber.Core.Contracts)."""
from __future__ import annotations

from dataclasses import dataclass, field, asdict
from typing import Any, Optional


@dataclass
class EngineRequest:
    audio_path: str
    output_dir: str
    base_name: str
    language: str = "auto"
    model_size: str = "large-v3"
    device: str = "auto"
    compute_type: str = "auto"
    batch_size: int = 16
    diarization_enabled: bool = True
    min_speakers: Optional[int] = None
    max_speakers: Optional[int] = None
    speaker_id_enabled: bool = False
    voices_dir: Optional[str] = None
    speaker_id_threshold: float = 0.55
    output_markdown: bool = True
    output_json: bool = True
    output_srt: bool = True
    output_text: bool = True
    model_cache_dir: str = ""
    # Découpe par silence des fichiers longs (transcription par chunks ; alignement et
    # diarisation restent sur le fichier entier).
    chunking_enabled: bool = False
    chunk_threshold_minutes: int = 20
    chunk_minutes: int = 10
    chunk_min_silence_seconds: float = 0.5
    # Garde-fou memoire : duree audio max acceptee (min). 0 = desactive.
    max_audio_minutes: int = 480

    @staticmethod
    def from_dict(d: dict[str, Any]) -> "EngineRequest":
        known = EngineRequest.__dataclass_fields__.keys()  # type: ignore[attr-defined]
        # Trace les cles ignorees : un drift de contrat .NET <-> Python (champ renomme/retire)
        # deviendrait sinon invisible (reglages envoyes par le service silencieusement perdus).
        ignored = set(d) - set(known)
        if ignored:
            import sys

            print(
                f"[engine] cles de requete ignorees (drift de contrat ?) : {sorted(ignored)}",
                file=sys.stderr,
                flush=True,
            )
        return EngineRequest(**{k: v for k, v in d.items() if k in known})


@dataclass
class SpeakerInfo:
    label: str
    name: Optional[str] = None
    confidence: Optional[float] = None


@dataclass
class EngineResult:
    status: str = "error"
    audio_path: str = ""
    duration_seconds: float = 0.0
    language: Optional[str] = None
    speaker_count: int = 0
    segment_count: int = 0
    speakers: list[SpeakerInfo] = field(default_factory=list)
    markdown_path: Optional[str] = None
    json_path: Optional[str] = None
    srt_path: Optional[str] = None
    text_path: Optional[str] = None
    engine_version: Optional[str] = None
    error: Optional[str] = None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)
