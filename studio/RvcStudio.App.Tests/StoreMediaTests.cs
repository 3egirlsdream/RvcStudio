using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RvcStudio.App;
using Xunit;

namespace RvcStudio.App.Tests;

public sealed class StoreMediaTests : IDisposable
{
    static StoreMediaTests()
    {
        if(!UriParser.IsKnownScheme("avares"))UriParser.Register(new GenericUriParser(GenericUriParserOptions.GenericAuthority|GenericUriParserOptions.NoUserInfo|GenericUriParserOptions.NoPort),"avares",-1);
    }
    private readonly string _root=Path.Combine(Path.GetTempPath(),"rvc-preview-tests-"+Guid.NewGuid().ToString("N"));
    private const string PreviewUrl="api/RvcStore/Asset?id=new-voice&kind=preview&revision=first";
    private static StoreItem Item(string revision="first")=>new(){Id="new-voice",Revision=revision,PreviewUrl=PreviewUrl.Replace("revision=first","revision="+revision)};
    private static byte[] Wave(byte sample=1)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write("RIFF"u8);writer.Write(38);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);
        writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(2);writer.Write(sample);writer.Write((byte)0);
        return stream.ToArray();
    }
    private static HttpResponseMessage Audio(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    private StoreService Service(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send)
        =>new(new AccountService(sessionRoot:Path.Combine(_root,"account")),new HttpClient(new Handler(send)),new("https://test.invalid",""),_root);

    [Fact]
    public async Task EveryPinnedBundledPreviewWorksWithNoNetworkAndUnknownRevisionFetchesNewAudio()
    {
        int calls=0;using var store=Service((_,_)=>{calls++;return Task.FromResult(Audio(Wave()));});
        using var metadata=StoreMedia.Embedded("previews","json");using var doc=JsonDocument.Parse(metadata);
        StoreItem? replacement=null;
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            var item=new StoreItem {Id=row.GetProperty("id").GetString()!,Revision=row.GetProperty("publishedRevision").GetString()!};
            Assert.True(StoreMedia.HasBundledRevision(item));
            var path=await StoreMedia.PreparePreviewAsync(store,item);
            Assert.Equal(row.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            Assert.Equal(0,calls);
            item.Revision="replacement";item.PreviewUrl=PreviewUrl;Assert.False(StoreMedia.HasBundledRevision(item));replacement=item;
        }
        await StoreMedia.PreparePreviewAsync(store,replacement!);Assert.Equal(1,calls);
    }
    [Fact]
    public async Task SlowCoverTransfersDoNotBlockInteractivePreview()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store=Service(async (request,token)=>
        {
            if(request.RequestUri!.Query.Contains("kind=cover")){started.TrySetResult();await release.Task.WaitAsync(token);}
            return Audio(Wave());
        });
        var cover=store.AssetAsync(PreviewUrl.Replace("kind=preview","kind=cover"));await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var otherCover=store.AssetAsync(PreviewUrl.Replace("kind=preview","kind=cover").Replace("id=new-voice","id=other"));
        try {Assert.Equal(Wave(),await store.AssetAsync(PreviewUrl).WaitAsync(TimeSpan.FromSeconds(3)));}
        finally {release.SetResult();await Task.WhenAll(cover,otherCover);}
    }
    [Fact]
    public async Task ConcurrentPreparationDownloadsOnceAndRestartUsesOfflineCache()
    {
        int calls=0;var bytes=Wave();
        using(var store=Service(async (_,token)=>{Interlocked.Increment(ref calls);await Task.Delay(30,token);return Audio(bytes);}))
        {
            var paths=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>StoreMedia.PreparePreviewAsync(store,Item())));
            Assert.Single(paths.Distinct());Assert.Equal(bytes,await File.ReadAllBytesAsync(paths[0]));Assert.Equal(1,calls);
        }
        using var offline=Service((_,_)=>throw new HttpRequestException("offline"));
        var file=await StoreMedia.PreparePreviewAsync(offline,Item());Assert.Equal(bytes,await File.ReadAllBytesAsync(file));
    }
    [Fact]
    public async Task RevisionChangesFetchNewPreviewAndCorruptCacheIsRepaired()
    {
        int calls=0;using var store=Service((request,_)=>Task.FromResult(Audio(Wave((byte)Interlocked.Increment(ref calls)))));
        var first=await StoreMedia.PreparePreviewAsync(store,Item());
        var second=await StoreMedia.PreparePreviewAsync(store,Item("second"));Assert.NotEqual(first,second);Assert.Equal(2,calls);
        foreach(var entry in Directory.EnumerateFiles(Path.Combine(_root,"Media","Assets")))File.WriteAllBytes(entry,[1,2,3]);
        File.WriteAllBytes(first,[0]);
        var repaired=await StoreMedia.PreparePreviewAsync(store,Item());Assert.Equal(3,calls);Assert.Equal(Wave(3),await File.ReadAllBytesAsync(repaired));
    }
    [Fact]
    public async Task CanceledRequestLeavesNoPartialCacheAndCanRetry()
    {
        int calls=0;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store=Service(async (_,token)=>
        {
            if(Interlocked.Increment(ref calls)==1){started.SetResult();await Task.Delay(Timeout.Infinite,token);}
            return Audio(Wave());
        });
        using var cancel=new CancellationTokenSource();var pending=StoreMedia.PreparePreviewAsync(store,Item(),cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.False(Directory.Exists(_root)&&Directory.EnumerateFiles(_root,"*.part",SearchOption.AllDirectories).Any());
        Assert.True(File.Exists(await StoreMedia.PreparePreviewAsync(store,Item())));Assert.Equal(2,calls);
    }
    [Fact]
    public async Task StoppingPendingPreviewClearsLoadingStateAndPreventsLatePlayback()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store=Service(async (_,token)=>{started.SetResult();await Task.Delay(Timeout.Infinite,token);return Audio(Wave());});
        var pending=StoreMedia.PreviewAsync(store,Item());await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("new-voice",StoreMedia.LoadingPreviewId);StoreMedia.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Null(StoreMedia.LoadingPreviewId);Assert.Null(StoreMedia.PlayingPreviewId);
    }
    [Fact]
    public async Task MutableAssetUrlsAreNotCachedAndInvalidAudioIsNotCommitted()
    {
        int calls=0;using var store=Service((_,_)=>{calls++;return Task.FromResult(Audio(Wave()));});
        await store.AssetAsync("api/RvcStore/Asset?id=new-voice&kind=preview");await store.AssetAsync("api/RvcStore/Asset?id=new-voice&kind=preview");Assert.Equal(2,calls);
        using var invalid=Service((_,_)=>Task.FromResult(Audio("not audio"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(()=>invalid.AssetAsync(PreviewUrl));
        Assert.False(Directory.Exists(_root)&&Directory.EnumerateFiles(_root,"*.asset",SearchOption.AllDirectories).Any());
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);}
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
