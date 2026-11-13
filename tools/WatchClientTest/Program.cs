// Teste WatchClient hors SimHub : connexion, sport, BPM pendant N secondes, puis arrêt.
// Compilé/lancé par tools\WatchClientTest\run.ps1
using System;
using System.Threading;
using User.DafitWatch;

class Program
{
    static void Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 40;
        var settings = new DafitWatchSettings();
        if (args.Length > 1) settings.DeviceAddress = args[1];

        var client = new WatchClient();
        client.HeartRateReceived += bpm => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  ♥ " + bpm + " bpm");
        client.Start(settings);

        string last = null;
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            var s = client.State + " | " + client.StatusText + " | batt " + client.Battery;
            if (s != last) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + s + "]"); last = s; }
            Thread.Sleep(200);
        }
        Console.WriteLine("Arrêt…");
        client.Stop();
        Console.WriteLine("[" + client.State + " | " + client.StatusText + "]");
    }
}
