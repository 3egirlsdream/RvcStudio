using System.Security.Cryptography;
using System.Text.Json;

namespace RvcStudio.App;

public sealed record StoreSignature(string Payload, string Signature);
public sealed record StoreSnapshot(string Kind,string AccountId,bool IsMember,bool CanManageStore,DateTimeOffset CheckedAt);
public sealed record StoreManifest(string Kind,int Schema,string ModelId,string Revision,string Access,long Size,string Sha256,
    string Version,int SampleRate,bool SupportsPitch,long ModelSize,long IndexSize,string Recommended);
public sealed record StoreGrant(string Kind,string AccountId,string ModelId,string Revision,string KeySha256);
public sealed record StoreLicense(StoreSignature Manifest,StoreSignature Grant,string ContentKey);
public sealed record StoreJobStatus(string Id,string ModelId,string Revision,string State,string Message);
public sealed class StoreItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Access { get; set; } = "Member";
    public string BuiltinFile { get; set; } = "";
    public string Version { get; set; } = "v2";
    public int SampleRate { get; set; }
    public bool SupportsPitch { get; set; } = true;
    public string Revision { get; set; } = "";
    public long Size { get; set; }
    public string State { get; set; } = "Draft";
    public string CoverUrl { get; set; } = "";
    public string PreviewUrl { get; set; } = "";
    public string Recommended { get; set; } = "{}";
    public bool IsBuiltin => !string.IsNullOrEmpty(BuiltinFile);
    public string Badge => Access=="Free" ? "免费" : "会员";
    public string Details => $"{Version.ToUpperInvariant()} · {SampleRate/1000} kHz · {(Size>0?UpdatePackage.FormatSize(Size):IsBuiltin?"已随软件附带":State=="Preview"?"试听样例":"待上架")}";
}
public sealed record StoreTrust(string ApiUrl,string PublicKeyPem)
{
    public static StoreTrust Load()
    {
        using var stream=typeof(StoreTrust).Assembly.GetManifestResourceStream("RvcStudio.StoreTrust.json")!;
        return JsonSerializer.Deserialize<StoreTrust>(stream,StoreCrypto.Json)!;
    }
}
public static class StoreCrypto
{
    public static readonly JsonSerializerOptions Json=new() { PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    public static bool ValidId(string? id) => id is {Length:>0 and <=64} && id.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-');
    public static T Verify<T>(StoreSignature envelope,string publicKey)
    {
        if(envelope.Payload.Length>4*1024*1024) throw new InvalidDataException("商城签名数据过大。");
        var bytes=Convert.FromBase64String(envelope.Payload); using var rsa=RSA.Create(); rsa.ImportFromPem(publicKey);
        if(rsa.KeySize<2048 || !rsa.VerifyData(bytes,Convert.FromBase64String(envelope.Signature),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)) throw new InvalidDataException("商城资源签名无效。");
        return JsonSerializer.Deserialize<T>(bytes,Json) ?? throw new InvalidDataException("商城数据为空。");
    }
    public static StoreSnapshot? Snapshot(AccountProfile? account,string publicKey)
    {
        if(account?.StoreSnapshot is null || string.IsNullOrWhiteSpace(publicKey)) return null;
        try { var snapshot=Verify<StoreSnapshot>(account.StoreSnapshot,publicKey); return snapshot.Kind=="membership" && snapshot.AccountId==account.Id ? snapshot : null; }
        catch(Exception ex) when(ex is CryptographicException or ArgumentException or FormatException or InvalidDataException or JsonException) { return null; }
    }
    public static StoreManifest ValidateLicense(StoreLicense license,string publicKey,string accountId,string modelId,string revision)
    {
        var manifest=Verify<StoreManifest>(license.Manifest,publicKey); var grant=Verify<StoreGrant>(license.Grant,publicKey);
        if(manifest.Kind!="modelManifest" || manifest.Schema!=1 || !ValidId(manifest.ModelId) || !ValidId(manifest.Revision) || manifest.ModelId!=modelId || manifest.Revision!=revision ||
           grant.Kind!="modelGrant" || grant.AccountId!=accountId || grant.ModelId!=modelId || grant.Revision!=revision || manifest.Access is not ("Free" or "Member") ||
           manifest.Size<=0 || manifest.Size>2147483648 || manifest.ModelSize<=0 || manifest.IndexSize<=0 || manifest.ModelSize+manifest.IndexSize>2147483648 ||
           manifest.Version is not ("v1" or "v2") || manifest.SampleRate is not (32000 or 40000 or 48000) || manifest.Sha256 is not {Length:64})
            throw new InvalidDataException("商城授权与账号或模型不匹配。");
        var key=Convert.FromBase64String(license.ContentKey);
        try { if(key.Length!=32 || Convert.ToHexString(SHA256.HashData(key))!=grant.KeySha256) throw new InvalidDataException("商城密钥验证失败。"); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return manifest;
    }
}

public static class StoreEntitlementPolicy
{
    public static bool IsMember(AccountProfile? account,string publicKey) => account?.StoreSnapshot is not null
        ? StoreCrypto.Snapshot(account,publicKey)?.IsMember==true : account?.IsMember==true;
    public static bool IsFree(bool running,bool? sessionMember,AccountProfile? account,string publicKey) =>
        !(running && sessionMember.HasValue ? sessionMember.Value : IsMember(account,publicKey));
}

public static class BuiltinVoices
{
    public static readonly StoreItem[] Items=
    [
        new() {Id="10000000000000000000000000000001",Name="莞宁",BuiltinFile="guanguanV1.pth",Version="v1",SampleRate=40000,Access="Free",State="Published"},
        new() {Id="10000000000000000000000000000002",Name="可棠",BuiltinFile="keruanV1.pth",Version="v1",SampleRate=40000,Access="Free",State="Published"},
        new() {Id="10000000000000000000000000000003",Name="绮音",BuiltinFile="kikiV1.pth",Version="v1",SampleRate=40000,Access="Free",State="Published"},
        new() {Id="10000000000000000000000000000004",Name="悠汐",BuiltinFile="youzhanv2-xi.pth",Version="v2",SampleRate=48000,Access="Free",State="Published"},
        new() {Id="20000000000000000000000000000001",Name="星澜",Version="v2",SampleRate=48000,State="Preview"},
        new() {Id="20000000000000000000000000000002",Name="晴栀",Version="v2",SampleRate=40000,State="Preview"},
        new() {Id="20000000000000000000000000000003",Name="知夏",Version="v2",SampleRate=40000,State="Preview"}
    ];
    public static StoreItem? Find(string basename) => Items.FirstOrDefault(i=>i.BuiltinFile.Equals(basename,StringComparison.OrdinalIgnoreCase));
    public static ModelTuningSettings Tuning(string json)
    {
        using var doc=JsonDocument.Parse(string.IsNullOrWhiteSpace(json)?"{}":json); var r=doc.RootElement;
        if(r.TryGetProperty("recommended",out var nested)) r=nested;
        double N(string key,double d) => r.TryGetProperty(key,out var v) && v.TryGetDouble(out var n)?n:d;
        var d=ModelTuningSettings.AppDefaults;
        var settings=new ModelTuningSettings(r.TryGetProperty("f0method",out var m)?m.GetString()??d.PitchMethod:d.PitchMethod,N("pitch",d.Pitch),N("formant",d.Formant),N("indexRate",d.IndexRate),N("rmsMixRate",d.RmsMixRate),N("threshold",d.Threshold),N("blockTime",d.BlockTime),N("crossfadeLength",d.CrossfadeLength),N("extraTime",d.ExtraTime));
        if(!ModelTuningSettings.TryValidate(settings,out var normalized,out var error)) throw new InvalidDataException(error); return normalized;
    }
}
