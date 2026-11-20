using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reloaded.Mod.Interfaces;

namespace ff16.qol.cutscenefastforward.Configuration;

public class Configurable<TParent> : IUpdatableConfigurable, IDisposable
    where TParent : Configurable<TParent>, new()
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private FileSystemWatcher? _watcher;
    private readonly object _ioLock = new();
    private bool _disposed;

    [JsonIgnore]
    [Browsable(false)]
    public string? FilePath { get; private set; }

    [JsonIgnore]
    [Browsable(false)]
    public string ConfigName { get; set; } = "Default";

    [JsonIgnore]
    [Browsable(false)]
    public Action Save { get; set; } = null!;

    public event Action<IUpdatableConfigurable>? ConfigurationUpdated;

    public static TParent FromFile(string filePath, string configName)
    {
        TParent result = ReadFromFile(filePath);
        result.Initialize(filePath, configName);
        return result;
    }

    private void Initialize(string filePath, string configName)
    {
        FilePath = filePath;
        ConfigName = configName;
        Save = OnSave;

        string? directory = Path.GetDirectoryName(filePath);
        string fileName = Path.GetFileName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!File.Exists(filePath))
                OnSave();

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
    }

    private void OnSave()
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        lock (_ioLock)
        {
            if (_watcher != null)
                _watcher.EnableRaisingEvents = false;

            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize((TParent)this, SerializerOptions);
                File.WriteAllText(FilePath, json);
            }
            finally
            {
                if (_watcher != null && !_disposed)
                    _watcher.EnableRaisingEvents = true;
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        lock (_ioLock)
        {
            try
            {
                TParent updated = ReadFromFile(FilePath);
                updated.FilePath = FilePath;
                updated.ConfigName = ConfigName;
                updated.Save = Save;
                ConfigurationUpdated?.Invoke(updated);
            }
            catch
            {
                // Ignore transient partial-write errors; Reloaded-II may emit multiple change events.
            }
        }
    }

    private static TParent ReadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return new TParent();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<TParent>(json, SerializerOptions) ?? new TParent();
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(25);
            }
            catch (JsonException)
            {
                return new TParent();
            }
        }

        return new TParent();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        GC.SuppressFinalize(this);
    }
}
