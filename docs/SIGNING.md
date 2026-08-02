# Signature de code — SignPath Foundation (gratuit, open source)

L'installeur `LocalTranscriber-win-Setup.exe` n'est pas signé : Windows SmartScreen
affiche donc « Éditeur inconnu ». La signature Authenticode supprime cet avertissement.

Voie retenue : **SignPath Foundation**, qui fournit **gratuitement** un certificat de
signature aux projets open source, avec la clé privée dans leur HSM et une intégration
GitHub Actions. Le certificat est de type OV (le nom de l'éditeur s'affiche) ; la
réputation SmartScreen continue de se construire avec les téléchargements, mais
l'« éditeur inconnu » disparaît immédiatement.

## Prérequis (faits)

- **Licence OSI** : `LICENSE` (MIT) à la racine du dépôt.
- **Dépôt public** avec build reproductible depuis GitHub Actions (workflow `Release`).

## Étapes côté John (une seule fois)

1. Candidater sur <https://signpath.org/apply> avec l'URL du dépôt
   `https://github.com/JoRouquette/local-transcriber` et la licence MIT.
2. Après approbation, dans la console SignPath.io (organisation offerte par la Foundation) :
   - créer/valider le **projet** lié au dépôt (slug de projet) ;
   - noter l'**Organization ID**, le **slug de projet**, et le **slug de la signing policy**
     (généralement `release-signing`) ;
   - créer un **API token** utilisateur.
3. Dans GitHub → repo → *Settings → Secrets and variables → Actions*, ajouter le secret
   **`SIGNPATH_API_TOKEN`**.
4. Me transmettre : Organization ID, slug de projet, slug de signing policy. Je câble la CI.

## Câblage CI (à activer après approbation)

SignPath signe à distance : le job `build-release` construit l'installeur **non signé**,
soumet l'artefact via l'action `SignPath/github-action-submit-signing-request`, récupère
l'artefact **signé**, puis `vpk upload` publie la release.

Ordre recommandé (pour que l'auto-update soit signé de bout en bout) :

1. `dotnet publish` des exes (GUI + Service) → **signer** ces exes chez SignPath.
2. `vpk pack` (empaquète les exes signés) → produit `Setup.exe` + `nupkg`.
3. **Signer** le `Setup.exe`.
4. `vpk upload github`.

Version minimale acceptable pour lever SmartScreen à l'installation : signer au moins le
`Setup.exe` (étape 3), car c'est le fichier que l'utilisateur double-clique.

Extrait de workflow (à insérer dans `.github/workflows/release.yml`, valeurs à compléter) :

```yaml
      - name: Signer le Setup.exe (SignPath)
        uses: SignPath/github-action-submit-signing-request@v2
        with:
          api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
          organization-id: '<ORG_ID>'
          project-slug: 'local-transcriber'
          signing-policy-slug: 'release-signing'
          artifact-configuration-slug: 'setup-exe'
          github-artifact-id: '${{ steps.upload.outputs.artifact-id }}'
          wait-for-completion: true
          output-artifact-directory: build/Releases-signed
```

L'artefact signé remplace ensuite l'original avant `vpk upload`. La configuration
d'artefact (`artifact-configuration-slug`) se déclare côté SignPath (quels fichiers signer
dans l'archive soumise).

## Rappel : timestamp

SignPath horodate la signature (timestamp authority), indispensable pour que la signature
reste valide après expiration du certificat.
