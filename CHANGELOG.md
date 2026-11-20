# Changelog

## v1.2.0

- Connexion bien plus fiable : connexion directe à l'adresse de la montre (plus besoin qu'elle soit « visible »), détection immédiate des coupures et reconnexion en moins d'une seconde, délai maximal sur chaque opération Bluetooth.
- Autres montres : profil « Cardio Bluetooth standard » pour tout appareil qui diffuse son cardio (ceintures, Garmin, Polar, Coros, Amazfit, Huawei… en mode diffusion), profil Da Fit détecté automatiquement.
- « Lister mes appareils » : appareils appairés à Windows puis ceux à proximité (♥ = cardio annoncé).
- Bouton « Diagnostic » : copie un rapport des services Bluetooth de la montre, à envoyer pour ajouter un nouveau modèle.
- Da Fit : si un sport tourne déjà sur la montre, le plugin n'y touche pas, et ne l'arrête à la fermeture que s'il l'a lui-même lancé.
- Nouvelles propriétés `Profile` et `Reconnects`.

## v1.1.0

- Le plugin s'appelle désormais **Smartwatch BPM** dans SimHub (les propriétés gardent le préfixe `DafitWatchPlugin.`, rien à changer dans les dashboards).
- Nouvelle icône dans le menu de gauche : une montre connectée avec un cœur.

## v1.0.0

Première version.

- Connexion Bluetooth LE à une montre Da Fit (protocole MoYoung), sans appairage ni app Da Fit.
- Lance à distance un sport sur la montre pour obtenir le cardio en continu (~2 mesures/s), l'arrête à la fermeture de SimHub.
- Propriétés `DafitWatchPlugin.*` : `HeartRate`, `HeartRateValid`, `HeartRatePercent`, `HeartRateZone`, `Min/Avg/MaxHeartRate`, `DataAge`, `Connected`, `State`, `StatusText`, `Battery`, `DeviceName`.
- Actions `Reconnect` et `ResetStats`.
- Reconnexion automatique et relance du sport si la montre décroche.
- Page de réglages : BPM en direct, recherche des montres, choix du sport, FC max.
- Mises à jour depuis GitHub : au démarrage de SimHub, le plugin compare sa version à la dernière release et propose l'installation dans un bandeau (fichier vérifié par SHA-256).
