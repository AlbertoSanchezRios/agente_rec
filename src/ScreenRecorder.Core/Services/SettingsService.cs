using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de configuración usando Registry
/// </summary>
public class SettingsService : ISettingsService
{
    private const string DefaultRegistryKey = @"SOFTWARE\ScreenRecorder";
    private const string SettingsValueName = "Settings";
    private readonly ILoggerService _logger;
    private readonly string _registryKey;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <param name="logger">Logger.</param>
    /// <param name="registryKey">Clave de registro a usar (para pruebas, permite una clave aislada).</param>
    public SettingsService(ILoggerService logger, string? registryKey = null)
    {
        _logger = logger;
        _registryKey = string.IsNullOrWhiteSpace(registryKey) ? DefaultRegistryKey : registryKey;
    }

    public RecordingSettings LoadSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(_registryKey);
            if (key != null)
            {
                var settingsJson = key.GetValue(SettingsValueName) as string;
                if (!string.IsNullOrEmpty(settingsJson))
                {
                    var settings = JsonSerializer.Deserialize<RecordingSettings>(settingsJson);
                    if (settings != null)
                    {
                        _logger.LogInfo("Configuración cargada desde Registry");
                        return settings;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al cargar configuración", ex);
        }

        // Retornar configuración por defecto
        _logger.LogInfo("Usando configuración por defecto");
        return new RecordingSettings();
    }

    public void SaveSettings(RecordingSettings settings)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(_registryKey, true);
            if (key != null)
            {
                var settingsJson = JsonSerializer.Serialize(settings, JsonOptions);
                key.SetValue(SettingsValueName, settingsJson);
                _logger.LogInfo("Configuración guardada en Registry");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError("No se tienen permisos de administrador para guardar configuración", ex);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al guardar configuración", ex);
            throw;
        }
    }

    public bool IsNetworkPathConfigured()
    {
        var settings = LoadSettings();
        return !string.IsNullOrWhiteSpace(settings.NetworkPath);
    }
}
