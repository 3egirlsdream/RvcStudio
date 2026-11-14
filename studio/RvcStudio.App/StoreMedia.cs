using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Media.Imaging;

namespace RvcStudio.App;

internal static class StoreMedia
{
    private static CancellationTokenSource? _previewEnd;
    private static int _previewVersion;
    public static string? PlayingPreviewId {get;private set;}
    public static event Action? PreviewChanged;
    public static Stream Embedded(string id,string extension)=>AssetLoader.Open(new Uri($"avares://RVC Studio/Assets/Store/{id}.{extension}"));
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
        string path;
        if(!string.IsNullOrEmpty(item.Revision))
        {
            var bytes=await store.AssetAsync(item.PreviewUrl,token);
            var wave=bytes.Length>=12 && System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF";
            var extension=wave?".wav":bytes.Length>=8&&System.Text.Encoding.ASCII.GetString(bytes,4,4)=="ftyp"?".m4a":".mp3";
            var folder=Path.Combine(store.CacheRoot,"Media");Directory.CreateDirectory(folder);
            if(!StoreCrypto.ValidId(item.Id)||!StoreCrypto.ValidId(item.Revision))throw new InvalidDataException("试听标识无效。");
            path=Path.Combine(folder,item.Id+"-"+item.Revision+"-preview"+extension);
            await File.WriteAllBytesAsync(path,bytes,token);
            token.ThrowIfCancellationRequested();
            if(requestVersion!=_previewVersion)return;
            if(!wave) {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});return;}
        }
        else path=CopyEmbedded(item.Id,"wav");
        token.ThrowIfCancellationRequested();
        if(requestVersion!=_previewVersion)return;
        Play(path);PlayingPreviewId=item.Id;PreviewChanged?.Invoke();
        var ending=new CancellationTokenSource();_previewEnd=ending;
        _=FinishPreview(path,ending);
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
        var ending=_previewEnd;_previewEnd=null;ending?.Cancel();
        _previewVersion++;
        if(OperatingSystem.IsWindows())PlaySound(null,IntPtr.Zero,0);
        PlayingPreviewId=null;PreviewChanged?.Invoke();
    }
}
