using System.Collections;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Lumi.Build;
using Microsoft.Build.Framework;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotPackagingTests
{
    [Fact]
    public void VerifiedZipIsExtractedAndReusedWithoutChangingTheExecutable()
    {
        using var fixture = new CliFixture();
        fixture.Zip();
        var task = fixture.CreateTask();

        Assert.True(task.Execute(), fixture.Errors);
        Assert.Equal(fixture.ExecutablePath, task.CliPath);
        Assert.Equal(CliFixture.Executable, File.ReadAllBytes(task.CliPath));
        var modified = File.GetLastWriteTimeUtc(task.CliPath);

        Assert.True(fixture.CreateTask().Execute(), fixture.Errors);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(task.CliPath));
    }

    [Theory]
    [InlineData("copilot-linux-x64.tar.gz")]
    [InlineData("copilot-linux-arm64.tar.gz")]
    [InlineData("copilot-linuxmusl-x64.tar.gz")]
    [InlineData("copilot-linuxmusl-arm64.tar.gz")]
    [InlineData("copilot-darwin-x64.tar.gz")]
    [InlineData("copilot-darwin-arm64.tar.gz")]
    public void UnixArchivesKeepTheExecutableLayoutAndMode(string asset)
    {
        using var fixture = new CliFixture(asset);
        fixture.Tar();

        Assert.True(fixture.CreateTask().Execute(), fixture.Errors);
        Assert.Equal(CliFixture.Executable, File.ReadAllBytes(fixture.ExecutablePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), File.GetUnixFileMode(fixture.ExecutablePath));
    }

    [Theory]
    [InlineData("../copilot.exe", false)]
    [InlineData("folder/copilot.exe", false)]
    [InlineData("copilot.exe", true)]
    public void UnexpectedZipLayoutIsRejectedBeforePublishing(string entryName, bool additionalEntry)
    {
        using var fixture = new CliFixture();
        fixture.Zip(entryName, additionalEntry);

        Assert.False(fixture.CreateTask().Execute());
        Assert.Contains("standalone ZIP", fixture.Errors, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ExecutablePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("../copilot", TarEntryType.RegularFile, 493)]
    [InlineData("copilot", TarEntryType.SymbolicLink, 493)]
    [InlineData("copilot", TarEntryType.RegularFile, 420)]
    public void UnexpectedTarEntryOrModeIsRejected(string name, TarEntryType type, int mode)
    {
        using var fixture = new CliFixture("copilot-linux-x64.tar.gz");
        fixture.Tar(name, type, (UnixFileMode)mode);

        Assert.False(fixture.CreateTask().Execute());
        Assert.Contains("regular copilot entry with mode 0755", fixture.Errors, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ExecutablePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void AdditionalTarEntryIsRejected()
    {
        using var fixture = new CliFixture("copilot-linux-x64.tar.gz");
        fixture.Tar(additionalEntry: true);

        Assert.False(fixture.CreateTask().Execute());
        Assert.Contains("additional entries", fixture.Errors, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ExecutablePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptCacheFailsEvenAfterSuccessfulAcquisition(bool corruptExecutable)
    {
        using var fixture = new CliFixture();
        fixture.Zip();
        Assert.True(fixture.CreateTask().Execute(), fixture.Errors);
        File.AppendAllText(corruptExecutable ? fixture.ExecutablePath : fixture.ArchivePath, "corruption");

        Assert.False(fixture.CreateTask().Execute());
        Assert.Contains("Checksum mismatch", fixture.Errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("different")]
    public void ReleaseManifestMustAgreeWithTheCheckedInPin(string scenario)
    {
        using var fixture = new CliFixture();
        fixture.Zip();
        var line = File.ReadAllText(fixture.ManifestPath);
        File.WriteAllText(fixture.ManifestPath, scenario switch
        {
            "missing" => "",
            "duplicate" => line + line,
            _ => new string('0', 64) + "  " + fixture.AssetName + "\n"
        });

        Assert.False(fixture.CreateTask().Execute());
        Assert.Contains("exactly one matching checked-in archive pin", fixture.Errors, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ExecutablePath));
    }

    [Fact]
    public void WrongExecutablePinDoesNotLeaveAnUsableOrPartialExecutable()
    {
        using var fixture = new CliFixture();
        fixture.Zip();
        var task = fixture.CreateTask();
        task.ExecutableSha256 = new string('0', 64);

        Assert.False(task.Execute());
        Assert.Contains("Checksum mismatch", fixture.Errors, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ExecutablePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void PinsCoverEverySupportedPlatformAndUseUniqueOfficialAssets()
    {
        var pins = ReadPins();
        Assert.Equal(
            new[] { "linux-arm64", "linux-musl-arm64", "linux-musl-x64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64" },
            pins.Select(pin => pin.Attribute("Include")!.Value.Split('/')[1]).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(8, pins.Select(pin => pin.Element("AssetName")!.Value).Distinct().Count());
        Assert.All(pins, pin =>
        {
            Assert.StartsWith("1.0.84-8/", pin.Attribute("Include")!.Value);
            Assert.Matches("^[a-f0-9]{64}$", pin.Element("ArchiveSha256")!.Value);
            Assert.Matches("^[a-f0-9]{64}$", pin.Element("ExecutableSha256")!.Value);
        });
    }

    [Fact]
    public void TestOutputReceivesOnlyThePinnedFullCliNotASecondCopilotRuntime()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx"
            : RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal) ? "linux-musl" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var rid = $"{os}-{arch}";
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
        var executable = Path.Combine(native, os == "win" ? "copilot.exe" : "copilot");
        var pin = Assert.Single(ReadPins(), item => item.Attribute("Include")!.Value == $"1.0.84-8/{rid}");

        Assert.True(File.Exists(executable), executable);
        Assert.Equal(pin.Element("ExecutableSha256")!.Value, CliFixture.Hash(executable));
        foreach (var forbidden in new[] { "copilot-runtime", "copilot-runtime.exe", "runtime.node", "copilot_runtime.dll",
                     "libcopilot_runtime.so", "libcopilot_runtime.dylib", ".copilot-runtime-assets", ".copilot-explicit-cli" })
            Assert.False(File.Exists(Path.Combine(native, forbidden)), $"Unexpected second Copilot runtime: {forbidden}");
        if (!OperatingSystem.IsWindows())
            Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode(executable) & UnixFileMode.UserExecute);
    }

    private static XElement[] ReadPins() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Packaging", "CopilotCliPins.props"))
            .Descendants("LumiCopilotCliPin").ToArray();

    private sealed class CliFixture(string assetName = "copilot-win32-x64.zip") : IDisposable
    {
        internal static readonly byte[] Executable = Encoding.UTF8.GetBytes("fake-only CLI bytes; never executed");
        private readonly RecordingBuildEngine _engine = new();
        private string _archiveHash = "";
        internal string DirectoryPath { get; } = CreateDirectory();
        internal string AssetName { get; } = assetName;
        internal string ArchivePath => Path.Combine(DirectoryPath, AssetName);
        internal string ManifestPath => Path.Combine(DirectoryPath, "SHA256SUMS.txt");
        internal string ExecutablePath => Path.Combine(DirectoryPath, AssetName.EndsWith(".zip", StringComparison.Ordinal) ? "copilot.exe" : "copilot");
        internal string Errors => string.Join(Environment.NewLine, _engine.Errors);

        internal void Zip(string entryName = "copilot.exe", bool additionalEntry = false)
        {
            using (var output = File.Create(ArchivePath))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                using (var entry = zip.CreateEntry(entryName).Open())
                    entry.Write(Executable);
                if (additionalEntry)
                    zip.CreateEntry("runtime.node");
            }
            WriteManifest();
        }

        internal void Tar(string entryName = "copilot", TarEntryType type = TarEntryType.RegularFile,
            UnixFileMode mode = (UnixFileMode)493, bool additionalEntry = false)
        {
            using (var output = File.Create(ArchivePath))
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip))
            {
                var entry = new PaxTarEntry(type, entryName) { Mode = mode };
                if (type == TarEntryType.SymbolicLink)
                    entry.LinkName = "../outside";
                else
                    entry.DataStream = new MemoryStream(Executable);
                writer.WriteEntry(entry);
                entry.DataStream?.Dispose();
                if (additionalEntry)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "runtime.node"));
            }
            WriteManifest();
        }

        internal AcquireFullCopilotCli CreateTask() => new()
        {
            BuildEngine = _engine,
            Version = "1.0.84-8",
            AssetName = AssetName,
            ArchiveSha256 = _archiveHash,
            ExecutableSha256 = Convert.ToHexStringLower(SHA256.HashData(Executable)),
            CacheDirectory = DirectoryPath
        };

        private void WriteManifest()
        {
            _archiveHash = Hash(ArchivePath);
            File.WriteAllText(ManifestPath, _archiveHash + "  " + AssetName + "\n");
        }
        internal static string Hash(string path)
        {
            using var source = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(source));
        }

        private static string CreateDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "LumiCopilotPackagingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        internal List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "CopilotPackagingTests";
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) =>
            throw new NotSupportedException();
    }
}
