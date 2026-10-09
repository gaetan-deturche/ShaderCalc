using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace ShaderCalc.App.Services;

/// <summary>Window and tab state kept between runs (state.json).</summary>
internal sealed class AppState
{
    public List<string> TabOrder { get; set; } = new List<string>();

    public string? ActiveTab { get; set; }

    public double Left { get; set; } = double.NaN;

    public double Top { get; set; } = double.NaN;

    public double Width { get; set; } = 1360;

    public double Height { get; set; } = 860;

    public bool IsMaximized { get; set; }

    public double SidePanelWidth { get; set; } = 420;

    public double ResultColumnWidth { get; set; } = 340;
}

/// <summary>
/// The worksheet files: every *.hlsl in the data folder (~/.shadercalc, or SHADERCALC_DATA_DIR) is a tab. Writes go
/// through a temporary file; edits made outside the app are reported (debounced), ignoring the app's own writes.
/// </summary>
internal sealed class WorksheetStore : IDisposable
{
    public const string Extension = ".hlsl";

    private const string Welcome = """
        // ShaderCalc: every line is HLSL, its value shows on the right.
        // Functions, structs and #defines are shared by every tab. Click a result for its bits,
        // units and the check against DXC + WARP. F1 on a name opens its documentation.

        float KineticEnergy(float mass, float speed)
        {
            return 0.5 * mass * speed * speed;
        }

        KineticEnergy(2 kg, 3 m/s)
        float3 n = normalize(float3(1, 2, 3))
        asuint(n.x)
        f16tof32(f32tof16(0.1))
        uint(5) - uint(7)
        pow(-2.0, 3.0)
        """;

    private readonly Dictionary<string, string> _knownTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _changeDebounce;

    private WorksheetStore(string folder, Dispatcher dispatcher)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        State = LoadState();
        _changeDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => CheckOutsideChanges(), dispatcher) { IsEnabled = false };
        _watcher = new FileSystemWatcher(folder, "*" + Extension + "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler onChange = (_, _) => dispatcher.BeginInvoke(() =>
        {
            _changeDebounce.Stop();
            _changeDebounce.Start();
        });
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => onChange(this, new FileSystemEventArgs(WatcherChangeTypes.Renamed, folder, null));
    }

    public string Folder { get; }

    public AppState State { get; }

    /// <summary>Raised (on the UI thread) with the files whose text changed outside the app, and the files added or removed.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>>? ChangedOutside;

    public static WorksheetStore Open(Dispatcher dispatcher)
    {
        string folder = Environment.GetEnvironmentVariable("SHADERCALC_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shadercalc");
        WorksheetStore store = new WorksheetStore(folder, dispatcher);
        if (store.ListFiles().Count == 0)
        {
            store.Write("scratch" + Extension, Welcome);
        }
        return store;
    }

    /// <summary>Files in tab order: the saved order first, then new files by name.</summary>
    public List<string> OrderedNames()
    {
        List<string> files = ListFiles();
        List<string> ordered = State.TabOrder.Where(name => files.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        ordered.AddRange(files.Where(name => !ordered.Contains(name, StringComparer.OrdinalIgnoreCase)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    private List<string> ListFiles() =>
        Directory.EnumerateFiles(Folder, "*" + Extension).Select(Path.GetFileName).OfType<string>()
            .Where(name => name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)).ToList();

    public string Read(string name)
    {
        string text = File.ReadAllText(Path.Combine(Folder, name));
        _knownTexts[name] = text;
        return text;
    }

    /// <summary>Saves through a temporary file, so a crash never leaves a half-written worksheet.</summary>
    public void Write(string name, string text)
    {
        if (_knownTexts.TryGetValue(name, out string? known) && known == text && File.Exists(Path.Combine(Folder, name)))
        {
            return;
        }
        _knownTexts[name] = text;
        string path = Path.Combine(Folder, name);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>A new empty worksheet named "worksheet N.hlsl".</summary>
    public string Create()
    {
        HashSet<string> existing = ListFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        int number = 1;
        string name;
        do
        {
            name = $"worksheet {number++}{Extension}";
        }
        while (existing.Contains(name));
        Write(name, string.Empty);
        return name;
    }

    /// <summary>Renames a worksheet; returns the new file name, or null with a reason.</summary>
    public string? Rename(string name, string newTitle, out string? problem)
    {
        problem = null;
        string newName = newTitle.Trim();
        if (!newName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            newName += Extension;
        }
        if (newName.Length <= Extension.Length || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problem = "That isn't a valid file name.";
            return null;
        }
        if (!string.Equals(newName, name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Folder, newName)))
        {
            problem = $"{newName} already exists.";
            return null;
        }
        File.Move(Path.Combine(Folder, name), Path.Combine(Folder, newName));
        if (_knownTexts.Remove(name, out string? text))
        {
            _knownTexts[newName] = text;
        }
        return newName;
    }

    /// <summary>Sends a worksheet to the Recycle Bin.</summary>
    public void Delete(string name)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Path.Combine(Folder, name), Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        _knownTexts.Remove(name);
    }

    private void CheckOutsideChanges()
    {
        _changeDebounce.Stop();
        List<string> files = ListFiles();
        List<string> changed = new List<string>();
        foreach (string name in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(Folder, name));
            }
            catch (IOException)
            {
                // Still being written: the next notification will catch it
                continue;
            }
            if (_knownTexts.TryGetValue(name, out string? known) && known != text)
            {
                _knownTexts[name] = text;
                changed.Add(name);
            }
        }
        List<string> added = files.Where(name => !_knownTexts.ContainsKey(name)).ToList();
        List<string> removed = _knownTexts.Keys.Where(name => !files.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (string name in removed)
        {
            _knownTexts.Remove(name);
        }
        if (changed.Count > 0 || added.Count > 0 || removed.Count > 0)
        {
            ChangedOutside?.Invoke(changed, added, removed);
        }
    }

    private AppState LoadState()
    {
        try
        {
            string path = Path.Combine(Folder, "state.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new AppState() : new AppState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new AppState();
        }
    }

    public void SaveState()
    {
        string path = Path.Combine(Folder, "state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _changeDebounce.Stop();
    }
}
