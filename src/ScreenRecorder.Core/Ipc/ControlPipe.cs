using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Constants;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Core.Ipc;

/// <summary>
/// IPC entre instancias mediante Mutex de instancia única + named pipe. Permite que Wctl y los
/// comandos legacy (perro/quieto/echate) actúen sobre la instancia ya en ejecución.
/// Vive en Core para que tanto el agente ligero (WSearchAgent) como la UI de ajustes
/// (WSearchCfg) y Wctl lo compartan sin arrastrar dependencias de interfaz.
/// </summary>
public static class ControlPipe
{
    public const string PipeName = AppConstants.PipeName;
    public const string MutexName = @"Global\WSearchAgentSingleInstance";

    public const string CommandShowConfig = "perro";
    public const string CommandStopAll = "echate";
    public const string CommandRestart = "quieto";

    public static bool IsStopFlagSet() => AppConstants.IsStopFlagSet();

    public static void SetStopFlag() => AppConstants.SetStopFlag();

    public static void ClearStopFlag() => AppConstants.ClearStopFlag();

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
    /// Envía un comando legacy (texto plano) a la instancia en ejecución por el named pipe.
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
    /// Envía un comando legacy reintentando: la instancia principal puede estar ARRANCANDO
    /// (mutex ya tomado, servidor del pipe aún no escuchando).
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
    /// Inicia el servidor del pipe en segundo plano. Cada petición se resuelve con
    /// <paramref name="handler"/> y su respuesta se devuelve por el mismo pipe. Acepta tanto el
    /// protocolo JSON de Wctl como los comandos legacy de texto plano (perro/quieto/echate).
    /// </summary>
    public static void StartServer(Func<ControlRequest, ControlResponse> handler, ILoggerService logger)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = NamedPipeServerStreamAcl.Create(
                        PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, CreatePipeSecurity());

                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    ControlResponse response;
                    if (line.TrimStart().StartsWith("{"))
                    {
                        var request = JsonSerializer.Deserialize<ControlRequest>(line, ControlClient.JsonOptions)
                                      ?? new ControlRequest();
                        logger.LogInfo($"Comando recibido por pipe: {request.Cmd}");
                        response = SafeInvoke(handler, request, logger);
                    }
                    else
                    {
                        var legacy = line.Trim();
                        logger.LogInfo($"Comando legacy recibido por pipe: {legacy}");
                        response = SafeInvoke(handler, new ControlRequest { Cmd = legacy }, logger);
                    }

                    try
                    {
                        writer.WriteLine(JsonSerializer.Serialize(response, ControlClient.JsonOptions));
                    }
                    catch
                    {
                        // El cliente legacy no lee la respuesta y ya cerró: no es un error.
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("Error en el servidor del pipe de control", ex);
                    await Task.Delay(2000);
                }
            }
        });
    }

    private static ControlResponse SafeInvoke(Func<ControlRequest, ControlResponse> handler,
        ControlRequest request, ILoggerService logger)
    {
        try
        {
            return handler(request);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error al ejecutar comando '{request.Cmd}'", ex);
            return ControlResponse.Fail(ex.Message);
        }
    }
}
