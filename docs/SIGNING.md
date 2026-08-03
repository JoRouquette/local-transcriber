# Signature de code

Deux voies en place : un **certificat auto-signé** (immédiat, pour les machines de confiance)
et, à terme, **SignPath Foundation** (publiquement reconnu, dès approbation).

## Voie 1 — Certificat auto-signé (actif)

Le certificat de signature (thumbprint dans le magasin `Cert:\CurrentUser\My`, nom
« LocalTranscriber Code Signing ») a été généré via `New-SelfSignedCertificate`. Le PFX et
sa version base64 + mot de passe se trouvent sous `C:\ProgramData\LocalTranscriber-signing\`
sur le poste du mainteneur (à sécuriser / supprimer après import dans les secrets).

**Secrets GitHub à définir** (repo → Settings → Secrets and variables → Actions) :

- `SIGNING_PFX_BASE64` : contenu de `PFX_BASE64.txt`.
- `SIGNING_PFX_PASSWORD` : contenu de `PFX_PASSWORD.txt`.

Une fois définis, la CI signe automatiquement l'installeur (via `build.ps1` →
`vpk pack --signParams`, avec horodatage DigiCert). Sans ces secrets, le build reste non signé.

**Faire confiance au certificat** sur une machine (PowerShell **en administrateur**) — sinon
la signature auto-signée reste « non approuvée » :

```powershell
$cer = "C:\ProgramData\LocalTranscriber-signing\lt-codesign.cer"
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Limite : un certificat auto-signé n'est reconnu que sur les machines où il est approuvé
(ton poste, ou un parc via GPO). Sur une machine tierce, la signature apparaît « non
approuvée ». D'où la voie 2 pour une diffusion large.

## Voie 2 — SignPath Foundation (gratuit, open source)

Fournit **gratuitement** un certificat de
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
