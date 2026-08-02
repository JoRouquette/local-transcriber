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
| `LocalTranscriber.Core` | Config, file de jobs (SQLite), miroir des chemins, invocation du moteur, index FTS5 + vecteurs, recherche hybride | .NET 8 (lib) |
| `LocalTranscriber.Service` | Hôte ASP.NET Core installable en service Windows : surveille le dossier, traite la file, indexe (FTS + vecteurs), pilote le sidecar d'embeddings, et **sert le MCP en HTTP** | ASP.NET Core + Worker |
| `LocalTranscriber.Gui` | GUI de paramétrage (dossiers, moteur, projets, snippets, service, file d'attente) | WPF |
| `LocalTranscriber.Mcp` | Bibliothèque d'outils et de ressources MCP (hébergée par le service) | SDK MCP C# |
| `engine/` | Transcription + alignement + diarisation + identification de locuteurs ; **sidecar d'embeddings** (e5-small) pour la recherche sémantique | Python (WhisperX + pyannote + sentence-transformers), gelé PyInstaller |

**Installeur léger** : le paquet ne contient que l'app .NET (petite), la **source** du moteur Python et le binaire **`uv`**. Au **premier lancement**, l'application met en place l'environnement Python (uv installe Python 3.11 + PyTorch CPU + les dépendances pinnées) dans `%LOCALAPPDATA%\LocalTranscriber\engine-env`, puis l'exe console `transcriber-engine.exe` sert de moteur ET de **sidecar d'embeddings résident** (`--serve-embeddings`). Les modèles (Whisper, pyannote, e5-small) se téléchargent au premier usage. Ce choix évite de geler torch (~plusieurs Go) et garde l'installeur bien sous la limite GitHub de 2 Go.

### Recherche

- **FTS5** (mots-clés) + **sémantique** (vecteurs e5-small multilingues, cosinus) fusionnés par **Reciprocal Rank Fusion**. La sémantique est le moteur principal ; les mots-clés rattrapent les termes exacts (noms propres, CIP).
- Le service est **seul rédacteur** de l'index (`index.db`) ; le MCP, dans le même processus, ne fait que **lire**.

## Fonctionnement

1. Vous déposez des audios dans `WatchRoot\<Projet>\...`.
2. Le service détecte les fichiers stables, les met en file (idempotent : jamais deux fois le même).
3. Le moteur transcrit (langue auto), aligne, diarise, et — si activé — identifie les locuteurs via des snippets de voix.
4. Les sorties sont écrites dans `OutputRoot\<Projet>\...` en `.md`, `.json`, `.srt`, `.txt`.
5. Le service indexe les sorties (FTS + vecteurs sémantiques via le sidecar d'embeddings).
6. Le service expose le MCP en HTTP local ; Claude Desktop liste, cherche (hybride) et lit les transcriptions, y compris comme ressources attachables.

## Prérequis

**Pour utiliser l'app installée :** Windows 10/11 (x64). Rien d'autre — tout est empaqueté. (Node/`npx` seulement si vous branchez Claude Desktop via le pont `mcp-remote` ; inutile avec l'URL native.)

**Pour builder :**

- [.NET SDK 8](https://dotnet.microsoft.com/download) + charge de travail Desktop (WPF).
- Outil Velopack : `dotnet tool install -g vpk`.
- Un compte Hugging Face gratuit (voir ci-dessous), pour la diarisation à l'exécution.

Python **n'est pas requis au build** : l'installeur embarque `uv`, qui met en place l'environnement Python 3.11 sur la machine de l'utilisateur au premier lancement.

## Jeton Hugging Face

La diarisation pyannote nécessite un jeton **gratuit** :

1. Créez un compte sur <https://huggingface.co>.
2. Acceptez les conditions de ces modèles (bouton « Agree ») :
   - <https://huggingface.co/pyannote/speaker-diarization-3.1>
   - <https://huggingface.co/pyannote/segmentation-3.0>
   - <https://huggingface.co/pyannote/embedding> (seulement si vous utilisez l'identification par snippets)
3. Générez un token « read » : <https://huggingface.co/settings/tokens>.
4. Renseignez-le dans **la GUI → onglet Général → « Jeton Hugging Face »**, puis Enregistrer.

Le token est stocké dans les paramètres de l'application (`config.json`, local à la machine, hors dépôt) — adapté à une distribution open source : rien n'est codé en dur, chaque utilisateur met le sien. Si le champ est vide, l'application retombe sur la variable d'environnement `HF_TOKEN`, puis sur un fichier `.env` à côté de l'exécutable. Le token est transmis au moteur uniquement par variable d'environnement du sous-processus (jamais écrit dans un fichier temporaire).

## Build

L'installeur est **léger** : il ne gèle pas le moteur. `build.ps1` publie l'app .NET, y copie la **source** du moteur Python et télécharge **`uv`**, puis packe avec Velopack.

```powershell
# Prérequis build : .NET SDK 8 + outil Velopack (dotnet tool install -g vpk). Python N'EST PAS requis au build.
.\build\build.ps1 -Version 0.1.0
```

L'installeur est produit dans `build\Releases\` (petit — quelques dizaines de Mo).

> `build\build-engine.ps1` (gel PyInstaller) reste disponible pour un usage optionnel, mais n'est plus utilisé par la chaîne d'installation.

## Installation

Lancez l'installeur `LocalTranscriber-Setup.exe` de `build\Releases\`. Velopack installe l'app dans `%LOCALAPPDATA%\LocalTranscriber` et gère les mises à jour.

**Mises à jour automatiques** : l'app vérifie les nouvelles releases GitHub au lancement. Onglet **À propos** → case *Installer automatiquement les mises à jour* (activée par défaut) : la nouvelle version est téléchargée puis installée à la fermeture. Décochée, l'app prévient et attend un clic sur *Installer et redémarrer*. Un bouton *Rechercher les mises à jour* permet aussi une vérification manuelle. (Actif uniquement sur l'app installée, pas en développement.)

Ensuite, dans la GUI, onglet **Service & File** :

1. **Installer / réinstaller le moteur** — au premier lancement, met en place l'environnement Python (uv installe Python 3.11 + PyTorch + dépendances). Nécessite une connexion Internet ; plusieurs minutes. Le journal s'affiche pendant l'installation.
2. **Installer le service** puis **Démarrer** (déclenche l'UAC — le service tourne en tâche de fond, au démarrage de Windows).

Tant que le moteur n'est pas installé, le service détecte et met en file les audios mais ne lance aucune transcription.

## Configuration (GUI)

Ouvrez **LocalTranscriber** (la GUI) :

- **Général** : dossier surveillé, dossier de sortie, cache modèles, réglages moteur par défaut (modèle, *device* `auto`/`cuda`/`cpu`, type de calcul, langue `auto`, diarisation, identification par snippets + seuil), et les **heures d'inactivité** (plages jours + heures pendant lesquelles aucune transcription n'est lancée — la détection continue, le CPU lourd s'arrête).
- **Projets** : déclarez des sous-dossiers avec des réglages spécifiques (ex. un projet en modèle `medium`, un autre avec identification de locuteurs activée). Un fichier hors projet utilise les réglages globaux. Bouton **(Re)traiter** pour re-mettre en file tous les audios d'un projet.
- **Service & File** : installer/démarrer/arrêter le service, suivre la file d'attente et les erreurs, et **Retraiter** le fichier sélectionné (force son retraitement même s'il a déjà été transcrit).

### Surveillance du dossier

Le service **sonde** le dossier toutes les `stabilization_seconds` (5 s par défaut) : scan récursif, détection des fichiers **stables** (non modifiés depuis ce délai, non verrouillés), **empreinte de contenu** pour ne jamais retranscrire deux fois le même fichier. Les fichiers déjà vus et inchangés ne sont pas re-hachés (économie CPU). Pendant les **heures d'inactivité**, la détection/mise en file continue mais aucune transcription n'est lancée.

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

- Le dossier `voices\` (nom configurable) contient un fichier audio par personne ; **le nom du fichier = le nom du locuteur**. Il sert de référence pour remplacer `SPEAKER_00` par un vrai nom, et n'est jamais transcrit. Guide complet + script de préparation : [`docs/voice-snippets.md`](docs/voice-snippets.md).
- Le `.md` est le format pensé pour Claude/MCP : frontmatter (source, langue, durée, locuteurs) + dialogue par tour de parole horodaté.

## Brancher Claude Desktop (MCP)

Le service expose le MCP en **HTTP local** sur `http://127.0.0.1:<mcpPort>/mcp` (défaut `8765`, `127.0.0.1` uniquement). Deux façons de le brancher dans `%APPDATA%\Claude\claude_desktop_config.json` (voir `docs\claude_desktop_config.example.json`) :

Pont `mcp-remote` (compatible partout, nécessite Node/`npx`) :

```json
{
  "mcpServers": {
    "local-transcriber": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:8765/mcp"]
    }
  }
}
```

URL native (si votre version de Claude Desktop accepte les serveurs MCP par URL) :

```json
{ "mcpServers": { "local-transcriber": { "url": "http://127.0.0.1:8765/mcp" } } }
```

Redémarrez Claude Desktop.

**Outils** : `list_projects`, `list_transcripts`, `get_speakers`, `search_transcripts(query, mode=hybrid|semantic|keyword, project?, speaker?, limit?)`, `get_transcript(path, speaker?, offset?, limit?)` (paginé, `next_offset`).

**Ressources** : chaque transcription est adressable en `transcript://{projet}/{fichier}` (Markdown), attachable directement dans une conversation.

## Exécuter sans installer (dev)

```powershell
# Moteur : selftest de l'environnement Python
cd engine ; python -m transcriber_engine --selftest

# Service en console (surveille + indexe + démarre le sidecar + sert le MCP HTTP)
dotnet run --project src\LocalTranscriber.Service
#   -> MCP disponible sur http://127.0.0.1:8765/mcp

# GUI
dotnet run --project src\LocalTranscriber.Gui
```

Le serveur MCP n'est plus un exécutable séparé : il est hébergé par le service (HTTP). Le sidecar d'embeddings (`transcriber-engine.exe --serve-embeddings`) est lancé automatiquement par le service.

En dev, `config.json` est cherché dans `%PROGRAMDATA%\LocalTranscriber\` ; créez-le via la GUI (bouton Enregistrer) ou copiez `config.example.json`.

## Dépannage

- **« Diarisation activée mais HF_TOKEN absent »** : créez le `.env` (voir plus haut) et vérifiez que vous avez accepté les conditions des modèles pyannote.
- **Rien ne se traite** : vérifiez que le service est démarré (onglet Service), que `WatchRoot`/`OutputRoot` sont bien configurés, et que l'extension du fichier est dans `fileTypes`.
- **Très lent** : en CPU, baissez le modèle (`medium`/`small`) et gardez `computeType = int8`. Un GPU NVIDIA accélère fortement (build `-Cuda`, `device = cuda`).
- **Un fichier a échoué** : consultez la colonne *Erreur* de la file et le journal d'événements Windows (source `LocalTranscriber`).
- **Modèles qui se retéléchargent** : vérifiez que `ModelCacheDir` pointe vers un dossier persistant et accessible par le compte du service.

## Versionnement & releases (CI/CD)

Les versions suivent **SemVer** et sont publiées automatiquement par GitHub Actions
(`.github/workflows/release.yml`) à partir de **commits conventionnels** :

- `feat: …` → version mineure, `fix: …` → version corrective, `feat!:` ou `BREAKING CHANGE:` → version majeure.
- Les autres types (`docs`, `ci`, `chore`, `refactor`, `test`, `build`) ne déclenchent **aucune** release.

Déroulé sur `main` : un job Linux exécute **semantic-release** en *dry-run* (calcul de la
version), puis, si une release est justifiée, un job `windows-latest` construit l'installeur
léger (.NET + source moteur + `uv` + `vpk pack`) et **Velopack** crée le tag `vX.Y.Z`, la GitHub
Release et publie `Setup.exe` + paquets (ce qui alimente l'**auto-update** de l'app).

Notes :

- **Première release** : il faut au moins un commit `feat:`/`fix:` — les commits non conventionnels ne déclenchent rien. Sinon, poser un tag initial `v0.1.0` comme point de départ.
- **Taille** : l'installeur embarque le moteur gelé (torch, etc.), l'asset est volumineux ; surveiller la limite GitHub de 2 Go/fichier au premier build.
- **À venir (GitFlow)** : une branche `develop` sera ajoutée en canal *prerelease* (beta) — extension simple de `.releaserc.json` (branche `develop` en `prerelease`) et du déclencheur du workflow.

## Licences

Composants tous gratuits et open source : WhisperX (BSD), faster-whisper/CTranslate2 (MIT), pyannote.audio (MIT, modèles sous conditions Hugging Face), SDK MCP C# (MIT), Velopack (MIT), PyInstaller (GPL avec exception permettant la distribution d'exécutables). Les poids Whisper sont sous licence MIT (OpenAI).
