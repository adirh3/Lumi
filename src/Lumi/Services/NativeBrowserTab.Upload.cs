#if !WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Lumi.Services;

internal sealed partial class NativeBrowserTab
{
    internal Task<string> UploadFileAsync(string? target, string? value) =>
        RunOperationAsync(() => UploadFileCoreAsync(target, value));

    private async Task<string> UploadFileCoreAsync(string? target, string? value)
    {
        System.Collections.Generic.IReadOnlyList<NativeUploadFile> files;
        try
        {
            files = NativeBrowserUpload.ValidatePaths(NativeBrowserUpload.ParsePaths(value));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "Error: " + ex.Message;
        }
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync(_lifetime.Token);
        var token = Guid.NewGuid().ToString("N");
        try
        {
            var ready = await ExecuteUploadScriptAsync(NativeBrowserUpload.Begin(token, target, files.Count));
            if (!ready.Succeeded)
                return ready.ToDisplayText();
            var buffer = new byte[NativeBrowserUpload.ChunkBytes];
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: NativeBrowserUpload.ChunkBytes, useAsync: true);
                if (stream.Length != file.Length)
                    return "Error: the upload file changed after validation. Retry with a stable file.";
                var initialized = await ExecuteUploadScriptAsync(NativeBrowserUpload.InitializeFile(token, index, file));
                if (!initialized.Succeeded)
                    return initialized.ToDisplayText();
                long readTotal = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, _lifetime.Token)) > 0)
                {
                    readTotal += count;
                    if (readTotal > file.Length)
                        return "Error: the upload file grew while reading. No files were attached.";
                    var received = await ExecuteUploadScriptAsync(NativeBrowserUpload.AppendChunk(
                        token, index, Convert.ToBase64String(buffer, 0, count)));
                    if (!received.Succeeded)
                        return received.ToDisplayText();
                }
                if (readTotal != file.Length || stream.Length != file.Length)
                    return "Error: the upload file changed while reading. No files were attached.";
            }
            return (await ExecuteUploadScriptAsync(NativeBrowserUpload.Commit(token))).ToDisplayText();
        }
        finally
        {
            try
            {
                if (!_disposed && _view is not null)
                    await BrowserService.OnUiThreadAsync(() => ExecuteScriptAsync(NativeBrowserUpload.Cleanup(token)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native upload staging cleanup failed ({ex.GetType().Name}); the document may have closed.");
            }
            _actionLock.Release();
        }
    }

    private Task<BrowserActionResult> ExecuteUploadScriptAsync(string script) =>
        BrowserService.OnUiThreadAsync(async () => BrowserActionResult.FromScript(await ExecuteScriptAsync(script)));
}
#endif
