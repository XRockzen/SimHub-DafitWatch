using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Windows.Media;

namespace User.DafitWatch
{
    [PluginDescription("Rythme cardiaque en direct depuis une montre connectée Da Fit (Bluetooth LE)")]
    [PluginAuthor("Amaury")]
    [PluginName("Smartwatch BPM")]
    public class DafitWatchPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal DafitWatchSettings Settings;
        internal readonly WatchClient Client = new WatchClient();

        // Statistiques de session (depuis le dernier reset)
        readonly object _statsLock = new object();
        int  _minHr, _maxHr;
        long _sumHr, _countHr;

        public PluginManager PluginManager { get; set; }
        public ImageSource   PictureIcon   { get { return PluginIcon.Image; } }
        public string        LeftMenuTitle { get { return "Smartwatch BPM"; } }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[DafitWatch] Starting " + PluginUpdater.Format(PluginUpdater.CurrentVersion));

            // Compare la version installée à la dernière release GitHub
            PluginUpdater.CleanupPrevious();
            PluginUpdater.CheckInBackground();

            Settings = this.ReadCommonSettings<DafitWatchSettings>("GeneralSettings", () => new DafitWatchSettings());
            if (string.IsNullOrEmpty(Settings.Profile)) Settings.Profile = WatchProfiles.AutoId;   // réglages d'avant la v1.2

            Client.HeartRateReceived += OnHeartRate;

            // --- Propriétés : DafitWatchPlugin.<Nom> ---
            this.AttachDelegate("HeartRate",        () => CurrentHeartRate());
            this.AttachDelegate("HeartRateValid",   () => CurrentHeartRate() > 0);
            this.AttachDelegate("HeartRatePercent", () => PercentOfMax());
            this.AttachDelegate("HeartRateZone",    () => Zone());
            this.AttachDelegate("DataAge",          () => DataAge());
            this.AttachDelegate("MinHeartRate",     () => { int mn, av, mx; GetStats(out mn, out av, out mx); return mn; });
            this.AttachDelegate("MaxHeartRate",     () => { int mn, av, mx; GetStats(out mn, out av, out mx); return mx; });
            this.AttachDelegate("AvgHeartRate",     () => { int mn, av, mx; GetStats(out mn, out av, out mx); return av; });
            this.AttachDelegate("UserMaxHeartRate", () => Settings.UserMaxHeartRate);
            this.AttachDelegate("Connected",        () => IsConnected());
            this.AttachDelegate("State",            () => Client.State.ToString());
            this.AttachDelegate("StatusText",       () => Client.StatusText);
            this.AttachDelegate("Battery",          () => Client.Battery);
            this.AttachDelegate("DeviceName",       () => Client.DeviceName);
            this.AttachDelegate("Profile",          () => Client.ProfileName);
            this.AttachDelegate("Reconnects",       () => Client.Reconnects);

            // --- Actions (mappables sur un bouton) ---
            this.AddAction("Reconnect",  (a, b) => Reconnect());
            this.AddAction("ResetStats", (a, b) => ResetStats());

            if (Settings.AutoConnect)
                Client.Start(Settings);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data) { }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("GeneralSettings", Settings);
            Client.HeartRateReceived -= OnHeartRate;
            Client.Stop();
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new DafitWatchControl(this);
        }

        // ------------------------------------------------------------------ logique

        internal int CurrentHeartRate()
        {
            return DataAge() >= 0 && DataAge() <= Settings.StaleSeconds ? Client.HeartRate : 0;
        }

        internal double DataAge()
        {
            var last = Client.LastValidHrUtc;
            if (last == DateTime.MinValue) return -1;
            return Math.Round((DateTime.UtcNow - last).TotalSeconds, 1);
        }

        internal bool IsConnected()
        {
            var s = Client.State;
            return s == WatchState.Streaming || s == WatchState.WaitingHeartRate;
        }

        internal int PercentOfMax()
        {
            int hr = CurrentHeartRate();
            if (hr <= 0 || Settings.UserMaxHeartRate <= 0) return 0;
            return (int)Math.Round(100.0 * hr / Settings.UserMaxHeartRate);
        }

        /// <summary>Zones classiques : 1 = 50-60 %, 2 = 60-70 %, 3 = 70-80 %, 4 = 80-90 %, 5 = 90 %+ ; 0 en dessous ou sans mesure.</summary>
        internal int Zone()
        {
            int p = PercentOfMax();
            if (p >= 90) return 5;
            if (p >= 80) return 4;
            if (p >= 70) return 3;
            if (p >= 60) return 2;
            if (p >= 50) return 1;
            return 0;
        }

        void OnHeartRate(int bpm)
        {
            lock (_statsLock)
            {
                if (_countHr == 0 || bpm < _minHr) _minHr = bpm;
                if (bpm > _maxHr) _maxHr = bpm;
                _sumHr += bpm;
                _countHr++;
            }
        }

        internal void GetStats(out int min, out int avg, out int max)
        {
            lock (_statsLock)
            {
                min = _minHr;
                max = _maxHr;
                avg = _countHr == 0 ? 0 : (int)Math.Round((double)_sumHr / _countHr);
            }
        }

        internal void ResetStats()
        {
            lock (_statsLock) { _minHr = 0; _maxHr = 0; _sumHr = 0; _countHr = 0; }
            SimHub.Logging.Current.Info("[DafitWatch] Stats reset");
        }

        /// <summary>Arrête puis relance la connexion avec les réglages actuels (hors thread UI).</summary>
        internal void Reconnect()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (_connLock)
                {
                    Client.Stop();
                    Client.Start(Settings);
                }
            });
        }

        internal void Disconnect()
        {
            System.Threading.Tasks.Task.Run(() => { lock (_connLock) Client.Stop(); });
        }

        readonly object _connLock = new object();

        internal void SaveSettings()
        {
            this.SaveCommonSettings("GeneralSettings", Settings);
        }
    }
}
