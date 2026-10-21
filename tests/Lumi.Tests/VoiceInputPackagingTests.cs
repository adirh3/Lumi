using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class VoiceInputPackagingTests
{
    [Fact]
    public void DesktopNativeAssetsAreCopiedToTheApplicationOutput()
    {
        using var voice = new VoiceInputService();
        if (!voice.IsAvailable)
            return;
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var rid = $"{platform}-{architecture}";
        var whisperRid = $"{(OperatingSystem.IsMacOS() ? "macos" : platform)}-{architecture}";
        var whisper = OperatingSystem.IsWindows() ? "whisper.dll"
            : OperatingSystem.IsMacOS() ? "libwhisper.dylib" : "libwhisper.so";
        var microphone = OperatingSystem.IsWindows() ? "SDL2.dll"
            : OperatingSystem.IsMacOS() ? "libSDL2-2.0.dylib" : "libSDL2-2.0.so";
        var microphoneRid = OperatingSystem.IsMacOS() ? "osx" : rid;
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", whisperRid, whisper)),
            $"Whisper's {whisperRid} CPU library is missing.");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, microphone))
                    || File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", microphoneRid, "native", microphone)),
            $"SDL's {rid} capture library is missing.");
    }

    [Fact]
    public void OnlyThisPlatformsGpuRuntimeIsCopied()
    {
        // Whisper.net's Vulkan package carries both its Windows and its Linux GPU library; each build keeps its own.
        var expected = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64 ? "linux-x64"
            : null;
        var vulkan = Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan");
        string?[] copied = Directory.Exists(vulkan) ? Directory.GetDirectories(vulkan).Select(Path.GetFileName).ToArray() : [];
        string?[] wanted = expected is null ? [] : [expected];
        Assert.Equal(wanted, copied);
        if (expected is not null)
            Assert.True(File.Exists(Path.Combine(vulkan, expected,
                OperatingSystem.IsWindows() ? "ggml-vulkan-whisper.dll" : "libggml-vulkan-whisper.so")));
    }

    [Fact]
    public void BothMacPackagesDeclareMicrophoneUsageAndPreserveSigningEntitlements()
    {
        var root = FindRepositoryRoot();
        var info = ReadPlist(Path.Combine(root, "build", "macOS", "Info.plist"));
        Assert.Equal("com.lumi.app", info["CFBundleIdentifier"].Value);
        Assert.Equal("Lumi", info["CFBundleExecutable"].Value);
        Assert.Equal("@VERSION@", info["CFBundleShortVersionString"].Value);
        Assert.Equal("@VERSION@", info["CFBundleVersion"].Value);
        Assert.Contains("microphone", info["NSMicrophoneUsageDescription"].Value);

        var entitlements = ReadPlist(Path.Combine(root, "build", "macOS", "Lumi.entitlements"));
        Assert.Equal("true", entitlements["com.apple.security.device.audio-input"].Name.LocalName);
        Assert.Equal("true", entitlements["com.apple.security.cs.allow-jit"].Name.LocalName);
        Assert.Equal("true", entitlements["com.apple.security.cs.disable-library-validation"].Name.LocalName);
        var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("--plist \"$RUNNER_TEMP/lumi-Info.plist\"", release);
        Assert.Contains("--signEntitlements build/macOS/Lumi.entitlements", release);
        Assert.Contains("sed \"s/@VERSION@/$RELEASE_VERSION/g\"", release);
    }

    private static Dictionary<string, XElement> ReadPlist(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return XDocument.Load(reader).Root!.Element("dict")!.Elements()
            .Chunk(2).ToDictionary(pair => pair[0].Value, pair => pair[1]);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The Lumi repository root was not found.");
    }
}
