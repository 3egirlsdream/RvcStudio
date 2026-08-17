using System.Text.Json.Serialization;

namespace RvcStudio.App;

public sealed class ApiEnvelope<T>
{
    public bool Success { get; set; }
    public int? Code { get; set; }
    public T? Data { get; set; }
    public ApiMessage? Message { get; set; }
}

public sealed class ApiMessage
{
    public string? Content { get; set; }
}

public sealed class AccountProfile
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UserType { get; set; } = string.Empty;
    public string MembershipType { get; set; } = "NONE";
    public DateTime? MembershipExpireDate { get; set; }
    public bool IsMember { get; set; }

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(DisplayName)
        ? "R"
        : DisplayName.Trim()[0].ToString().ToUpperInvariant();
}

public sealed class AuthResult
{
    public string Token { get; set; } = string.Empty;
    public AccountProfile Account { get; set; } = new();
}

public sealed class MembershipPlan
{
    public string Code { get; set; } = string.Empty;
    public string MembershipType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool Recommended { get; set; }

    [JsonIgnore]
    public string PriceText => $"¥{Price:0.##}";
}

public sealed class MembershipOrder
{
    public string OutTradeNo { get; set; } = string.Empty;
    public string PayUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime ExpireAt { get; set; }
}

public sealed class MembershipOrderStatus
{
    public string OutTradeNo { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MembershipType { get; set; } = string.Empty;
    public DateTime? MembershipExpireDate { get; set; }
}

public sealed class AccountSession
{
    public string Token { get; set; } = string.Empty;
    public AccountProfile Account { get; set; } = new();
}

public sealed class RvcStudioApiException(string message) : Exception(message);
