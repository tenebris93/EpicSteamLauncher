using System;
using System.Threading;
using System.Diagnostics;

namespace EpicSteamLauncher
{
    internal class Program
    {
        // Referenced code from: https://seanzwrites.com/posts/how-to-play-epic-games-on-steam-and-steamlink/

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("ERROR: Needs launch URL and EXE Name");
                return;
            }

            var epicUrl = args[0];
            var exeName = args[1];

            var ps = new ProcessStartInfo(epicUrl)
            {
                UseShellExecute = true,
                Verb = "open"
            };

            Console.WriteLine($"Starting url: {epicUrl}");
            Process.Start(ps);

            Thread.Sleep(5000);

            var gameProcesses = Process.GetProcessesByName(exeName);

            if (gameProcesses.Length != 1)
            {
                Console.WriteLine($"Could not find a single process with name: {exeName}");
                return;
            }

            Console.WriteLine($"Game started.");
            gameProcesses[0].WaitForExit();
        }
    }
}
