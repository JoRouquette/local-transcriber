# Brancher LocalTranscriber à Claude Desktop (MCP)

LocalTranscriber expose ses transcriptions à Claude Desktop via un **serveur MCP** servi
en HTTP local. Une fois branché, vous pouvez demander à Claude, en langage naturel, de
chercher dans vos transcriptions, de lire un compte rendu, de résumer une réunion, etc.

Ce guide détaille la configuration pas à pas. Un fichier d'exemple prêt à copier est fourni :
[`claude_desktop_config.example.json`](./claude_desktop_config.example.json).

## Prérequis

- LocalTranscriber installé et le **worker démarré**. Le serveur MCP est hébergé par le
  worker : s'il est arrêté, l'endpoint MCP est injoignable. Dans l'application, onglet
  **Traitements et fichiers**, vérifiez que le worker est « En cours d'exécution » et que la
  pastille **MCP** est verte.
- **Claude Desktop** installé (le MCP n'est pas utilisé par l'app web ni mobile).
- Pour la méthode « pont » (variante B) uniquement : **Node.js** (fournit `npx`).

## Étape 1 — Repérer l'endpoint MCP

Le serveur écoute sur `http://127.0.0.1:<mcpPort>/mcp`, sur `127.0.0.1` uniquement (jamais
exposé au réseau). Le port par défaut est **8765**.

Vous retrouvez l'URL exacte dans l'application, onglet **À propos**, ligne « Endpoint MCP » —
c'est cette valeur qu'il faut reporter dans la configuration. Si vous avez changé `mcp_port`
dans la configuration de l'app, adaptez le port en conséquence.

## Étape 2 — Ouvrir le fichier de configuration de Claude Desktop

Le fichier se trouve à :

```
%APPDATA%\Claude\claude_desktop_config.json
```

(Collez ce chemin dans la barre d'adresse de l'Explorateur, ou dans Exécuter `Win+R`.)

Vous pouvez aussi y accéder depuis Claude Desktop : **Paramètres → Développeur → Modifier la
configuration**. Si le fichier n'existe pas encore, créez-le avec `{ "mcpServers": {} }`.

## Étape 3 — Ajouter le serveur

Choisissez **une** des deux variantes et fusionnez-la dans la clé `mcpServers`.

### Variante A — URL native (recommandée si votre version la supporte)

```json
{
  "mcpServers": {
    "local-transcriber": {
      "url": "http://127.0.0.1:8765/mcp"
    }
  }
}
```

C'est la plus simple : aucune dépendance, Claude Desktop se connecte directement à l'URL HTTP.

### Variante B — Pont `mcp-remote` (compatible partout, nécessite Node.js)

Si votre version de Claude Desktop ne reconnaît pas les serveurs déclarés par `url`, passez
par le pont `mcp-remote`, qui relaie le transport pour vous :

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

> Si vous avez déjà d'autres serveurs sous `mcpServers`, ajoutez simplement l'entrée
> `local-transcriber` à côté des existantes — ne remplacez pas tout le bloc.

## Étape 4 — Redémarrer Claude Desktop

Fermez Claude Desktop **complètement** (quittez aussi l'icône dans la zone de notification,
près de l'horloge — une simple fermeture de fenêtre ne suffit pas), puis relancez-le. La
configuration MCP n'est lue qu'au démarrage.

## Étape 5 — Vérifier

Dans une conversation Claude Desktop, l'outil MCP `local-transcriber` doit apparaître (icône
outils / connecteurs). Testez avec une demande du type :

> Cherche dans mes transcriptions les passages où l'on parle de « budget ».

Claude doit pouvoir lister, chercher (recherche hybride mots-clés + sémantique) et lire vos
transcriptions.

## Dépannage

- **Aucun outil `local-transcriber` visible** : vérifiez que le JSON est valide (une virgule
  oubliée suffit à invalider tout le fichier), puis redémarrez Claude Desktop complètement.
- **Erreur de connexion / endpoint injoignable** : le worker est probablement arrêté. Dans
  l'app, onglet **Traitements et fichiers**, démarrez le worker et attendez que la pastille
  **MCP** passe au vert. Le serveur MCP n'existe que tant que le worker tourne.
- **Le port ne correspond pas** : l'URL de la config doit utiliser le même port que celui
  affiché dans l'onglet **À propos** (défaut 8765).
- **Variante B : `npx` introuvable** : installez Node.js, ou utilisez plutôt la variante A.
- **Rien ne remonte alors que tout est branché** : assurez-vous qu'au moins un fichier a été
  transcrit (l'index est alimenté par les sorties du worker) et que le dossier de sortie est
  bien celui configuré.

## Authentification par jeton (optionnelle, machine multi-comptes)

Par défaut, le serveur MCP n'exige pas de jeton : la restriction à `127.0.0.1` suffit sur une
machine mono-utilisateur. Sur une **machine partagée** (plusieurs comptes Windows), un autre
utilisateur pourrait toutefois interroger `127.0.0.1:8765` et lire vos transcriptions. Vous
pouvez alors exiger un **jeton d'accès local** :

1. Dans la configuration de l'app (`config.json`), passez `mcp_require_token` à `true`, puis
   redémarrez le service (onglet **Traitements et fichiers** → **Redémarrer**).
2. Dans l'onglet **À propos**, la ligne « Endpoint MCP » affiche désormais l'URL **avec le
   jeton** (`http://127.0.0.1:8765/mcp?token=…`). C'est cette URL complète qu'il faut reporter
   dans `claude_desktop_config.json` (à la place de l'URL sans jeton), puis redémarrer Claude
   Desktop complètement.

Le jeton peut aussi être fourni via l'en-tête `Authorization: Bearer <jeton>` si votre client
le permet. Sans jeton (ou avec un jeton erroné), le serveur répond `401`.

## Notes de sécurité

Le serveur MCP écoute exclusivement sur `127.0.0.1` (boucle locale) : il n'est pas accessible
depuis le réseau. Tout reste sur votre machine ; aucune donnée n'est envoyée à l'extérieur par
LocalTranscriber. Le jeton d'accès (voir ci-dessus) est stocké sous votre profil utilisateur
(`%LOCALAPPDATA%`), lisible par vous seul.
