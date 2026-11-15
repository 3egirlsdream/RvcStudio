using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RvcStudio.App;

public sealed class StoreService : IDisposable
{
    private readonly HttpClient _client;
    private readonly AccountService _accounts;
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _assetLocks=new();
    private readonly SemaphoreSlim _coverSlots=new(1,1),_previewSlots=new(2,2);
    public StoreTrust Trust { get; }
    public string CacheRoot { get; }
    public string Status { get; private set; } = "";
    public bool CanManage => StoreCrypto.Snapshot(_accounts.Account,Trust.PublicKeyPem)?.CanManageStore==true;
    public StoreService(AccountService accounts,HttpClient? client=null,StoreTrust? trust=null,string? cacheRoot=null)
    {
        _accounts=accounts; Trust=trust??StoreTrust.Load(); _client=client??new HttpClient();
        _client.Timeout=TimeSpan.FromMinutes(30);
        var url=Environment.GetEnvironmentVariable("RVC_STUDIO_STORE_API_URL")??Trust.ApiUrl;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https") throw new InvalidDataException("商城服务必须使用HTTPS。");
        _client.BaseAddress=new Uri(url.TrimEnd('/')+"/");
        CacheRoot=cacheRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RvcStudio","Store");
    }
    public bool CanUse(string access) => _accounts.IsAuthenticated && StoreCrypto.Snapshot(_accounts.Account,Trust.PublicKeyPem) is { } snapshot && (access=="Free" || snapshot.IsMember || snapshot.CanManageStore);
    public async Task<IReadOnlyList<StoreItem>> CatalogAsync(bool management=false,CancellationToken token=default)
    {
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var items=await ApiAsync<List<StoreItem>>(HttpMethod.Get,$"Catalog?management={management.ToString().ToLowerInvariant()}",null,timeout.Token);
            Status=""; return items;
        }
        catch(Exception ex) when(!token.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or RvcStudioApiException)
        {
            Status="商城服务暂不可用，当前显示本地试听、免费内置音色和已授权的下载。会员商品的上架状态暂无法获取。";
            var cached=Downloaded().Concat(BuiltinVoices.Items).DistinctBy(i=>i.Id).ToList(); return cached;
        }
    }
    private string Folder(string id,string rev)
    {
        if(!StoreCrypto.ValidId(id)||!StoreCrypto.ValidId(rev)) throw new InvalidDataException("商城模型标识无效。");
        return SafeCachePath("Packages",id,rev);
    }
    private string SafeCachePath(params string[] parts)
    {
        var root=Path.GetFullPath(CacheRoot);
        var path=Path.Combine(new[]{root}.Concat(parts).ToArray());
        for(var current=path;current is not null;current=Path.GetDirectoryName(current))
            if((Directory.Exists(current)||File.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("商城缓存目录不能使用链接或重解析点。");
        return path;
    }
    private string GrantName => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_accounts.Account?.Id??"anonymous")))+".grant";
    internal string MediaPath(string filename)=>SafeCachePath("Media",filename);
    public bool IsDownloaded(StoreItem item) => StoreCrypto.ValidId(item.Revision) && File.Exists(Path.Combine(Folder(item.Id,item.Revision),"content.rvcm")) && File.Exists(Path.Combine(Folder(item.Id,item.Revision),GrantName));
    public IReadOnlyList<StoreItem> Downloaded()
    {
        var root=SafeCachePath("Packages"); if(!Directory.Exists(root)) return [];
        var result=new List<StoreItem>();
        foreach(var path in Directory.EnumerateFiles(root,"item.json",new EnumerationOptions {RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=true}))
            try { var item=JsonSerializer.Deserialize<StoreItem>(File.ReadAllText(path),StoreCrypto.Json); if(item is not null && IsDownloaded(item)) result.Add(item); }
            catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException) { }
        return result;
    }
    public async Task DownloadAsync(StoreItem item,IProgress<double>? progress,CancellationToken token)
    {
        if(!_accounts.IsAuthenticated) throw new RvcStudioApiException("请先登录。");
        var accountId=_accounts.Account!.Id;
        var grantName=GrantName;
        var license=await ApiAsync<StoreLicense>(HttpMethod.Post,"AuthorizeModel",new {item.Id,item.Revision},token);
        var manifest=StoreCrypto.ValidateLicense(license,Trust.PublicKeyPem,accountId,item.Id,item.Revision);
        var folder=Folder(item.Id,item.Revision); Directory.CreateDirectory(folder); var part=Path.Combine(folder,"content.part");
        long offset=File.Exists(part)?new FileInfo(part).Length:0;
        if(offset>manifest.Size) {File.Delete(part);offset=0;}
        if(offset!=manifest.Size)
        {
            using var request=Request(HttpMethod.Get,$"Download?id={item.Id}&revision={item.Revision}");
            if(offset>0) request.Headers.Range=new RangeHeaderValue(offset,null);
            using var response=await _client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token); response.EnsureSuccessStatusCode();
            if(response.StatusCode!=HttpStatusCode.PartialContent) offset=0;
            else if(response.Content.Headers.ContentRange?.From!=offset) throw new InvalidDataException("断点下载位置无效。");
            await using var stream=await response.Content.ReadAsStreamAsync(token);
            await using var file=new FileStream(part,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None);
            var buffer=new byte[131072]; long total=offset;
            while(true)
            {
                using var stall=CancellationTokenSource.CreateLinkedTokenSource(token); stall.CancelAfter(TimeSpan.FromSeconds(30));
                var count=await stream.ReadAsync(buffer,stall.Token); if(count==0) break;
                total+=count; if(total>manifest.Size) throw new InvalidDataException("模型包大小超过声明值。");
                await file.WriteAsync(buffer.AsMemory(0,count),token); progress?.Report(total*100d/manifest.Size);
            }
        }
        try { UpdatePackage.VerifyFile(part,manifest.Size,manifest.Sha256); }
        catch { if(File.Exists(part)) File.Delete(part); throw; }
        if(!_accounts.IsAuthenticated || _accounts.Account?.Id!=accountId) throw new RvcStudioApiException("账号已切换，请使用当前账号重新下载。");
        File.Move(part,Path.Combine(folder,"content.rvcm"),true);
        await ProtectedLocalStorage.WriteJsonAsync(Path.Combine(folder,grantName),license,token);
        await File.WriteAllTextAsync(Path.Combine(folder,"item.json"),JsonSerializer.Serialize(item,StoreCrypto.Json),token);
    }
    public async Task<object> LoadPayloadAsync(StoreItem item,CancellationToken token=default)
    {
        try
        {
        if(!_accounts.IsAuthenticated) throw new RvcStudioApiException("请先登录。");
        var folder=Folder(item.Id,item.Revision);
        if(!File.Exists(Path.Combine(folder,GrantName)) || !File.Exists(Path.Combine(folder,"content.rvcm"))) throw new RvcStudioApiException("当前账号没有完整的音色下载或授权，请重新下载。");
        var license=await ProtectedLocalStorage.ReadJsonAsync<StoreLicense>(Path.Combine(folder,GrantName),token) ?? throw new InvalidDataException("该账号没有离线模型授权。");
        var manifest=StoreCrypto.ValidateLicense(license,Trust.PublicKeyPem,_accounts.Account!.Id,item.Id,item.Revision);
        if(!CanUse(manifest.Access)) throw new RvcStudioApiException("当前账号无法使用会员音色，请开通或续费会员。");
        var path=Path.Combine(folder,"content.rvcm"); UpdatePackage.VerifyFile(path,manifest.Size,manifest.Sha256);
        return new { model_id=item.Id,revision=item.Revision,account_id=_accounts.Account.Id,package_path=path,content_key=license.ContentKey,manifest=license.Manifest,grant=license.Grant,snapshot=_accounts.Account.StoreSnapshot };
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
        {throw new InvalidDataException($"商城音色 {item.Id} 验证失败，请重新下载并确认账号资格。");}
    }
    public void DeleteDownload(StoreItem item)
    {
        var folder=Folder(item.Id,item.Revision);
        foreach(var name in new[]{GrantName}) { var file=Path.Combine(folder,name); if(File.Exists(file)) File.Delete(file); }
        // Keep ciphertext if another account on this Windows profile has its own grant.
        if(Directory.Exists(folder) && !Directory.EnumerateFiles(folder,"*.grant").Any())
            foreach(var name in new[]{"content.rvcm","content.part","item.json"}) {var path=Path.Combine(folder,name);if(File.Exists(path))File.Delete(path);}
    }
    private string SettingsPath(StoreItem item)
    {
        if(!StoreCrypto.ValidId(item.Id))throw new InvalidDataException("商城模型标识无效。");
        return SafeCachePath("Settings",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_accounts.Account?.Id??""))),item.Id+".json");
    }
    public ModelTuningSettings Settings(StoreItem item)
    {
        var path=SettingsPath(item);
        try
        {
            var settings=File.Exists(path)?JsonSerializer.Deserialize<ModelTuningSettings>(File.ReadAllText(path),StoreCrypto.Json):null;
            return settings is not null && ModelTuningSettings.TryValidate(settings,out var normalized,out _)?normalized:BuiltinVoices.Tuning(item.Recommended);
        }
        catch(Exception ex) when(ex is IOException or JsonException) {return BuiltinVoices.Tuning(item.Recommended);}
    }
    public void SaveSettings(StoreItem item,ModelTuningSettings settings)
    {
        if(!StoreCrypto.ValidId(item.Id) || !ModelTuningSettings.TryValidate(settings,out var normalized,out var error)) throw new InvalidDataException("商城模型参数无效。");
        var path=SettingsPath(item); Directory.CreateDirectory(Path.GetDirectoryName(path)!); UpdateTransaction.WriteDurable(path,normalized);
    }
    public async Task<byte[]> AssetAsync(string path,CancellationToken token=default)
    {
        if(!path.StartsWith("api/RvcStore/Asset?",StringComparison.Ordinal)) throw new InvalidDataException("商城资源地址无效。");
        // Never cache a mutable "current version" URL. Published revision URLs are immutable.
        var revision=path.Split('?',2)[1].Split('&').FirstOrDefault(p=>p.StartsWith("revision=",StringComparison.Ordinal))?[9..];
        var cachePath=StoreCrypto.ValidId(revision??"")?SafeCachePath("Media","Assets",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))+".asset"):null;
        var gate=_assetLocks.GetOrAdd(path,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(token);
        try
        {
            var preview=path.Contains("kind=preview",StringComparison.Ordinal);
            if(cachePath is not null && await StoreAssetCache.ReadAsync(cachePath,token) is { } cached && (!preview||StoreMedia.AudioExtension(cached) is not null))return cached;
            // Large covers must not queue ahead of a small interactive preview.
            var slots=preview?_previewSlots:_coverSlots;await slots.WaitAsync(token);
            byte[] bytes;
            try
            {
                using var stall=CancellationTokenSource.CreateLinkedTokenSource(token);stall.CancelAfter(TimeSpan.FromSeconds(30));
                using var request=Request(HttpMethod.Get,path);using var response=await _client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stall.Token);response.EnsureSuccessStatusCode();
                await using var input=await response.Content.ReadAsStreamAsync(stall.Token);using var output=new MemoryStream();var buffer=new byte[65536];
                while(true){var n=await input.ReadAsync(buffer,stall.Token);if(n==0)break;if(output.Length+n>StoreAssetCache.MaximumBytes)throw new InvalidDataException("试听或封面过大。");output.Write(buffer,0,n);}
                bytes=output.ToArray();
                if(preview&&StoreMedia.AudioExtension(bytes) is null)throw new InvalidDataException("试听音频格式无效。");
            }
            finally {slots.Release();}
            if(cachePath is not null)
                try {await StoreAssetCache.WriteAsync(cachePath,bytes,token);}
                catch(IOException) { /* A public media cache failure must not prevent playback. */ }
                catch(UnauthorizedAccessException) { }
            return bytes;
        }
        finally {gate.Release();}
    }
    public async Task<StoreJobStatus> UploadAsync(StoreItem item,string? model,string? index,string cover,string preview,string recommended,CancellationToken token)
    {
        using var form=new MultipartFormDataContent();
        foreach(var (key,value) in new Dictionary<string,string>{{"Id",item.Id},{"Name",item.Name},{"Description",item.Description},{"Access",item.Access},{"Version",item.Version},{"SampleRate",item.SampleRate.ToString()},{"SupportsPitch",item.SupportsPitch.ToString()},{"Recommended",recommended}}) form.Add(new StringContent(value),key);
        void Add(string name,string? path) {if(path is not null)form.Add(new StreamContent(File.OpenRead(path)),name,Path.GetFileName(path));}
        Add("ModelFile",model);Add("IndexFile",index);Add("CoverFile",cover);Add("PreviewFile",preview);
        using var request=Request(HttpMethod.Post,"Upload");request.Content=form;using var response=await _client.SendAsync(request,token);return await Read<StoreJobStatus>(response,token);
    }
    public Task<StoreJobStatus> JobAsync(string id,CancellationToken token)=>ApiAsync<StoreJobStatus>(HttpMethod.Get,"UploadStatus?id="+Uri.EscapeDataString(id),null,token);
    public Task<List<StoreJobStatus>> JobsAsync(CancellationToken token)=>ApiAsync<List<StoreJobStatus>>(HttpMethod.Get,"Jobs",null,token);
    public Task<bool> PublishAsync(string id,string revision,CancellationToken token)=>ApiAsync<bool>(HttpMethod.Post,"Publish",new {id,revision},token);
    public Task<bool> UnpublishAsync(string id,CancellationToken token)=>ApiAsync<bool>(HttpMethod.Post,"Unpublish",new {id},token);
    public Task<bool> RetryAsync(string id,CancellationToken token)=>ApiAsync<bool>(HttpMethod.Post,"Retry",new {id},token);
    private HttpRequestMessage Request(HttpMethod method,string path)
    {
        var request=new HttpRequestMessage(method,path.StartsWith("api/")?path:"api/RvcStore/"+path);
        if(_accounts.IsAuthenticated)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_accounts.Token);return request;
    }
    private async Task<T> ApiAsync<T>(HttpMethod method,string path,object? body,CancellationToken token)
    {
        using var request=Request(method,path);if(body is not null)request.Content=JsonContent.Create(body,options:StoreCrypto.Json);
        using var response=await _client.SendAsync(request,token);return await Read<T>(response,token);
    }
    private static async Task<T> Read<T>(HttpResponseMessage response,CancellationToken token)
    {
        var json=await response.Content.ReadAsStringAsync(token);
        ApiEnvelope<T>? result;
        try {result=JsonSerializer.Deserialize<ApiEnvelope<T>>(json,StoreCrypto.Json);}
        catch(JsonException) {throw new RvcStudioApiException("商城服务返回的数据无效，请稍后重试。");}
        if(!response.IsSuccessStatusCode||result?.Success!=true||result.Data is null)throw new RvcStudioApiException(result?.Message?.Content??"商城请求失败，请稍后重试。");return result.Data;
    }
    public void Dispose()=>_client.Dispose();
}
