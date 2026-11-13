# Changelog

## v1.0.0

Première version.

- Connexion Bluetooth LE à une montre Da Fit (protocole MoYoung), sans appairage ni app Da Fit.
- Lance à distance un sport sur la montre pour obtenir le cardio en continu (~2 mesures/s), l'arrête à la fermeture de SimHub.
- Propriétés `DafitWatchPlugin.*` : `HeartRate`, `HeartRateValid`, `HeartRatePercent`, `HeartRateZone`, `Min/Avg/MaxHeartRate`, `DataAge`, `Connected`, `State`, `StatusText`, `Battery`, `DeviceName`.
- Actions `Reconnect` et `ResetStats`.
- Reconnexion automatique et relance du sport si la montre décroche.
- Page de réglages : BPM en direct, recherche des montres, choix du sport, FC max.
- Mises à jour depuis GitHub : au démarrage de SimHub, le plugin compare sa version à la dernière release et propose l'installation dans un bandeau (fichier vérifié par SHA-256).
