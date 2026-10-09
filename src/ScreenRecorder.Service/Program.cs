using System.ServiceProcess;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Service;

/// <summary>
/// Punto de entrada del servicio Windows de Screen Recorder (watchdog SYSTEM).
/// Sin argumentos corre como servicio; con /console corre en consola para depuración.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("/console", StringComparer.OrdinalIgnoreCase) ||
            Environment.UserInteractive)
        {
            var logger = new LoggerService();
            using var service = new WatchdogService(logger);
            service.StartConsole();
            Console.WriteLine("Watchdog en marcha (modo consola). Pulse ENTER para salir.");
            Console.ReadLine();
            service.StopConsole();
            return 0;
        }

        ServiceBase.Run(new WatchdogService(new LoggerService()));
        return 0;
    }
}
