namespace CodeFlow.Runtime.Web;

public sealed class WebToolOptions
{
    public const string SectionName = "WebTools";

    public IList<string> AllowedSchemes { get; set; } = ["https", "http"];

    public int SearchTimeoutSeconds { get; set; } = 15;

    public int FetchTimeoutSeconds { get; set; } = 20;

    public int MaxRedirects { get; set; } = 5;

    public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    public long MaxExtractedTextBytes { get; set; } = 256 * 1024;

    public int MaxSearchResults { get; set; } = 8;

    public bool BlockPrivateNetworks { get; set; } = true;

    public bool SendCredentials { get; set; }

    public bool AllowCookies { get; set; }

    public bool AllowAuthHeaders { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (AllowedSchemes is null || AllowedSchemes.Count == 0)
        {
            errors.Add("WebTools:AllowedSchemes must contain at least one scheme.");
        }
        else
        {
            foreach (var scheme in AllowedSchemes)
            {
                if (string.IsNullOrWhiteSpace(scheme))
                {
                    errors.Add("WebTools:AllowedSchemes must not contain blank entries.");
                }
                else if (!string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("WebTools:AllowedSchemes may only contain http or https.");
                }
            }
        }

        if (SearchTimeoutSeconds <= 0)
        {
            errors.Add("WebTools:SearchTimeoutSeconds must be greater than zero.");
        }

        if (FetchTimeoutSeconds <= 0)
        {
            errors.Add("WebTools:FetchTimeoutSeconds must be greater than zero.");
        }

        if (MaxRedirects < 0)
        {
            errors.Add("WebTools:MaxRedirects must be zero or greater.");
        }

        if (MaxResponseBytes <= 0)
        {
            errors.Add("WebTools:MaxResponseBytes must be greater than zero.");
        }

        if (MaxExtractedTextBytes <= 0)
        {
            errors.Add("WebTools:MaxExtractedTextBytes must be greater than zero.");
        }

        if (MaxSearchResults <= 0)
        {
            errors.Add("WebTools:MaxSearchResults must be greater than zero.");
        }

        if (!BlockPrivateNetworks)
        {
            errors.Add("WebTools:BlockPrivateNetworks must remain true.");
        }

        if (SendCredentials)
        {
            errors.Add("WebTools:SendCredentials must remain false.");
        }

        if (AllowCookies)
        {
            errors.Add("WebTools:AllowCookies must remain false.");
        }

        if (AllowAuthHeaders)
        {
            errors.Add("WebTools:AllowAuthHeaders must remain false.");
        }

        return errors;
    }
}
