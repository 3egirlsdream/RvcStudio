using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RvcStudio.App;

// Public cover/preview data only. A revisioned URL is its cache key; model grants
// and member ciphertext continue to use the separate account-bound store cache.
internal static class StoreAssetCache
{
    private const int HeaderSize=44;
    public const int MaximumBytes=32*1024*1024;
    public static async Task<byte[]?> ReadAsync(string path,CancellationToken token)
    {
        try
        {
            var file=new FileInfo(path);
            if(!file.Exists||file.Length<=HeaderSize||file.Length>HeaderSize+MaximumBytes)return null;
            var bytes=await File.ReadAllBytesAsync(path,token);
            if(bytes.Length<=HeaderSize||!bytes.AsSpan(0,8).SequenceEqual("RVCAS001"u8)||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40,4))!=bytes.Length-HeaderSize||
                !SHA256.HashData(bytes.AsSpan(HeaderSize)).AsSpan().SequenceEqual(bytes.AsSpan(8,32)))return null;
            return bytes[HeaderSize..];
        }
        catch(IOException){return null;}
        catch(UnauthorizedAccessException){return null;}
    }
    public static async Task WriteAsync(string path,byte[] bytes,CancellationToken token)
    {
        if(bytes.Length==0||bytes.Length>MaximumBytes)throw new InvalidDataException("试听或封面大小无效。");
        var data=new byte[HeaderSize+bytes.Length];"RVCAS001"u8.CopyTo(data);
        SHA256.HashData(bytes).CopyTo(data,8);BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40,4),bytes.Length);bytes.CopyTo(data,HeaderSize);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".part";
        try {await File.WriteAllBytesAsync(temporary,data,token);token.ThrowIfCancellationRequested();File.Move(temporary,path,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
