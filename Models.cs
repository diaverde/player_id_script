record PlayerToIdentify
{
    public int ContentTitleCatId { get; set; }
    public long Cid { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }
}

record ContentMetadata
{
    public long Cid { get; set; }
    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }
    public Guid HomeTeamGuid { get; set; }
    public Guid AwayTeamGuid { get; set; }
}

public class TeamProfile
{
    public int Id { get; set; }
    public Guid Guid { get; set; }
    public string? Name { get; set; }
    public string? Market { get; set; }
    public string? Alias { get; set; }
    public string? Abbreviation { get; set; }
    public string? League { get; set; }
    public TeamMember[]? Coaches { get; set; }
    public TeamMember[]? Staff { get; set; }
    public TeamMember[]? Players { get; set; }
}

public class TeamMember
{
    public Guid Id { get; set; }
    public string? FullName { get; set; }
    public string? ShortName { get; set; }
    public string? PreferredName { get; set; }
    public string? JerseyNumber { get; set; }
}
