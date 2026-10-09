using System;
using System.IO;
using Microsoft.Win32;
using Newtonsoft.Json;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de configuración usando Registry
/// </summary>
public class SettingsService : ISettingsService
{
    private const string RegistryKey = @"SOFTWARE\ScreenRecorder";
    private const string SettingsValueName = "Settings";
    private readonly ILoggerService _logger;

    public SettingsService(ILoggerService logger)
    {
        _logger = logger;
    }

    public RecordingSettings LoadSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
            if (key != null)
            {
                var settingsJson = key.GetValue(SettingsValueName) as string;
                if (!string.IsNullOrEmpty(settingsJson))
                {
                    var settings = JsonConvert.DeserializeObject<RecordingSettings>(settingsJson);
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
            using var key = Registry.LocalMachine.CreateSubKey(RegistryKey, true);
            if (key != null)
            {
                var settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
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
