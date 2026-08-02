# Snippets de voix — identification des locuteurs

LocalTranscriber peut remplacer les étiquettes génériques de diarisation (`SPEAKER_00`,
`SPEAKER_01`, …) par de **vrais noms**, en comparant chaque locuteur détecté à des
**échantillons de voix de référence** (les *snippets*) que vous fournissez.

## Principe

1. Vous déposez, par projet, un dossier `voices/` contenant **un fichier audio par personne**.
2. Le **nom du fichier = le nom du locuteur** (ex. `Jonathan.wav` → étiquette « Jonathan »).
3. À chaque transcription, le moteur calcule un *embedding* (empreinte vocale) par snippet,
   puis compare chaque groupe de parole diarisé à ces empreintes. Au-delà d'un **seuil de
   similarité** (cosinus, réglable dans la GUI, défaut 0.55), l'étiquette est remplacée par le nom.

Sans `voices/` ou en dessous du seuil, les étiquettes génériques sont conservées.

## Emplacement

```
<DossierSurveillé>\<Projet>\
    voices\
        Jonathan.wav
        Marie.wav
        Karim.flac
    reunion_2026-08-01.mp3
```

Le nom du dossier (`voices`) est configurable (par projet ou globalement, champ
`voices_dir_name`). Les fichiers du dossier `voices/` ne sont **jamais transcrits** :
ils servent uniquement de référence.

## Bonnes pratiques pour un bon snippet

- **Un seul locuteur** par fichier, qui parle en continu.
- **10 à 30 secondes** de parole nette suffisent (au-delà, peu de gain).
- **Voix seule** : pas de musique, pas de deuxième voix, pas de forte réverbération.
- **Parole naturelle** (phrases normales), pas un simple « allô allô ».
- Idéalement le **même micro / contexte** que les enregistrements à transcrire.
- **Format recommandé : `.wav` (ou `.flac`), mono 16 kHz** — ce sont les formats lus
  nativement pour l'empreinte vocale. Les `.m4a`/`.mp3` peuvent ne pas être décodés pour
  l'identification : convertissez-les d'abord avec le script fourni (voir plus bas). Si
  l'identification échoue, le moteur le signale dans ses logs et garde les étiquettes
  génériques.

## Texte à lire pour l'enregistrement

Faites lire l'un de ces passages à la personne, à **voix naturelle et posée**, dans une
pièce calme. Chacun dure environ **25 à 35 secondes** — largement suffisant pour un bon
échantillon. Les textes sont volontairement variés phonétiquement.

### Français

> Bonjour, je m'appelle _(votre prénom)_ et j'enregistre ma voix pour aider le logiciel à me
> reconnaître. Ce matin, le ciel est dégagé et une légère brise traverse le jardin. J'aime
> prendre le temps de lire à voix haute, calmement, en articulant chaque mot. Le petit chat
> gris dort près de la fenêtre pendant que le café chauffe doucement dans la cuisine.
> Lorsque j'aurai terminé cette dernière phrase, l'échantillon sera assez long pour être
> utile. Merci d'avance, et bonne journée à toutes et à tous.

### English

> Hello, my name is _(your first name)_, and I'm recording my voice so the software can
> recognize me. This morning the sky is clear, and a gentle breeze drifts across the garden.
> I enjoy reading out loud, slowly and calmly, giving each word its own space. The small grey
> cat is asleep by the window while the coffee warms quietly in the kitchen. By the time I
> finish this last sentence, the sample should be long enough to be useful. Thank you very
> much, and have a wonderful day.

## Préparer un snippet avec le script fourni

Le script `scripts/prepare-voice-snippet.ps1` extrait un échantillon propre (mono 16 kHz)
à partir d'un enregistrement existant. Il nécessite **FFmpeg** dans le PATH.

```powershell
# Extraire 20 s à partir de 00:05 d'un enregistrement, pour le locuteur "Jonathan"
.\scripts\prepare-voice-snippet.ps1 -Name "Jonathan" `
    -Input "C:\enregistrements\reunion.mp3" `
    -VoicesDir "C:\Transcriptions\Inbox\Reunions\voices" `
    -Start 5 -Duration 20
```

Le fichier `Jonathan.wav` (mono, 16 kHz) est créé dans le dossier `voices/` cible.

### Enregistrer directement depuis le micro (optionnel)

Pour capturer une voix à la volée, listez d'abord vos périphériques audio :

```powershell
ffmpeg -list_devices true -f dshow -i dummy
```

puis enregistrez 20 s depuis le micro choisi (remplacez le nom exact du périphérique) :

```powershell
ffmpeg -y -f dshow -i audio="Microphone (Realtek Audio)" -t 20 -ac 1 -ar 16000 `
    "C:\Transcriptions\Inbox\Reunions\voices\Jonathan.wav"
```

## Activer l'identification

Dans la GUI, onglet **Général** : cochez **« Identifier les locuteurs (snippets de voix) »**,
ajustez le **seuil de similarité** si besoin, puis **Enregistrer**. La diarisation doit être
active (elle aussi nécessite le jeton Hugging Face). Réglages par projet possibles dans
l'onglet **Projets**.

> Note : l'identification par snippets utilise le modèle `pyannote/embedding`, également
> soumis aux conditions Hugging Face — acceptez-les une fois sur sa page (voir README).
