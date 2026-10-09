using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.UI;

/// <summary>
/// IPC entre instancias de la aplicación mediante Mutex de instancia única + named pipe.
/// Permite que los comandos (perro, echate, quieto) actúen sobre la instancia ya en ejecución.
/// </summary>
public static class ControlPipe
{
    public const string PipeName = "ScreenRecorderControl";
    public const string MutexName = @"Global\WSearchHostSingleInstance";

    public const string CommandShowConfig = "perro";
    public const string CommandStopAll = "echate";
    public const string CommandRestart = "quieto";

    /// <summary>
    /// Bandera de detención voluntaria ('echate'): mientras exista, los arranques automáticos
    /// (/background, p. ej. el minutero de la tarea programada) no inician la grabación.
    /// Se borra con 'quieto', al Guardar en Ajustes o al ejecutar la app manualmente.
    /// </summary>
    private static readonly string StopFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ScreenRecorder", "stopped");

    public static bool IsStopFlagSet()
    {
        try { return File.Exists(StopFlagPath); }
        catch { return false; }
    }

    public static void SetStopFlag()
    {
        try
        {
            var dir = Path.GetDirectoryName(StopFlagPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(StopFlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { /* nunca romper por la bandera */ }
    }

    public static void ClearStopFlag()
    {
        try { if (File.Exists(StopFlagPath)) File.Delete(StopFlagPath); }
        catch { /* nunca romper por la bandera */ }
    }

    /// <summary>
    /// Intenta adquirir el Mutex de instancia única. Devuelve null si ya hay otra instancia.
    /// El Mutex debe mantenerse durante toda la vida de la instancia principal.
    /// </summary>
    public static Mutex? TryAcquireInstanceMutex()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
            if (createdNew)
            {
                return mutex; // somos la primera instancia
            }

            // El mutex ya existía: intentar tomarlo sin esperar
            try
            {
                if (mutex.WaitOne(0))
                {
                    return mutex; // la instancia anterior terminó pero el mutex no se liberó aún
                }

                mutex.Dispose();
                return null; // hay otra instancia en ejecución
            }
            catch (AbandonedMutexException)
            {
                // La instancia anterior se cerró abruptamente; ahora el mutex es nuestro
                return mutex;
            }
        }
        catch (Exception)
        {
            mutex?.Dispose();
            return null; // ante cualquier duda, asumir que ya hay instancia
        }
    }

    /// <summary>
    /// Envía un comando a la instancia en ejecución por el named pipe.
    /// </summary>
    public static void SendCommand(string command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        pipe.Connect(2000);
        var bytes = Encoding.UTF8.GetBytes(command);
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
    }

    /// <summary>
    /// Envía un comando reintentando: la instancia principal puede estar ARRANCANDO
    /// (mutex ya tomado, servidor del pipe aún no escuchando). Usado por /postinstall para
    /// que la ventana de configuración no se pierda por esa carrera.
    /// </summary>
    public static bool SendCommandWithRetry(string command, int attempts, int delayMs)
    {
        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                SendCommand(command);
                return true;
            }
            catch
            {
                if (i < attempts) Thread.Sleep(delayMs);
            }
        }
        return false;
    }

    /// <summary>
    /// Indica si hay una instancia principal en ejecución (sin quedarse con el mutex).
    /// </summary>
    public static bool IsInstanceRunning()
    {
        var mutex = TryAcquireInstanceMutex();
        if (mutex == null) return true;
        try { mutex.ReleaseMutex(); } catch { /* no se tenía */ }
        mutex.Dispose();
        return false;
    }

    /// <summary>
    /// Construye la seguridad del pipe: SOLO SISTEMA y Administradores pueden conectar.
    /// Así un proceso de un usuario estándar no puede enviar 'echate'/'quieto'/'perro' al
    /// grabador (anti-manipulación); los comandos quedan reservados a TI elevado.
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Inicia el servidor del pipe en segundo plano. Cada comando recibido se notifica en <paramref name="onCommand"/>.
    /// El callback se ejecuta en un hilo de segundo plano: marshalar a la UI si se muestran ventanas.
    /// </summary>
    public static void StartServer(Action<string> onCommand, ILoggerService logger)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = NamedPipeServerStreamAcl.Create(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, CreatePipeSecurity());

                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var command = (await reader.ReadToEndAsync()).Trim();

                    if (!string.IsNullOrEmpty(command))
                    {
                        logger.LogInfo($"Comando recibido por pipe: {command}");
                        try
                        {
                            onCommand(command);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"Error al ejecutar comando '{command}'", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Si el pipe se rompe, esperar un momento y volver a escuchar
                    logger.LogError("Error en el servidor del pipe de control", ex);
                    await Task.Delay(2000);
                }
            }
        });
    }
}
