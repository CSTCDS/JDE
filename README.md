# Utils.Colas

Portail d'utilitaires JDE (.NET 10, ASP.NET Core minimal API + pages statiques).
Fusion de **Lire .h** (ex-`Utils.htm` d'appli-jade03) et **JDE Compare** (ex-NERcompare).

## Structure

| Élément | Rôle |
|---|---|
| `Program.cs` | Hébergement : HTTPS, auth Windows (Negotiate), en-têtes de sécurité, cache, `/health`, API communes (`/api/version`, `/api/windows-user`, `/api/connections`) |
| `Infrastructure/OracleConnections.cs` | Modèle de connexion Oracle unifié + `ConnectionStore` (déchiffrement `ENC:`) |
| `Modules/LireH/` | API `/api/lireh/*` (lecture/téléchargement des en-têtes `.h`) |
| `Modules/JdeCompare/` | API `/api/jdecompare/*` (comparaison des specs, ObjectBrowser) |
| `wwwroot/index.html` | Menu, alimenté par `wwwroot/config/utilities.json` |
| `wwwroot/common/` | Bandeau partagé (`banner.js` / `banner.css`, sélecteur de connexion) |
| `wwwroot/lireh/`, `wwwroot/jdecompare/` | Pages des utilitaires |
| `Tools/ObjectBrowser/` | Binaires ObjectBrowser (**non versionnés**, à copier depuis NERcompare) |
| `tests/Utils.Colas.Tests/` | Tests xUnit (auth, cache, en-têtes, endpoints, logique de comparaison) |
| `build-update.ps1`, `Deploy/Install-Update.template.ps1` | Construction et installation des livraisons UPD (voir plus bas) |

### Ajouter un utilitaire

1. Ajouter une entrée dans `wwwroot/config/utilities.json` (`id`, `title`, `description`, `url`, `icon`).
2. Créer `wwwroot/<id>/index.html` avec `<div id="banner"></div>` et `<script src="../common/banner.js"></script>` ; appeler l'API via `UtilsColas.api('api/<id>/...')`.
3. Si besoin d'une API : créer `Modules/<Id>/<Id>Endpoints.cs` (`MapGroup("/api/<id>")`) et l'enregistrer dans `Program.cs`.
4. Ajouter les tests correspondants.

## Connexions Oracle

- Définies dans `appsettings.json` → `OracleConnections` (sans identifiants).
- Identifiants dans `secrets.json` (copie de `secrets.template.json`, **jamais versionné ni publié**).
- Valeurs chiffrées préfixées `ENC:` : DPAPI (compatible lire.h) puis repli DataProtection (compatible NERcompare, clés dans `keys/`).
  ```powershell
  powershell -ExecutionPolicy Bypass -File Tools\encrypt-secrets.ps1 -Machine   # -Machine : scope LocalMachine (serveur IIS)
  ```
- Rotation : régénérer les valeurs `ENC:` puis recycler le pool IIS.
- JDE Compare : la connexion de référence est choisie dans le bandeau (cookie `JDE-Connection`) ;
  `JdeCompare:Environments` associe chaque environnement JDE à une connexion (ordre = source/cible par défaut).

## Exécution locale

Pré-requis : SDK .NET 10 (installé dans `%USERPROFILE%\.dotnet` sur ce poste).

```powershell
copy secrets.template.json secrets.json   # puis renseigner UserId/Password
powershell -ExecutionPolicy Bypass -File .\run-local.ps1          # https://localhost:6070
powershell -ExecutionPolicy Bypass -File .\run-local.ps1 -Test    # tests
```

Sans `secrets.json`, l'application démarre (avertissement) ; seules les requêtes Oracle échouent.

## Livraison sur le serveur IIS (UPD)

Le serveur n'est pas accessible directement : les mises à jour sont livrées par **copier-coller** de deux fichiers.
Le site IIS de production pointe sur `C:\sites\JDE` ; son nom et son pool sont détectés automatiquement d'après ce chemin.

### 1. Construire la livraison (poste de développement)

```powershell
git commit ...                                                    # la livraison doit correspondre à un commit
powershell -ExecutionPolicy Bypass -File .\build-update.ps1      # tests + publication
```

Résultat dans `livraisons\` (non versionné) :

| Fichier | Contenu |
|---|---|
| `UPDyyyyMMdd-HHmm.zip` | publication complète (`site\`) + `manifest.json` (identifiant, commit, auteur) |
| `Install-UPDyyyyMMdd-HHmm.ps1` | script d'installation, avec l'empreinte SHA-256 du zip intégrée |

L'identifiant UPD est injecté dans l'assembly : il apparaît dans `/api/version` (`release`) et en pied de page de JDE Compare.

### 2. Installer (serveur)

Copier les deux fichiers dans un même dossier **local** du serveur, en dehors du site (ex. `C:\Livraisons\Utils.Colas\`), puis :

- **clic droit sur `Install-UPD….ps1` > « Exécuter avec PowerShell »** : le script redemande lui-même les droits
  administrateur (fenêtre UAC) et la fenêtre reste ouverte à la fin pour lire le résultat ;
- ou dans une console : `powershell -ExecutionPolicy Bypass -File .\Install-UPD20261029-1706.ps1`

Si Windows bloque le script (fichier marqué « provenant d'un autre ordinateur ») : *Propriétés > Débloquer* sur le `.ps1`.
Éviter un lecteur réseau mappé : il n'est pas visible depuis la session administrateur ouverte par l'UAC.

| Étape | Détail |
|---|---|
| 1. Contrôles | SHA-256 du zip, Hosting Bundle .NET 10, site IIS sur `C:\sites\JDE`, contenu du zip. **Rien n'est arrêté tant que ces contrôles ne sont pas passés.** |
| 2. Arrêt | site + pool (si le pool est partagé : `app_offline.htm` à la place de l'arrêt du pool) |
| 3. Sauvegarde | `C:\sites\JDE.bak-<horodatage>` ; les 5 plus récentes sont conservées (`-KeepBackups`) |
| 4. Publication | copie **miroir** (les fichiers absents de la livraison sont supprimés) ; conservés : `secrets.json`, `appsettings.Production.json`, `keys\`, `logs\`, binaires ObjectBrowser (remplacés seulement s'ils sont livrés) ; droits NTFS du pool ; auth Windows imposée, anonyme désactivé |
| 5. Redémarrage | pool + site |
| 6. Vérification | `GET /health` ; si l'application répond en erreur : **retour arrière automatique**. Si le résultat est non concluant (certificat, 401 IIS…), l'opérateur confirme manuellement. |

Options : `-SitePath` (défaut `C:\sites\JDE`), `-HealthUrl https://<hôte>/health`, `-KeepBackups 5`,
`-NoPause` (pas d'attente de la touche Entrée, pour une exécution automatisée ; code de sortie 0 = OK, 1 = échec).
Journal : `C:\sites\JDE\logs\deploy\` (transcription, robocopy, `history.log` = historique des livraisons).

**Retour arrière manuel** (avec n'importe quel script `Install-UPD*.ps1`) :

```powershell
.\Install-UPD20261029-1706.ps1 -Rollback                                      # dernière sauvegarde
.\Install-UPD20261029-1706.ps1 -Rollback -BackupPath C:\sites\JDE.bak-20261029-170612
```

**Première installation** : le site IIS doit exister (pool *No Managed Code*, liaison HTTPS) et le Hosting Bundle .NET 10
être installé. `secrets.json` est créé depuis le modèle : renseigner les identifiants puis les chiffrer avec
`Tools\encrypt-secrets.ps1 -Machine`.

**Checklist après livraison** : `/health` = 200 ; menu affiché avec l'utilisateur Windows ; numéro UPD visible en pied de page
de JDE Compare ; Lire .h lit un objet ; JDE Compare charge les statuts ; aucun Ctrl+F5 nécessaire.

### Installation directe (serveur accessible avec le SDK)

`deploy-iis.ps1` publie et crée/configure le site IIS en une fois :

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy-iis.ps1 -SitePath C:\sites\JDE [-CertThumbprint <thumbprint>]
```

## Cache

HTML : `no-store` ; autres fichiers statiques : `no-cache` (revalidation ETag). Aucun Ctrl+F5 nécessaire après mise à jour.
