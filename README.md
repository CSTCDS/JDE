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

## Déploiement IIS

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy-iis.ps1 [-CertThumbprint <thumbprint>]
```

Le script publie, sauvegarde le site (`<site>.bak-<horodatage>`), copie en préservant `secrets.json`, `keys/`, `logs/`
et les binaires ObjectBrowser, configure l'auth Windows (anonyme désactivé) puis vérifie `/health`.

**Rollback** : arrêter le pool, restaurer le dossier `.bak-<horodatage>`, redémarrer le pool.

**Checklist après déploiement** : `/health` = 200 ; menu affiché avec l'utilisateur Windows ; Lire .h lit un objet ;
JDE Compare charge les statuts ; un appel anonyme renvoie 401 ; une modification de page est visible sans Ctrl+F5.

## Cache

HTML : `no-store` ; autres fichiers statiques : `no-cache` (revalidation ETag). Aucun Ctrl+F5 nécessaire après mise à jour.
