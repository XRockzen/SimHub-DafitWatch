using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace User.DafitWatch
{
    /// <summary>
    /// Ce qu'il faut faire, selon le modèle de montre, pour qu'elle diffuse son cardio
    /// sur le service Bluetooth standard Heart Rate (0x180D / 0x2A37).
    /// Ajouter un modèle = ajouter une classe ici et la déclarer dans <see cref="WatchProfiles"/>.
    /// </summary>
    public abstract class WatchProfile
    {
        /// <summary>Identifiant enregistré dans les réglages.</summary>
        public abstract string Id { get; }

        /// <summary>Nom affiché.</summary>
        public abstract string DisplayName { get; }

        /// <summary>Vrai si ce profil reconnaît l'appareil d'après ses services GATT.</summary>
        public virtual bool Matches(ICollection<Guid> services) { return false; }

        /// <summary>Juste après la connexion, avant toute commande : abonnements propres au modèle.</summary>
        public virtual Task Attach(WatchLink link, CancellationToken ct)
        {
            return Task.FromResult(0);
        }

        /// <summary>
        /// Démarrer la diffusion si la montre l'exige. N'est appelé que si aucun cardio
        /// n'arrive déjà (sport lancé à la main sur la montre, ceinture cardio…).
        /// </summary>
        public virtual Task Start(WatchLink link, DafitWatchSettings settings, CancellationToken ct)
        {
            return Task.FromResult(0);
        }

        /// <summary>Plus de cardio depuis un moment : relancer la diffusion.</summary>
        public virtual Task Resync(WatchLink link, DafitWatchSettings settings, CancellationToken ct)
        {
            return Start(link, settings, ct);
        }

        /// <summary>Déconnexion volontaire (fermeture de SimHub, bouton Déconnecter).</summary>
        public virtual Task Stop(WatchLink link, DafitWatchSettings settings)
        {
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Tout appareil qui diffuse déjà son cardio en Heart Rate standard :
    /// ceintures cardio, et montres en mode « diffusion de la fréquence cardiaque »
    /// (Garmin, Polar, Coros, Suunto, Amazfit/Zepp, Huawei, Xiaomi récentes…).
    /// </summary>
    public class StandardHeartRateProfile : WatchProfile
    {
        public override string Id { get { return "standard"; } }
        public override string DisplayName { get { return "Cardio Bluetooth standard"; } }
    }

    /// <summary>
    /// Montres de l'app Da Fit (protocole MoYoung, service 0xFEEA) : elles ne diffusent le cardio
    /// que pendant un sport, que l'on lance à distance (CMD 0x68 + type de sport, 0xFF pour arrêter).
    /// </summary>
    public class DaFitProfile : WatchProfile
    {
        static readonly Guid SvcMoyoung   = new Guid("0000feea-0000-1000-8000-00805f9b34fb");
        static readonly Guid ChMoyoungOut = new Guid("0000fee2-0000-1000-8000-00805f9b34fb");
        const byte CmdStartStopSport = 0x68;   // CMD_START_STOP_MEASURE_DYNAMIC_RATE (Gadgetbridge)
        const byte SportStop         = 0xFF;

        public override string Id { get { return "dafit"; } }
        public override string DisplayName { get { return "Da Fit (MoYoung)"; } }

        static readonly Guid ChMoyoungIn  = new Guid("0000fee3-0000-1000-8000-00805f9b34fb");

        public override bool Matches(ICollection<Guid> services)
        {
            return services.Contains(SvcMoyoung);
        }

        /// <summary>
        /// Écoute les messages de la montre : elle annonce elle-même le début (0x68 + type)
        /// et la fin (0x68 FF) d'un sport, qu'il soit lancé à distance ou à la main.
        /// </summary>
        public override async Task Attach(WatchLink link, CancellationToken ct)
        {
            try
            {
                var ch = await link.GetCharacteristic(SvcMoyoung, ChMoyoungIn, ct).ConfigureAwait(false);
                if (ch == null) return;
                ch.ValueChanged += (s, e) =>
                {
                    byte[] p;
                    try { p = WinRtInterop.ToBytes(e.CharacteristicValue); } catch { return; }
                    if (p.Length >= 6 && p[0] == 0xFE && p[1] == 0xEA && p[4] == CmdStartStopSport)
                        WatchClient.Log(p[5] == SportStop ? "montre : sport arrêté" : "montre : sport démarré (type " + p[5] + ")");
                    else if (p.Length >= 5 && p[0] == 0xFE && p[1] == 0xEA)
                        WatchClient.Log("montre : message 0x" + p[4].ToString("X2"));
                };
                await link.Subscribe(ch, true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { WatchClient.Log("écoute Da Fit : " + WinRtInterop.Describe(ex)); }
        }

        public override Task Start(WatchLink link, DafitWatchSettings settings, CancellationToken ct)
        {
            byte sport = (byte)Math.Max(0, Math.Min(254, settings.SportType));
            return Send(link, CmdStartStopSport, new[] { sport }, ct);
        }

        public override Task Stop(WatchLink link, DafitWatchSettings settings)
        {
            if (!settings.StopSportOnExit) return Task.FromResult(0);
            return Send(link, CmdStartStopSport, new[] { SportStop }, CancellationToken.None);
        }

        static async Task Send(WatchLink link, byte cmd, byte[] payload, CancellationToken ct)
        {
            var ch = await link.GetCharacteristic(SvcMoyoung, ChMoyoungOut, ct).ConfigureAwait(false);
            if (ch == null) throw new Exception("service Da Fit (FEEA) introuvable");

            int len = payload.Length + 5;
            var packet = new byte[len];
            packet[0] = 0xFE;
            packet[1] = 0xEA;
            packet[2] = (byte)(0x20 + (len >> 8));   // en-tête MoYoung pour MTU > 20
            packet[3] = (byte)(len & 0xFF);
            packet[4] = cmd;
            Array.Copy(payload, 0, packet, 5, payload.Length);

            var status = await ch.WriteValueAsync(WinRtInterop.ToBuffer(packet), GattWriteOption.WriteWithoutResponse)
                                 .AsTask(5000, ct).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                throw new Exception("commande refusée par la montre (" + status + ")");
        }
    }

    public static class WatchProfiles
    {
        public const string AutoId = "auto";

        /// <summary>Profils spécifiques d'abord : le standard sert de repli.</summary>
        public static readonly WatchProfile[] All =
        {
            new DaFitProfile(),
            new StandardHeartRateProfile(),
        };

        public static WatchProfile Resolve(string id, ICollection<Guid> services)
        {
            if (!string.IsNullOrEmpty(id) && id != AutoId)
                foreach (var p in All)
                    if (p.Id == id) return p;
            foreach (var p in All)
                if (p.Matches(services)) return p;
            return All[All.Length - 1];
        }
    }
}
