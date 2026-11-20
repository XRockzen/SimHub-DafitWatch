// Teste le client Bluetooth du plugin hors SimHub. Compilé/lancé par tools\WatchClientTest\run.ps1
//   run <secondes> <adresse> [profil]   connexion + cardio pendant N secondes, puis arrêt
//   list                                appareils appairés + à proximité
//   diag <adresse>                      rapport de diagnostic
using System;
using System.Threading;
using User.DafitWatch;

class Program
{
    static int Main(string[] args)
    {
        log4net.Config.BasicConfigurator.Configure();   // journaux [DafitWatch] dans la console
        var mode = args.Length > 0 ? args[0] : "run";

        if (mode == "list")
        {
            foreach (var d in BluetoothTools.ListDevices(8000, CancellationToken.None).Result)
                Console.WriteLine(d);
            return 0;
        }

        ulong address;
        if (args.Length < 2 && mode == "diag" || args.Length > 2 && !BluetoothTools.TryParseAddress(args[2], out address) && mode == "run")
        {
            Console.WriteLine("adresse manquante ou invalide");
            return 1;
        }

        if (mode == "diag")
        {
            BluetoothTools.TryParseAddress(args[1], out address);
            Console.WriteLine(BluetoothTools.Diagnose(address, CancellationToken.None).Result);
            return 0;
        }

        int seconds = args.Length > 1 ? int.Parse(args[1]) : 40;
        var settings = new DafitWatchSettings();
        if (args.Length > 2) settings.DeviceAddress = args[2];
        if (args.Length > 3) settings.Profile = args[3];

        var client = new WatchClient();
        client.HeartRateReceived += bpm => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  ♥ " + bpm + " bpm");
        client.Start(settings);

        string last = null;
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            var s = client.State + " | " + client.StatusText + " | " + client.ProfileName + " | batt " + client.Battery;
            if (s != last && client.State != WatchState.Streaming || last == null) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + s + "]"); }
            last = s;
            Thread.Sleep(200);
        }
        Console.WriteLine("Arrêt…");
        client.Stop();
        Console.WriteLine("[" + client.State + " | " + client.StatusText + "] reconnexions : " + client.Reconnects);
        return 0;
    }
}
