# -*- mode: python ; coding: utf-8 -*-
# Spec PyInstaller pour geler le moteur en un executable autonome.
# Build : pyinstaller transcriber-engine.spec --noconfirm
from PyInstaller.utils.hooks import collect_all

block_cipher = None

datas, binaries, hiddenimports = [], [], []

# Ces paquets embarquent des donnees (modeles d'assets, configs, .pyd/.so) qu'il
# faut collecter explicitement pour que l'exe gele fonctionne hors venv.
_heavy_packages = [
    "whisperx",
    "faster_whisper",
    "ctranslate2",
    "pyannote",
    "pyannote.audio",
    "pyannote.core",
    "pyannote.database",
    "pyannote.metrics",
    "pyannote.pipeline",
    "asteroid_filterbanks",
    "speechbrain",
    "lightning_fabric",
    "pytorch_lightning",
    "torchaudio",
    "torch",
    "librosa",
    "soundfile",
    "transformers",
    "huggingface_hub",
    "sentence_transformers",
]

for _pkg in _heavy_packages:
    try:
        d, b, h = collect_all(_pkg)
        datas += d
        binaries += b
        hiddenimports += h
    except Exception as _e:  # le paquet peut etre absent selon l'install torch
        print(f"[spec] collect_all({_pkg}) ignore : {_e}")

a = Analysis(
    ["pyi_entry.py"],
    pathex=["."],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=["tkinter", "matplotlib.tests", "PyQt5"],
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="transcriber-engine",
    console=True,
    disable_windowed_traceback=False,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    name="transcriber-engine",
)
