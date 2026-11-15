using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Platform;
using Avalonia.Media.Imaging;

namespace RvcStudio.App;

internal static class StoreMedia
{
    private static readonly StandardAssetLoader Resources=new(typeof(StoreMedia).Assembly);
    private sealed record BundledPreview(string Id,string PublishedRevision,string Sha256);
    private static readonly Lazy<BundledPreview[]> Bundled=new(()=>{
        using var stream=Embedded("previews","json");
        return JsonSerializer.Deserialize<BundledPreview[]>(stream,StoreCrypto.Json)??[];
    });
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> PreparationLocks=new();
    private static CancellationTokenSource? _previewEnd,_previewLoad;
    private static int _previewVersion;
    public static string? PlayingPreviewId {get;private set;}
    public static string? LoadingPreviewId {get;private set;}
    public static event Action? PreviewChanged;
    public static Stream Embedded(string id,string extension)=>Resources.Open(new Uri($"avares://RVC Studio/Assets/Store/{id}.{extension}"));
    public static bool HasBundledRevision(StoreItem item)=>Bundled.Value.Any(p=>p.Id==item.Id && (string.IsNullOrEmpty(item.Revision)||p.PublishedRevision==item.Revision));
    public static Bitmap? Cover(string id)
    {
        try {using var stream=Embedded(id,"png");return new Bitmap(stream);} catch {return null;}
    }
    public static string CopyEmbedded(string id,string extension)
    {
        if(!StoreCrypto.ValidId(id) || extension is not ("png" or "wav"))throw new InvalidDataException("资源标识无效。");
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RvcStudio","Store","Media");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,id+"."+extension);
        using var source=Embedded(id,extension);using var target=File.Create(path);source.CopyTo(target);return path;
    }
    public static string? AudioExtension(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>=12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8,4).SequenceEqual("WAVE"u8))return ".wav";
        if(bytes.Length>=12 && bytes.Slice(4,4).SequenceEqual("ftyp"u8))return ".m4a";
        if(bytes.Length>=3 && (bytes[..3].SequenceEqual("ID3"u8)||bytes[0]==0xff&&(bytes[1]&0xe0)==0xe0))return ".mp3";
        return null;
    }
    // This method performs no audio-device work, so it can also warm a preview in
    // the background or be checked with an offline HTTP handler in tests.
    public static async Task<string> PreparePreviewAsync(StoreService store,StoreItem item,CancellationToken token=default)
    {
        if(!StoreCrypto.ValidId(item.Id)||!string.IsNullOrEmpty(item.Revision)&&!StoreCrypto.ValidId(item.Revision))throw new InvalidDataException("试听标识无效。");
        var key=store.CacheRoot+"/"+item.Id+"/"+item.Revision;
        var gate=PreparationLocks.GetOrAdd(key,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(token);
        try
        {
            byte[] bytes;
            var bundled=Bundled.Value.FirstOrDefault(p=>p.Id==item.Id && (string.IsNullOrEmpty(item.Revision)||p.PublishedRevision==item.Revision));
            if(bundled is not null)
            {
                using var source=Embedded(item.Id,"wav");using var output=new MemoryStream();await source.CopyToAsync(output,token);bytes=output.ToArray();
                if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(bundled.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("内置试听校验失败。");
            }
            else bytes=await store.AssetAsync(item.PreviewUrl,token);
            var extension=AudioExtension(bytes)??throw new InvalidDataException("试听音频格式无效。");
            var revision=string.IsNullOrEmpty(item.Revision)?"embedded":item.Revision;
            var path=store.MediaPath(item.Id+"-"+revision+"-preview"+extension);
            if(File.Exists(path) && new FileInfo(path).Length==bytes.Length)
            {
                var existing=await File.ReadAllBytesAsync(path,token);
                if(SHA256.HashData(existing).SequenceEqual(SHA256.HashData(bytes)))return path;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".part";
            try {await File.WriteAllBytesAsync(temporary,bytes,token);token.ThrowIfCancellationRequested();File.Move(temporary,path,true);}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
            return path;
        }
        finally {gate.Release();}
    }
    public static async Task PreloadPreviewsAsync(StoreService store,IReadOnlyList<StoreItem> items,CancellationToken token)
    {
        using var slots=new SemaphoreSlim(2,2);
        async Task Warm(StoreItem item)
        {
            bool entered=false;
            try {await slots.WaitAsync(token);entered=true;await PreparePreviewAsync(store,item,token);}
            catch(Exception ex) when(ex is OperationCanceledException or IOException or HttpRequestException or UnauthorizedAccessException) { }
            finally {if(entered)slots.Release();}
        }
        await Task.WhenAll(items.Select(Warm));
    }
    [DllImport("winmm.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool PlaySound(string? sound,IntPtr module,uint flags);
    public static void Play(string path)
    {
        Stop();
        if(!OperatingSystem.IsWindows()||!PlaySound(path,IntPtr.Zero,0x20000|0x1|0x2))throw new IOException("无法播放试听音频。");
    }
    public static async Task PreviewAsync(StoreService store,StoreItem item,CancellationToken token=default)
    {
        Stop();
        var requestVersion=_previewVersion;
        using var loading=CancellationTokenSource.CreateLinkedTokenSource(token);
        _previewLoad=loading;LoadingPreviewId=item.Id;PreviewChanged?.Invoke();
        try
        {
            var path=await PreparePreviewAsync(store,item,loading.Token);
            loading.Token.ThrowIfCancellationRequested();
            if(requestVersion!=_previewVersion)return;
            _previewLoad=null;LoadingPreviewId=null;
            if(Path.GetExtension(path)!=".wav")
            {
                Stop();System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});return;
            }
            Play(path);PlayingPreviewId=item.Id;PreviewChanged?.Invoke();
            var ending=new CancellationTokenSource();_previewEnd=ending;_=FinishPreview(path,ending);
        }
        finally
        {
            if(_previewLoad==loading){_previewLoad=null;LoadingPreviewId=null;PreviewChanged?.Invoke();}
        }
    }
    private static async Task FinishPreview(string path,CancellationTokenSource ending)
    {
        try
        {
            uint byteRate=0;uint size=0;
            using(var stream=File.OpenRead(path))
            using(var reader=new BinaryReader(stream))
            {
                stream.Position=12;
                while(stream.Position+8<=stream.Length)
                {
                    var name=System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));var length=reader.ReadUInt32();var next=stream.Position+length+(length&1);
                    if(next>stream.Length)break;
                    if(name=="fmt "&&length>=16){stream.Position+=8;byteRate=reader.ReadUInt32();}
                    if(name=="data"){size=length;break;}
                    stream.Position=next;
                }
            }
            if(byteRate>0&&size>0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(size/(double)byteRate,0.1,1800)),ending.Token);
                if(_previewEnd==ending)Stop();
            }
        }
        catch(OperationCanceledException) { }
        catch(IOException) { }
        finally {if(_previewEnd==ending)_previewEnd=null;ending.Dispose();}
    }
    public static void Stop()
    {
        var loading=_previewLoad;_previewLoad=null;LoadingPreviewId=null;loading?.Cancel();
        var ending=_previewEnd;_previewEnd=null;ending?.Cancel();
        _previewVersion++;
        if(OperatingSystem.IsWindows())PlaySound(null,IntPtr.Zero,0);
        PlayingPreviewId=null;PreviewChanged?.Invoke();
    }
}
