using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Tests the OS credential store integration in <see cref="SettingsViewModel"/>: mode migration
/// (Stored→CredentialStore moves the plaintext key into the store and clears the field), the
/// save/clear commands, and the IsByokCredentialStoreSupported flag.
/// </summary>
public sealed class SettingsViewModelCredentialStoreTests
{
    private static SettingsViewModel CreateVm(FakeSecureKeyStore? store = null, AppData? data = null)
    {
        var dataStore = new DataStore(data ?? new AppData());
        return new SettingsViewModel(
            dataStore,
            new CopilotService(),
            new BrowserService(),
            new UpdateService(),
            secureKeyStore: store ?? new FakeSecureKeyStore());
    }

    [Fact]
    public void NewEndpoint_DefaultsToCredentialStore_WhenSupported()
    {
        var vm = CreateVm(new FakeSecureKeyStore { IsSupported = true });

        vm.AddByokEndpointCommand.Execute(null);

        Assert.NotNull(vm.SelectedByokEndpoint);
        Assert.Equal(ByokApiKeyMode.CredentialStore, vm.SelectedByokEndpoint!.ApiKeyMode);
        Assert.True(vm.UsesCredentialStoreApiKey);
        Assert.True(vm.IsByokCredentialStoreSupported);
    }

    [Fact]
    public void NewEndpoint_DefaultsToEnvVar_WhenStoreUnsupported()
    {
        var vm = CreateVm(new FakeSecureKeyStore { IsSupported = false });

        vm.AddByokEndpointCommand.Execute(null);

        Assert.NotNull(vm.SelectedByokEndpoint);
        Assert.Equal(ByokApiKeyMode.EnvVar, vm.SelectedByokEndpoint!.ApiKeyMode);
        Assert.False(vm.IsByokCredentialStoreSupported);
    }

    [Fact]
    public async Task SwitchingToCredentialStore_MovesPlaintextKeyIntoStore_AndClearsField()
    {
        var store = new FakeSecureKeyStore();
        var vm = CreateVm(store);
        vm.AddByokEndpointCommand.Execute(null);
        var ep = vm.SelectedByokEndpoint!;
        ep.ApiKeyMode = ByokApiKeyMode.Stored;
        ep.ApiKey = "plaintext-secret-123";

        // Now switch to CredentialStore.
        var option = Assert.Single(vm.ByokApiKeyModeOptions, item => item.Value == ByokApiKeyMode.CredentialStore);
        await vm.ChangeByokApiKeyModeCommand.ExecuteAsync(option);

        // OS store received the key, plaintext field cleared.
        var expectedKey = SecureKeyStoreFactory.EndpointKey(ep.Id);
        Assert.True(store.Contains(expectedKey));
        var stored = await store.GetAsync(expectedKey);
        Assert.Equal("plaintext-secret-123", stored);
        Assert.Null(ep.ApiKey);
    }

    [Fact]
    public void ApiKeyModeOptions_OmitCredentialStore_WhenUnsupported()
    {
        var vm = CreateVm(new FakeSecureKeyStore { IsSupported = false });

        Assert.DoesNotContain(vm.ByokApiKeyModeOptions, option => option.Value == ByokApiKeyMode.CredentialStore);
        Assert.Contains(vm.ByokApiKeyModeOptions, option => option.Value == ByokApiKeyMode.EnvVar);
    }

    [Fact]
    public void UnsupportedPersistedCredentialStoreMode_IsCoercedToEnvVar()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new ByokEndpoint
        {
            Id = "unsupported-store",
            ApiKeyMode = ByokApiKeyMode.CredentialStore,
        });

        var vm = CreateVm(new FakeSecureKeyStore { IsSupported = false }, data);

        Assert.Equal(ByokApiKeyMode.EnvVar, data.Settings.ByokEndpoints[0].ApiKeyMode);
        Assert.True(vm.IsByokValidationVisible);
        Assert.False(string.IsNullOrEmpty(vm.ByokValidationMessage));
    }

    [Fact]
    public async Task SwitchingToCredentialStore_WhenWriteFails_PreservesModeAndPlaintext()
    {
        var store = new FakeSecureKeyStore { SetException = new InvalidOperationException("store unavailable") };
        var vm = CreateVm(store);
        vm.AddByokEndpointCommand.Execute(null);
        var endpoint = vm.SelectedByokEndpoint!;
        endpoint.ApiKeyMode = ByokApiKeyMode.Stored;
        endpoint.ApiKey = "plaintext-secret-123";
        var configurationChangeCount = 0;
        vm.ByokConfigurationChanged += () => configurationChangeCount++;

        var option = Assert.Single(vm.ByokApiKeyModeOptions, item => item.Value == ByokApiKeyMode.CredentialStore);
        await vm.ChangeByokApiKeyModeCommand.ExecuteAsync(option);

        Assert.Equal(ByokApiKeyMode.Stored, endpoint.ApiKeyMode);
        Assert.Equal("plaintext-secret-123", endpoint.ApiKey);
        Assert.Equal(0, configurationChangeCount);
        Assert.True(vm.IsByokValidationVisible);
        Assert.False(string.IsNullOrEmpty(vm.ByokValidationMessage));
    }

    [Fact]
    public async Task SaveByokCredentialStoreKey_WritesToStore_AndClearsEntry()
    {
        var store = new FakeSecureKeyStore();
        var vm = CreateVm(store);
        vm.AddByokEndpointCommand.Execute(null);
        var ep = vm.SelectedByokEndpoint!;
        ep.ApiKeyMode = ByokApiKeyMode.CredentialStore;

        vm.ByokCredentialStoreKeyEntry = "new-secret-from-ui";
        await vm.SaveByokCredentialStoreKeyCommand.ExecuteAsync(null);

        var expectedKey = SecureKeyStoreFactory.EndpointKey(ep.Id);
        var stored = await store.GetAsync(expectedKey);
        Assert.Equal("new-secret-from-ui", stored);
        // Entry box reset to blank after a successful save (never echoes the secret).
        Assert.Null(vm.ByokCredentialStoreKeyEntry);
        Assert.True(vm.HasByokCredentialStoreEntry);
    }

    [Fact]
    public async Task ClearByokCredentialStoreKey_RemovesEntry()
    {
        var store = new FakeSecureKeyStore();
        var vm = CreateVm(store);
        vm.AddByokEndpointCommand.Execute(null);
        var ep = vm.SelectedByokEndpoint!;
        ep.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        var expectedKey = SecureKeyStoreFactory.EndpointKey(ep.Id);
        await store.SetAsync(expectedKey, "to-be-cleared");

        await vm.ClearByokCredentialStoreKeyCommand.ExecuteAsync(null);

        Assert.False(store.Contains(expectedKey));
        Assert.False(vm.HasByokCredentialStoreEntry);
    }

    [Fact]
    public async Task DeleteEndpoint_RemovesOsStoreEntry()
    {
        var store = new FakeSecureKeyStore();
        var vm = CreateVm(store);
        vm.AddByokEndpointCommand.Execute(null);
        var ep = vm.SelectedByokEndpoint!;
        var expectedKey = SecureKeyStoreFactory.EndpointKey(ep.Id);
        await store.SetAsync(expectedKey, "orphan-if-not-cleaned");

        await vm.DeleteByokEndpointCommand.ExecuteAsync(null);

        Assert.False(store.Contains(expectedKey));
    }
}
