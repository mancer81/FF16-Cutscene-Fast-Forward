using Reloaded.Mod.Interfaces;

namespace ff16.qol.cutscenefastforward.Configuration;

public sealed class Configurator : IConfiguratorV3
{
    private const string ConfigFileName = "Config.json";

    private string? _modDirectory;
    private string? _configDirectory;
    private ConfiguratorContext _context;
    private IConfigurable[]? _configurations;

    public Configurator()
    {
    }

    public Configurator(string configDirectory)
    {
        _configDirectory = configDirectory;
    }

    public void SetModDirectory(string modDirectory)
    {
        _modDirectory = modDirectory;
    }

    public void SetConfigDirectory(string configDirectory)
    {
        _configDirectory = configDirectory;
    }

    public void SetContext(in ConfiguratorContext context)
    {
        _context = context;
    }

    public IConfigurable[] GetConfigurations()
    {
        if (_configurations != null)
            return _configurations;

        string targetDir = !string.IsNullOrEmpty(_configDirectory)
            ? _configDirectory
            : _modDirectory ?? AppContext.BaseDirectory;

        string configPath = Path.Combine(targetDir, ConfigFileName);
        _configurations =
        [
            Config.FromFile(configPath, "Default")
        ];
        return _configurations;
    }

    public T GetConfiguration<T>(int index) where T : class, IConfigurable
    {
        return (T)GetConfigurations()[index];
    }

    public bool TryRunCustomConfiguration() => false;

    public void Migrate(string oldDirectory, string newDirectory)
    {
        string oldPath = Path.Combine(oldDirectory, ConfigFileName);
        string newPath = Path.Combine(newDirectory, ConfigFileName);
        if (File.Exists(oldPath) && !File.Exists(newPath))
        {
            Directory.CreateDirectory(newDirectory);
            File.Move(oldPath, newPath);
        }
    }
}
