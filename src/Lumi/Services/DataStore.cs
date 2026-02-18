using System;
using System.IO;
using System.Text.Json;
using Lumi.Models;

namespace Lumi.Services;

public class DataStore
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");
    private static readonly string DataFile = Path.Combine(AppDir, "data.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private AppData _data;

    public DataStore()
    {
        Directory.CreateDirectory(AppDir);
        _data = Load();
    }

    public AppData Data => _data;

    public void Save()
    {
        var json = JsonSerializer.Serialize(_data, JsonOptions);
        File.WriteAllText(DataFile, json);
    }

    private static AppData Load()
    {
        if (!File.Exists(DataFile))
            return new AppData();

        var json = File.ReadAllText(DataFile);
        return JsonSerializer.Deserialize<AppData>(json, JsonOptions) ?? new AppData();
    }
}
