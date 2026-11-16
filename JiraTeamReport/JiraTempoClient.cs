using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JiraTeamReport;

public sealed record Team(int Id, string Name);
public sealed record Employee(string Id, string Name);
public sealed record ReportCell(string TaskText, string TaskUrl, string EpicText, string EpicUrl)
{
    public static ReportCell Empty { get; } = new("", "", "", "");
}
public sealed class ReportData
{
    public List<Employee> Employees { get; } = new();
    public Dictionary<(string Employee, DateTime Date), ReportCell> Cells { get; } = new();
}
internal sealed class Worklog
{
    [JsonPropertyName("issue")] public WorklogIssue? Issue { get; set; }
    [JsonPropertyName("timeSpentSeconds")] public int Seconds { get; set; }
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("startDate")] public string? StartDate { get; set; }
    [JsonPropertyName("author")] public WorklogAuthor? Author { get; set; }
    [JsonPropertyName("worker")] public string? Worker { get; set; }
    [JsonPropertyName("workerKey")] public string? WorkerKey { get; set; }
    [JsonPropertyName("team")] public WorklogTeam? Team { get; set; }
}
internal sealed class WorklogIssue { [JsonPropertyName("key")] public string Key { get; set; } = ""; [JsonPropertyName("id")] public JsonElement Id { get; set; } }
internal sealed class WorklogAuthor { [JsonPropertyName("accountId")] public string? AccountId { get; set; } [JsonPropertyName("key")] public string? Key { get; set; } [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; }
internal sealed class WorklogTeam { [JsonPropertyName("id")] public int Id { get; set; } }
internal sealed class Issue { [JsonPropertyName("key")] public string Key { get; set; } = ""; [JsonPropertyName("fields")] public IssueFields Fields { get; set; } = new(); }
internal sealed class IssueFields
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("issuetype")] public IssueType? Type { get; set; }
    [JsonPropertyName("parent")] public ParentIssue? Parent { get; set; }
    [JsonPropertyName("customfield_10014")] public string? EpicLink { get; set; }
    [JsonPropertyName("customfield_10008")] public string? EpicLinkLegacy { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}
internal sealed class IssueType { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
internal sealed class ParentIssue { [JsonPropertyName("key")] public string Key { get; set; } = ""; }
internal sealed class Page<T> { [JsonPropertyName("results")] public List<T> Results { get; set; } = []; }

internal sealed class ApiLoggingHandler(string source, Action<string>? log) : DelegatingHandler
{
    private const int MaxBodyLength = 50000;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Write($"OUT [{source}] {request.Method} {request.RequestUri}", requestBody);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR [{source}] {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        Write($"IN  [{source}] {(int)response.StatusCode} {response.ReasonPhrase} {request.RequestUri}", responseBody);
        return response;
    }

    private void Write(string header, string body)
    {
        if (body.Length > MaxBodyLength) body = body[..MaxBodyLength] + "… [обрезано]";
        log?.Invoke($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {header}" + (body.Length == 0 ? "" : Environment.NewLine + body));
    }
}
public sealed class JiraTempoClient : IDisposable
{
    private readonly HttpClient jira;
    private readonly HttpClient tempo;
    private readonly string baseUrl;
    private string? epicLinkFieldId;
    private readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    private readonly Action<string>? logger;

    public JiraTempoClient(string url, string username, string password, Action<string>? logger = null)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new ArgumentException("Введите корректный URL Jira, например https://company.atlassian.net");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Введите логин и пароль Jira.");
        if (uri.Host.EndsWith("atlassian.net", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Этот режим без Tempo API-токена работает с Jira Server/Data Center. Для Jira Cloud Tempo требует отдельную авторизацию.");
        baseUrl = uri.ToString().TrimEnd('/');
        this.logger = logger;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        jira = CreateHttpClient(uri, basic, "Jira");
        tempo = CreateHttpClient(uri, basic, "Tempo");
    }

    private HttpClient CreateHttpClient(Uri baseAddress, string basic, string source)
    {
        var handler = new ApiLoggingHandler(source, logger) { InnerHandler = new HttpClientHandler() };
        var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
    public async Task<List<Team>> GetTeamsAsync()
    {
        using var response = await tempo.GetAsync("rest/tempo-teams/2/team?limit=1000");
        await Ensure(response, "Tempo Teams");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results) ? results : root;
        if (items.ValueKind != JsonValueKind.Array) return [];
        var list = new List<Team>();
        foreach (var item in items.EnumerateArray())
        {
            var id = ReadInt(item, "id"); var name = ReadString(item, "name");
            if (id > 0 && name.Length > 0) list.Add(new Team(id, name));
        }
        return list.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<ReportData> BuildReportAsync(Team team, DateTime from, DateTime to)
    {
        epicLinkFieldId ??= await DiscoverEpicLinkFieldAsync();
        logger?.Invoke($"Epic Link field metadata lookup: {epicLinkFieldId ?? "not found; using standard field IDs"}");
        var teamEmployees = await GetTeamMembersAsync(team.Id);
        var searchBody = new Dictionary<string, object>
        {
            ["from"] = from.ToString("yyyy-MM-dd"),
            ["to"] = to.ToString("yyyy-MM-dd"),
            ["teamId"] = new[] { team.Id }
        };
        using var response = await tempo.PostAsJsonAsync("rest/tempo-timesheets/4/worklogs/search", searchBody);
        await Ensure(response, "Tempo Worklogs");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var worklogItems = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results) ? results : root;
        var logs = new List<Worklog>();
        if (worklogItems.ValueKind == JsonValueKind.Array)
            foreach (var item in worklogItems.EnumerateArray())
            {
                var worklog = item.Deserialize<Worklog>(json);
                if (worklog is not null) logs.Add(worklog);
            }
        logs = logs.Where(w => w.Issue is not null && w.Seconds > 0 && WorkDate(w) >= from.Date && WorkDate(w) <= to.Date).ToList();
        var workerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs)
        {
            if (log.Issue is not null && string.IsNullOrEmpty(log.Issue.Key)) log.Issue.Key = log.Issue.Id.ToString();
            var worker = log.Worker ?? log.WorkerKey;
            if (!string.IsNullOrWhiteSpace(worker))
            {
                if (!workerNames.TryGetValue(worker, out var displayName))
                {
                    displayName = await GetWorkerDisplayNameAsync(worker);
                    workerNames[worker] = displayName;
                }
                if (log.Author is null) log.Author = new WorklogAuthor { Key = worker, DisplayName = displayName };
                else if (string.IsNullOrWhiteSpace(log.Author.DisplayName)) log.Author.DisplayName = displayName;
            }
        }
        logs = logs.Where(w => w.Author is not null && EmployeeId(w).Length > 0).ToList();

        var issueMap = await FetchIssuesAsync(logs.Select(w => w.Issue!.Key).Distinct());
        var epicKeys = issueMap.Values.Select(i => GetEpicKey(i, issueMap)).Where(k => k is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(k => !issueMap.ContainsKey(k)).ToList();
        foreach (var pair in await FetchIssuesAsync(epicKeys)) issueMap[pair.Key] = pair.Value;

        var employeeById = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
        foreach (var employee in teamEmployees) employeeById[employee.Id] = employee;
        foreach (var worklog in logs)
        {
            var id = EmployeeId(worklog);
            if (!employeeById.ContainsKey(id))
            {
                var name = string.IsNullOrWhiteSpace(worklog.Author!.DisplayName) ? id : worklog.Author.DisplayName;
                employeeById[id] = new Employee(id, name);
            }
        }
        var result = new ReportData(); result.Employees.AddRange(employeeById.Values);
        var groups = logs.Where(w => issueMap.ContainsKey(w.Issue!.Key)).GroupBy(w =>
        {
            var id = EmployeeId(w);
            return (Employee: employeeById.TryGetValue(id, out var employee) ? employee.Id : id, Date: WorkDate(w));
        });
        foreach (var group in groups)
        {
            var topEpic = group.GroupBy(w => GetEpicKey(issueMap[w.Issue!.Key], issueMap) ?? "(no epic)")
                .Select(g => new { Key = g.Key, Seconds = g.Sum(w => w.Seconds) }).OrderByDescending(x => x.Seconds).ThenBy(x => x.Key, StringComparer.Ordinal).First();
            var topTask = group.Where(w => (GetEpicKey(issueMap[w.Issue!.Key], issueMap) ?? "(no epic)") == topEpic.Key)
                .GroupBy(w => w.Issue!.Key).Select(g => new { Key = g.Key, Seconds = g.Sum(w => w.Seconds) })
                .OrderByDescending(x => x.Seconds).ThenBy(x => x.Key, StringComparer.Ordinal).First();
            var task = issueMap[topTask.Key]; var epicKey = GetEpicKey(task, issueMap);
            var epic = epicKey is not null && issueMap.TryGetValue(epicKey, out var found) ? found : null;
            result.Cells[group.Key] = new ReportCell(task.Fields.Summary, $"{baseUrl}/browse/{task.Key}",
                epic?.Fields.Summary ?? "", epic is null ? "" : $"{baseUrl}/browse/{epic.Key}");
        }
        return result;
    }

    private static string EmployeeId(Worklog worklog) => worklog.Worker ?? worklog.WorkerKey ?? worklog.Author?.Key ?? worklog.Author?.AccountId ?? worklog.Author?.DisplayName ?? "";

    private async Task<List<Employee>> GetTeamMembersAsync(int teamId)
    {
        using var response = await tempo.GetAsync($"rest/tempo-teams/2/team/{teamId}/member");
        await Ensure(response, "Tempo Team Members");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results) ? results : root;
        var employees = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var member = item.TryGetProperty("member", out var nested) ? nested : item;
                if (member.ValueKind != JsonValueKind.Object) continue;
                if (member.TryGetProperty("activeInJira", out var active) && active.ValueKind == JsonValueKind.False) continue;
                var key = ReadString(member, "key");
                if (key.Length == 0) key = ReadString(member, "name");
                if (key.Length == 0) continue;
                var name = ReadString(member, "displayname");
                if (name.Length == 0) name = ReadString(member, "displayName");
                if (name.Length == 0) name = await GetWorkerDisplayNameAsync(key);
                employees[key] = new Employee(key, name);
            }
        }
        return employees.Values.ToList();
    }
    private async Task<string> GetWorkerDisplayNameAsync(string worker)
    {
        try
        {
            using var response = await jira.GetAsync($"rest/api/2/user?username={Uri.EscapeDataString(worker)}");
            if (!response.IsSuccessStatusCode) return worker;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ReadString(doc.RootElement, "displayName") is { Length: > 0 } name ? name : worker;
        }
        catch { return worker; }
    }
    private static DateTime WorkDate(Worklog worklog)
    {
        var value = worklog.Started?.Trim() ?? "";
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) return date.Date;

        // Jira Data Center can emit offsets such as +0500; DateTimeOffset expects +05:00.
        var normalized = System.Text.RegularExpressions.Regex.Replace(value, @"([+-]\d{2})(\d{2})$", "$1:$2");
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return date.Date;

        if (DateTime.TryParse(worklog.StartDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate)) return startDate.Date;
        throw new FormatException($"Tempo вернул неподдерживаемый формат даты worklog: {value}");
    }

    private async Task<Dictionary<string, Issue>> FetchIssuesAsync(IEnumerable<string> keys)
    {
        using var gate = new SemaphoreSlim(8);
        var tasks = keys.Select(async key =>
        {
            await gate.WaitAsync();
            try { return (Key: key, Issue: await GetIssueAsync(key)); }
            finally { gate.Release(); }
        });
        var loaded = await Task.WhenAll(tasks);
        return loaded.Where(x => x.Issue is not null).ToDictionary(x => x.Key, x => x.Issue!, StringComparer.OrdinalIgnoreCase);
    }
    private async Task<Issue?> GetIssueAsync(string key)
    {
        var fields = "summary,issuetype,parent,customfield_10014,customfield_10008";
        if (!string.IsNullOrWhiteSpace(epicLinkFieldId) && !fields.Contains(epicLinkFieldId, StringComparison.OrdinalIgnoreCase)) fields += "," + epicLinkFieldId;
        using var response = await jira.GetAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={fields}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var legacy = await jira.GetAsync($"rest/api/2/issue/{Uri.EscapeDataString(key)}?fields={fields}");
            if (legacy.StatusCode == HttpStatusCode.NotFound) return null;
            await Ensure(legacy, $"Jira issue {key}");
            await using var legacyStream = await legacy.Content.ReadAsStreamAsync(); return NormalizeEpicLink(await JsonSerializer.DeserializeAsync<Issue>(legacyStream, json));
        }
        await Ensure(response, $"Jira issue {key}");
        await using var stream = await response.Content.ReadAsStreamAsync(); return NormalizeEpicLink(await JsonSerializer.DeserializeAsync<Issue>(stream, json));
    }
    private async Task<string?> DiscoverEpicLinkFieldAsync()
    {
        foreach (var version in new[] { "2", "3" })
        {
            try
            {
                using var response = await jira.GetAsync($"rest/api/{version}/field");
                if (!response.IsSuccessStatusCode) continue;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                string? namedMatch = null;
                foreach (var field in document.RootElement.EnumerateArray())
                {
                    var id = ReadString(field, "id");
                    var name = ReadString(field, "name");
                    var customType = field.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object ? ReadString(schema, "custom") : "";
                    if (customType.Contains("gh-epic-link", StringComparison.OrdinalIgnoreCase)) return id;
                    if (name.Equals("Epic Link", StringComparison.OrdinalIgnoreCase) || name.Equals("Ссылка на эпик", StringComparison.OrdinalIgnoreCase) || name.Equals("Связь с эпиком", StringComparison.OrdinalIgnoreCase)) namedMatch = id;
                }
                if (!string.IsNullOrWhiteSpace(namedMatch)) return namedMatch;
            }
            catch { }
        }
        return null;
    }

    private Issue? NormalizeEpicLink(Issue? issue)
    {
        if (issue is null) return null;
        if (!string.IsNullOrWhiteSpace(epicLinkFieldId) && issue.Fields.AdditionalFields?.TryGetValue(epicLinkFieldId, out var value) == true)
        {
            if (value.ValueKind == JsonValueKind.String) issue.Fields.EpicLink ??= value.GetString();
            else if (value.ValueKind == JsonValueKind.Number) issue.Fields.EpicLink ??= value.ToString();
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("key", out var key)) issue.Fields.EpicLink ??= key.ToString();
            logger?.Invoke($"Epic Link parse: issue={issue.Key}; field={epicLinkFieldId}; raw={value}");
        }
        else
        {
            var available = issue.Fields.AdditionalFields is null ? "none" : string.Join(", ", issue.Fields.AdditionalFields.Keys.Where(k => k.StartsWith("customfield_", StringComparison.OrdinalIgnoreCase)));
            logger?.Invoke($"Epic Link field value absent: issue={issue.Key}; expected={epicLinkFieldId ?? "unknown"}; EpicLink={issue.Fields.EpicLink ?? issue.Fields.EpicLinkLegacy ?? "null"}; received custom fields={available}");
        }
        return issue;
    }
    private static string? GetEpicKey(Issue issue, IReadOnlyDictionary<string, Issue> issueMap)
    {
        if (string.Equals(issue.Fields.Type?.Name, "Epic", StringComparison.OrdinalIgnoreCase)) return issue.Key;
        if (!string.IsNullOrWhiteSpace(issue.Fields.EpicLink)) return issue.Fields.EpicLink;
        if (!string.IsNullOrWhiteSpace(issue.Fields.EpicLinkLegacy)) return issue.Fields.EpicLinkLegacy;
        if (issue.Fields.Parent?.Key is { Length: > 0 } parentKey && issueMap.TryGetValue(parentKey, out var parent))
        {
            if (string.Equals(parent.Fields.Type?.Name, "Epic", StringComparison.OrdinalIgnoreCase)) return parent.Key;
            return GetEpicKey(parent, issueMap);
        }
        return null;
    }
    private static int ReadInt(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static string ReadString(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
    public void Dispose() { jira.Dispose(); tempo.Dispose(); }
    private static async Task Ensure(HttpResponseMessage response, string source)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        body = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
        body = System.Text.RegularExpressions.Regex.Replace(body, "\\s+", " ").Trim();
        if (body.Length > 500) body = body[..500] + "…";
        throw new HttpRequestException($"{source}: {(int)response.StatusCode} {response.ReasonPhrase}. Проверьте URL и права пользователя. Ответ сервера: {body}");
    }
}




































