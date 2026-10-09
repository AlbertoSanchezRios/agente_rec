using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Cola persistente de envíos a la NUC (SQLite en ProgramData\ScreenRecorder\uploads.db).
/// El agente y el servicio watchdog la drenan: un fragmento encolado se reintenta para
/// siempre (con backoff) hasta subirse, aunque el equipo se reinicie a mitad.
/// </summary>
public class UploadQueueService
{
    private readonly string _dbPath = string.Empty;
    private readonly object _lock = new object();

    private volatile bool _available;
    private readonly ILoggerService? _logger;
    private int _fallosConsecutivos;
    private const int UmbralDisyuntor = 3;

    /// <summary>True si la base de la cola está operativa. Si es false, los callers deben
    /// usar el drenado directo (sin cola) para que nada se acumule.</summary>
    public bool IsAvailable => _available;

    public UploadQueueService(ILoggerService? logger = null)
    {
        _logger = logger;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ScreenRecorder");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "uploads.db");
            Init();
            _available = true;
        }
        catch (Exception ex)
        {
            // La cola ES reconstruible (los archivos en disco se re-encuentran con el
            // escaneo de huérfanos): si la base está corrupta, borrarla y reintentar.
            // NUNCA en silencio: este fallo antes se tragaba y los videos se acumulaban.
            _logger?.LogError("Cola de envíos (SQLite): la base uploads.db no abrió. Intentando recrearla.", ex);
            try
            {
                foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
                {
                    try
                    {
                        var f = _dbPath + ext;
                        if (!string.IsNullOrEmpty(_dbPath) && File.Exists(f)) File.Delete(f);
                    }
                    catch { /* uno a uno */ }
                }
                Init();
                _available = true;
                _logger?.LogInfo("Cola de envíos (SQLite): base recreada correctamente.");
            }
            catch (Exception ex2)
            {
                _available = false;
                _logger?.LogError("Cola de envíos (SQLite): INDISPONIBLE tras intentar recrear la base. " +
                                  "La subida pasará al modo DIRECTO (sin cola); investigue la causa.", ex2);
            }
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Default Timeout=30;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 30000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Ejecuta una operacion aislada y devuelve si tuvo éxito. Fallos consecutivos
    /// disparan el disyuntor: la cola se marca NO disponible en caliente (con autorreparación)
    /// para que los callers pasen al drenado DIRECTO. Sin esto, una base que abre en el ctor
    /// pero falla después producía acumulación silenciosa (pendientes=-1, subidos=0, sin errores).</summary>
    private bool Guarded(Action action)
    {
        try
        {
            action();
            _fallosConsecutivos = 0;
            return true;
        }
        catch (Exception ex)
        {
            var n = Interlocked.Increment(ref _fallosConsecutivos);
            if (n >= UmbralDisyuntor && _available)
            {
                _logger?.LogError($"Cola de envíos (SQLite): {n} fallos consecutivos. Último: {ex.Message}. " +
                                  "Se pasa al MODO DIRECTO (subida sin cola) y se intenta reparar la base.");
                RepararOInutilizar();
            }
            return false;
        }
    }

    /// <summary>Intenta recrear la base; si no revive, marca la cola no disponible (modo directo).</summary>
    private void RepararOInutilizar()
    {
        try
        {
            foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
            {
                try
                {
                    var f = _dbPath + ext;
                    if (!string.IsNullOrEmpty(_dbPath) && File.Exists(f)) File.Delete(f);
                }
                catch { /* uno a uno */ }
            }
            Init();
            _fallosConsecutivos = 0;
            _available = true;
            _logger?.LogInfo("Cola de envíos (SQLite): base recreada correctamente tras los fallos.");
        }
        catch (Exception ex2)
        {
            _available = false;
            _logger?.LogError("Cola de envíos (SQLite): INDISPONIBLE tras intentar reparar. " +
                              "La subida seguirá en MODO DIRECTO (sin cola) y funcionará igual.", ex2);
        }
    }

    private void Init()
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Default Timeout=30;");
            conn.Open();
            using var cmdPragma = conn.CreateCommand();
            cmdPragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 30000;";
            cmdPragma.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS uploads (" +
                "path TEXT PRIMARY KEY," +
                "estado TEXT NOT NULL," +
                "intentos INTEGER NOT NULL DEFAULT 0," +
                "creado TEXT NOT NULL," +
                "enviado TEXT);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Añade un archivo a la cola (idempotente por ruta).</summary>
    public void Enqueue(string path)
    {
        if (!_available) return;
        lock (_lock)
        Guarded(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO uploads (path, estado, intentos, creado) VALUES ($p, 'pendiente', 0, $c) " +
                "ON CONFLICT(path) DO UPDATE SET estado = 'pendiente';";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        });
    }

    public int CountPending()
    {
        if (!_available) return -1;
        lock (_lock)
        {
            var resultado = -1;
            var ok = Guarded(() =>
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM uploads WHERE estado = 'pendiente';";
                resultado = Convert.ToInt32(cmd.ExecuteScalar());
            });
            // -1 = no se pudo consultar; el disyuntor (Guarded) lleva la cuenta de fallos
            // y cae al modo directo tras varios consecutivos.
            return ok ? resultado : -1;
        }
    }

    public List<string> GetPending(int limit)
    {
        var list = new List<string>();
        if (!_available) return list;
        lock (_lock)
        Guarded(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM uploads WHERE estado = 'pendiente' ORDER BY creado LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
        });
        return list;
    }

    public void MarkSent(string path)
    {
        if (!_available) return;
        lock (_lock)
        Guarded(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM uploads WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        });
    }

    public void MarkFailed(string path)
    {
        if (!_available) return;
        lock (_lock)
        Guarded(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE uploads SET intentos = intentos + 1 WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Elimina entradas cuyo archivo ya no existe (limpieza).</summary>
    public void PruneMissingFiles()
    {
        if (!_available) return;
        List<string> rows = new List<string>();
        lock (_lock)
        Guarded(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM uploads;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(reader.GetString(0));
        });
        foreach (var p in rows)
        {
            if (!File.Exists(p)) MarkSent(p); // el archivo ya no existe: fuera de la cola
        }
    }
}
