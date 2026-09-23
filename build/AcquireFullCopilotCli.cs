#nullable disable
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Build.Framework;

namespace Lumi.Build
{
    // RoslynCodeTaskFactory compiles against netstandard. The host's public TarReader
    // and Unix mode APIs are reflected so the build needs no extra package or tool.
    public sealed class AcquireFullCopilotCli : Microsoft.Build.Utilities.Task
    {
        [Required] public string Version { get; set; } = "";
        [Required] public string AssetName { get; set; } = "";
        [Required] public string ArchiveSha256 { get; set; } = "";
        [Required] public string ExecutableSha256 { get; set; } = "";
        [Required] public string CacheDirectory { get; set; } = "";
        public int DownloadTimeoutSeconds { get; set; } = 600;
        [Output] public string CliPath { get; private set; } = "";

        public override bool Execute()
        {
            try
            {
                if (!Regex.IsMatch(Version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$")
                    || !Regex.IsMatch(AssetName, @"^copilot-[a-z0-9-]+\.(?:zip|tar\.gz)$")
                    || !Regex.IsMatch(ArchiveSha256, @"^[a-fA-F0-9]{64}$")
                    || !Regex.IsMatch(ExecutableSha256, @"^[a-fA-F0-9]{64}$")
                    || DownloadTimeoutSeconds <= 0)
                    throw new InvalidDataException("Invalid full Copilot CLI version, asset or checksum pin.");

                var cache = Path.GetFullPath(CacheDirectory);
                Directory.CreateDirectory(cache);
                using (AcquireCacheLock(Path.Combine(cache, ".acquire.lock")))
                {
                    var release = "https://github.com/github/copilot-cli/releases/download/v" + Version + "/";
                    var manifest = Path.Combine(cache, "SHA256SUMS.txt");
                    if (!File.Exists(manifest))
                        Download(release + "SHA256SUMS.txt", manifest, null);
                    VerifyReleaseManifest(manifest);

                    var archive = Path.Combine(cache, AssetName);
                    if (!File.Exists(archive))
                        Download(release + AssetName, archive, ArchiveSha256);
                    VerifyHash(archive, ArchiveSha256);

                    var executableName = AssetName.EndsWith(".zip", StringComparison.Ordinal)
                        ? "copilot.exe" : "copilot";
                    var executable = Path.Combine(cache, executableName);
                    if (!File.Exists(executable))
                    {
                        var temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            ExtractExecutable(archive, temporary, executableName);
                            VerifyHash(temporary, ExecutableSha256);
                            SetExecutableMode(temporary, executableName);
                            File.Move(temporary, executable);
                        }
                        finally
                        {
                            if (File.Exists(temporary))
                                File.Delete(temporary);
                        }
                    }

                    // A cached archive or executable is never trusted just because it exists.
                    VerifyHash(executable, ExecutableSha256);
                    SetExecutableMode(executable, executableName);
                    CliPath = executable;
                    Log.LogMessage(MessageImportance.Low, "Verified official Copilot CLI {0}: {1}", Version, executable);
                }
                return true;
            }
            catch (Exception error)
            {
                var cause = error is TargetInvocationException && error.InnerException != null
                    ? error.InnerException : error;
                Log.LogError("Full Copilot CLI {0} ({1}): {2} Cache: {3}",
                    Version, AssetName, cause.Message, CacheDirectory);
                return false;
            }
        }

        private FileStream AcquireCacheLock(string path)
        {
            var deadline = DateTime.UtcNow.AddSeconds(DownloadTimeoutSeconds + 30d);
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    throw new IOException("Timed out waiting for the full Copilot CLI cache lock.");
                }
            }
        }

        private void Download(string url, string destination, string expectedHash)
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Log.LogMessage(MessageImportance.High, "Downloading official Copilot CLI asset: {0}", url);
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(DownloadTimeoutSeconds)))
                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                        input.CopyToAsync(output, 81920, deadline.Token).GetAwaiter().GetResult();
                }
                if (expectedHash != null)
                    VerifyHash(temporary, expectedHash);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private void VerifyReleaseManifest(string path)
        {
            var pattern = @"^([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(AssetName) + @"[ \t]*$";
            var matches = File.ReadAllLines(path)
                .Select(line => Regex.Match(line, pattern))
                .Where(match => match.Success)
                .ToArray();
            if (matches.Length != 1 || !string.Equals(
                    matches[0].Groups[1].Value, ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Official SHA256SUMS.txt does not contain exactly one matching checked-in archive pin. " +
                    "Review build/CopilotCliPins.props; do not use an unverified CLI.");
        }

        private static void VerifyHash(string path, string expected)
        {
            string actual;
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                actual = string.Concat(sha.ComputeHash(input).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Checksum mismatch for '" + path + "'. Expected " + expected + ", got " + actual +
                    ". Remove the corrupt full CLI cache and rebuild.");
        }

        private static void ExtractExecutable(string archive, string destination, string expectedName)
        {
            if (archive.EndsWith(".zip", StringComparison.Ordinal))
            {
                using (var input = File.OpenRead(archive))
                using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count != 1 || zip.Entries[0].FullName != expectedName)
                        throw new InvalidDataException("Expected a standalone ZIP containing only " + expectedName + ".");
                    using (var source = zip.Entries[0].Open())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                        source.CopyTo(output);
                }
                return;
            }

            var readerType = Assembly.Load("System.Formats.Tar").GetType("System.Formats.Tar.TarReader", true);
            var next = readerType.GetMethod("GetNextEntry", new[] { typeof(bool) });
            using (var input = File.OpenRead(archive))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = (IDisposable)Activator.CreateInstance(readerType, gzip, false))
            {
                var entry = next.Invoke(reader, new object[] { false });
                if (entry == null)
                    throw new InvalidDataException("The standalone CLI tarball is empty.");
                var type = entry.GetType();
                var name = (string)type.GetProperty("Name").GetValue(entry);
                var kind = type.GetProperty("EntryType").GetValue(entry).ToString();
                var mode = Convert.ToInt32(type.GetProperty("Mode").GetValue(entry), CultureInfo.InvariantCulture);
                if (name != expectedName || (kind != "RegularFile" && kind != "V7RegularFile") || mode != 493)
                    throw new InvalidDataException("Expected only a regular " + expectedName + " entry with mode 0755.");
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    ((Stream)type.GetProperty("DataStream").GetValue(entry)).CopyTo(output);
                if (next.Invoke(reader, new object[] { false }) != null)
                    throw new InvalidDataException("The standalone CLI tarball contains unexpected additional entries.");
            }
        }

        private static void SetExecutableMode(string path, string executableName)
        {
            if (executableName != "copilot" || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            var method = typeof(File).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(candidate => candidate.Name == "SetUnixFileMode"
                    && candidate.GetParameters()[0].ParameterType == typeof(string));
            method.Invoke(null, new[] { (object)path, Enum.ToObject(method.GetParameters()[1].ParameterType, 493) });
        }
    }
}
