using System.Data;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

class Program
{
    static async Task Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        string connectionString = config["ConnectionStrings:DefaultConnection"]!;
        string oaiApiUrl = config["AppSettings:openai_api:url"]!;
        string oaiApiKey = config["AppSettings:openai_api:key"]!;

        // 1. Get players that have not been identified
        List<PlayerToIdentify> unidentifiedPlayers = GetPlayersToIdentify(connectionString);

        // 2. Get team data from the database
        List<TeamProfile> teams = GetTeamData(connectionString);

        // 3. Get relevant teams from content metadata
        long[] cids = unidentifiedPlayers.Select(p => p.Cid).ToArray();
        List<ContentMetadata> contentTeams = GetTeamsFromContent(connectionString, cids);

        // 4. Assign the corresponding team GUIDs
        List<long> contentToSkip = new List<long>();
        foreach (var contentEntry in contentTeams)
        {
            try
            {
                contentEntry.HomeTeamGuid = teams.First(t => t.Id == contentEntry.HomeTeamId).Guid;
                contentEntry.AwayTeamGuid = teams.First(t => t.Id == contentEntry.AwayTeamId).Guid;
                //Console.WriteLine($"Content {contentEntry.Cid} - Home {contentEntry.HomeTeamId} ({contentEntry.HomeTeamGuid}) vs Away {contentEntry.AwayTeamId} ({contentEntry.AwayTeamGuid})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error assigning team GUIDs for content {contentEntry.Cid} - {contentEntry.HomeTeamId} / {contentEntry.AwayTeamId}: {ex.Message}");
                //throw;
                contentToSkip.Add(contentEntry.Cid);
            }
        }

        // 5. Retrieve roster data for each team
        teams = GetTeamRoster(connectionString, teams);

        // 6. Identify players using OpenAI API
        int identifiedCount = 0;
        foreach (var player in unidentifiedPlayers)
        {
            // Skip dev cases
            if (contentToSkip.Contains(player.Cid))
            {
                Console.WriteLine($"Skipping player {player.Name} with CID {player.Cid} due to missing team data.");
                continue;
            }

            var homeTeamGuid = contentTeams.FirstOrDefault(c => c.Cid == player.Cid)?.HomeTeamGuid;
            var awayTeamGuid = contentTeams.FirstOrDefault(c => c.Cid == player.Cid)?.AwayTeamGuid;

            var homeTeamInfo = teams.FirstOrDefault(t => t.Guid == homeTeamGuid);
            var awayTeamInfo = teams.FirstOrDefault(t => t.Guid == awayTeamGuid);

            try
            {
                string prompt = buildGPTPrompt(homeTeamInfo, awayTeamInfo, player.Name);
                //Console.WriteLine($"Identifying player: {player.Name} with prompt: {prompt}");
                var response = await IdentifyPlayerFromApi(oaiApiUrl, oaiApiKey, prompt);
                //Console.WriteLine(JsonSerializer.Deserialize<JsonElement>(response, new JsonSerializerOptions { WriteIndented = true }));
                if (response.TryGetProperty("choices", out var identifiedPlayer))
                {
                    string playerInfo = identifiedPlayer[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(playerInfo) && Guid.TryParse(playerInfo, out Guid result))
                    {
                        // Update the player record in the database
                        UpdatePlayerRecord(connectionString, player.ContentTitleCatId, playerInfo);
                        Console.WriteLine($"Identified {playerInfo} as player {player.Name}");
                        identifiedCount++;
                    }
                    else
                    {
                        Console.WriteLine($"No player identified for {player.Name}");
                    }
                }

                await Task.Delay(900); // Delay to avoid hitting API rate limits
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error identifying player {player.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Identified {identifiedCount} players out of {unidentifiedPlayers.Count} total players.");
        Console.WriteLine("Player identification process completed.");
        Console.WriteLine($"Skipped {string.Join(',', contentToSkip)}.");
    }

    static List<PlayerToIdentify> GetPlayersToIdentify(string connectionString)
    {
        var playerRecords = new List<PlayerToIdentify>();
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            //@"SELECT ID, CID, Name 
            @"SELECT TOP(30) ID, CID, Name 
            FROM [RSN_CDN].[dbo].[ContentTitleCat]
            WHERE Category='Player' AND Value IS NULL"
            //AND ID > 25621"
            , conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            PlayerToIdentify player = new PlayerToIdentify
            {
                ContentTitleCatId = reader.GetInt32(0),
                Cid = reader.GetInt64(1),
                Name = reader.GetString(2)
            };
            playerRecords.Add(player);
        }
        return playerRecords;
    }

    static List<TeamProfile> GetTeamData(string connectionString)
    {
        var teams = new List<TeamProfile>();
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT ID, srGuid, Name, srAlias, SportLUID_Code FROM [RSN_CDN].[dbo].[Team_Full] WHERE srGuid IS NOT null;", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            TeamProfile team = new TeamProfile
            {
                Id = reader.GetInt32(0),
                Guid = reader.GetGuid(1),
                Name = reader.GetString(2),
                Alias = reader.IsDBNull(3) ? null : reader.GetString(3),
                League = reader.IsDBNull(4) ? null : reader.GetString(4)
            };

            teams.Add(team);
        }

        // Special case for the Athletics, as they don't appear in the Team_Full table
        if (teams.All(t => t.Name != "Athletics"))
        {
            teams.Add(new TeamProfile
            {
                Id = 85, // ID in the RSN_CDN database
                Guid = new Guid("27A59D3B-FF7C-48EA-B016-4798F560F5E1"), // GUID in SportRadar
                Name = "Athletics",
                Alias = "ATH",
                League = "MLB"
            });
        }
        return teams;
    }

    static List<ContentMetadata> GetTeamsFromContent(string connectionString, long[] cids)
    {
        var content = new List<ContentMetadata>();
        StringBuilder stringBuilder = new StringBuilder();
        foreach (long cId in cids)
        {
            stringBuilder.Append($"{cId},");
        }
        stringBuilder.Remove(stringBuilder.Length - 1, 1);
        string cidRange = stringBuilder.ToString();

        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            @$"SELECT CID, HomeID, VisitorID  
            FROM [RSN_CDN].[dbo].[Content_Full]
            WHERE CID IN ({cidRange})", conn);
        //Console.WriteLine($"Querying content for CIDs: {cidRange}");
        //Console.WriteLine($"SQL Command: {cmd.CommandText}");
        //cmd.Parameters.AddWithValue("@CIDs", cidRange);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ContentMetadata contentMetadata = new ContentMetadata
            {
                Cid = reader.GetInt64(0),
                HomeTeamId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                AwayTeamId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
            };

            // If HomeID or VisitorID is null, we skip this entry
            if (contentMetadata.HomeTeamId == 0 || contentMetadata.AwayTeamId == 0)
            {
                continue;
            }

            content.Add(contentMetadata);
        }
        return content;
    }

    static List<TeamProfile> GetTeamRoster(string connectionString, List<TeamProfile> teams)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        foreach (var team in teams)
        {
            string query = string.Empty;
            if (team.League == "NBA")
            {
                query = $"SELECT guid, fullname, abbrname, jerseynumber FROM [SportRadar].[dbo].[nbaPlayer] WHERE teamGuid = @teamGuid;";
            }
            else if (team.League == "NHL")
            {
                query = $"SELECT guid, fullname, abbrname, jerseynumber FROM [SportRadar].[dbo].[nhlPlayer] WHERE teamGuid = @teamGuid;";
            }
            else if (team.League == "MLB")
            {
                query = $"SELECT guid, fullname, preferredname, jerseynumber FROM [SportRadar].[dbo].[mlbPlayer] WHERE teamGuid = @teamGuid;";
            }

            List<TeamMember> players = new List<TeamMember>();
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@teamGuid", team.Guid);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                TeamMember member = new TeamMember
                {
                    Id = reader.GetGuid(0),
                    FullName = reader.GetString(1),
                    ShortName = reader.GetString(2),
                    JerseyNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3).ToString()
                };

                players.Add(member);
            }

            team.Players = players.ToArray();
        }
        return teams;
    }

    static string buildGPTPrompt(TeamProfile team1, TeamProfile team2, string userPrompt)
    {
        const string baseText =
            @"Help me identify a player from the roster of two teams. I will send you a single text in the user content. 
            This text may contain: 
            - a part of the player''s name, 
            - a text and number combination with a reference to any of the teams and a jersey number, 
            - several of the previous combinations,separated by spaces. 
            I will send you here a json-like text with two rosters with detailed information of each team''s players and coaches.
            Simply identify the ""id"" attribute of the most likely person (or people, if several were identified from a space
            separated input) and return it as simple string or a comma-separated string if more than one person was found.
            Take these considerations for each case: - For case 1: When trying to match text, it is ok to find approximations;
            - For case 2: split the input between text and number, and try to match the text part with the initial of the team name,
            market or alias, then within that team use the numerical part to find the person using only the exact jersey number match;
            - For case 3: for every space separated part, consider the previous instructions for the other cases.
            If no suitable person is found, return an empty string. These are the rosters:
            { ""Team1"": <Team1Roster>, ""Team2"": <Team2Roster> }";

        string instructions = baseText
            .Replace("<Team1Roster>", JsonSerializer.Serialize(team1))
            .Replace("<Team2Roster>", JsonSerializer.Serialize(team2));

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = userPrompt }
            },
            max_completion_tokens = 3500,
            temperature = 1.0,
            top_p = 1,
            response_format = new { type = "text" }
        };

        return JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    static async Task<JsonElement> IdentifyPlayerFromApi(string url, string apiKey, string requestBody)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "C# App");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(responseString);
    }

    static void UpdatePlayerRecord(string connectionString, int contentTitleCatId, string value)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE [RSN_CDN].[dbo].[ContentTitleCat] SET Value = @Value WHERE ID = @ID", conn);
        cmd.Parameters.AddWithValue("@Value", value);
        cmd.Parameters.AddWithValue("@ID", contentTitleCatId);
        cmd.ExecuteNonQuery();
    }
}