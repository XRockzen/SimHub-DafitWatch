namespace User.DafitWatch
{
    public class DafitWatchSettings
    {
        /// <summary>Adresse Bluetooth (ex. AA:BB:CC:DD:EE:FF). Vide = recherche par nom.</summary>
        public string DeviceAddress { get; set; }

        /// <summary>Nom Bluetooth annoncé par la montre (utilisé si l'adresse est vide).</summary>
        public string DeviceName { get; set; }

        /// <summary>Connexion automatique au démarrage de SimHub.</summary>
        public bool AutoConnect { get; set; }

        /// <summary>Type de sport lancé sur la montre (codes MoYoung : 17 = vélo d'intérieur).</summary>
        public int SportType { get; set; }

        /// <summary>Arrête le sport sur la montre quand SimHub se ferme.</summary>
        public bool StopSportOnExit { get; set; }

        /// <summary>Fréquence cardiaque maximale, pour le % et les zones.</summary>
        public int UserMaxHeartRate { get; set; }

        /// <summary>Au-delà de ce délai sans mesure, HeartRate repasse à 0.</summary>
        public int StaleSeconds { get; set; }

        public DafitWatchSettings()
        {
            DeviceAddress    = "";
            DeviceName       = "H33";
            AutoConnect      = true;
            SportType        = 17;
            StopSportOnExit  = true;
            UserMaxHeartRate = 190;
            StaleSeconds     = 5;
        }
    }
}
