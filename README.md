# Smartwatch BPM — montre Da Fit → SimHub

Plugin SimHub qui lit le rythme cardiaque d'une montre **Da Fit** (protocole MoYoung) en Bluetooth LE
et l'expose comme une vraie source de données SimHub (overlays, Dash Studio, autres plugins).

Aucun jailbreak : le plugin démarre à distance un sport sur la montre (commande MoYoung `0x68`),
ce qui la fait diffuser son cardio sur le service Bluetooth standard Heart Rate (`0x180D` / `0x2A37`,
~2 mesures/s). Le sport est arrêté (`0x68 FF`) à la fermeture de SimHub.

## Installation

Télécharger `User.DafitWatch.dll` depuis la [dernière release](https://github.com/XRockzen/SimHub-DafitWatch/releases/latest)
et la copier dans le dossier de SimHub (SimHub fermé). Ou depuis les sources :

```powershell
.\plugin\build.ps1 -Deploy     # SimHub fermé
```

Au premier lancement, SimHub demande d'activer le plugin « Smartwatch BPM ».
Dans son onglet : « Lister mes appareils », cliquer sur la montre, puis « Enregistrer et (re)connecter ».

## Montres compatibles

| Profil | Appareils | Ce que fait le plugin |
|---|---|---|
| Cardio Bluetooth standard | ceintures cardio ; montres en mode « diffusion du cardio » (Garmin, Polar, Coros, Suunto, Amazfit/Zepp, Huawei, Xiaomi récentes…) | lit le service Heart Rate `0x180D` |
| Da Fit (MoYoung) | montres de l'app Da Fit | lance un sport à distance si aucun cardio n'arrive, puis lit `0x180D` |

Le profil est détecté automatiquement. Pour un autre modèle : bouton **Diagnostic** dans l'onglet
du plugin, puis envoyer le rapport (copié dans le presse-papiers) en issue GitHub.
S'il contient le service `180D`, le modèle marche déjà ; sinon il faut un profil dédié
(une classe dans `plugin/WatchProfiles.cs`), faisable si son protocole est connu (Gadgetbridge).
Apple Watch, Fitbit et autres montres fermées ne diffusent pas leur cardio : pas compatibles.

## Fiabilité de la connexion

- La montre n'accepte qu'**une** liaison données : coupez le Bluetooth du téléphone (ou l'app de la montre)
  pendant que vous roulez, sinon le téléphone la reprend dès qu'elle se libère.
- **Inutile d'appairer la montre à Windows.** Pour une montre avec appels Bluetooth, l'appairage Windows
  en fait un casque audio et n'aide pas la liaison données.
- Si la montre devient introuvable (plus aucune annonce), redémarrez-la.

## Mises à jour

À chaque démarrage de SimHub, le plugin compare sa version à la dernière release GitHub.
S'il y a plus récent, un bandeau apparaît dans son onglet : « Installer » télécharge la DLL,
vérifie son empreinte SHA-256, remplace l'ancienne (demande admin si SimHub est dans Program Files),
puis il suffit de redémarrer SimHub.

Publier une version (compile, tag, push et release avec DLL + `.sha256`) :

```powershell
.\release.ps1 -Version 1.1.0 -Notes "- Ajout de ..."
```
Avant de connecter : fermer l'app Da Fit / couper le Bluetooth du téléphone (la montre n'accepte qu'une connexion).

## Propriétés (`DafitWatchPlugin.*`)

| Propriété | Description |
|---|---|
| `HeartRate` | bpm, 0 si pas de mesure récente |
| `HeartRateValid` | true si une mesure fraîche est disponible |
| `HeartRatePercent` / `HeartRateZone` | % de la FC max réglée, zone 0–5 |
| `MinHeartRate` / `AvgHeartRate` / `MaxHeartRate` | statistiques depuis le dernier reset |
| `DataAge` | secondes depuis la dernière mesure (-1 = jamais) |
| `Connected`, `State`, `StatusText` | état de la connexion |
| `Battery`, `DeviceName`, `UserMaxHeartRate` | infos |
| `Profile`, `Reconnects` | type de montre détecté, nombre de reconnexions depuis le démarrage |

Actions : `DafitWatchPlugin.Reconnect`, `DafitWatchPlugin.ResetStats`.

Depuis un autre plugin : `PluginManager.GetPropertyValue("DafitWatchPlugin.HeartRate")`.

## Outils

- `tools/ble_explorer.py` — exploration BLE (scan, dump GATT, écoute, envoi de commandes MoYoung). Python + `bleak` (`.venv`).
- `tools/WatchClientTest/run.ps1` — teste le client Bluetooth du plugin sans SimHub (`run <s> <adresse>`, `list`, `diag <adresse>`).

## Notes techniques

- Compilé avec le MSBuild de Windows (C# 5, .NET 4.8) ; les API Bluetooth WinRT sont référencées
  depuis `C:\Windows\System32\WinMetadata` (pas de SDK Windows requis), cf. `WinRtInterop.cs`.
- Montre testée : « H33 » (app Da Fit). Pendant le démarrage du sport, la montre envoie `0` bpm ~10 s (ignoré).
- Référence protocole : Gadgetbridge, `MoyoungConstants.java` / `MoyoungDeviceSupport.java`.
