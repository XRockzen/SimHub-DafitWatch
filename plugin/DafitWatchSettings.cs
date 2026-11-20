namespace User.DafitWatch
{
    public class DafitWatchSettings
    {
        /// <summary>Adresse Bluetooth (ex. AA:BB:CC:DD:EE:FF). Vide = recherche par nom.</summary>
        public string DeviceAddress { get; set; }

        /// <summary>Nom Bluetooth de la montre (affichage, et recherche si l'adresse est vide).</summary>
        public string DeviceName { get; set; }

        /// <summary>Profil de montre : "auto", "standard" ou "dafit" (voir WatchProfiles).</summary>
        public string Profile { get; set; }

        /// <summary>Connexion automatique au démarrage de SimHub.</summary>
        public bool AutoConnect { get; set; }

        /// <summary>Da Fit : type de sport lancé sur la montre (codes MoYoung : 17 = vélo d'intérieur).</summary>
        public int SportType { get; set; }

        /// <summary>Da Fit : arrête le sport sur la montre quand SimHub se ferme.</summary>
        public bool StopSportOnExit { get; set; }

        /// <summary>Fréquence cardiaque maximale, pour le % et les zones.</summary>
        public int UserMaxHeartRate { get; set; }

        /// <summary>Au-delà de ce délai sans mesure, HeartRate repasse à 0.</summary>
        public int StaleSeconds { get; set; }

        public DafitWatchSettings()
        {
            DeviceAddress    = "";
            DeviceName       = "";
            Profile          = WatchProfiles.AutoId;
            AutoConnect      = true;
            SportType        = 17;
            StopSportOnExit  = true;
            UserMaxHeartRate = 190;
            StaleSeconds     = 5;
        }
    }
}
