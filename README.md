# Utils.Colas

Portail d'utilitaires JDE (.NET 10, ASP.NET Core minimal API + pages statiques).
Fusion de **Lire .h** (ex-`Utils.htm` d'appli-jade03) et **JDE Compare** (ex-NERcompare).

## Structure

| Élément | Rôle |
|---|---|
| `Program.cs` | Hébergement : HTTPS, auth Windows (Negotiate), en-têtes de sécurité, cache, `/health`, API communes (`/api/version`, `/api/windows-user`, `/api/connections`) |
| `Infrastructure/OracleConnections.cs` | Modèle de connexion Oracle unifié + `ConnectionStore` (déchiffrement `ENC:`) |
| `Modules/LireH/` | API `/api/lireh/*` (lecture/téléchargement des en-têtes `.h`) |
| `Modules/JdeCompare/` | API `/api/jdecompare/*` (comparaison des specs et des paquets, analyse IA des écarts) |
| `wwwroot/index.html` | Menu, alimenté par `wwwroot/config/utilities.json` |
| `wwwroot/common/` | Bandeau partagé (`banner.js` / `banner.css`, sélecteur de connexion) |
| `wwwroot/lireh/`, `wwwroot/jdecompare/` | Pages des utilitaires |
| `Tools/encrypt-secrets.ps1` | Chiffrement DPAPI des secrets (`secrets.json`) |
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
- **Liste unique des environnements** : Lire .h, JDE Compare et le bandeau affichent les connexions de
  `OracleConnections`, triées par `Order` (aucune autre liste à maintenir). Lire .h lit `<Schema>.F98780R` ;
  JDE Compare utilise `Schema` comme nom d'environnement (schéma des specs). Une connexion sans `DataSource`
  (clé orpheline, ex. `E1` d'un ancien `secrets.json`) est ignorée, avec un avertissement dans les logs.
  Les clés de `secrets.json` doivent donc reprendre les noms de connexion (`DV920`, `PYI920`, `QA920`, `PD920`).
- JDE Compare : la connexion de référence est choisie dans le bandeau (cookie `JDE-Connection`).

### Adressage (EZConnect) et surcharge serveur

- `DataSource` est en **EZConnect** (`host_jde92eX:1521/jde92eX`) : aucun client Oracle ni `tnsnames.ora` requis.
- Pour changer une adresse sur le serveur sans relivrer : copier `appsettings.Production.template.json`
  en `C:\sites\JDE\appsettings.Production.json`, ne garder que les valeurs utiles, recycler le pool.
  Ce fichier n'est ni versionné, ni publié, ni écrasé par `Install-UPD*.ps1`.
- Alias TNS possible : renseigner `TnsAdmin` (dossier contenant `tnsnames.ora`, appliqué une fois au démarrage) et `DataSource` = alias.

### Erreurs Oracle affichées

| Code | Message | Action |
|---|---|---|
| ORA-12154 / 12262 | Identifiant de connexion introuvable | Vérifier `DataSource` (host:port/service) ou `TnsAdmin` |
| ORA-12545 | Hôte introuvable (DNS) | Vérifier le nom d'hôte depuis le serveur |
| ORA-12541 | Aucun listener | Vérifier hôte et port |
| ORA-12514 / 12505 | Service inconnu | Vérifier le service après `/` |
| ORA-12170 / 12543 / 12535 | Injoignable / délai dépassé | Réseau, pare-feu |
| ORA-01017 | Identifiants refusés | `secrets.json` |
| ORA-28000 / 28001 | Compte verrouillé / mot de passe expiré | DBA puis `secrets.json` |
| ORA-00942 | Table inaccessible | Droits / schéma |

## Paquets objets (F98780R)

Extraction commune (`Infrastructure/JdePackage.cs`) du zip `XMOMRBLOB` de `<schéma>.F98780R`. Les zips imbriqués (ex. `specs.zip`) sont dépliés sous la forme `specs.zip/...`.
Limites : blob 64 Mo, 5000 fichiers, 256 Mo décompressés.

### Lire .h : fichier affiché

Le premier fichier trouvé selon cet ordre est affiché :

1. `include64/<objet>.h`
2. un `.h`/`.c` d'un autre dossier (priorité au nom de l'objet, puis `.h`)
3. un fichier de `specs.zip` dont le nom contient l'objet
4. sinon, l'arborescence du zip s'ouvre et l'utilisateur choisit le fichier

L'arborescence reste disponible pour afficher ou télécharger un autre fichier (`/api/lireh/header?...&path=<chemin>`).

### JDE Compare : comparaison des paquets

- `/compare` compare aussi les paquets source et cible fichier par fichier (SHA-256 du contenu ; les dates du zip sont ignorées).
- Les fichiers modifiés, présents seulement en source ou présents seulement en cible sont listés dans le détail de l'objet.
- Le bouton « Voir » affiche le diff d'un fichier (`POST /api/jdecompare/package-diff`).
- Si les specs sont identiques mais que les paquets diffèrent, l'objet est classé « ≠ Différent ».
- Le détail de l'objet affiche le « Résumé repository » (comparaison des specs F9860/F9861/F98780R).

### JDE Compare : analyse IA des écarts (Azure OpenAI)

Si le diff d'un fichier dépasse `JdeCompare:AiDiffLineThreshold` lignes affichées (2 par défaut ; une ligne modifiée compte pour 2), un bouton **✨ Analyse IA** est proposé.
L'analyse produit une synthèse, les changements regroupés par thème et les impacts à vérifier.
Le serveur recalcule le diff à partir de l'objet et du chemin du fichier : le navigateur n'envoie aucun texte. Chaque demande est journalisée (utilisateur, objet, fichier).

Configuration (vide = fonction désactivée, le bouton n'apparaît pas) :

| Clé | Fichier serveur | Exemple |
|---|---|---|
| `AzureOpenAI:Endpoint` | `appsettings.Production.json` | `https://<ressource>.openai.azure.com` |
| `AzureOpenAI:Deployment` | `appsettings.Production.json` | `gpt-4o` |
| `AzureOpenAI:ApiKey` | `secrets.json` (chiffrable avec `Tools\encrypt-secrets.ps1 -Machine`) | |
| `AzureOpenAI:Proxy` | `appsettings.Production.json` (optionnel, authentifié avec l'identité du pool) | `http://proxy:8080` |
| `AzureOpenAI:ApiVersion`, `TimeoutSeconds`, `MaxInputChars` | `appsettings.json` | `2024-10-21`, `90`, `30000` |

Le diff (contenu des sources de l'objet) est envoyé à la ressource Azure OpenAI configurée.

#### Sans Azure OpenAI : « 📋 Copier pour Copilot »

Le bouton **📋 Copier pour Copilot** est toujours proposé au-delà du seuil, même si Azure OpenAI n'est pas configuré. Il copie dans le presse-papiers les consignes et le diff, à coller (Ctrl+V) dans l'application Copilot de Windows ou dans Microsoft 365 Copilot Chat.
Le texte est limité à `JdeCompare:CopilotMaxChars` caractères (12000 par défaut) ; au-delà, le diff est tronqué et la mention est indiquée à l'IA. Aucune donnée ne sort du serveur : c'est l'utilisateur qui colle le texte dans son propre Copilot.

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
| 4. Publication | copie **miroir** (les fichiers absents de la livraison sont supprimés) ; conservés : `secrets.json`, `appsettings.Production.json`, `keys\`, `logs\` ; droits NTFS du pool ; auth Windows imposée, anonyme désactivé |
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
