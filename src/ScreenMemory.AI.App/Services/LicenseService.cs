using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenMemory.AI.App.Services;

public sealed class LicenseState
{
    public bool IsPro { get; init; }

    public string PlanName => IsPro ? "Pro" : "Free";

    public string Status { get; init; } = "free";

    public string LicenseKey { get; init; } = string.Empty;

    public string InstanceId { get; init; } = string.Empty;

    public string CustomerEmail { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public DateTimeOffset? ActivatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class LicenseActivationResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public LicenseState State { get; init; } = new();
}

public sealed class LicenseService
{
    public const int FreeScreenshotLimit = 200;

    // Fill these once Lemon Squeezy product IDs are created. Leave null to skip product checking during local testing.
    private const int ExpectedStoreId = 0;
    private const int ExpectedProductId = 0;
    private const int ExpectedVariantId = 0;
    private const string CheckoutUrl = "https://your-store.lemonsqueezy.com/buy/your-checkout-link";

    private static readonly Uri ActivateEndpoint = new("https://api.lemonsqueezy.com/v1/licenses/activate");
    private static readonly Uri ValidateEndpoint = new("https://api.lemonsqueezy.com/v1/licenses/validate");
    private static readonly Uri DeactivateEndpoint = new("https://api.lemonsqueezy.com/v1/licenses/deactivate");

    private readonly HttpClient _httpClient;
    private readonly string _licensePath;
    private LicenseState? _cachedState;

    public LicenseService(HttpClient? httpClient = null, string? licensePath = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _licensePath = licensePath ?? GetDefaultLicensePath();
    }

    public string UpgradeUrl => CheckoutUrl;

    public LicenseState GetState()
    {
        if (_cachedState is not null)
        {
            return _cachedState;
        }

        _cachedState = LoadLocalLicense() ?? new LicenseState();
        return _cachedState;
    }

    public bool IsPro => GetState().IsPro;

    public bool CanIndexNewScreenshot(int currentScreenshotCount) =>
        IsPro || currentScreenshotCount < FreeScreenshotLimit;

    public string GetUsageLabel(int currentScreenshotCount)
    {
        if (IsPro)
        {
            return $"{currentScreenshotCount:N0} screenshots indexed";
        }

        return $"{Math.Min(currentScreenshotCount, FreeScreenshotLimit):N0}/{FreeScreenshotLimit:N0} free screenshots";
    }

    public async Task<LicenseActivationResult> ActivateAsync(string licenseKey, CancellationToken token = default)
    {
        var normalizedKey = licenseKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return new LicenseActivationResult { Message = "Enter a license key." };
        }

        try
        {
            var response = await PostFormAsync(
                ActivateEndpoint,
                new Dictionary<string, string>
                {
                    ["license_key"] = normalizedKey,
                    ["instance_name"] = GetMachineInstanceName()
                },
                token);

            var activation = JsonSerializer.Deserialize<LemonActivationResponse>(response, JsonOptions);
            if (activation is null)
            {
                return new LicenseActivationResult { Message = "License server returned an unreadable response." };
            }

            if (!activation.Activated)
            {
                return new LicenseActivationResult
                {
                    Message = string.IsNullOrWhiteSpace(activation.Error)
                        ? "License activation failed."
                        : activation.Error
                };
            }

            if (!IsExpectedProduct(activation.Meta))
            {
                return new LicenseActivationResult { Message = "This license key is for a different product." };
            }

            var state = CreateState(normalizedKey, activation.LicenseKey, activation.Instance, activation.Meta);
            SaveLocalLicense(state);
            _cachedState = state;

            return new LicenseActivationResult
            {
                Success = true,
                Message = "ScreenMemory AI Pro activated.",
                State = state
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or CryptographicException)
        {
            return new LicenseActivationResult { Message = $"Activation failed: {ex.Message}" };
        }
    }

    public async Task<LicenseActivationResult> ValidateOnlineAsync(CancellationToken token = default)
    {
        var state = GetState();
        if (!state.IsPro || string.IsNullOrWhiteSpace(state.LicenseKey) || string.IsNullOrWhiteSpace(state.InstanceId))
        {
            return new LicenseActivationResult
            {
                Success = false,
                Message = "No active Pro license found.",
                State = state
            };
        }

        try
        {
            var response = await PostFormAsync(
                ValidateEndpoint,
                new Dictionary<string, string>
                {
                    ["license_key"] = state.LicenseKey,
                    ["instance_id"] = state.InstanceId
                },
                token);

            var validation = JsonSerializer.Deserialize<LemonValidationResponse>(response, JsonOptions);
            if (validation?.Valid == true &&
                IsActiveStatus(validation.LicenseKey?.Status) &&
                IsExpectedProduct(validation.Meta))
            {
                return new LicenseActivationResult
                {
                    Success = true,
                    Message = "License is valid.",
                    State = state
                };
            }

            return new LicenseActivationResult
            {
                Message = validation?.Error ?? "License is no longer valid.",
                State = state
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new LicenseActivationResult
            {
                Success = true,
                Message = $"Offline validation only: {ex.Message}",
                State = state
            };
        }
    }

    public async Task<LicenseActivationResult> DeactivateAsync(CancellationToken token = default)
    {
        var state = GetState();
        if (string.IsNullOrWhiteSpace(state.LicenseKey) || string.IsNullOrWhiteSpace(state.InstanceId))
        {
            ClearLocalLicense();
            return new LicenseActivationResult { Success = true, Message = "Local license removed." };
        }

        try
        {
            var response = await PostFormAsync(
                DeactivateEndpoint,
                new Dictionary<string, string>
                {
                    ["license_key"] = state.LicenseKey,
                    ["instance_id"] = state.InstanceId
                },
                token);

            var deactivation = JsonSerializer.Deserialize<LemonDeactivationResponse>(response, JsonOptions);
            if (deactivation?.Deactivated == false)
            {
                return new LicenseActivationResult
                {
                    Message = deactivation.Error ?? "License deactivation failed.",
                    State = state
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new LicenseActivationResult
            {
                Message = $"Could not deactivate online: {ex.Message}",
                State = state
            };
        }

        ClearLocalLicense();
        return new LicenseActivationResult
        {
            Success = true,
            Message = "License deactivated on this device.",
            State = GetState()
        };
    }

    private async Task<string> PostFormAsync(Uri endpoint, Dictionary<string, string> form, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"License server returned {(int)response.StatusCode}."
                : body);
        }

        return body;
    }

    private LicenseState? LoadLocalLicense()
    {
        if (!File.Exists(_licensePath))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(_licensePath);
            var plaintext = ProtectedData.Unprotect(encrypted, GetEntropy(), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LicenseState>(plaintext, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveLocalLicense(LicenseState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_licensePath)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var encrypted = ProtectedData.Protect(plaintext, GetEntropy(), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_licensePath, encrypted);
    }

    private void ClearLocalLicense()
    {
        if (File.Exists(_licensePath))
        {
            File.Delete(_licensePath);
        }

        _cachedState = new LicenseState();
    }

    private static LicenseState CreateState(
        string licenseKey,
        LemonLicenseKey? lemonLicenseKey,
        LemonInstance? instance,
        LemonMeta? meta)
    {
        return new LicenseState
        {
            IsPro = IsActiveStatus(lemonLicenseKey?.Status),
            Status = lemonLicenseKey?.Status ?? "active",
            LicenseKey = licenseKey,
            InstanceId = instance?.Id ?? string.Empty,
            CustomerEmail = meta?.CustomerEmail ?? string.Empty,
            ProductName = meta?.ProductName ?? "ScreenMemory AI Pro",
            ActivatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = TryParseDate(lemonLicenseKey?.ExpiresAt)
        };
    }

    private static bool IsExpectedProduct(LemonMeta? meta)
    {
        if (meta is null)
        {
            return ExpectedStoreId == 0 && ExpectedProductId == 0 && ExpectedVariantId == 0;
        }

        return (ExpectedStoreId == 0 || meta.StoreId == ExpectedStoreId) &&
               (ExpectedProductId == 0 || meta.ProductId == ExpectedProductId) &&
               (ExpectedVariantId == 0 || meta.VariantId == ExpectedVariantId);
    }

    private static bool IsActiveStatus(string? status) =>
        string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? TryParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string GetDefaultLicensePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ScreenMemory AI");
        return Path.Combine(folder, "license.lic");
    }

    private static byte[] GetEntropy() => Encoding.UTF8.GetBytes("ScreenMemory AI License v1");

    private static string GetMachineInstanceName() =>
        $"{Environment.MachineName} - {Environment.UserName}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class LemonActivationResponse
    {
        [JsonPropertyName("activated")]
        public bool Activated { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("license_key")]
        public LemonLicenseKey? LicenseKey { get; set; }

        [JsonPropertyName("instance")]
        public LemonInstance? Instance { get; set; }

        [JsonPropertyName("meta")]
        public LemonMeta? Meta { get; set; }
    }

    private sealed class LemonValidationResponse
    {
        [JsonPropertyName("valid")]
        public bool Valid { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("license_key")]
        public LemonLicenseKey? LicenseKey { get; set; }

        [JsonPropertyName("meta")]
        public LemonMeta? Meta { get; set; }
    }

    private sealed class LemonDeactivationResponse
    {
        [JsonPropertyName("deactivated")]
        public bool Deactivated { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class LemonLicenseKey
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }
    }

    private sealed class LemonInstance
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class LemonMeta
    {
        [JsonPropertyName("store_id")]
        public int StoreId { get; set; }

        [JsonPropertyName("product_id")]
        public int ProductId { get; set; }

        [JsonPropertyName("variant_id")]
        public int VariantId { get; set; }

        [JsonPropertyName("product_name")]
        public string ProductName { get; set; } = string.Empty;

        [JsonPropertyName("customer_email")]
        public string CustomerEmail { get; set; } = string.Empty;
    }
}
