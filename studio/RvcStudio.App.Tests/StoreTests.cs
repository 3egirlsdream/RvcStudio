using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RvcStudio.App;
using Xunit;

namespace RvcStudio.App.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly RSA _key=RSA.Create(2048);
    private readonly string _root=Path.Combine(Path.GetTempPath(),"rvc-store-client-"+Guid.NewGuid().ToString("N"));
    private string Public=>_key.ExportSubjectPublicKeyInfoPem();
    private const string Id="test-model",Revision="test-revision";
    public StoreTests()=>Directory.CreateDirectory(_root);
    private StoreSignature Sign<T>(T data)
    {var bytes=JsonSerializer.SerializeToUtf8Bytes(data,StoreCrypto.Json);return new(Convert.ToBase64String(bytes),Convert.ToBase64String(_key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)));}
    private AccountProfile Profile(string id="account-a",bool member=true)=>new(){Id=id,IsMember=member,MembershipExpireDate=DateTime.Now.AddYears(-1),StoreSnapshot=Sign(new StoreSnapshot("membership",id,member,false,DateTimeOffset.UtcNow.AddYears(-1)))};
    private StoreLicense License(byte[] bytes,string account="account-a")
    {
        var key=RandomNumberGenerator.GetBytes(32);return new(Sign(new StoreManifest("modelManifest",1,Id,Revision,"Member",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)),"v2",40000,true,10,20,"{}")),
            Sign(new StoreGrant("modelGrant",account,Id,Revision,Convert.ToHexString(SHA256.HashData(key)))),Convert.ToBase64String(key));
    }
    private AccountService Account(AccountProfile profile)
    {
        var client=new HttpClient(new Handler(_=>Json(new AuthResult {Token="test-session",Account=profile}))) {BaseAddress=new("https://test.invalid/")};
        return new(new RvcStudioApiClient(client),Path.Combine(_root,profile.Id));
    }
    private static HttpResponseMessage Json<T>(T data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new ApiEnvelope<T>{Success=true,Data=data},StoreCrypto.Json))};

    [Fact]
    public void EngineWireFormatUsesLowercaseSignatureEnvelopeKeys()
    {
        var license=License([1,2,3]);
        var payload=EngineClient.EncodePayload(new {model_id=Id,manifest=license.Manifest,grant=license.Grant});
        using var doc=JsonDocument.Parse(payload!.ToJsonString());
        Assert.Equal(license.Manifest.Payload,doc.RootElement.GetProperty("manifest").GetProperty("payload").GetString());
        Assert.Equal(license.Grant.Signature,doc.RootElement.GetProperty("grant").GetProperty("signature").GetString());
    }
    [Fact]
    public void MembershipSnapshotsRemainValidOfflineAfterDatesPassButRefreshBlocksNextSession()
    {
        var member=Profile();var expired=Profile(member:false);
        Assert.True(StoreEntitlementPolicy.IsMember(member,Public));
        Assert.False(StoreEntitlementPolicy.IsFree(true,true,expired,Public));
        Assert.True(StoreEntitlementPolicy.IsFree(false,null,expired,Public));
        Assert.False(StoreEntitlementPolicy.IsFree(false,null,member,Public));
    }
    [Fact]
    public void SnapshotCannotBeCopiedToAnotherAccountOrEditedForAdminRights()
    {
        var profile=Profile();profile.Id="other";Assert.Null(StoreCrypto.Snapshot(profile,Public));
        profile=Profile();profile.StoreSnapshot=profile.StoreSnapshot! with {Payload=Convert.ToBase64String("{}"u8.ToArray())};
        Assert.Null(StoreCrypto.Snapshot(profile,Public));Assert.False(StoreEntitlementPolicy.IsMember(profile,Public));
        Assert.False(profile.CanManageStore);
    }
    [Fact]
    public void LicenseRejectsOtherAccountVersionKeyAndSignature()
    {
        var license=License([1,2,3]);Assert.Equal(Id,StoreCrypto.ValidateLicense(license,Public,"account-a",Id,Revision).ModelId);
        Assert.Throws<InvalidDataException>(()=>StoreCrypto.ValidateLicense(license,Public,"account-b",Id,Revision));
        Assert.Throws<InvalidDataException>(()=>StoreCrypto.ValidateLicense(license,Public,"account-a",Id,"other"));
        Assert.Throws<InvalidDataException>(()=>StoreCrypto.ValidateLicense(license with {ContentKey=Convert.ToBase64String(new byte[32])},Public,"account-a",Id,Revision));
        using var other=RSA.Create(2048);Assert.Throws<InvalidDataException>(()=>StoreCrypto.ValidateLicense(license,other.ExportSubjectPublicKeyInfoPem(),"account-a",Id,Revision));
    }
    [Fact]
    public async Task DownloadCachesCiphertextAndDpapiGrantWithoutAnyPlainModelFiles()
    {
        var bytes="test encrypted content"u8.ToArray();var license=License(bytes);
        using var account=Account(Profile());await account.LoginAsync("a","pass");
        using var service=new StoreService(account,new HttpClient(new Handler(r=>r.Method==HttpMethod.Post?Json(license):new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),new("https://test.invalid",Public),Path.Combine(_root,"cache"));
        var item=new StoreItem {Id=Id,Revision=Revision,Name="Test",Access="Member"};await service.DownloadAsync(item,null,CancellationToken.None);
        Assert.True(service.IsDownloaded(item));Assert.Single(service.Downloaded());Assert.NotNull(await service.LoadPayloadAsync(item));
        var files=Directory.GetFiles(Path.Combine(_root,"cache"),"*",SearchOption.AllDirectories);
        Assert.DoesNotContain(files,p=>p.EndsWith(".pth")||p.EndsWith(".index"));
        var grant=files.Single(p=>p.EndsWith(".grant"));Assert.DoesNotContain(license.ContentKey,System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(grant)));
        service.DeleteDownload(item);Assert.False(service.IsDownloaded(item));
    }
    [Fact]
    public async Task DownloadResumesFromPartAndRejectsCorruptedCiphertext()
    {
        var bytes="encrypted bytes"u8.ToArray();var license=License(bytes);using var account=Account(Profile());await account.LoginAsync("a","p");
        var cache=Path.Combine(_root,"cache");var folder=Path.Combine(cache,"Packages",Id,Revision);Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"content.part"),bytes[..4]);
        bool ranged=false;
        using var service=new StoreService(account,new HttpClient(new Handler(r=>
        {
            if(r.Method==HttpMethod.Post)return Json(license);
            ranged=r.Headers.Range?.Ranges.Single().From==4;
            var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(bytes[4..])};response.Content.Headers.ContentRange=new(4,bytes.Length-1,bytes.Length);return response;
        })),new("https://test.invalid",Public),cache);
        await service.DownloadAsync(new(){Id=Id,Revision=Revision},null,CancellationToken.None);Assert.True(ranged);
        var file=Path.Combine(folder,"content.rvcm");File.WriteAllBytes(file,"corrupt"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadPayloadAsync(new(){Id=Id,Revision=Revision}));
    }
    [Fact]
    public async Task CatalogFallsBackAndAnotherAccountCannotUseExistingGrant()
    {
        using var account=Account(Profile());await account.LoginAsync("a","p");
        using var store=new StoreService(account,new HttpClient(new Handler(_=>throw new HttpRequestException("offline"))),new("https://test.invalid",Public),Path.Combine(_root,"cache"));
        Assert.Equal(7,(await store.CatalogAsync()).Count);Assert.NotEmpty(store.Status);
        Assert.True(store.CanUse("Member"));
        using var other=Account(Profile("account-b",false));await other.LoginAsync("b","p");
        using var otherStore=new StoreService(other,new HttpClient(new Handler(_=>throw new HttpRequestException())),new("https://test.invalid",Public),Path.Combine(_root,"cache"));
        Assert.False(otherStore.CanUse("Member"));
        await Assert.ThrowsAsync<RvcStudioApiException>(()=>otherStore.LoadPayloadAsync(new(){Id=Id,Revision=Revision}));
    }
    [Fact]
    public async Task OfflineRestartPreservesMembershipAndOnlineDowngradeBlocksWithoutRedownloading()
    {
        var bytes="encrypted fixture"u8.ToArray();var license=License(bytes);var cache=Path.Combine(_root,"cache");
        using(var account=Account(Profile()))
        {
            await account.LoginAsync("a","p");
            using var store=new StoreService(account,new HttpClient(new Handler(r=>r.Method==HttpMethod.Post?Json(license):new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),new("https://test.invalid",Public),cache);
            await store.DownloadAsync(new(){Id=Id,Revision=Revision},null,CancellationToken.None);
        }
        var online=false;var member=false;
        using var client=new RvcStudioApiClient(new HttpClient(new Handler(_=>online?Json(Profile(member:member)):new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("maintenance")})){BaseAddress=new("https://test.invalid/")});
        using var restarted=new AccountService(client,Path.Combine(_root,"account-a"));await restarted.InitializeAsync();
        using var storeAfterRestart=new StoreService(restarted,new HttpClient(new Handler(_=>throw new HttpRequestException())),new("https://test.invalid",Public),cache);
        var item=new StoreItem {Id=Id,Revision=Revision};
        Assert.True(storeAfterRestart.CanUse("Member"));Assert.NotNull(await storeAfterRestart.LoadPayloadAsync(item));
        online=true;await restarted.RefreshAsync();
        Assert.True(storeAfterRestart.IsDownloaded(item));Assert.False(storeAfterRestart.CanUse("Member"));
        await Assert.ThrowsAsync<RvcStudioApiException>(()=>storeAfterRestart.LoadPayloadAsync(item));
        member=true;await restarted.RefreshAsync();Assert.NotNull(await storeAfterRestart.LoadPayloadAsync(item));
    }
    [Fact]
    public async Task StoreModelsCannotReachLocalExportEvenWhenCalledWithoutUi()
    {
        var profiles=new ModelProfileService(Path.Combine(_root,"profiles.json"));var packages=new ModelPackageService(_root,profiles);
        var model=new ManagedModel("protected","store://id/revision","index","params",false);
        Assert.False(model.CanExport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>packages.ExportAsync([model],Path.Combine(_root,"exports"),false));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>Task.FromResult(response(r));}
    public void Dispose(){_key.Dispose();Directory.Delete(_root,true);}
}
