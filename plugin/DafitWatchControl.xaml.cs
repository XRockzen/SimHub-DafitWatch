using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.DafitWatch
{
    public partial class DafitWatchControl : UserControl
    {
        readonly DafitWatchPlugin _plugin;
        readonly DispatcherTimer _timer;
        bool _scanning;

        // Codes de sport MoYoung (Gadgetbridge MoyoungConstants.TRAINING_TYPE_*)
        static readonly List<KeyValuePair<int, string>> Sports = new List<KeyValuePair<int, string>>
        {
            new KeyValuePair<int, string>(17, "Vélo d'intérieur (17)"),
            new KeyValuePair<int, string>(18, "Exercice libre (18)"),
            new KeyValuePair<int, string>(13, "Fitness (13)"),
            new KeyValuePair<int, string>(2,  "Vélo (2)"),
            new KeyValuePair<int, string>(1,  "Course (1)"),
            new KeyValuePair<int, string>(0,  "Marche (0)"),
        };

        public DafitWatchControl(DafitWatchPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            CmbSport.ItemsSource = Sports;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) => RefreshStatus();

            Loaded += (s, e) =>
            {
                LoadSettings();
                RefreshStatus();
                _timer.Start();
            };
            Unloaded += (s, e) => _timer.Stop();
        }

        void LoadSettings()
        {
            var st = _plugin.Settings;
            TxtAddress.Text = st.DeviceAddress;
            TxtName.Text = st.DeviceName;
            if (!Sports.Any(p => p.Key == st.SportType))
                Sports.Add(new KeyValuePair<int, string>(st.SportType, "Code " + st.SportType));
            CmbSport.SelectedValue = st.SportType;
            ChkStopOnExit.IsChecked = st.StopSportOnExit;
            ChkAutoConnect.IsChecked = st.AutoConnect;
            TxtMaxHr.Text = st.UserMaxHeartRate.ToString();
            TxtStale.Text = st.StaleSeconds.ToString();
        }

        bool SaveSettings()
        {
            TxtError.Text = "";
            var address = TxtAddress.Text.Trim();
            ulong dummy;
            if (address.Length > 0 && !WatchClient.TryParseAddress(address, out dummy))
            {
                TxtError.Text = "Adresse invalide (format attendu : AA:BB:CC:DD:EE:FF).";
                return false;
            }
            int maxHr, stale;
            if (!int.TryParse(TxtMaxHr.Text.Trim(), out maxHr) || maxHr < 100 || maxHr > 230)
            {
                TxtError.Text = "FC max invalide (entre 100 et 230).";
                return false;
            }
            if (!int.TryParse(TxtStale.Text.Trim(), out stale) || stale < 1 || stale > 60)
            {
                TxtError.Text = "Péremption invalide (entre 1 et 60 s).";
                return false;
            }

            var st = _plugin.Settings;
            st.DeviceAddress = address;
            st.DeviceName = TxtName.Text.Trim();
            if (CmbSport.SelectedValue is int) st.SportType = (int)CmbSport.SelectedValue;
            st.StopSportOnExit = ChkStopOnExit.IsChecked == true;
            st.AutoConnect = ChkAutoConnect.IsChecked == true;
            st.UserMaxHeartRate = maxHr;
            st.StaleSeconds = stale;
            _plugin.SaveSettings();
            return true;
        }

        void RefreshStatus()
        {
            var c = _plugin.Client;
            int hr = _plugin.CurrentHeartRate();
            TxtBpm.Text = hr > 0 ? hr.ToString() : "--";

            int zone = _plugin.Zone();
            TxtZone.Text = hr > 0 ? string.Format("{0} % FC max · zone {1}", _plugin.PercentOfMax(), zone) : "";

            TxtState.Text = c.StatusText;
            switch (c.State)
            {
                case WatchState.Streaming:        DotState.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)); break;
                case WatchState.WaitingHeartRate: DotState.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); break;
                case WatchState.Searching:
                case WatchState.Connecting:       DotState.Fill = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)); break;
                default:                          DotState.Fill = Brushes.Gray; break;
            }

            var device = c.DeviceName;
            TxtDevice.Text = string.IsNullOrEmpty(device) ? "" :
                string.Format("{0}  {1}  ·  batterie {2}", device, c.DeviceAddress,
                              c.Battery >= 0 ? c.Battery + " %" : "?");

            int min, avg, max;
            _plugin.GetStats(out min, out avg, out max);
            TxtStats.Text = max > 0 ? string.Format("Session : min {0} · moy {1} · max {2}", min, avg, max) : "";

            BtnDisconnect.IsEnabled = c.IsRunning;
        }

        // ------------------------------------------------------------------ boutons

        void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (SaveSettings()) _plugin.Reconnect();
        }

        void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Disconnect();
        }

        void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ResetStats();
        }

        async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;
            _scanning = true;
            BtnScan.IsEnabled = false;
            TxtScan.Text = "  recherche pendant 8 s…";
            try
            {
                var results = await WatchClient.Scan(8000, CancellationToken.None, null);
                // Les appareils nommés d'abord : la montre annonce son nom
                ListScan.ItemsSource = results.OrderBy(r => string.IsNullOrEmpty(r.Name)).ThenByDescending(r => r.Rssi).ToList();
                ListScan.Visibility = Visibility.Visible;
                TxtScan.Text = string.Format("  {0} appareil(s) — clique sur la montre", results.Count);
            }
            catch (Exception ex)
            {
                TxtScan.Text = "  erreur : " + ex.Message;
            }
            finally
            {
                _scanning = false;
                BtnScan.IsEnabled = true;
            }
        }

        void ListScan_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var r = ListScan.SelectedItem as ScanResult;
            if (r == null) return;
            TxtAddress.Text = r.AddressText;
            if (!string.IsNullOrEmpty(r.Name)) TxtName.Text = r.Name;
        }
    }
}
