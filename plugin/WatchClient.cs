using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace User.DafitWatch
{
    public enum WatchState
    {
        Stopped,
        Searching,
        Connecting,
        WaitingHeartRate,
        Streaming,
        Reconnecting
    }

    /// <summary>Liaison perdue : on se reconnecte tout de suite, sans attendre.</summary>
    class LinkLostException : Exception
    {
        public LinkLostException(string message) : base(message) { }
    }

    /// <summary>
    /// Une connexion GATT ouverte vers la montre (appareil, session, services en cache).
    /// Tout est jeté et recréé à chaque reconnexion : après une coupure, Windows
    /// invalide ces objets (« l'objet a été fermé »).
    /// </summary>
    public class WatchLink : IDisposable
    {
        public const int OpTimeoutMs = 10000;
        public const int ConnectTimeoutMs = 20000;

        public BluetoothLEDevice Device { get; private set; }
        public GattSession Session { get; private set; }
        readonly Dictionary<Guid, GattDeviceService> _services = new Dictionary<Guid, GattDeviceService>();
        readonly Dictionary<Guid, GattCharacteristic> _chars = new Dictionary<Guid, GattCharacteristic>();

        public ICollection<Guid> ServiceUuids { get { return _services.Keys; } }

        public WatchLink(BluetoothLEDevice device)
        {
            Device = device;
        }

        public async Task Open(CancellationToken ct)
        {
            Session = await GattSession.FromDeviceIdAsync(Device.BluetoothDeviceId).AsTask(OpTimeoutMs, ct).ConfigureAwait(false);
            if (Session != null) Session.MaintainConnection = true;   // Windows garde la liaison et la rétablit seul

            // La première requête GATT déclenche la connexion radio : elle peut échouer
            // le temps que la montre réponde.
            for (int attempt = 1; ; attempt++)
            {
                // Établir la liaison radio peut prendre plus longtemps que les requêtes suivantes
                var r = await Device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ConnectTimeoutMs, ct).ConfigureAwait(false);
                if (r.Status == GattCommunicationStatus.Success)
                {
                    foreach (var s in r.Services)
                        if (!_services.ContainsKey(s.Uuid)) _services[s.Uuid] = s; else s.Dispose();
                    return;
                }
                if (attempt >= 4)
                    throw new Exception("montre injoignable : hors de portée, en veille ou connectée à un autre appareil (téléphone ?)");
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
        }

        public async Task<GattCharacteristic> GetCharacteristic(Guid service, Guid characteristic, CancellationToken ct)
        {
            GattCharacteristic ch;
            lock (_chars) if (_chars.TryGetValue(characteristic, out ch)) return ch;

            GattDeviceService svc;
            if (!_services.TryGetValue(service, out svc)) return null;
            for (int attempt = 1; ; attempt++)
            {
                var r = await svc.GetCharacteristicsForUuidAsync(characteristic, BluetoothCacheMode.Uncached).AsTask(OpTimeoutMs, ct).ConfigureAwait(false);
                if (r.Status == GattCommunicationStatus.Success)
                {
                    ch = r.Characteristics.FirstOrDefault();
                    if (ch != null) lock (_chars) _chars[characteristic] = ch;
                    return ch;
                }
                if (attempt >= 3) throw new LinkLostException("caractéristique " + Short(characteristic) + " injoignable (" + r.Status + ")");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        public async Task Subscribe(GattCharacteristic ch, bool enable, CancellationToken ct)
        {
            var value = !enable ? GattClientCharacteristicConfigurationDescriptorValue.None
                      : ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(value).AsTask(OpTimeoutMs, ct).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                throw new LinkLostException("abonnement " + Short(ch.Uuid) + " refusé (" + status + ")");
        }

        public static string Short(Guid uuid)
        {
            var s = uuid.ToString();
            return s.EndsWith("-0000-1000-8000-00805f9b34fb") ? s.Substring(4, 4).ToUpperInvariant() : s;
        }

        public void Dispose()
        {
            foreach (var s in _services.Values) { try { s.Dispose(); } catch { } }
            _services.Clear();
            lock (_chars) _chars.Clear();
            if (Session != null) { try { Session.MaintainConnection = false; Session.Dispose(); } catch { } }
            if (Device != null) { try { Device.Dispose(); } catch { } }
            Session = null;
            Device = null;
        }
    }

    /// <summary>
    /// Connexion BLE à une montre ou une ceinture cardio, avec reconnexion automatique.
    /// Le cardio est lu sur le service standard Heart Rate (0x180D / 0x2A37) ;
    /// le profil de la montre (<see cref="WatchProfile"/>) se charge de le faire diffuser.
    /// </summary>
    public class WatchClient
    {
        static readonly Guid SvcHeartRate = GattServiceUuids.HeartRate;
        static readonly Guid ChHrMeasure  = GattCharacteristicUuids.HeartRateMeasurement;
        static readonly Guid SvcBattery   = GattServiceUuids.Battery;
        static readonly Guid ChBattery    = GattCharacteristicUuids.BatteryLevel;

        const int SearchTimeoutMs   = 15000;
        const int NoDataResyncSec   = 12;   // plus rien sur 2A37 -> on relance la diffusion
        const int MaxResyncs        = 3;    // puis on se reconnecte complètement
        const int DisconnectGraceMs = 4000; // coupure radio tolérée (Windows se reconnecte seul)
        const int MaxRetryDelayMs   = 10000;

        DafitWatchSettings _settings;

        // --- État publié (lu par le plugin depuis d'autres threads) ---
        readonly object _lock = new object();
        int      _heartRate;
        DateTime _lastValidHrUtc  = DateTime.MinValue;
        DateTime _lastHrPacketUtc = DateTime.MinValue;
        int      _battery = -1;
        WatchState _state = WatchState.Stopped;
        string   _statusText = "Arrêté";
        string   _deviceName = "";
        string   _deviceAddress = "";
        string   _profileName = "";
        int      _reconnects;

        /// <summary>Déclenché à chaque mesure valide (bpm &gt; 0), sur un thread du pool.</summary>
        public event Action<int> HeartRateReceived;

        // --- Connexion courante ---
        CancellationTokenSource _cts;
        Task _loop;
        WatchLink _link;
        WatchProfile _profile;
        GattCharacteristic _hrChar;
        GattCharacteristic _batteryChar;
        volatile bool _linkClosed;
        volatile bool _resubscribeNeeded;
        volatile bool _weStarted;   // le plugin a lancé la diffusion (sport) pendant cette session
        DateTime _disconnectedSinceUtc = DateTime.MinValue;

        public int HeartRate              { get { lock (_lock) return _heartRate; } }
        public DateTime LastValidHrUtc    { get { lock (_lock) return _lastValidHrUtc; } }
        DateTime LastHrPacketUtc          { get { lock (_lock) return _lastHrPacketUtc; } }
        public int Battery                { get { lock (_lock) return _battery; } }
        public WatchState State           { get { lock (_lock) return _state; } }
        public string StatusText          { get { lock (_lock) return _statusText; } }
        public string DeviceName          { get { lock (_lock) return _deviceName; } }
        public string DeviceAddress       { get { lock (_lock) return _deviceAddress; } }
        public string ProfileName         { get { lock (_lock) return _profileName; } }
        public int Reconnects             { get { lock (_lock) return _reconnects; } }
        public bool IsRunning             { get { return _loop != null; } }

        // ------------------------------------------------------------------ cycle de vie

        public void Start(DafitWatchSettings settings)
        {
            if (_loop != null) return;
            _settings = settings;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunLoop(token));
        }

        /// <summary>Arrête proprement (le profil arrête la diffusion si demandé). Bloque au plus ~6 s.</summary>
        public void Stop()
        {
            if (_loop == null) return;
            _cts.Cancel();
            try { _loop.Wait(6000); } catch (AggregateException) { }
            _loop = null;
            _cts.Dispose();
            _cts = null;
            SetState(WatchState.Stopped, "Arrêté");
        }

        async Task RunLoop(CancellationToken ct)
        {
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndStream(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    var msg = WinRtInterop.Describe(ex);
                    Log("connexion interrompue : " + msg);
                    SetState(WatchState.Reconnecting, "Reconnexion… (" + msg + ")");
                }
                // Coupure en pleine mesure : on retente tout de suite
                bool streamed = LastValidHrUtc > DateTime.UtcNow.AddSeconds(-30);

                try { await Cleanup(ct.IsCancellationRequested).ConfigureAwait(false); }
                catch (Exception ex) { Log("nettoyage : " + WinRtInterop.Describe(ex)); }

                if (ct.IsCancellationRequested) break;
                lock (_lock) _reconnects++;

                // Reconnexion immédiate après une coupure en pleine mesure, puis de plus en plus espacée
                failures = streamed ? 0 : failures + 1;
                int delay = failures == 0 ? 500 : Math.Min(MaxRetryDelayMs, 1000 << Math.Min(failures - 1, 4));
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        // ------------------------------------------------------------------ connexion

        /// <summary>Renvoie faux si la montre n'a pas été trouvée ; sinon ne se termine que par une exception.</summary>
        async Task<bool> ConnectAndStream(CancellationToken ct)
        {
            var device = await OpenDevice(ct).ConfigureAwait(false);
            if (device == null) return false;

            _linkClosed = false;
            _resubscribeNeeded = false;
            _disconnectedSinceUtc = DateTime.MinValue;
            _link = new WatchLink(device);
            device.ConnectionStatusChanged += OnConnectionStatusChanged;

            SetState(WatchState.Connecting, "Connexion à " + DeviceName + "…");
            await _link.Open(ct).ConfigureAwait(false);
            if (_link.Session != null) _link.Session.SessionStatusChanged += OnSessionStatusChanged;

            if (!_link.ServiceUuids.Contains(SvcHeartRate))
                throw new Exception("cet appareil n'expose pas le service cardio standard (0x180D)");

            _profile = WatchProfiles.Resolve(_settings.Profile, _link.ServiceUuids);
            lock (_lock) _profileName = _profile.DisplayName;

            _hrChar = await _link.GetCharacteristic(SvcHeartRate, ChHrMeasure, ct).ConfigureAwait(false);
            if (_hrChar == null) throw new Exception("caractéristique cardio (0x2A37) introuvable");
            _hrChar.ValueChanged += OnHeartRate;
            var subscribedAt = DateTime.UtcNow;
            await _link.Subscribe(_hrChar, true, ct).ConfigureAwait(false);
            await _profile.Attach(_link, ct).ConfigureAwait(false);
            Log("connectée à " + DeviceName + " (" + DeviceAddress + "), profil " + _profile.DisplayName);

            // Sport déjà lancé (à la main, ou par une session précédente) : on ne touche à rien.
            // Renvoyer un démarrage à une montre Da Fit déjà en sport peut l'interrompre.
            SetState(WatchState.WaitingHeartRate, "Connectée — écoute du cardio…");
            for (int i = 0; i < 20 && LastHrPacketUtc < subscribedAt; i++)
                await Task.Delay(200, ct).ConfigureAwait(false);
            if (LastHrPacketUtc >= subscribedAt)
            {
                Log("la montre diffuse déjà le cardio");
            }
            else
            {
                Log("démarrage de la diffusion (" + _profile.DisplayName + ")");
                await _profile.Start(_link, _settings, ct).ConfigureAwait(false);
                _weStarted = true;
                SetState(WatchState.WaitingHeartRate, "Connectée — démarrage du capteur…");
            }
            lock (_lock) if (_lastHrPacketUtc < subscribedAt) _lastHrPacketUtc = DateTime.UtcNow;

            // La batterie est accessoire : lue en parallèle, elle ne retarde jamais le cardio
            var link = _link;
            var unused = Task.Run(() => SetupBattery(link, ct));

            await Monitor(ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>Surveille la liaison ; ne se termine que par une exception (coupure, annulation).</summary>
        async Task Monitor(CancellationToken ct)
        {
            int resyncs = 0;
            var lastResync = DateTime.UtcNow;

            while (true)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;

                if (_linkClosed)
                    throw new LinkLostException("session Bluetooth fermée par Windows");
                if (_disconnectedSinceUtc != DateTime.MinValue &&
                    (now - _disconnectedSinceUtc).TotalMilliseconds > DisconnectGraceMs)
                    throw new LinkLostException("montre déconnectée");

                DateTime lastPacket, lastValid;
                lock (_lock) { lastPacket = _lastHrPacketUtc; lastValid = _lastValidHrUtc; }

                if ((now - lastValid).TotalSeconds < 5)
                {
                    resyncs = 0;
                    SetState(WatchState.Streaming, "Connectée — " + HeartRate + " bpm");
                }
                else if (_disconnectedSinceUtc != DateTime.MinValue)
                {
                    SetState(WatchState.Reconnecting, "Liaison coupée, reconnexion…");
                }
                else if ((now - lastPacket).TotalSeconds < 5)
                {
                    SetState(WatchState.WaitingHeartRate, "Connectée — capteur en stabilisation…");
                }

                if (_disconnectedSinceUtc != DateTime.MinValue) continue;

                // La radio est revenue d'elle-même : l'abonnement est perdu pour un appareil non appairé.
                // On se réabonne, puis on laisse au cardio le temps de revenir avant toute relance.
                if (_resubscribeNeeded)
                {
                    _resubscribeNeeded = false;
                    lastResync = now;
                    Log("liaison rétablie, réabonnement au cardio");
                    await _link.Subscribe(_hrChar, true, ct).ConfigureAwait(false);
                    continue;
                }

                // Plus rien depuis un moment (sport arrêté sur la montre…) : on relance la diffusion
                if ((now - lastPacket).TotalSeconds > NoDataResyncSec && (now - lastResync).TotalSeconds > NoDataResyncSec)
                {
                    if (++resyncs > MaxResyncs)
                        throw new LinkLostException("plus de données de la montre");
                    Log("aucune donnée depuis " + NoDataResyncSec + " s, relance (" + resyncs + ")");
                    lastResync = now;
                    SetState(WatchState.WaitingHeartRate, "Relance de la mesure…");
                    await _profile.Resync(_link, _settings, ct).ConfigureAwait(false);
                    _weStarted = true;
                }
            }
        }

        async Task SetupBattery(WatchLink link, CancellationToken ct)
        {
            try
            {
                if (!link.ServiceUuids.Contains(SvcBattery)) return;
                var ch = await link.GetCharacteristic(SvcBattery, ChBattery, ct).ConfigureAwait(false);
                if (ch == null || link != _link) return;
                if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    ch.ValueChanged += OnBattery;
                    _batteryChar = ch;
                    await link.Subscribe(ch, true, ct).ConfigureAwait(false);
                }
                var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(WatchLink.OpTimeoutMs, ct).ConfigureAwait(false);
                if (read.Status == GattCommunicationStatus.Success)
                    UpdateBattery(WinRtInterop.ToBytes(read.Value));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("batterie : " + WinRtInterop.Describe(ex)); }
        }

        /// <summary>Retrouve la montre : directement par adresse (appareil appairé ou déjà vu), sinon par recherche.</summary>
        async Task<BluetoothLEDevice> OpenDevice(CancellationToken ct)
        {
            ulong address;
            bool byAddress = BluetoothTools.TryParseAddress(_settings.DeviceAddress, out address);
            string name = (_settings.DeviceName ?? "").Trim();
            if (!byAddress && name.Length == 0)
            {
                SetState(WatchState.Stopped, "Aucune montre choisie — choisis-la dans la liste ci-dessous");
                return null;
            }

            if (byAddress)
            {
                lock (_lock) _deviceAddress = BluetoothTools.FormatAddress(address);
                SetState(WatchState.Connecting, "Connexion à " + _deviceAddress + "…");
                // Pas besoin d'annonce Bluetooth : Windows se connecte à l'adresse
                var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(WatchLink.OpTimeoutMs, ct).ConfigureAwait(false);
                if (dev != null)
                {
                    lock (_lock) _deviceName = string.IsNullOrEmpty(dev.Name) ? name : dev.Name;
                    return dev;
                }
            }

            // Appareil inconnu de Windows (adresse aléatoire, jamais vu) : on écoute les annonces
            SetState(WatchState.Searching, "Recherche de la montre…");
            var found = (await BluetoothTools.Scan(SearchTimeoutMs, ct, r => Match(r, byAddress, address, name)).ConfigureAwait(false))
                        .FirstOrDefault(r => Match(r, byAddress, address, name));
            if (found == null)
            {
                SetState(WatchState.Searching, "Montre introuvable — allumée, à portée, et pas connectée au téléphone ?");
                return null;
            }

            lock (_lock)
            {
                _deviceAddress = found.AddressText;
                _deviceName = string.IsNullOrEmpty(found.Name) ? name : found.Name;
            }
            return await BluetoothLEDevice.FromBluetoothAddressAsync(found.Address, found.AddressType).AsTask(WatchLink.OpTimeoutMs, ct).ConfigureAwait(false);
        }

        static bool Match(ScanResult r, bool byAddress, ulong address, string name)
        {
            return byAddress ? r.Address == address : string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase);
        }

        async Task Cleanup(bool stopping)
        {
            var link = _link;
            if (link == null) return;

            // On n'arrête que ce que le plugin a lancé : un sport démarré à la main reste actif
            if (stopping && _weStarted && _profile != null && !_linkClosed)
            {
                try
                {
                    await WithTimeout(_profile.Stop(link, _settings), 2000).ConfigureAwait(false);
                    Log("diffusion arrêtée sur la montre");
                }
                catch (Exception ex) { Log("arrêt : " + WinRtInterop.Describe(ex)); }
            }

            if (_hrChar != null)
            {
                _hrChar.ValueChanged -= OnHeartRate;
                if (stopping && !_linkClosed)
                {
                    try { await WithTimeout(link.Subscribe(_hrChar, false, CancellationToken.None), 1500).ConfigureAwait(false); }
                    catch { }
                }
            }
            if (_batteryChar != null) _batteryChar.ValueChanged -= OnBattery;
            if (link.Device != null) link.Device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            if (link.Session != null) link.Session.SessionStatusChanged -= OnSessionStatusChanged;

            link.Dispose();
            _link = null;
            _hrChar = null;
            _batteryChar = null;
            _profile = null;
            lock (_lock) { _heartRate = 0; }
        }

        static async Task WithTimeout(Task task, int ms)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)).ConfigureAwait(false) != task)
                throw new TimeoutException("délai dépassé");
            await task.ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ événements Bluetooth

        void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                if (_disconnectedSinceUtc == DateTime.MinValue) _disconnectedSinceUtc = DateTime.UtcNow;
                Log("radio : déconnectée");
            }
            else
            {
                if (_disconnectedSinceUtc != DateTime.MinValue) _resubscribeNeeded = true;
                _disconnectedSinceUtc = DateTime.MinValue;
                Log("radio : connectée");
            }
        }

        void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                _linkClosed = true;
                Log("session GATT fermée (" + args.Error + ")");
            }
        }

        void OnHeartRate(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] b;
            try { b = WinRtInterop.ToBytes(args.CharacteristicValue); }
            catch { return; }
            if (b.Length < 2) return;
            int bpm = (b[0] & 0x01) != 0 && b.Length >= 3 ? b[1] | (b[2] << 8) : b[1];
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                _lastHrPacketUtc = now;
                // 0 = capteur en cours de stabilisation après le démarrage de la mesure
                if (bpm <= 0 || bpm > 250) return;
                _heartRate = bpm;
                _lastValidHrUtc = now;
            }

            var handler = HeartRateReceived;
            if (handler != null) ThreadPool.QueueUserWorkItem(_ => handler(bpm));
        }

        void OnBattery(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try { UpdateBattery(WinRtInterop.ToBytes(args.CharacteristicValue)); } catch { }
        }

        void UpdateBattery(byte[] b)
        {
            if (b.Length > 0) lock (_lock) _battery = b[0];
        }

        // ------------------------------------------------------------------ utilitaires

        void SetState(WatchState state, string text)
        {
            lock (_lock) { _state = state; _statusText = text; }
        }

        internal static void Log(string msg)
        {
            SimHub.Logging.Current.Info("[DafitWatch] " + msg);
        }
    }
}
