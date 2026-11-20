using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace User.DafitWatch
{
    public class ScanResult
    {
        public ulong  Address     { get; set; }
        public string AddressText { get { return BluetoothTools.FormatAddress(Address); } }
        public BluetoothAddressType AddressType { get; set; }
        public string Name        { get; set; }
        public short  Rssi        { get; set; }
        public bool   HasHeartRate { get; set; }
    }

    /// <summary>Appareil proposé dans la liste des réglages.</summary>
    public class DeviceChoice
    {
        public ulong  Address     { get; set; }
        public string AddressText { get { return BluetoothTools.FormatAddress(Address); } }
        public string Name        { get; set; }
        public string Origin      { get; set; }
        public bool   HasHeartRate { get; set; }

        public override string ToString()
        {
            return string.Format("{0,-24} {1}  {2}{3}",
                string.IsNullOrEmpty(Name) ? "(sans nom)" : Name, AddressText, Origin,
                HasHeartRate ? "  ♥ cardio" : "");
        }
    }

    public static class BluetoothTools
    {
        // ------------------------------------------------------------------ adresses

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
            return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, null, out address);
        }

        // ------------------------------------------------------------------ recherche

        /// <summary>
        /// Écoute les annonces BLE. S'arrête au bout de <paramref name="timeoutMs"/>
        /// ou dès qu'un appareil satisfait <paramref name="stopWhen"/>.
        /// </summary>
        public static async Task<List<ScanResult>> Scan(int timeoutMs, CancellationToken ct, Func<ScanResult, bool> stopWhen)
        {
            var seen = new Dictionary<ulong, ScanResult>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    if (e.Advertisement.ServiceUuids.Contains(GattServiceUuids.HeartRate)) r.HasHeartRate = true;
                    if (stopWhen != null && stopWhen(r)) done.TrySetResult(true);
                }
            };
            watcher.Stopped += (w, e) => done.TrySetResult(false);

            watcher.Start();
            using (ct.Register(() => done.TrySetCanceled()))
            {
                try { await Task.WhenAny(done.Task, Task.Delay(timeoutMs)).ConfigureAwait(false); }
                finally { watcher.Stop(); }
            }
            ct.ThrowIfCancellationRequested();

            lock (seen) return seen.Values.OrderByDescending(r => r.Rssi).ToList();
        }

        /// <summary>Appareils appairés à Windows (BLE et Bluetooth classique), puis ceux qui émettent à proximité.</summary>
        public static async Task<List<DeviceChoice>> ListDevices(int scanMs, CancellationToken ct)
        {
            var result = new Dictionary<ulong, DeviceChoice>();

            try
            {
                var le = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))
                                                .AsTask(10000, ct).ConfigureAwait(false);
                foreach (var di in le)
                {
                    using (var d = await BluetoothLEDevice.FromIdAsync(di.Id).AsTask(10000, ct).ConfigureAwait(false))
                        if (d != null && !result.ContainsKey(d.BluetoothAddress))
                            result[d.BluetoothAddress] = new DeviceChoice { Address = d.BluetoothAddress, Name = di.Name, Origin = "appairé (BLE)" };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { WatchClient.Log("liste BLE appairés : " + WinRtInterop.Describe(ex)); }

            try
            {
                var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))
                                                     .AsTask(10000, ct).ConfigureAwait(false);
                foreach (var di in classic)
                {
                    using (var d = await BluetoothDevice.FromIdAsync(di.Id).AsTask(10000, ct).ConfigureAwait(false))
                        if (d != null && !result.ContainsKey(d.BluetoothAddress))
                            result[d.BluetoothAddress] = new DeviceChoice { Address = d.BluetoothAddress, Name = di.Name, Origin = "appairé" };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { WatchClient.Log("liste appairés : " + WinRtInterop.Describe(ex)); }

            if (scanMs > 0)
            {
                foreach (var r in await Scan(scanMs, ct, null).ConfigureAwait(false))
                {
                    DeviceChoice c;
                    if (result.TryGetValue(r.Address, out c)) { c.HasHeartRate |= r.HasHeartRate; continue; }
                    result[r.Address] = new DeviceChoice
                    {
                        Address = r.Address, Name = r.Name, HasHeartRate = r.HasHeartRate,
                        Origin = "à proximité (" + r.Rssi + " dBm)",
                    };
                }
            }

            // Appareils nommés et cardio d'abord, les annonces anonymes à la fin
            return result.Values
                .OrderByDescending(c => c.HasHeartRate)
                .ThenBy(c => c.Origin.StartsWith("à proximité") ? 1 : 0)
                .ThenBy(c => string.IsNullOrEmpty(c.Name) ? 1 : 0)
                .ThenBy(c => c.Name)
                .ToList();
        }

        // ------------------------------------------------------------------ diagnostic

        // Environment.OSVersion renvoie 6.2 sans manifeste d'application : on lit le registre
        static string WindowsVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return string.Format("{0} {1} (build {2})", k.GetValue("ProductName"), k.GetValue("DisplayVersion"), k.GetValue("CurrentBuild"));
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        static readonly Dictionary<Guid, string> KnownServices = new Dictionary<Guid, string>
        {
            { new Guid("00001800-0000-1000-8000-00805f9b34fb"), "Generic Access" },
            { new Guid("00001801-0000-1000-8000-00805f9b34fb"), "Generic Attribute" },
            { new Guid("0000180a-0000-1000-8000-00805f9b34fb"), "Device Information" },
            { new Guid("0000180d-0000-1000-8000-00805f9b34fb"), "Heart Rate (standard)" },
            { new Guid("0000180f-0000-1000-8000-00805f9b34fb"), "Battery" },
            { new Guid("0000feea-0000-1000-8000-00805f9b34fb"), "MoYoung / Da Fit" },
            { new Guid("0000fee0-0000-1000-8000-00805f9b34fb"), "Huami / Zepp / Mi Band" },
            { new Guid("0000fe95-0000-1000-8000-00805f9b34fb"), "Xiaomi" },
            { new Guid("0000fe86-0000-1000-8000-00805f9b34fb"), "Huawei" },
        };

        static readonly Guid[] DeviceInfoStrings =
        {
            new Guid("00002a29-0000-1000-8000-00805f9b34fb"), // fabricant
            new Guid("00002a24-0000-1000-8000-00805f9b34fb"), // modèle
            new Guid("00002a26-0000-1000-8000-00805f9b34fb"), // firmware
            new Guid("00002a28-0000-1000-8000-00805f9b34fb"), // logiciel
            new Guid("00002a27-0000-1000-8000-00805f9b34fb"), // matériel
        };

        /// <summary>
        /// Rapport texte (services GATT, propriétés, infos fabricant) à envoyer au développeur
        /// pour ajouter le support d'un nouveau modèle de montre.
        /// </summary>
        public static async Task<string> Diagnose(ulong address, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Smartwatch BPM " + PluginUpdater.Format(PluginUpdater.CurrentVersion) + " — diagnostic Bluetooth");
            sb.AppendLine("Date : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Windows : " + WindowsVersion());

            using (var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(10000, ct).ConfigureAwait(false))
            {
                if (dev == null) { sb.AppendLine("Appareil introuvable."); return sb.ToString(); }
                sb.AppendLine("Nom : " + dev.Name);
                sb.AppendLine("Apparence : 0x" + dev.Appearance.RawValue.ToString("X4"));
                sb.AppendLine("Appairé à Windows (BLE) : " + (dev.DeviceInformation != null && dev.DeviceInformation.Pairing.IsPaired));
                sb.AppendLine("Type d'adresse : " + dev.BluetoothAddressType);

                var sr = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(15000, ct).ConfigureAwait(false);
                sb.AppendLine("Services GATT : " + sr.Status);
                sb.AppendLine();
                if (sr.Status != GattCommunicationStatus.Success) return sb.ToString();

                foreach (var svc in sr.Services)
                {
                    using (svc)
                    {
                        string label;
                        KnownServices.TryGetValue(svc.Uuid, out label);
                        sb.AppendLine("[" + WatchLink.Short(svc.Uuid) + "] " + (label ?? ""));
                        var cr = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(10000, ct).ConfigureAwait(false);
                        if (cr.Status != GattCommunicationStatus.Success) { sb.AppendLine("    (" + cr.Status + ")"); continue; }
                        foreach (var ch in cr.Characteristics)
                        {
                            sb.Append("    " + WatchLink.Short(ch.Uuid) + "  " + ch.CharacteristicProperties);
                            if (DeviceInfoStrings.Contains(ch.Uuid) && ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                            {
                                try
                                {
                                    var v = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(5000, ct).ConfigureAwait(false);
                                    if (v.Status == GattCommunicationStatus.Success)
                                        sb.Append("  = \"" + Encoding.UTF8.GetString(WinRtInterop.ToBytes(v.Value)).TrimEnd('\0') + "\"");
                                }
                                catch { }
                            }
                            sb.AppendLine();
                        }
                    }
                }
            }
            return sb.ToString();
        }
    }
}
