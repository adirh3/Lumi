using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private string _userName;
    [ObservableProperty] private string _preferredModel;

    public SettingsViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        _userName = dataStore.Data.Settings.UserName ?? "";
        _preferredModel = dataStore.Data.Settings.PreferredModel;
    }

    [RelayCommand]
    private void Save()
    {
        _dataStore.Data.Settings.UserName = UserName.Trim();
        _dataStore.Data.Settings.PreferredModel = PreferredModel;
        _dataStore.Save();
    }
}
