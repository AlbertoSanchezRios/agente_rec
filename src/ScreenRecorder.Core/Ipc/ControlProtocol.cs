using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ScreenRecorder.Core.Constants;

namespace ScreenRecorder.Core.Ipc;

/// <summary>Solicitud de control enviada al agente por el named pipe.</summary>
public sealed class ControlRequest
{
    public string Cmd { get; set; } = string.Empty;
    public Dictionary<string, string>? Args { get; set; }
}

/// <summary>Respuesta del agente. <see cref="Data"/> transporta el resultado en JSON.</summary>
public sealed class ControlResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }

    public static ControlResponse Success(object? data = null) => new()
    {
        Ok = true,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, ControlClient.JsonOptions)
    };

    public static ControlResponse Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// Cliente del canal de control (usado por Wctl y por las instancias de control). El protocolo
/// es una petición JSON por línea y una respuesta JSON por línea; también acepta los comandos
/// legacy de texto plano (perro/quieto/echate) para compatibilidad.
/// </summary>
public static class ControlClient
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Envía un comando y espera la respuesta. Devuelve null si no hay instancia viva.
    /// </summary>
    public static ControlResponse? Send(string cmd, IDictionary<string, string>? args = null,
        int connectTimeoutMs = 2000, int attempts = 1, int retryDelayMs = 500)
    {
        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                return SendOnce(cmd, args, connectTimeoutMs);
            }
            catch (TimeoutException)
            {
                return null; // no hay instancia escuchando
            }
            catch when (i < attempts)
            {
                Thread.Sleep(retryDelayMs);
            }
        }
        return null;
    }

    private static ControlResponse SendOnce(string cmd, IDictionary<string, string>? args, int connectTimeoutMs)
    {
        using var pipe = new NamedPipeClientStream(".", AppConstants.PipeName, PipeDirection.InOut,
            PipeOptions.None);
        try
        {
            pipe.Connect(connectTimeoutMs);
        }
        catch (TimeoutException)
        {
            throw;
        }

        var request = new ControlRequest
        {
            Cmd = cmd,
            Args = args == null ? null : new Dictionary<string, string>(args)
        };

        var payload = JsonSerializer.Serialize(request, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            return ControlResponse.Fail("sin respuesta del agente");
        }

        try
        {
            return JsonSerializer.Deserialize<ControlResponse>(line, JsonOptions)
                   ?? ControlResponse.Fail("respuesta vacía del agente");
        }
        catch (JsonException)
        {
            // Respuesta de una versión antigua que no hablaba JSON.
            return ControlResponse.Fail($"respuesta no reconocida: {line}");
        }
    }
}
