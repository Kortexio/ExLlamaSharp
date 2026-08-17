using System.Text.Json;

namespace ExLlamaSharp.Server.Services.Ui;

/// <summary>Persisted UI preferences (setup wizard, simple/advanced).</summary>
public sealed class OnboardingState
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly string _path;
    private readonly object _gate = new();

    public OnboardingState()
    {
        var root = Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExLlamaSharp");
        }

        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "ui-state.json");
        Load();
    }

    public bool TourCompleted { get; set; }
    public bool AdvancedMode { get; set; }
    public int SetupStep { get; set; }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(new PersistDto
            {
                TourCompleted = TourCompleted,
                AdvancedMode = AdvancedMode,
                SetupStep = SetupStep,
            }, JsonOpts);
            File.WriteAllText(_path, json);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<PersistDto>(File.ReadAllText(_path));
            if (dto is null)
            {
                return;
            }

            TourCompleted = dto.TourCompleted;
            AdvancedMode = dto.AdvancedMode;
            SetupStep = dto.SetupStep;
        }
        catch
        {
            // first run / corrupt file
        }
    }

    private sealed class PersistDto
    {
        public bool TourCompleted { get; set; }
        public bool AdvancedMode { get; set; }
        public int SetupStep { get; set; }
    }
}
