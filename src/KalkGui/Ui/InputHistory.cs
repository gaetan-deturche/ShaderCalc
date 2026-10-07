using System.IO;
using System.Text.Json;

namespace KalkGui.Ui;

/// <summary>Console-style Up/Down input history, persisted between runs.</summary>
internal sealed class InputHistory
{
    private const int MaxEntries = 1000;

    private readonly string _filePath;
    private readonly List<string> _entries;
    private int _browseIndex;
    private string _pendingInput = string.Empty;

    public InputHistory(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
        _browseIndex = _entries.Count;
    }

    public void Add(string input)
    {
        if (_entries.Count == 0 || _entries[^1] != input)
        {
            _entries.Add(input);
        }
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        _browseIndex = _entries.Count;
        Save();
    }

    public bool TryGetPrevious(string currentInput, out string input)
    {
        input = string.Empty;
        if (_browseIndex == 0)
        {
            return false;
        }
        if (_browseIndex == _entries.Count)
        {
            _pendingInput = currentInput;
        }
        _browseIndex--;
        input = _entries[_browseIndex];
        return true;
    }

    public bool TryGetNext(out string input)
    {
        input = string.Empty;
        if (_browseIndex >= _entries.Count)
        {
            return false;
        }
        _browseIndex++;
        input = _browseIndex == _entries.Count ? _pendingInput : _entries[_browseIndex];
        return true;
    }

    private static List<string> Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath)) ?? new List<string>();
        }
        catch (JsonException)
        {
            // A corrupt history file only costs the history
            return new List<string>();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries));
    }
}
