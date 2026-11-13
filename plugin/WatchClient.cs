using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace User.DafitWatch
{
    public enum WatchState
    {
        Stopped,
        Searching,
        Connecting,
        WaitingHeartRate,
        Streaming
    }

    public class ScanResult
    {
        public ulong  Address     { get; set; }
        public string AddressText { get { return WatchClient.FormatAddress(Address); } }
        public BluetoothAddressType AddressType { get; set; }
        public string Name        { get; set; }
        public short  Rssi        { get; set; }

        public override string ToString()
        {
            return string.Format("{0}  {1}  ({2} dBm)", AddressText, string.IsNullOrEmpty(Name) ? "?" : Name, Rssi);
        }
    }

    /// <summary>
    /// Connexion BLE à une montre Da Fit (protocole MoYoung) :
    /// démarre un sport à distance (CMD 0x68) puis lit le rythme cardiaque
    /// sur le service standard Heart Rate (0x180D / 0x2A37). Se reconnecte tout seul.
    /// </summary>
    public class WatchClient
    {
        static readonly Guid SvcHeartRate  = GattServiceUuids.HeartRate;
        static readonly Guid ChHrMeasure   = GattCharacteristicUuids.HeartRateMeasurement;
        static readonly Guid SvcBattery    = GattServiceUuids.Battery;
        static readonly Guid ChBattery     = GattCharacteristicUuids.BatteryLevel;
        static readonly Guid SvcMoyoung    = new Guid("0000feea-0000-1000-8000-00805f9b34fb");
        static readonly Guid ChMoyoungOut  = new Guid("0000fee2-0000-1000-8000-00805f9b34fb");

        const byte CmdStartStopSport = 0x68;   // CMD_START_STOP_MEASURE_DYNAMIC_RATE (Gadgetbridge)
        const byte SportStop         = 0xFF;

        const int SearchTimeoutMs     = 15000;
        const int RetryDelayMs        = 5000;
        const int NoDataRestartSec    = 20;   // plus rien sur 2A37 -> on relance le sport
        const int MaxSportRestarts    = 3;    // puis on se reconnecte complètement

        // --- Réglages (copiés au Start) ---
        string _targetAddress;
        string _targetName;
        byte   _sportType;
        bool   _stopSportOnExit;

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

        /// <summary>Déclenché à chaque mesure valide (bpm &gt; 0), sur un thread BLE.</summary>
        public event Action<int> HeartRateReceived;

        // --- Connexion courante ---
        CancellationTokenSource _cts;
        Task _loop;
        BluetoothLEDevice _device;
        GattSession _session;
        readonly List<GattDeviceService> _services = new List<GattDeviceService>();
        GattCharacteristic _hrChar;
        GattCharacteristic _batteryChar;
        GattCharacteristic _writeChar;

        public int HeartRate              { get { lock (_lock) return _heartRate; } }
        public DateTime LastValidHrUtc    { get { lock (_lock) return _lastValidHrUtc; } }
        public int Battery                { get { lock (_lock) return _battery; } }
        public WatchState State           { get { lock (_lock) return _state; } }
        public string StatusText          { get { lock (_lock) return _statusText; } }
        public string DeviceName          { get { lock (_lock) return _deviceName; } }
        public string DeviceAddress       { get { lock (_lock) return _deviceAddress; } }
        public bool IsRunning             { get { return _loop != null; } }

        // ------------------------------------------------------------------ cycle de vie

        public void Start(DafitWatchSettings settings)
        {
            if (_loop != null) return;
            _targetAddress   = (settings.DeviceAddress ?? "").Trim();
            _targetName      = (settings.DeviceName ?? "").Trim();
            _sportType       = (byte)Math.Max(0, Math.Min(254, settings.SportType));
            _stopSportOnExit = settings.StopSportOnExit;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunLoop(token));
        }

        /// <summary>Arrête proprement (envoie l'arrêt du sport si demandé). Bloque au plus ~6 s.</summary>
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
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndStream(ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log("Erreur : " + ex.Message);
                    SetStatus("Erreur : " + ex.Message);
                }
                // (C# 5 : pas d'await dans un finally)
                try { await Cleanup(ct.IsCancellationRequested && _stopSportOnExit); }
                catch (Exception ex) { Log("nettoyage : " + ex.Message); }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(RetryDelayMs, ct); } catch (OperationCanceledException) { }
            }
        }

        // ------------------------------------------------------------------ connexion

        async Task ConnectAndStream(CancellationToken ct)
        {
            SetState(WatchState.Searching, "Recherche de la montre…");
            var found = await FindWatch(ct);
            if (found == null)
            {
                SetStatus("Montre introuvable — est-elle connectée au téléphone ?");
                return;
            }

            SetState(WatchState.Connecting, "Connexion à " + found + "…");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(found.Address, found.AddressType).AsTask();
            if (_device == null) throw new Exception("connexion refusée par Windows");
            lock (_lock)
            {
                _deviceName = string.IsNullOrEmpty(_device.Name) ? found.Name : _device.Name;
                _deviceAddress = found.AddressText;
            }

            // Garde la liaison ouverte même entre deux requêtes GATT
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask();
            if (_session != null) _session.MaintainConnection = true;

            _hrChar = await GetCharacteristic(SvcHeartRate, ChHrMeasure, ct);
            if (_hrChar == null) throw new Exception("service cardiaque introuvable");
            _writeChar = await GetCharacteristic(SvcMoyoung, ChMoyoungOut, ct);
            if (_writeChar == null) throw new Exception("service Da Fit (FEEA) introuvable");
            _batteryChar = await GetCharacteristic(SvcBattery, ChBattery, ct);

            _hrChar.ValueChanged += OnHeartRate;
            var status = await _hrChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask();
            if (status != GattCommunicationStatus.Success)
                throw new Exception("abonnement au rythme cardiaque refusé (" + status + ")");

            if (_batteryChar != null)
            {
                _batteryChar.ValueChanged += OnBattery;
                try
                {
                    var read = await _batteryChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask();
                    if (read.Status == GattCommunicationStatus.Success)
                        UpdateBattery(WinRtInterop.ToBytes(read.Value));
                    await _batteryChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask();
                }
                catch (Exception ex) { Log("batterie : " + ex.Message); }
            }

            lock (_lock) _lastHrPacketUtc = DateTime.UtcNow;
            await StartSport();
            SetState(WatchState.WaitingHeartRate, "Connectée — démarrage du capteur…");
            Log("connectée à " + DeviceName + " (" + DeviceAddress + ")");

            int restarts = 0;
            while (true)
            {
                await Task.Delay(1000, ct);

                DateTime lastPacket, lastValid;
                lock (_lock) { lastPacket = _lastHrPacketUtc; lastValid = _lastValidHrUtc; }
                var now = DateTime.UtcNow;

                if ((now - lastValid).TotalSeconds < 5)
                {
                    restarts = 0;
                    SetState(WatchState.Streaming, "Connectée — " + HeartRate + " bpm");
                }
                else if ((now - lastPacket).TotalSeconds < 5)
                {
                    SetState(WatchState.WaitingHeartRate, "Connectée — capteur en stabilisation…");
                }

                if ((now - lastPacket).TotalSeconds > NoDataRestartSec)
                {
                    if (++restarts > MaxSportRestarts)
                        throw new Exception("plus de données de la montre");
                    Log("aucune donnée depuis " + NoDataRestartSec + " s, relance du sport (" + restarts + ")");
                    SetState(WatchState.WaitingHeartRate, "Relance du sport sur la montre…");
                    await StartSport();
                    lock (_lock) _lastHrPacketUtc = DateTime.UtcNow;
                }
            }
        }

        Task StartSport()
        {
            return SendMoyoung(CmdStartStopSport, new[] { _sportType });
        }

        async Task SendMoyoung(byte cmd, byte[] payload)
        {
            var w = _writeChar;
            if (w == null) return;
            int len = payload.Length + 5;
            var packet = new byte[len];
            packet[0] = 0xFE;
            packet[1] = 0xEA;
            packet[2] = (byte)(0x20 + (len >> 8));   // en-tête MoYoung pour MTU > 20
            packet[3] = (byte)(len & 0xFF);
            packet[4] = cmd;
            Array.Copy(payload, 0, packet, 5, payload.Length);
            await w.WriteValueAsync(WinRtInterop.ToBuffer(packet), GattWriteOption.WriteWithoutResponse).AsTask();
        }

        async Task<GattCharacteristic> GetCharacteristic(Guid service, Guid characteristic, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var sr = await _device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached).AsTask();
                if (sr.Status == GattCommunicationStatus.Success)
                {
                    if (sr.Services.Count == 0) return null;
                    var svc = sr.Services[0];
                    lock (_services) _services.Add(svc);
                    var cr = await svc.GetCharacteristicsForUuidAsync(characteristic, BluetoothCacheMode.Uncached).AsTask();
                    if (cr.Status == GattCommunicationStatus.Success && cr.Characteristics.Count > 0)
                        return cr.Characteristics[0];
                }
                await Task.Delay(1000, ct);
            }
            throw new Exception("montre injoignable (service " + service.ToString().Substring(4, 4) + ")");
        }

        async Task Cleanup(bool sendStop)
        {
            if (sendStop && _writeChar != null)
            {
                try
                {
                    await WithTimeout(SendMoyoung(CmdStartStopSport, new[] { SportStop }), 2000);
                    Log("sport arrêté sur la montre");
                }
                catch (Exception ex) { Log("arrêt du sport : " + ex.Message); }
            }

            if (_hrChar != null)
            {
                _hrChar.ValueChanged -= OnHeartRate;
                try
                {
                    await WithTimeout(_hrChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(), 1500);
                }
                catch { }
            }
            if (_batteryChar != null) _batteryChar.ValueChanged -= OnBattery;

            lock (_services)
            {
                foreach (var s in _services) { try { s.Dispose(); } catch { } }
                _services.Clear();
            }
            if (_session != null) { try { _session.Dispose(); } catch { } }
            if (_device != null) { try { _device.Dispose(); } catch { } }

            _hrChar = null; _batteryChar = null; _writeChar = null;
            _session = null; _device = null;
            lock (_lock) { _heartRate = 0; }
        }

        static async Task WithTimeout(Task task, int ms)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)) != task)
                throw new TimeoutException("délai dépassé");
            await task;
        }

        // ------------------------------------------------------------------ notifications

        void OnHeartRate(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var b = WinRtInterop.ToBytes(args.CharacteristicValue);
            if (b.Length < 2) return;
            int bpm = (b[0] & 0x01) != 0 && b.Length >= 3 ? b[1] | (b[2] << 8) : b[1];
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                _lastHrPacketUtc = now;
                // 0 = capteur en cours de stabilisation après le démarrage du sport
                if (bpm <= 0 || bpm > 250) return;
                _heartRate = bpm;
                _lastValidHrUtc = now;
            }

            var handler = HeartRateReceived;
            if (handler != null) handler(bpm);
        }

        void OnBattery(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            UpdateBattery(WinRtInterop.ToBytes(args.CharacteristicValue));
        }

        void UpdateBattery(byte[] b)
        {
            if (b.Length > 0) lock (_lock) _battery = b[0];
        }

        // ------------------------------------------------------------------ recherche

        async Task<ScanResult> FindWatch(CancellationToken ct)
        {
            ulong wanted = 0;
            bool byAddress = TryParseAddress(_targetAddress, out wanted);
            string name = _targetName;
            if (!byAddress && string.IsNullOrEmpty(name))
                throw new Exception("ni adresse ni nom de montre configurés");

            var results = await Scan(SearchTimeoutMs, ct, r =>
                byAddress ? r.Address == wanted
                          : string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            return results.FirstOrDefault(r =>
                byAddress ? r.Address == wanted
                          : string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Écoute les annonces BLE. S'arrête au bout de <paramref name="timeoutMs"/>
        /// ou dès qu'un appareil satisfait <paramref name="stopWhen"/>.
        /// </summary>
        public static async Task<List<ScanResult>> Scan(int timeoutMs, CancellationToken ct, Func<ScanResult, bool> stopWhen)
        {
            var seen = new Dictionary<ulong, ScanResult>();
            var done = new TaskCompletionSource<bool>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

            watcher.Received += (w, e) =>
            {
                lock (seen)
                {
                    ScanResult r;
                    if (!seen.TryGetValue(e.BluetoothAddress, out r))
                    {
                        r = new ScanResult { Address = e.BluetoothAddress };
                        seen[e.BluetoothAddress] = r;
                    }
                    r.AddressType = e.BluetoothAddressType;
                    r.Rssi = e.RawSignalStrengthInDBm;
                    if (!string.IsNullOrEmpty(e.Advertisement.LocalName)) r.Name = e.Advertisement.LocalName;
                    if (stopWhen != null && stopWhen(r)) done.TrySetResult(true);
                }
            };
            watcher.Stopped += (w, e) => done.TrySetResult(false);

            watcher.Start();
            using (ct.Register(() => done.TrySetCanceled()))
            {
                try { await Task.WhenAny(done.Task, Task.Delay(timeoutMs)); }
                finally { watcher.Stop(); }
            }
            ct.ThrowIfCancellationRequested();

            lock (seen) return seen.Values.OrderByDescending(r => r.Rssi).ToList();
        }

        // ------------------------------------------------------------------ utilitaires

        public static string FormatAddress(ulong a)
        {
            var bytes = BitConverter.GetBytes(a);
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0]);
        }

        public static bool TryParseAddress(string text, out ulong address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var hex = text.Replace(":", "").Replace("-", "").Trim();
            return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        void SetState(WatchState state, string text)
        {
            lock (_lock) { _state = state; _statusText = text; }
        }

        void SetStatus(string text)
        {
            lock (_lock) _statusText = text;
        }

        static void Log(string msg)
        {
            SimHub.Logging.Current.Info("[DafitWatch] " + msg);
        }
    }
}
