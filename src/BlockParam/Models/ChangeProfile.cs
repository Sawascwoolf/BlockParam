using Newtonsoft.Json;

namespace BlockParam.Models;

/// <summary>
/// A saved bulk operation preset that can be reloaded and applied.
/// </summary>
public class ChangeProfile
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("pathPattern")]
    public string PathPattern { get; set; } = "";

    [JsonProperty("memberDatatype")]
    public string MemberDatatype { get; set; } = "";

    [JsonProperty("newValue")]
    public string NewValue { get; set; } = "";

    /// <summary>"broadest", "narrowest", or null (let user choose)</summary>
    [JsonProperty("scopePreference")]
    public string? ScopePreference { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("created")]
    public DateTime Created { get; set; }
}
