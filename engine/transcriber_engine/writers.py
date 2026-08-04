"""Ecriture des sorties : JSON structure, texte brut, Markdown (LLM-friendly), SRT."""
from __future__ import annotations

import datetime as _dt
import json
import os
from typing import Any, Optional


def _fmt_ts(seconds: float) -> str:
    if seconds is None:
        seconds = 0.0
    td = _dt.timedelta(seconds=float(seconds))
    total = int(td.total_seconds())
    h, rem = divmod(total, 3600)
    m, s = divmod(rem, 60)
    return f"{h:02d}:{m:02d}:{s:02d}"


def display_speaker(seg: dict[str, Any]) -> str:
    return seg.get("speaker_name") or seg.get("speaker_label") or "Inconnu"


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)


def write_text(path: str, segments: list[dict[str, Any]]) -> None:
    with open(path, "w", encoding="utf-8") as f:
        for seg in segments:
            f.write((seg.get("text") or "").strip() + "\n")


def write_srt(path: str, segments: list[dict[str, Any]], with_speaker: bool = True) -> None:
    try:
        import srt as _srt
    except Exception:
        _write_srt_manual(path, segments, with_speaker)
        return

    subs = []
    for i, seg in enumerate(segments, start=1):
        text = (seg.get("text") or "").strip()
        if with_speaker:
            text = f"[{display_speaker(seg)}] {text}"
        subs.append(
            _srt.Subtitle(
                index=i,
                start=_dt.timedelta(seconds=float(seg.get("start") or 0.0)),
                end=_dt.timedelta(seconds=float(seg.get("end") or 0.0)),
                content=text,
            )
        )
    with open(path, "w", encoding="utf-8") as f:
        f.write(_srt.compose(subs))


def _write_srt_manual(path: str, segments: list[dict[str, Any]], with_speaker: bool) -> None:
    def ts(sec: float) -> str:
        td = _dt.timedelta(seconds=float(sec or 0.0))
        total_ms = int(td.total_seconds() * 1000)
        h, rem = divmod(total_ms, 3_600_000)
        m, rem = divmod(rem, 60_000)
        s, ms = divmod(rem, 1000)
        return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"

    with open(path, "w", encoding="utf-8") as f:
        for i, seg in enumerate(segments, start=1):
            text = (seg.get("text") or "").strip()
            if with_speaker:
                text = f"[{display_speaker(seg)}] {text}"
            f.write(f"{i}\n{ts(seg.get('start'))} --> {ts(seg.get('end'))}\n{text}\n\n")


def write_markdown(
    path: str,
    segments: list[dict[str, Any]],
    meta: dict[str, Any],
) -> None:
    """Markdown avec frontmatter + dialogue groupe par tour de parole.

    Format pense pour l'exploitation par un LLM/MCP.
    """
    speakers = meta.get("speakers", [])
    speaker_line = ", ".join(
        (sp.get("name") or sp.get("label")) for sp in speakers
    ) if speakers else "n/a"

    # json.dumps produit une scalaire JSON (guillemets + echappement) qui est un
    # YAML valide : evite qu'un " dans source_file/speakers casse le frontmatter.
    lines: list[str] = []
    lines.append("---")
    lines.append(f'source: {json.dumps(str(meta.get("source_file", "")))}')
    lines.append(f'transcribed_at: {json.dumps(str(meta.get("transcribed_at", "")))}')
    lines.append(f'language: {json.dumps(str(meta.get("language", "")))}')
    lines.append(f'duration: {json.dumps(_fmt_ts(meta.get("duration_seconds", 0.0)))}')
    lines.append(f"speaker_count: {meta.get('speaker_count', 0)}")
    lines.append(f'speakers: {json.dumps(speaker_line)}')
    lines.append(f'engine: {json.dumps("whisperx " + str(meta.get("model_size", "")))}')
    lines.append("---")
    lines.append("")
    lines.append(f"# Transcription — {os.path.basename(meta.get('source_file', ''))}")
    lines.append("")

    # Regroupement des segments consecutifs du meme locuteur.
    current_speaker: Optional[str] = None
    buffer: list[str] = []
    turn_start: float = 0.0

    def flush() -> None:
        if buffer and current_speaker is not None:
            lines.append(f"**{current_speaker}** _[{_fmt_ts(turn_start)}]_ : " + " ".join(buffer).strip())
            lines.append("")

    for seg in segments:
        spk = display_speaker(seg)
        text = (seg.get("text") or "").strip()
        if not text:
            continue
        if spk != current_speaker:
            flush()
            current_speaker = spk
            buffer = []
            turn_start = float(seg.get("start") or 0.0)
        buffer.append(text)
    flush()

    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
