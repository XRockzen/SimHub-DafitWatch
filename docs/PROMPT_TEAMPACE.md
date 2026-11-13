# Intégrer le cardio de la montre Da Fit dans TeamPace

Un autre plugin SimHub, **« Smartwatch BPM »** (`User.DafitWatch`, dépôt XRockzen/SimHub-DafitWatch),
lit en Bluetooth le rythme cardiaque du pilote sur sa montre et le publie comme propriétés SimHub.
Je voudrais que **TeamPace enregistre ces données cardio avec ses données de tour/session**,
pour pouvoir analyser où et quand je suis tendu sur un circuit.

Ne modifie pas le plugin montre : on le lit seulement, via les propriétés SimHub.

## Ce qui est disponible dans SimHub

Lecture : `PluginManager.GetPropertyValue("DafitWatchPlugin.<Nom>")` (renvoie `object`).

| Propriété | Type | Signification |
|---|---|---|
| `HeartRate` | int | BPM actuel. **0 = pas de mesure valide** (préchauffage du capteur, montre déconnectée, mesure périmée) |
| `HeartRateValid` | bool | `true` si `HeartRate` est une mesure fraîche (< 5 s par défaut) |
| `HeartRatePercent` | int | % de la FC max réglée par le pilote (0 si invalide) |
| `HeartRateZone` | int | 0 à 5 (1 = 50-60 %, 2 = 60-70 %, 3 = 70-80 %, 4 = 80-90 %, 5 = ≥ 90 % FC max) |
| `UserMaxHeartRate` | int | FC max réglée dans le plugin montre |
| `DataAge` | double | secondes depuis la dernière mesure valide (-1 = jamais) |
| `MinHeartRate` / `AvgHeartRate` / `MaxHeartRate` | int | stats depuis le dernier reset **du plugin montre** (pas par tour : ne pas s'en servir pour TeamPace) |
| `Connected` | bool | montre connectée (en mesure ou en préchauffage) |
| `State` | string | `Stopped`, `Searching`, `Connecting`, `WaitingHeartRate`, `Streaming` |
| `StatusText` | string | état lisible (FR) |
| `Battery` | int | % batterie montre, -1 si inconnu |
| `DeviceName` | string | nom Bluetooth de la montre |

Comportement à connaître :
- La montre envoie **~2 mesures par seconde** ; `DataUpdate` de SimHub tourne bien plus vite, donc la même valeur est lue plusieurs fois de suite.
- Après la connexion, il y a **~15 s de préchauffage** avec `HeartRateValid = false`.
- Capteur optique au poignet : des trous ou des valeurs aberrantes sont possibles quand le volant secoue. Ignorer les échantillons invalides plutôt que les compter comme 0.
- **Le plugin montre peut être absent, désactivé ou déconnecté** : `GetPropertyValue` peut renvoyer `null`. TeamPace doit fonctionner exactement comme avant dans ce cas (dépendance optionnelle, pas de référence à la DLL `User.DafitWatch`).

## Ce que j'aimerais

1. **Par tour** : BPM moyen, min, max, et le % du tour avec une mesure valide (fiabilité). Moyenne **pondérée par le temps** sur les seuls échantillons valides, pas une moyenne des appels à `DataUpdate`.
2. **Par secteur** si TeamPace a déjà la notion de secteurs : BPM moyen et max.
3. **Temps passé dans chaque zone** (0 à 5) par tour et par session.
4. **Session** : BPM au départ (pic des premières secondes), moyenne et max de session.
5. Stocker tout ça **avec les données de tour existantes**, dans le même format et au même endroit que le reste. Les anciennes données sans cardio doivent rester lisibles.
6. Si TeamPace a déjà un affichage ou un export des tours, y ajouter les colonnes cardio quand elles existent.

Adapte-toi à l'architecture actuelle de TeamPace (structures de tour, stockage, UI, contraintes de compilation) et propose-moi le plan avant de coder si ça touche au format des données enregistrées.
