using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Lumi.Services;

internal sealed record NativeUploadFile(string Path, string Name, long Length, string MimeType);

internal static class NativeBrowserUpload
{
    internal const int MaxFiles = 20;
    internal const long MaxFileBytes = 100L * 1024 * 1024;
    internal const long MaxTotalBytes = 250L * 1024 * 1024;
    internal const int ChunkBytes = 192 * 1024;

    internal static IReadOnlyList<string> ParsePaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Upload needs absolute file paths in value.");
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('['))
            return trimmed.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        try
        {
            using var data = JsonDocument.Parse(trimmed);
            if (data.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Upload paths must be a JSON array of strings.");
            var paths = new List<string>();
            foreach (var item in data.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new ArgumentException("Each upload path must be a nonempty string.");
                paths.Add(item.GetString()!);
            }
            return paths;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ArgumentException("Invalid JSON for upload paths.", ex);
        }
    }

    internal static IReadOnlyList<NativeUploadFile> ValidatePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > MaxFiles)
            throw new ArgumentException($"Upload requires 1-{MaxFiles} files.");
        var files = new List<NativeUploadFile>();
        foreach (var path in paths)
        {
            if (!System.IO.Path.IsPathFullyQualified(path))
                throw new ArgumentException("Upload paths must be fully qualified.");
            var fullPath = System.IO.Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (!file.Exists)
                throw new FileNotFoundException("The upload file does not exist.", fullPath);
            files.Add(new(fullPath, file.Name, file.Length, GetMimeType(file.Extension)));
        }
        ValidateSizes(files.Select(file => file.Length));
        return files;
    }

    internal static void ValidateSizes(IEnumerable<long> lengths)
    {
        long total = 0;
        foreach (var length in lengths)
        {
            if (length < 0 || length > MaxFileBytes)
                throw new ArgumentException("Each upload file must be at most 100 MB.");
            total += length;
            if (total > MaxTotalBytes)
                throw new ArgumentException("The total upload size must be at most 250 MB.");
        }
    }

    internal static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };

    private static string Quote(string value) => "\"" + JavaScriptEncoder.Default.Encode(value) + "\"";
    private static string Slot(string token) => "Symbol.for(" + Quote("lumi.native.upload." + token) + ")";

    internal static string Begin(string token, string? target, int fileCount) =>
        "(() => {const target=" + Quote(target ?? "") + ";const count=" + fileCount.ToString(CultureInfo.InvariantCulture) +
        ";" + ResolveInput + "if(!input)return {ok:false,message:error};" +
        "if(input.disabled||input.matches(':disabled')||input.getAttribute('aria-disabled')==='true')" +
        "return {ok:false,message:'The file input is disabled.'};" +
        "if(count>1&&!input.multiple)return {ok:false,message:'The file input does not accept multiple files.'};" +
        "window[" + Slot(token) + "]={input,entries:[],count};return {ok:true,message:'File input ready.'};})()";

    internal static string InitializeFile(string token, int index, NativeUploadFile file) =>
        "(() => {const s=window[" + Slot(token) + "];if(!s)return {ok:false,message:'The upload document changed.'};" +
        "s.entries[" + index.ToString(CultureInfo.InvariantCulture) + "]={name:" + Quote(file.Name) +
        ",type:" + Quote(file.MimeType) + ",buffer:new Uint8Array(" + file.Length.ToString(CultureInfo.InvariantCulture) +
        "),offset:0};return {ok:true,message:'Upload buffer ready.'};})()";

    internal static string AppendChunk(string token, int index, string base64) =>
        "(() => {const s=window[" + Slot(token) + "],e=s?.entries[" + index.ToString(CultureInfo.InvariantCulture) +
        "];if(!e)return {ok:false,message:'The upload document changed.'};" +
        "const bytes=atob(" + Quote(base64) + ");if(e.offset+bytes.length>e.buffer.length)" +
        "return {ok:false,message:'The upload file changed while reading.'};" +
        "for(let i=0;i<bytes.length;i++)e.buffer[e.offset+i]=bytes.charCodeAt(i);" +
        "e.offset+=bytes.length;return {ok:true,message:'Upload chunk received.'};})()";

    internal static string Commit(string token) =>
        "(() => {const s=window[" + Slot(token) + "];" +
        "if(!s||!s.input.isConnected||s.input.ownerDocument!==document)" +
        "return {ok:false,message:'The file input was removed or the document changed.'};" +
        "if(s.input.disabled||s.input.matches(':disabled'))return {ok:false,message:'The file input is disabled.'};" +
        "if(s.entries.length!==s.count||s.entries.some(e=>!e||e.offset!==e.buffer.length))" +
        "return {ok:false,message:'The upload payload is incomplete.'};" +
        "const dt=new DataTransfer();for(const e of s.entries)dt.items.add(new File([e.buffer],e.name,{type:e.type}));" +
        "s.input.files=dt.files;s.input.dispatchEvent(new Event('input',{bubbles:true}));" +
        "s.input.dispatchEvent(new Event('change',{bubbles:true}));" +
        "const attached=Array.from(s.input.files||[]);" +
        "if(attached.length!==s.count||attached.some((f,i)=>f.name!==s.entries[i].name||f.size!==s.entries[i].buffer.length))" +
        "return {ok:false,message:'The page did not retain the attached files; the action was not retried.'};" +
        "return {ok:true,message:'Uploaded: '+attached.map(f=>f.name).join(', ')};})()";

    internal static string Cleanup(string token) =>
        "(() => {delete window[" + Slot(token) + "];return true;})()";

    private const string ResolveInput = """
        const files=Array.from(document.querySelectorAll('input[type="file"]'))
            .filter(el=>!el.closest('[inert]'));
        let input=null,error='No matching file input found. Pass its CSS selector or upload label.';
        const unique=items=>Array.from(new Set(items.filter(Boolean)));
        const associated=el=>{
            if(el.matches('input[type="file"]'))return [el];
            if(el.control?.matches('input[type="file"]'))return [el.control];
            const byId=el.getAttribute('for');
            if(byId){const field=document.getElementById(byId);if(field?.type==='file')return [field];}
            const nested=Array.from(el.querySelectorAll('input[type="file"]'));
            if(nested.length)return nested;
            return Array.from(el.parentElement?.querySelectorAll('input[type="file"]')||[]);
        };
        let candidates=[];
        if(!target)candidates=files;
        else if(/^#?\d+$/.test(target.trim())){
            const registry=document[Symbol.for('lumi.browser.elements.v1')];
            const node=registry?.nodes.get(target.trim().replace(/^#/,''))?.deref();
            if(!node||!node.isConnected||node.ownerDocument!==document)
                error='Stale or unknown element reference; observe the current tab again.';
            else candidates=associated(node);
        }else{
            try{candidates=unique(Array.from(document.querySelectorAll(target)).flatMap(associated));}catch{}
            if(!candidates.length){
                const lower=target.trim().toLowerCase();
                const text=el=>[el.id,el.name,el.title,el.getAttribute('aria-label'),
                    ...Array.from(el.labels||[]).map(label=>label.textContent)].filter(Boolean)
                    .map(value=>String(value).replace(/\s+/g,' ').trim().toLowerCase());
                candidates=files.filter(el=>text(el).includes(lower));
                if(!candidates.length)candidates=files.filter(el=>text(el).some(value=>value.includes(lower)));
                if(!candidates.length){
                    const labels=Array.from(document.querySelectorAll('label,button,[role="button"]'))
                        .filter(el=>[el.textContent,el.getAttribute('aria-label'),el.title].some(value=>
                            String(value||'').replace(/\s+/g,' ').trim().toLowerCase()===lower));
                    candidates=unique(labels.flatMap(associated));
                    if(!candidates.length&&labels.length===1&&files.length===1)candidates=files;
                }
            }
        }
        candidates=unique(candidates).filter(el=>el.isConnected&&el.ownerDocument===document&&!el.closest('[inert]'));
        if(candidates.length===1)input=candidates[0];
        else if(candidates.length>1)error='Ambiguous file input. Pass a specific CSS selector. Candidates: '+
            candidates.map(el=>el.id||el.name||'(unnamed)').join(', ');
        """;
}
