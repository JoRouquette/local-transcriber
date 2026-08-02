# LocalTranscriber

Application Windows locale et gratuite qui **surveille un dossier**, **transcrit et diarise** automatiquement les fichiers audio qui y arrivent, écrit les résultats dans un dossier de sortie miroir, et expose le tout à **Claude Desktop via un serveur MCP** pour pouvoir interroger les transcriptions en langage naturel.

Tout tourne **en local** : aucun service payant, aucune donnée envoyée à un tiers (hors téléchargement initial des modèles).

## Sommaire

- [Architecture](#architecture)
- [Fonctionnement](#fonctionnement)
- [Prérequis](#prérequis)
- [Jeton Hugging Face (gratuit, requis pour la diarisation)](#jeton-hugging-face)
- [Build](#build)
- [Installation](#installation)
- [Configuration (GUI)](#configuration-gui)
- [Conventions de dossiers](#conventions-de-dossiers)
- [Brancher Claude Desktop (MCP)](#brancher-claude-desktop-mcp)
- [Exécuter sans installer (dev)](#exécuter-sans-installer-dev)
- [Dépannage](#dépannage)
- [Licences](#licences)

## Architecture

Une seule solution .NET 8 (`LocalTranscriber.sln`) + un moteur Python gelé :

| Composant | Rôle | Techno |
|---|---|---|
| `LocalTranscriber.Core` | Config, file de jobs (SQLite), miroir des chemins, invocation du moteur, index de recherche FTS5 | .NET 8 (lib) |
| `LocalTranscriber.Service` | Service Windows : surveille le dossier, traite la file, rafraîchit l'index | .NET Worker Service |
| `LocalTranscriber.Gui` | GUI de paramétrage (dossiers, moteur, projets, snippets, service, file d'attente) | WPF |
| `LocalTranscriber.Mcp` | Serveur MCP stdio interrogé par Claude Desktop | SDK MCP C# |
| `engine/` | Transcription + alignement + diarisation + identification de locuteurs | Python (WhisperX + pyannote), gelé PyInstaller |

Le moteur Python est **gelé en un exécutable autonome** (`transcriber-engine.exe`) : l'utilisateur final n'a **pas** besoin d'installer Python. Les modèles (Whisper, pyannote) se téléchargent au premier traitement dans le cache local.

## Fonctionnement

1. Vous déposez des audios dans `WatchRoot\<Projet>\...`.
2. Le service détecte les fichiers stables, les met en file (idempotent : jamais deux fois le même).
3. Le moteur transcrit (langue auto), aligne, diarise, et — si activé — identifie les locuteurs via des snippets de voix.
4. Les sorties sont écrites dans `OutputRoot\<Projet>\...` en `.md`, `.json`, `.srt`, `.txt`.
5. Le serveur MCP indexe les sorties ; Claude Desktop peut lister, chercher et lire les transcriptions.

## Prérequis

**Pour utiliser l'app installée :** Windows 10/11 (x64). Rien d'autre — tout est empaqueté.

**Pour builder :**

- [.NET SDK 8](https://dotnet.microsoft.com/download) + charge de travail Desktop (WPF).
- [Python 3.11](https://www.python.org/downloads/) (pour geler le moteur).
- Outil Velopack : `dotnet tool install -g vpk`
- (Optionnel, build GPU) pilotes NVIDIA + CUDA 12.x.
- Un compte Hugging Face gratuit (voir ci-dessous).

## Jeton Hugging Face

La diarisation pyannote nécessite un jeton **gratuit** :

1. Créez un compte sur <https://huggingface.co>.
2. Acceptez les conditions de ces modèles (bouton « Agree ») :
   - <https://huggingface.co/pyannote/speaker-diarization-3.1>
   - <https://huggingface.co/pyannote/segmentation-3.0>
   - <https://huggingface.co/pyannote/embedding> (seulement si vous utilisez l'identification par snippets)
3. Générez un token « read » : <https://huggingface.co/settings/tokens>.
4. Copiez `.env.example` en `.env` (à côté de l'exécutable installé, ou à la racine du repo en dev) et collez le token :

   ```
   HF_TOKEN=hf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
   ```

Le token n'est jamais écrit dans les fichiers de config ou temporaires : il transite uniquement par la variable d'environnement `HF_TOKEN`.

## Build

```powershell
# 1. Geler le moteur Python (CPU par défaut ; -Cuda pour GPU)
.\build\build-engine.ps1            # ou : .\build\build-engine.ps1 -Cuda

# 2. Build complet + installeur Velopack
.\build\build.ps1 -Version 0.1.0    # ajoute -Cuda pour un build GPU
#    (utilisez -SkipEngine si le moteur est déjà gelé)
```

L'installeur est produit dans `build\Releases\`.

> Les versions de paquets (`ModelContextProtocol`, `Microsoft.Data.Sqlite`, PyTorch, WhisperX…) sont des points de départ. Au premier build, vérifiez/mettez à jour vers les dernières versions stables (`dotnet add package`, `pip install -U`).

## Installation

Lancez l'installeur `LocalTranscriber-Setup.exe` de `build\Releases\`. Velopack installe l'app dans `%LOCALAPPDATA%\LocalTranscriber` et gère les mises à jour.

Ensuite, depuis la GUI (onglet **Service & File**) : **Installer le service** puis **Démarrer** (déclenche l'UAC — le service tourne en tâche de fond, au démarrage de Windows).

## Configuration (GUI)

Ouvrez **LocalTranscriber** (la GUI) :

- **Général** : dossier surveillé, dossier de sortie, cache modèles, et réglages moteur par défaut (modèle, *device* `auto`/`cuda`/`cpu`, type de calcul, langue `auto`, diarisation, identification par snippets + seuil).
- **Projets** : déclarez des sous-dossiers avec des réglages spécifiques (ex. un projet en modèle `medium`, un autre avec identification de locuteurs activée). Un fichier hors projet utilise les réglages globaux.
- **Service & File** : installer/démarrer/arrêter le service, suivre la file d'attente et les erreurs.

**Enregistrer** écrit `config.json` dans `%PROGRAMDATA%\LocalTranscriber\` ; le service le recharge automatiquement.

## Conventions de dossiers

```
WatchRoot\
  Reunions\                 <- un "projet"
    voices\                 <- snippets de voix (optionnel) : Jonathan.wav, Marie.wav...
    2026-08-01_comite.mp3
  Interviews\
    entretien_01.m4a

OutputRoot\                 <- miroir de l'arborescence
  Reunions\
    2026-08-01_comite.md
    2026-08-01_comite.json
    2026-08-01_comite.srt
    2026-08-01_comite.txt
```

- Le dossier `voices\` (nom configurable) contient un fichier audio par personne ; **le nom du fichier = le nom du locuteur**. Il sert de référence pour remplacer `SPEAKER_00` par un vrai nom, et n'est jamais transcrit.
- Le `.md` est le format pensé pour Claude/MCP : frontmatter (source, langue, durée, locuteurs) + dialogue par tour de parole horodaté.

## Brancher Claude Desktop (MCP)

Ajoutez le serveur MCP au fichier de config de Claude Desktop
(`%APPDATA%\Claude\claude_desktop_config.json`), voir `docs\claude_desktop_config.example.json` :

```json
{
  "mcpServers": {
    "local-transcriber": {
      "command": "C:\\Users\\<vous>\\AppData\\Local\\LocalTranscriber\\current\\LocalTranscriber.Mcp.exe",
      "args": []
    }
  }
}
```

Redémarrez Claude Desktop. Outils exposés : `list_projects`, `list_transcripts`, `search_transcripts` (recherche plein-texte, filtrable par projet/locuteur) et `get_transcript`.

## Exécuter sans installer (dev)

```powershell
# Moteur : selftest de l'environnement Python
cd engine ; python -m transcriber_engine --selftest

# Service en console (Ctrl+C pour arrêter)
dotnet run --project src\LocalTranscriber.Service

# GUI
dotnet run --project src\LocalTranscriber.Gui

# Serveur MCP (stdio) — normalement lancé par Claude Desktop
dotnet run --project src\LocalTranscriber.Mcp
```

En dev, `config.json` est cherché dans `%PROGRAMDATA%\LocalTranscriber\` ; créez-le via la GUI (bouton Enregistrer) ou copiez `config.example.json`.

## Dépannage

- **« Diarisation activée mais HF_TOKEN absent »** : créez le `.env` (voir plus haut) et vérifiez que vous avez accepté les conditions des modèles pyannote.
- **Rien ne se traite** : vérifiez que le service est démarré (onglet Service), que `WatchRoot`/`OutputRoot` sont bien configurés, et que l'extension du fichier est dans `fileTypes`.
- **Très lent** : en CPU, baissez le modèle (`medium`/`small`) et gardez `computeType = int8`. Un GPU NVIDIA accélère fortement (build `-Cuda`, `device = cuda`).
- **Un fichier a échoué** : consultez la colonne *Erreur* de la file et le journal d'événements Windows (source `LocalTranscriber`).
- **Modèles qui se retéléchargent** : vérifiez que `ModelCacheDir` pointe vers un dossier persistant et accessible par le compte du service.

## Licences

Composants tous gratuits et open source : WhisperX (BSD), faster-whisper/CTranslate2 (MIT), pyannote.audio (MIT, modèles sous conditions Hugging Face), SDK MCP C# (MIT), Velopack (MIT), PyInstaller (GPL avec exception permettant la distribution d'exécutables). Les poids Whisper sont sous licence MIT (OpenAI).
