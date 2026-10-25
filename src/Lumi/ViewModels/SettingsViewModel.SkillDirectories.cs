using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Capabilities;

namespace Lumi.ViewModels;

public partial class SettingsViewModel
{
    public ObservableCollection<string> PersonalSkillDirectories { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPersonalSkillDirectoryCommand))]
    private string _newPersonalSkillDirectory = "";

    [ObservableProperty] private string? _personalSkillDirectoryError;
    private bool _personalSkillDirectoriesDisposed;

    public event Action? PersonalSkillDirectoriesChanged;

    public bool IsPersonalSkillDirectoriesModified
        => !PersonalSkillDirectories.SequenceEqual(
            _defaults.PersonalSkillDirectories, CapabilityQuery.DirectoryComparer);

    private void InitializePersonalSkillDirectories(UserSettings settings)
    {
        foreach (var folder in settings.PersonalSkillDirectories)
            PersonalSkillDirectories.Add(folder);
    }

    private void OnPersonalSkillDirectoriesChanged(object? source)
    {
        if (_personalSkillDirectoriesDisposed || ReferenceEquals(source, this))
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPersonalSkillDirectoriesChanged(source));
            return;
        }

        var folders = _dataStore.Data.Settings.PersonalSkillDirectories;
        if (PersonalSkillDirectories.SequenceEqual(folders, CapabilityQuery.DirectoryComparer))
            return;

        PersonalSkillDirectories.Clear();
        foreach (var folder in folders)
            PersonalSkillDirectories.Add(folder);
        OnPropertyChanged(nameof(IsPersonalSkillDirectoriesModified));
    }

    public void AddPersonalSkillDirectories(IEnumerable<string> folders)
    {
        var normalized = new List<string>();
        foreach (var folder in folders)
        {
            if (!PersonalSkillFolders.TryNormalize(folder, out var path))
            {
                PersonalSkillDirectoryError = Loc.Setting_SkillFolders_InvalidPath;
                return;
            }
            normalized.Add(path);
        }

        if (normalized.Count == 0)
            return;

        var additions = normalized
            .Distinct(CapabilityQuery.DirectoryComparer)
            .Where(path => !PersonalSkillDirectories.Any(folder =>
                PersonalSkillFolders.TryNormalize(folder, out var existing)
                && CapabilityQuery.DirectoryComparer.Equals(existing, path)))
            .ToArray();

        if (additions.Length == 0)
        {
            PersonalSkillDirectoryError = Loc.Setting_SkillFolders_AlreadyAdded;
            return;
        }

        foreach (var folder in additions)
            PersonalSkillDirectories.Add(folder);
        NewPersonalSkillDirectory = "";
        PersonalSkillDirectoryError = null;
        SavePersonalSkillDirectories();
    }

    [RelayCommand(CanExecute = nameof(CanAddPersonalSkillDirectory))]
    private void AddPersonalSkillDirectory()
        => AddPersonalSkillDirectories([NewPersonalSkillDirectory]);

    private bool CanAddPersonalSkillDirectory()
        => !string.IsNullOrWhiteSpace(NewPersonalSkillDirectory);

    [RelayCommand]
    private void RemovePersonalSkillDirectory(string folder)
    {
        if (!PersonalSkillDirectories.Remove(folder))
            return;

        PersonalSkillDirectoryError = null;
        SavePersonalSkillDirectories();
    }

    [RelayCommand]
    private void RevertPersonalSkillDirectories()
        => SetPersonalSkillDirectories(_defaults.PersonalSkillDirectories);

    [RelayCommand]
    private void RefreshPersonalSkills()
        => PersonalSkillDirectoriesChanged?.Invoke();

    private void SetPersonalSkillDirectories(IReadOnlyList<string> folders)
    {
        NewPersonalSkillDirectory = "";
        PersonalSkillDirectoryError = null;
        if (PersonalSkillDirectories.SequenceEqual(folders, CapabilityQuery.DirectoryComparer))
            return;

        PersonalSkillDirectories.Clear();
        foreach (var folder in folders)
            PersonalSkillDirectories.Add(folder);
        SavePersonalSkillDirectories();
    }

    private void SavePersonalSkillDirectories()
    {
        _dataStore.Data.Settings.PersonalSkillDirectories = [.. PersonalSkillDirectories];
        Save();
        OnPropertyChanged(nameof(IsPersonalSkillDirectoriesModified));
        _dataStore.NotifyPersonalSkillDirectoriesChanged(this);
        PersonalSkillDirectoriesChanged?.Invoke();
    }
}
